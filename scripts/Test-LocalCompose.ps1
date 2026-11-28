$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# A unique Compose project, temporary credentials and free host ports isolate this
# check from the developer's normal database. Cleanup only touches this project.
function Get-FreePort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return $listener.LocalEndpoint.Port }
    finally { $listener.Stop() }
}

$root = Split-Path -Parent $PSScriptRoot
$project = 'exactledger-smoke-' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
$envFile = Join-Path $root "artifacts/$project.env"
$appPassword = [Guid]::NewGuid().ToString('N')
$apiPort = Get-FreePort
$seedPassword = 'seed_' + [Guid]::NewGuid().ToString('N')
$settings = [ordered]@{
    POSTGRES_PASSWORD = [Guid]::NewGuid().ToString('N')
    MIGRATION_PASSWORD = [Guid]::NewGuid().ToString('N')
    APP_PASSWORD = $appPassword
    SEED_PASSWORD = $seedPassword
    JWT_SIGNING_KEY = [Convert]::ToBase64String(([Guid]::NewGuid().ToByteArray() + [Guid]::NewGuid().ToByteArray()))
    POSTGRES_PORT = Get-FreePort
    API_PORT = $apiPort
    CLEARING_SCHEDULE_ENABLED = 'true'
    ESCROW_SCHEDULE_ENABLED = 'true'
    ESCROW_INTERVAL_SECONDS = '60'
}
$savedEnvironment = @{}
$composeArguments = @('compose', '--project-name', $project, '--env-file', $envFile, '--profile', 'app')

function Invoke-Compose {
    & docker @composeArguments @args
    if ($LASTEXITCODE -ne 0) { throw "Compose failed with exit code $LASTEXITCODE." }
}

function Wait-Api {
    $deadline = [DateTime]::UtcNow.AddMinutes(2)
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            $response = Invoke-WebRequest "http://127.0.0.1:$apiPort/health/live" -UseBasicParsing -TimeoutSec 5
            if ($response.StatusCode -eq 200) { return }
        }
        catch { Start-Sleep -Seconds 2 }
    }
    throw 'The containerized API did not become live.'
}

function Assert-RuntimeSql([string]$Sql, [string]$Expected) {
    $result = Invoke-Compose exec -T -e "PGPASSWORD=$appPassword" postgres psql `
        -h 127.0.0.1 -U exactledger_app -d exactledger -v ON_ERROR_STOP=1 -q -t -A -c $Sql
    if (($result -join "`n").Trim() -ne $Expected) { throw "Unexpected runtime SQL result: $result" }
}

function Wait-SchedulerAudit($Headers, [int]$MinimumRuns) {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $events = Invoke-RestMethod "http://127.0.0.1:$apiPort/v1/audit-events?limit=100" -Headers $Headers
        if (@($events | Where-Object { $_.operation -eq 'identity.seeded' }).Count -ne 9) {
            throw 'Expected exactly nine identity provisioning audit events.'
        }
        if (@($events | Where-Object { $_.operation -eq 'escrow.release-run' -and $_.actorId -eq 'scheduler' }).Count -ge $MinimumRuns) {
            return
        }
        Start-Sleep -Seconds 1
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'The enabled escrow worker did not record its startup run.'
}

Push-Location $root
try {
    New-Item -ItemType Directory -Path (Split-Path -Parent $envFile) -Force | Out-Null
    $settings.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" } | Set-Content -LiteralPath $envFile -Encoding ASCII
    foreach ($key in $settings.Keys) {
        $savedEnvironment[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        # Set explicit values: empty inherited variables can override --env-file
        # on newer PowerShell/.NET versions even when cleared with a null value.
        [Environment]::SetEnvironmentVariable($key, [string]$settings[$key], 'Process')
    }

    Invoke-Compose config --quiet
    Invoke-Compose up --build --detach
    Wait-Api
    $openApi = Invoke-RestMethod "http://127.0.0.1:$apiPort/openapi/v1.json"
    if ($openApi.info.title -ne 'ExactLedger API') { throw 'Unexpected OpenAPI document.' }
    $login = Invoke-RestMethod "http://127.0.0.1:$apiPort/v1/auth/login" -Method Post -ContentType 'application/json' `
        -Body (@{ username = 'finance'; password = $seedPassword } | ConvertTo-Json)
    $headers = @{ Authorization = 'Bearer ' + $login.accessToken }
    # Background jobs add legitimate audit events; verify provisioning separately
    # and wait for the real worker rather than racing API liveness against startup.
    Wait-SchedulerAudit $headers 1
    $uid = Invoke-Compose exec -T api id -u
    if (($uid -join '').Trim() -eq '0') { throw 'API must not run as root.' }

    Assert-RuntimeSql "SELECT current_user = 'exactledger_app' AND (SELECT count(*) FROM ledger.accounts) >= 30 AND (SELECT count(*) FROM ledger.__ef_migrations_history) >= 2" 't'
    Assert-RuntimeSql "SELECT has_table_privilege(current_user, 'ledger.journal_lines', 'INSERT') AND NOT has_table_privilege(current_user, 'ledger.journal_lines', 'UPDATE') AND NOT has_table_privilege(current_user, 'ledger.journal_lines', 'DELETE') AND NOT has_schema_privilege(current_user, 'ledger', 'CREATE')" 't'
    Assert-RuntimeSql "SELECT NOT bool_or(rolsuper OR rolcreatedb OR rolcreaterole) FROM pg_roles WHERE rolname IN ('exactledger_app', 'exactledger_migrations')" 't'
    Assert-RuntimeSql "INSERT INTO ledger.parties (id, name, status) VALUES ('dca9abe8-4459-45f0-afd8-bd17cc3db535', 'Synthetic Compose Smoke', 1); SELECT count(*) FROM ledger.parties WHERE name = 'Synthetic Compose Smoke'" '1'

    # Recreate containers without deleting the volume: migrations must be safe to
    # rerun and an inserted row must survive, rather than merely being re-seeded.
    Invoke-Compose down
    Invoke-Compose up --detach
    Wait-Api
    Assert-RuntimeSql "SELECT count(*) FROM ledger.parties WHERE name = 'Synthetic Compose Smoke'" '1'
    Assert-RuntimeSql "SELECT count(*) FROM ledger.audit_events WHERE operation='identity.seeded'" '9'
    Wait-SchedulerAudit $headers 2
    Write-Output 'Compose smoke check passed: migrations, roles, API, automatic escrow and persistent data.'
}
catch {
    & docker @composeArguments logs --no-color
    throw
}
finally {
    try {
        & docker @composeArguments down --volumes --remove-orphans --rmi local
        if ($LASTEXITCODE -ne 0) { Write-Warning "Cleanup of $project failed; inspect this isolated project." }
    }
    finally {
        foreach ($key in $savedEnvironment.Keys) {
            [Environment]::SetEnvironmentVariable($key, $savedEnvironment[$key], 'Process')
        }
        if (Test-Path -LiteralPath $envFile) { Remove-Item -LiteralPath $envFile }
        Pop-Location
    }
}
