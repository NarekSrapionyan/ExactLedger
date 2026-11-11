# Sprint 1 acceptance and release gate

Task: [QA-001 — Sprint 1 Full Acceptance (BE-001–BE-006)](https://app.clickup.com/t/z8r3fe3mxr).

Scope: BE-001 through BE-006, including all BE-001/BE-002 subtasks. The release is a
database-independent Domain foundation, not the completed educational MVP.

The feature baseline is commit `a7c1dfed92b0f6cfbdd7f61d655e479e80fb1601` (merged PR #11).
The QA package adds acceptance tests and this runbook; record its final merged commit as the
release candidate. Any later change invalidates the candidate until its affected checks and
the full suite are rerun. Keep BE-007 PRs #12/#13/#14 outside this release candidate.

## Ownership and entry conditions

- The QA Champion owns independent acceptance and the requirement-to-evidence matrix.
- Developers own feature fixes and their regression tests. A developer's green run is evidence,
  not independent QA approval.
- BE-001 through BE-006 and their implementation subtasks must be complete and merged.
- Use a clean checkout/worktree, the SDK pinned by `global.json`, and locked dependencies.
- This scope requires no PostgreSQL, Docker, authentication server or mobile simulator.
- The ClickUp QA list contains one Sprint 1 task. Define later-stage QA tasks separately
  when needed; this acceptance does not certify database, security or full mobile workflows.

## Reproduce the full check

Record the exact commit, operating system, `dotnet --version`, command exit codes, test counts,
failures/skips and coverage. Use a new results directory for every independent run to avoid
combining old coverage. The following PowerShell sequence stops at the first failed command:

```powershell
git status --short
git rev-parse HEAD
dotnet --version
$qaResults = 'artifacts/sprint1-' + [guid]::NewGuid().ToString('N')
dotnet restore ExactLedger.sln --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed' }
dotnet build ExactLedger.sln --configuration Release --no-restore -warnaserror
if ($LASTEXITCODE -ne 0) { throw 'Release build failed' }
dotnet test ExactLedger.sln --configuration Release --no-build --no-restore --logger trx --results-directory $qaResults --collect "XPlat Code Coverage" --settings coverage.runsettings
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
./scripts/Check-DomainCoverage.ps1 -ResultsDirectory $qaResults
```

Run the coverage script with PowerShell 7 on Linux/macOS. If Windows PowerShell's execution
policy prevents this reviewed script from running, use a process-only invocation:
`powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/Check-DomainCoverage.ps1 -ResultsDirectory $qaResults`.
Do not change the machine execution policy.

Expected: zero build warnings/errors, all tests pass with no unexplained skips, Domain line
coverage at least 85%, and both hosted CI jobs pass for the candidate. Preserve TRX files,
Cobertura XML, `domain-coverage-summary.md`, the CI run link and any defect reports.
Test counts may grow; confirm the acceptance tests are actually included instead of relying
only on an old count. For a focused composition check:

```powershell
dotnet test tests/el.Domain.Tests --configuration Release --no-build --no-restore --filter FullyQualifiedName~Sprint1AcceptanceTests
```

## Requirement-to-evidence checklist

Record each row as PASS / FAIL / BLOCKED / NOT APPLICABLE, with a test name or manual evidence,
the candidate SHA, reviewer and defect links. A blank or blocked in-scope row is not a pass.

| Area | Required checks | Existing evidence |
|---|---|---|
| BE-001 foundation | Clean restore/build/startup; pinned SDK/locks; allowed project dependencies; no EF/HTTP in Domain; financial source-policy negative fixtures; secrets kept outside tracked config | `DependencyTests`, `FinancialSourcePolicyTests`, startup instructions, configuration review |
| BE-002 currency/Money | USD/2, JPY/0, KWD/3; explicit reference version; signed limits +/-99,999,999,999; overflow; zero/negative values; cross-currency rejection; exact BigInteger totals | `Currency*Tests`, `MoneyTests`, `MoneyAggregationTests` |
| BE-002 text/JSON | Exact exponents, culture independence, round trips; numeric/null/duplicate/unknown JSON rejection; strict parsing without implicit rounding | `MoneyTextTests`, `MoneyJsonContractTests`, `docs/contracts/money/` fixtures |
| BE-003 percentages | 0..100%; max six fractional digits; 2.900000 -> 2,900,000/100,000,000; exact signed products beyond 64 bits | `PercentageTextTests`, `ScaledPercentageTests` |
| BE-004 rounding | All three explicit modes; invalid/default mode and denominator rejection; 39 golden vectors per mode; signed 2.345 USD; exact trace; Money conversion overflow | `RoundingGoldenVectorTests`, `RoundingTests`, `RoundingPolicyTests` |
| BE-005 allocation | Five units at weights 50/30/20 -> 3/1/1; zero weights; ordinal ID ties; invalid input; 10,000 seeded cases for conservation, quota error and Largest Remainder winner selection; input-order independence; no cumulative partial refunds | `WeightedAllocationTests` |
| BE-006 ledger | Typed nonempty IDs; entity/value equality; defined enum values; party/account lifecycle; account ownership/currency; positive lines; at least two lines; one currency; active accounts; exact balanced immutable journals; zero generated components omitted, submitted zeros rejected | `IdentityTests`, `PartyTests`, `AccountTests`, `JournalEntryTests` |
| Cross-module composition | Synthetic 250.00 USD examples with/without 2% regional deduction become balanced journals; five-unit allocations in USD/JPY/KWD become correct credit lines; zero allocations are omitted only for generated components | `Sprint1AcceptanceTests` |
| API host boundary | Health in Development and Production; Development OpenAPI title/version; Production hides OpenAPI and unknown routes return 404 ProblemDetails; startup without database | `ApiHostTests` plus process smoke below |

The cross-module examples compose primitives in memory; they do not prove BE-011 settlement,
HTTP financial endpoints, database posting or legal/regional compliance. All example parties,
rules and account movements are synthetic.

## Process smoke

Use the documented HTTPS launch profile (`dotnet run --project src/el.Api --configuration Release
--no-build --launch-profile https`) and a trusted local development certificate. Verify:

- `GET https://localhost:7243/health/live`: 200, `Healthy`.
- `GET https://localhost:7243/openapi/v1.json`: 200, title `ExactLedger API`, version `v1`.
- An unknown route returns 404 ProblemDetails; there are no settlement/login/balance endpoints.

Repeat with the host environment explicitly set to Production, using an available local URL
and no launch profile. Health remains available; `/openapi/v1.json` and unknown routes return
404 `application/problem+json`. Stop only the process started for the smoke check. Do not
interpret liveness as database readiness. Host integration tests cover the same behavior
without requiring a local certificate.

## Excluded from Sprint 1

PostgreSQL/EF migrations/runtime-role privileges and Docker (BE-007), atomic posting/locks and
balance queries (BE-008), authentication/ownership authorization (BE-009), published regional
rules/profiles, live settlement/idempotency, escrow lifecycle, refunds, clearing/exports,
performance targets and mobile acceptance belong to later tasks. Record them as out of scope,
not as passed or implemented. Journal construction in BE-006 is not a database commit.

## Release decision

1. Freeze the candidate SHA and complete this checklist; no unresolved blocker/high-severity
   financial defect or unexplained failed/skipped in-scope test may remain.
2. QA records an explicit independent PASS with their name, candidate SHA, evidence and any
   accepted lower-severity issues. An automated run alone does not close this gate.
3. Open a release PR into `main` for the tested BE-001..BE-006 candidate. Require a non-author
   approval, resolved review discussions and both Linux/Windows CI checks on the release PR.
   If `development` advanced into BE-007, use a release branch from the accepted candidate
   rather than releasing untested work.
4. Merge and tag the accepted version (proposed `v0.1.0`, subject to existing tags). Verify
   post-merge CI. Record the merge commit, tag and release contents in the QA task.
5. Resume integration of BE-007 through its reviewed dependency chain. If work must continue
   during acceptance, preserve the frozen release branch and carry fixes to development.

Only mark the Sprint 1 QA gate complete after its declared exit criteria are met. Keep the
automated preflight result separate from independent QA sign-off and release completion.
