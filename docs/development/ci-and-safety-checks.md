# CI and safety checks

Tasks: BE-001.2 and BE-001.3.

For the database-independent BE-001..BE-006 release scope, use the
[Sprint 1 acceptance and release gate](sprint-1-qa.md) alongside this CI guide.

## Run locally

```sh
dotnet restore ExactLedger.sln --locked-mode
dotnet build ExactLedger.sln --configuration Release --no-restore -warnaserror
dotnet test ExactLedger.sln --configuration Release --no-build --no-restore --logger trx --results-directory artifacts/test-results --collect "XPlat Code Coverage" --settings coverage.runsettings
```

Run the coverage gate using PowerShell (PowerShell 7 on Linux/macOS):

```powershell
./scripts/Check-DomainCoverage.ps1
```

For a focused guard run:

```sh
dotnet test tests/el.ArchitectureTests --configuration Release
```

## Workflow and merge checks

`Backend CI` runs on pull requests into `development`/`main`, pushes to those branches, and manual dispatch. Linux and Windows use the SDK from `global.json`, locked restore, warnings as errors and the same tests. TRX and Cobertura XML are retained as artifacts for 14 days, including when tests fail. The workflow has read-only repository permissions and no deployment credentials.

Require these status checks in the repository branch rules for both shared branches:

- `Build and test (ubuntu-latest)`
- `Build and test (windows-latest)`

Require review by someone other than the author. A workflow file does not itself enable branch protection; repository branch rules must be configured separately. PostgreSQL-backed tests will join this workflow when BE-007 introduces migrations and database fixtures. Current API tests need no database service.

## Coverage

The gate merges covered source lines from all Domain coverage reports and requires at least 85% line coverage. API/Infrastructure and test fixtures do not inflate the Domain percentage. Empty Domain scaffolding is reported as **N/A**, never 100%. Missing reports fail; Domain source with no reported executable lines also fails and needs inspection. Enum-only or metadata-only additions should be reviewed if they trigger this diagnostic.

Results live under the ignored `artifacts/test-results` directory. Start each independent local collection with a fresh results directory to avoid merging stale runs; hosted runners use a clean checkout.

## Architecture and monetary policy

`el.ArchitectureTests` verifies the allowed production project-reference graph, forbids Domain package/framework dependencies and prevents shared build files from injecting project/framework/package references.

A small test-only Roslyn policy inspects production C# in `el.Domain` and `el.Application`, excluding `bin`/`obj`. It rejects:

| Rule | Forbidden usage |
| --- | --- |
| EL001 | `float`/`double`, including inferred binary floating-point expressions |
| EL002 | Decimal division, `/=` and `decimal.Divide` in exact financial calculation paths |
| EL003 | `DateTime`/`DateTimeOffset` current-time properties and `TimeProvider.System` |
| EL004 | Current/default thread culture reads |
| EL005 | HTTP, persistence and serialization APIs in Domain/Application |

Use scaled integers/BigInteger quotient and remainder, explicitly supplied time and invariant formatting. Negative fixtures exercise each rule, aliases, static imports and malformed source. Valid integer arithmetic is an allowed fixture. Invalid fixture code exists only in test strings, never in production projects.

The policy is deliberately scoped to Domain/Application. API/Infrastructure may use HTTP, serializers, databases, clocks and telemetry types. No broad exception or inline suppression switch is provided; change a rule with its positive/negative tests and review the reason if the architecture changes.
