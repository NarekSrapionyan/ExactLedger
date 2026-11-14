# Database

Task: BE-007.1 — DbContext & PostgreSQL Setup

## Projects

- `el.Application/Abstractions/IUnitOfWork` is the transaction port used by use cases. It runs the work, saves changes and commits in one transaction; any exception rolls everything back. Units of work do not nest. Application has no EF Core dependency.
- `el.Infrastructure/Persistence/ExactLedgerDbContext` maps all tables into the `ledger` schema, with the EF migrations history in `ledger.__ef_migrations_history`. Mappings are `IEntityTypeConfiguration` classes in `Persistence/Configurations` (BE-007.2/BE-007.3).
- `AddInfrastructure` registers the DbContext and `IUnitOfWork`. No retrying execution strategy is configured, so a failed unit of work is never replayed silently.

## Schema (BE-007.2)

All tables are in the `ledger` schema. Constraints repeat the domain rules, so invalid rows are rejected even when they do not come through the domain model. Enums are stored as their integer values (all start at 1).

| Table | Key | Constraints |
|---|---|---|
| `currencies` | `code` | `code` is three uppercase letters. The supported currency codes; definitions and accounts reference them, so an unknown currency is rejected. |
| `currency_definitions` | `(code, version)` | FK `code` → `currencies`; exponent 0–4; version ≥ 1. |
| `parties` | `id` (uuid) | Name not blank (≤ 200 characters); status Active/Suspended/Closed. |
| `journal_entries` | `id` (uuid) | Unique `reference` (`^[A-Z0-9.:_-]{1,100}$`); description not blank; FK `currency` → `currencies`; optional `reversal_of` → `(id, currency)` of the reversed entry, unique (an entry is reversed at most once) and never the entry itself; `finalized` flag. |
| `journal_lines` | `(entry_id, line_number)` | `amount_minor` BIGINT between 1 and 99,999,999,999; side Debit/Credit; `(entry_id, currency)` → `journal_entries(id, currency)` and `(account_id, currency)` → `accounts(id, currency)`, so a line's currency equals its entry's and its account's. Accounts with postings cannot be deleted. |
| `accounts` | `id` (uuid) | Unique `code` matching `^[A-Z0-9._-]{1,64}$`; defined type and status; payable/escrow accounts have a party and system accounts do not; one account per party, type and currency; FK `party_id` → `parties` and `currency` → `currencies`, both `RESTRICT` on delete. |

Sums of BIGINT minor units are read with `QueryExactIntegerAsync`, which returns PostgreSQL's NUMERIC result as an exact `BigInteger` instead of narrowing it to a 64-bit integer.

### Seed data

Only synthetic data is seeded: five parties whose names all start with "Synthetic" (two sellers, an affiliate, a card processor and a tax authority), the reference currency definitions (USD/2, JPY/0, KWD/3), and these account families for each currency:

| Family | Type | Party |
|---|---|---|
| `CLEARING.BANK.<CUR>` | Clearing | none |
| `REVENUE.COMMISSION.<CUR>` | Revenue | none |
| `PAYABLE.TAX.<CUR>` | Payable | Synthetic Tax Authority |
| `PAYABLE.PROCESSOR.<CUR>` | Payable | Synthetic Card Processor |
| `PAYABLE.<PARTY>.<CUR>`, `ESCROW.<PARTY>.<CUR>` | Payable, Escrow | each seller and the affiliate |

Seed IDs are derived from fixed names, so they are identical on every machine and in every migration.

## Journal safety (BE-007.3)

Journal rows are mapped as persistence records (`JournalEntryRecord`, `JournalLineRecord`); the domain `JournalEntry` is written through `IJournalEntryStore` inside a unit of work. The migration `LedgerSafetyGuards` adds checks that hold even for SQL that bypasses the domain:

- **Controlled finalization.** An entry is inserted unfinalized, its lines are inserted, and `ledger.finalize_journal_entry(id)` marks it finalized. It is the only way to finalize: the runtime role cannot update `journal_entries`, and finalizing twice fails (`EL105`).
- **No lines after finalization** (`EL101`).
- **Deferred completed-entry validation.** At commit, every entry touched by the transaction must be finalized (`EL103`) and have at least two lines with total debits exactly equal to total credits, summed as NUMERIC (`EL102`). The whole transaction is rolled back otherwise.
- **Immutable history.** Triggers reject `UPDATE`, `DELETE` and `TRUNCATE` of journal entries and lines for every role, including the schema owner (`EL104`); the only allowed change is the one-time finalization.

The SQLSTATE codes are constants in `LedgerDatabase`.

## Roles and privileges

| Role | Purpose | Privileges |
|---|---|---|
| Migration role (schema owner) | Applies migrations; owns tables and functions | Everything in `ledger`; `SECURITY DEFINER` functions run as this role |
| `exactledger_runtime` (NOLOGIN) | Group role for the API's login user | `USAGE` on `ledger`; `SELECT` on all tables; `INSERT` into parties, accounts, journal entries and lines; `UPDATE (status)` on parties and accounts; `EXECUTE` on `finalize_journal_entry`. No `UPDATE`/`DELETE`/`TRUNCATE` of history, no DDL, no changes to currencies. |

The migration creates `exactledger_runtime` if it does not exist; it needs a migration role that can create roles (or a DBA creates it beforehand). Create the API's login user as a member, for example:

```sql
CREATE ROLE exactledger_app LOGIN PASSWORD '<secret>' IN ROLE exactledger_runtime;
```

Tables added by later migrations must grant their runtime privileges explicitly.

## Migrations

Migrations live in `el.Infrastructure/Persistence/Migrations`. `dotnet-ef` is pinned in `.config/dotnet-tools.json`, and `el.Api` is the startup project (it references `Microsoft.EntityFrameworkCore.Design` for design time only).

```sh
dotnet tool restore
dotnet ef migrations add <Name> --project src/el.Infrastructure --startup-project src/el.Api --output-dir Persistence/Migrations
```

Apply them with the migration role, never the runtime role:

```sh
# PowerShell: $env:ConnectionStrings__ExactLedgerMigrations = "Host=...;Database=exactledger;Username=<migration role>;Password=<secret>"
dotnet ef database update --project src/el.Infrastructure --startup-project src/el.Api
```

`DesignTimeDbContextFactory` reads `ConnectionStrings__ExactLedgerMigrations`; adding a migration does not connect to a database. The integration tests apply all migrations to an empty database and check that the model has no pending changes.

## Connection strings

| Name | Role | Used by |
|---|---|---|
| `ConnectionStrings:ExactLedger` | Least-privileged runtime role | The API |
| `ConnectionStrings:ExactLedgerMigrations` | Schema-owning migration role | Migrations only (BE-007.3) |

The committed `appsettings.json` contains an empty slot only. The connection string is read when a DbContext is first created, so the API and its liveness check start without a database; the first database use fails with a clear message if it is missing. Never commit credentials. Set them locally with user secrets:

```sh
dotnet user-secrets --project src/el.Api set "ConnectionStrings:ExactLedger" "Host=localhost;Port=5432;Database=exactledger;Username=<runtime user>;Password=<password>"
```

or with the `ConnectionStrings__ExactLedger` environment variable. Use a member of `exactledger_runtime` for the API and the migration role only for `dotnet ef database update`.

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
