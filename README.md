# ExactLedger

ExactLedger is a billing and settlement engine for marketplaces, featuring precise multi-party payment splits, double-entry accounting, configurable fees, escrow, refunds, and automated reconciliation.

The backend owns all monetary calculations and accounting rules. A native Swift/SwiftUI mobile client consumes the same API for finance operations, configuration, and payee views.

## Core capabilities

- Currency-aware money and deterministic payment allocation with no rounding variance.
- An immutable double-entry ledger with atomic posting and balance verification.
- Versioned regional deduction rules, corporate fee profiles, and escrow policies.
- Daily clearing, financial records, CSV payout instructions, and reconciliation.
- Role-based access and reproducible calculation traces.

The repository currently contains the backend foundation and API host. Settlement, persistence, authentication, clearing, and the mobile application are developed in subsequent tasks. The current health endpoint checks process liveness; it does not check database readiness.

## Architecture

ExactLedger uses Clean Architecture within a modular monolith.

| Project | Responsibility | Project references |
| --- | --- | --- |
| `src/el.Domain` | Money, entities, accounting rules, and invariants | None |
| `src/el.Application` | Use cases and interfaces for external dependencies | Domain |
| `src/el.Infrastructure` | Persistence and implementations of application interfaces | Application, Domain |
| `src/el.Api` | HTTP endpoints, configuration, dependency injection, and scheduled-job hosting | Application, Infrastructure |
| `tests/el.Domain.Tests` | Domain unit and property tests | Domain |
| `tests/el.IntegrationTests` | API and infrastructure integration tests | API, Infrastructure |
| `src/el.Mobile` | Native Swift/SwiftUI client workspace | API contract only |

Business features use folders inside these projects. The API is the composition root: concrete infrastructure adapters are registered there when introduced. Domain code does not reference EF Core, HTTP, or serializers. See [the architecture decision](docs/architecture/0001-solution-foundation.md).

## Prerequisites

- .NET SDK **10.0.203**, pinned in `global.json`.
- Git.
- PostgreSQL when working on persistence. The current host starts without a database.
- macOS and Xcode for future Swift client development; Xcode is not needed to build the backend.

The backend targets .NET 10 and C# 14. NuGet versions are defined in `Directory.Packages.props`; committed `packages.lock.json` files pin resolved dependencies.

## Build and run

From the repository root:

```sh
dotnet restore ExactLedger.sln --locked-mode
dotnet build ExactLedger.sln --configuration Release --no-restore
dotnet test ExactLedger.sln --configuration Release --no-build --no-restore
```

Trust the local development certificate once:

```sh
dotnet dev-certs https --trust
```

Start the API:

```sh
dotnet run --project src/el.Api --launch-profile https
```

| URL | Purpose |
| --- | --- |
| `https://localhost:7243/health/live` | Process liveness; returns `200 Healthy` |
| `https://localhost:7243/openapi/v1.json` | OpenAPI document in Development |
| `http://localhost:5243` | Redirects to HTTPS |

`src/el.Api/el.Api.http` contains sample requests. The OpenAPI JSON document is available without an embedded Swagger UI. OpenAPI is not exposed in Production.

## Configuration

Shared, non-secret defaults belong in `src/el.Api/appsettings.json`; Development logging belongs in `appsettings.Development.json`. `Properties/launchSettings.json` contains local launch URLs and the Development environment.

Optional `src/el.Api/appsettings.Local.json` is ignored by Git and loaded after shared/environment settings. In Development, user secrets override that local file; environment variables and command-line arguments have the highest precedence. Prefer user secrets for local credentials:

```sh
dotnet user-secrets set "ConnectionStrings:ExactLedger" "Host=localhost;Port=5432;Database=exactledger;Username=exactledger_app;Password=<local-password>" --project src/el.Api
```

Alternatively, provide the `ConnectionStrings__ExactLedger` environment variable. The committed connection string is intentionally empty. EF Core, database provisioning, and migrations are introduced with the persistence tasks; no database connection is opened by this foundation.

For a deployed host, set `AllowedHosts` to its actual hostnames and configure HTTPS through the hosting environment. Launch profiles are local development settings.

## Tests

`el.IntegrationTests` verifies API startup, liveness, Development OpenAPI, and Production ProblemDetails behaviour using an in-memory ASP.NET Core host. `el.Domain.Tests` is prepared for the financial tests added with the first domain features; it intentionally contains no placeholder passing tests.

To run only host tests:

```sh
dotnet test tests/el.IntegrationTests --configuration Release
```

When intentionally changing package versions, update `Directory.Packages.props`, run `dotnet restore ExactLedger.sln`, and commit the updated lock files.

## Contributing

Use `development` as the integration branch and `main` for verified releases. Create focused working branches from the latest `development`, and open feature, fix, test, and documentation pull requests back into `development`. Promote reviewed and validated changes from `development` to `main` through a separate release pull request.

```sh
git switch development
git pull --ff-only origin development
git switch -c feature/backend-solution-setup
```

Do not commit feature work directly to either shared branch. Pull request titles follow `type(scope): summary (TASK-ID)`, for example `feat(domain): add Money value object (BE-002.2)`.

Link the ClickUp task, explain the change and its validation, and use the [pull request template](.github/pull_request_template.md). Formatting follows `.editorconfig`; build warnings are treated as errors. Keep credentials, generated artifacts, and personal IDE files out of Git.
