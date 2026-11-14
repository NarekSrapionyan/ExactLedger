# Local Docker environment

Task: BE-007.4 — Local Docker Compose Environment.

Use Docker Desktop with Linux containers (or Docker Engine on Linux) and Docker
Compose v2 or newer. Run commands from the repository root. The full Docker mode
does not require a locally installed .NET SDK.

## First setup

Copy `.env.example` to `.env` and choose three separate local passwords. `.env` is
ignored by Git and excluded from image builds. Use generated letters/digits/`_`/`-`
passwords to avoid Compose and connection-string escaping. Change `POSTGRES_PORT`
or `API_PORT` if the default port is already in use.

```powershell
Copy-Item .env.example .env
```

On Linux/macOS, use `cp .env.example .env` instead.

## Full environment: PostgreSQL, migrations and API

```sh
docker compose --profile app up --build -d
docker compose --profile app ps -a
docker compose --profile app logs -f api
```

Compose waits for PostgreSQL's TCP healthcheck, runs the migration service to a
successful exit, then starts the API. `migrate` exiting with code 0 is expected.
An initialization or migration failure prevents the dependent API from starting;
inspect `docker compose logs postgres migrate` rather than bypassing the failure.

With the default ports:

- Liveness: <http://localhost:8080/health/live>
- OpenAPI: <http://localhost:8080/openapi/v1.json>
- Host database address: `localhost:5432`; inside Compose: `postgres:5432`.

These are the existing API foundation endpoints. Compose does not add settlement
or posting endpoints. `/health/live` checks the process, not database readiness.
The local API uses the Development environment and HTTP; published ports bind
only to `127.0.0.1`. This configuration is for local development and QA.

Rebuild after changing application code or migrations:

```sh
docker compose --profile app up --build -d
```

Both API and migration images run as a non-root user and contain runtime files,
not the .NET SDK. The SDK build image matches `global.json`; the ASP.NET runtime
image follows the .NET 10 servicing tag. Use `docker compose build --pull` when
refreshing base images.

## Database in Docker, API on the host

This mode supports IDE debugging and `dotnet watch`. The host needs the SDK from
`global.json`. Stop a previously running containerized API first to avoid
confusing the two instances:

```sh
docker compose --profile app stop api
docker compose up -d postgres
docker compose run --build --rm migrate
```

Set the runtime connection with your `.env` APP_PASSWORD and POSTGRES_PORT values:

```sh
dotnet user-secrets --project src/el.Api set "ConnectionStrings:ExactLedger" "Host=localhost;Port=5432;Database=exactledger;Username=exactledger_app;Password=<APP_PASSWORD from .env>"
dotnet run --project src/el.Api
```

The existing launch profile serves <https://localhost:7243/health/live> and
<https://localhost:7243/openapi/v1.json>. Trust the local certificate with
`dotnet dev-certs https --trust` if necessary. Host API configuration does not
automatically read Compose's `.env` file. Never give the API the bootstrap or
migration connection string.

## Credentials and database permissions

| Login / role | Use |
| --- | --- |
| `postgres` | First-run local database/role provisioning using POSTGRES_PASSWORD. |
| `exactledger_migrations` | Applies EF migrations using MIGRATION_PASSWORD; owns the `ledger` schema. Not a superuser and cannot create roles or databases. |
| `exactledger_runtime` | Non-login group with the privileges granted by migrations. |
| `exactledger_app` | API login using APP_PASSWORD; inherits only runtime privileges. |

The PostgreSQL init script creates roles once, on an empty volume. The migration
service receives only the migration connection; the API receives only the runtime
connection. See [database safety and migrations](database.md) for the journal guards.

The API can insert new valid entries through controlled finalization. It cannot
change the schema, rewrite/delete posted history, or append lines after finalization.

## Stop, persistence and reset

```sh
docker compose --profile app down
```

This removes containers and the network while retaining the named `postgres-data`
volume. The next startup keeps existing data and applies any pending migrations.
Changing passwords in `.env` does not change roles in an existing database: update
the database credentials explicitly, or reset disposable local data as follows.

**This reset deletes this Compose project's local database and all its data:**

```sh
docker compose --profile app down --volumes
docker compose --profile app up --build -d
```

## Automated smoke check

`scripts/Test-LocalCompose.ps1` builds and starts a separate temporary Compose
project with generated credentials and free host ports. It verifies migrations,
API liveness/OpenAPI, the non-root API user, runtime-role restrictions, and that
an inserted row survives container recreation. It removes only its own containers,
images, network, volume and temporary environment file afterwards.

Linux CI runs it using PowerShell 7. On Windows, it can also be run with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-LocalCompose.ps1
```

The existing integration tests continue to use isolated Testcontainers databases;
they neither require this Compose stack nor connect to its persistent volume.

Compose dependency semantics: [Docker startup order](https://docs.docker.com/compose/how-tos/startup-order/).
Migration executable: [EF Core migration bundles](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying#bundles).
