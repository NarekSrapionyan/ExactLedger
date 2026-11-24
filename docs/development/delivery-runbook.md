# Backend delivery runbook

Use synthetic data only. Run from the repository root. This covers backend
acceptance; Swift screens/device accessibility remain part of the mobile work.

## Clean startup and seed

Install Docker Desktop with Linux containers. Follow [local Docker setup](local-docker.md)
to create `.env` with separate generated database passwords, a 16+ character
`SEED_PASSWORD` and a random base64 JWT signing key. Never commit that file.

```powershell
docker compose --profile app up --build -d
docker compose --profile app ps -a
Invoke-RestMethod http://localhost:8080/health/live
```

For a clean demonstration **without deleting an existing database**, copy `.env`
to a locally ignored `.env.demo`, choose unused API/PostgreSQL ports, and use
`docker compose --project-name exactledger-demo --env-file .env.demo --profile app`
in place of `docker compose`. Its separate named volume starts empty. `migrate`
and `seed` must exit 0 before the API starts. Schema changes run as the migration
role; the API keeps the restricted application role. The migration seeds parties,
accounts, USD/JPY/KWD, regions and published example profiles. Identity seeding
adds the documented role-bound logins without granting them extra permissions.

## Repeatable Finance and Payee journey

Use a fresh demonstration database for these exact trade/key names. The following
PowerShell uses the default port; adjust `$api` for an isolated demo project.
Enter the local SEED_PASSWORD at the prompt. Tokens stay in memory.

```powershell
$api = 'http://localhost:8080'
$credential = Get-Credential -UserName finance -Message 'Enter the local synthetic SEED_PASSWORD'
$demoPassword = $credential.GetNetworkCredential().Password
function Login($user) {
    (Invoke-RestMethod "$api/v1/auth/login" -Method Post -ContentType application/json -Body (
        @{ username = $user; password = $demoPassword } | ConvertTo-Json)).accessToken
}
$finance = @{ Authorization = 'Bearer ' + (Login 'finance') }
$integrator = @{ Authorization = 'Bearer ' + (Login 'integrator'); 'Idempotency-Key' = 'demo-trade-1' }
$admin = @{ Authorization = 'Bearer ' + (Login 'admin') }
$payee = @{ Authorization = 'Bearer ' + (Login 'payee-seller-1') }
$profiles = Invoke-RestMethod "$api/v1/fee-profiles" -Headers $admin
$profile = ($profiles | Where-Object { $_.content.corporateCode -eq 'DEMO-SIMPLE' }).content
$day = [DateTime]::UtcNow.ToString('yyyy-MM-dd')
$trade = @{
    tradeId = 'DEMO-DELIVERY-1'; occurredAt = $day + 'T00:00:00Z'; currency = 'USD'
    gross = @{ amount = '250.00'; currency = 'USD' }; regionCode = 'DEMO-NONE'
    profileId = $profile.profileId; corporateCode = $profile.corporateCode
    beneficiaries = @(@{ lineId = 'seller'; partyId = '625ef6d0-ce43-3186-d39e-d28c006d70c9'
        kind = 'Remainder'; weight = 0; rateUnits = 0 })
}
$settlement = Invoke-RestMethod "$api/v1/settlements" -Method Post -Headers $integrator `
    -ContentType application/json -Body ($trade | ConvertTo-Json -Depth 8)
Invoke-RestMethod "$api/v1/settlements/$($settlement.id)/trace" -Headers $finance
$batch = Invoke-RestMethod "$api/v1/clearing-batches" -Method Post -Headers $finance `
    -ContentType application/json -Body (@{ businessDate = $day; currency = 'USD' } | ConvertTo-Json)
Invoke-RestMethod "$api/v1/reports/batch-summary?batchId=$($batch.id)" -Headers $finance
Invoke-RestMethod "$api/v1/reports/payee-statement" -Headers $payee
Invoke-RestMethod "$api/v1/clearing-batches/$($batch.id)/export" -Method Post -Headers $finance
New-Item artifacts -ItemType Directory -Force | Out-Null
Invoke-WebRequest "$api/v1/clearing-batches/$($batch.id)/simulate-statement" -Method Post `
    -Headers $finance -OutFile artifacts/synthetic-statement.csv
Invoke-RestMethod "$api/v1/clearing-batches/$($batch.id)/confirm-simulated" -Method Post `
    -Headers $finance -ContentType text/csv -InFile artifacts/synthetic-statement.csv
Invoke-RestMethod "$api/v1/reports/batch-summary?batchId=$($batch.id)" -Headers $finance
Invoke-RestMethod "$api/v1/reports/payee-statement" -Headers $payee
$exportHeaders = @{ Authorization = $finance.Authorization; 'Idempotency-Key' = 'demo-report-1' }
$export = Invoke-RestMethod "$api/v1/reports/journal-register/exports" -Method Post `
    -Headers $exportHeaders -ContentType application/json -Body '{}'
Invoke-WebRequest ($api + $export.downloadUrl) -Headers $finance -OutFile artifacts/journal.csv
(Get-FileHash artifacts/journal.csv -Algorithm SHA256).Hash.ToLowerInvariant() -eq $export.sha256
```

The simple profile charges 1% processor fee: seller allocation/payout is 247.50 USD,
processor payable is 2.50 USD. Before confirmation the 247.50 is reserved, paid is
zero and no payable debit/bank credit exists. After confirmation, reserved is zero
and paid = payable debit = bank credit = 247.50. Reconfirming identical statement
bytes changes no balance. The trial balance difference is always zero.

For the PRP's standard waterfall/escrow/refund examples, follow
[settlements](settlements.md), [escrow](escrow.md) and [refunds](refunds.md).
Use an unbatched settlement to demonstrate an eligible refund. Released escrow
creates a new payable for a later batch; exporting alone never pays it.

## Backup and restore smoke test

Use the same Compose project/options as startup. This retains the source database
and restores into a **new** database. Commands below work in PowerShell and avoid
redirecting binary pg_dump output through Windows PowerShell's text pipeline.

```powershell
docker compose exec -T postgres sh -c 'pg_dump -U postgres -d exactledger -Fc -f /tmp/exactledger.dump'
docker compose cp postgres:/tmp/exactledger.dump artifacts/exactledger.dump
docker compose exec -T postgres createdb -U postgres -O exactledger_migrations exactledger_restore
docker compose exec -T postgres pg_restore -U postgres --exit-on-error --no-owner --role=exactledger_migrations -d exactledger_restore /tmp/exactledger.dump
docker compose exec -T postgres psql -U postgres -d exactledger_restore -c 'SELECT count(*) FROM ledger.settlements; SELECT count(*) FROM ledger.report_exports;'
docker compose exec -T postgres psql -U postgres -d exactledger_restore -c 'SELECT currency, sum(CASE WHEN side=1 THEN amount_minor::numeric ELSE -amount_minor::numeric END) AS difference FROM ledger.journal_lines GROUP BY currency;'
```

Compare row counts and artifact SHA-256 values to the source; each currency
difference must be zero. Repoint an isolated API's **runtime** connection to
`exactledger_restore` to inspect restored reports. Keep migration credentials out
of API configuration. Restore into an already provisioned local cluster so the
runtime/migration roles exist. A database dump does not provision cluster roles.
Use a new database name for a repeat; do not overwrite another developer's data.

## Automated verification and reference measurement

```powershell
dotnet restore ExactLedger.sln --locked-mode
dotnet build ExactLedger.sln -c Release --no-restore -warnaserror
dotnet test ExactLedger.sln -c Release --no-build --no-restore --collect 'XPlat Code Coverage' --settings coverage.runsettings --results-directory artifacts/delivery-tests
powershell -NoProfile -File scripts/Check-DomainCoverage.ps1 -ResultsDirectory artifacts/delivery-tests
python scripts/Measure-Delivery.py
```

The measurement uses Python 3.11+ standard library and an entirely new Compose
project with random credentials/ports, creates 10,000 real HTTP settlements at five
concurrent clients, cuts/exports/confirms the first 1,000-trade batch, measures reads,
checks reconciliation/hash and restores a custom-format backup into a second
database. Its `finally` removes only that generated project/volumes and temporary
credential file. Results and synthetic dump stay under ignored
`artifacts/be014-delivery/`. See [recorded measurements](performance.md).
