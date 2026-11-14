# Database

Task: BE-007.1 — DbContext & PostgreSQL Setup

## Projects

- `el.Application/Abstractions/IUnitOfWork` is the transaction port used by use cases. It runs the work, saves changes and commits in one transaction; any exception rolls everything back. Units of work do not nest. Application has no EF Core dependency.
- `el.Infrastructure/Persistence/ExactLedgerDbContext` maps all tables into the `ledger` schema, with the EF migrations history in `ledger.__ef_migrations_history`. Mappings are `IEntityTypeConfiguration` classes in `Persistence/Configurations` (BE-007.2/BE-007.3).
- `AddInfrastructure` registers the DbContext and `IUnitOfWork`. No retrying execution strategy is configured, so a failed unit of work is never replayed silently.

## Connection strings

| Name | Role | Used by |
|---|---|---|
| `ConnectionStrings:ExactLedger` | Least-privileged runtime role | The API |
| `ConnectionStrings:ExactLedgerMigrations` | Schema-owning migration role | Migrations only (BE-007.3) |

The committed `appsettings.json` contains an empty slot only. The connection string is read when a DbContext is first created, so the API and its liveness check start without a database; the first database use fails with a clear message if it is missing. Never commit credentials. Set them locally with user secrets:

```sh
dotnet user-secrets --project src/el.Api set "ConnectionStrings:ExactLedger" "Host=localhost;Port=5432;Database=exactledger;Username=<runtime user>;Password=<password>"
```

or with the `ConnectionStrings__ExactLedger` environment variable. Runtime and migration roles, grants and history protections arrive with the migrations in BE-007.3.

To run a local PostgreSQL for the API, for example:

```sh
docker run --name exactledger-db -e POSTGRES_PASSWORD=<choose a local password> -p 5432:5432 -d postgres:17.6-alpine
```

## Tests

Database tests use Testcontainers and need Docker. `PostgreSqlFixture` starts one `postgres:17.6-alpine` container (`PostgreSqlFixture.Image`) per test run and fails with a clear message if it does not start within three minutes. Every test class creates its own database with `CreateDatabaseAsync`, so tests never share state. The container credentials are generated test values.

Mark database tests with `[Collection(PostgreSqlCollection.Name)]` and `[Trait("Category", PostgreSqlCollection.Category)]`. If Docker cannot pull images (for example behind a proxy), pull `PostgreSqlFixture.Image` once while connected, or run the rest without Docker:

```sh
dotnet test ExactLedger.sln -c Release --filter "Category!=PostgreSQL"
```
