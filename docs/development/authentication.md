# Authentication, ownership and business audit

Task: BE-009. Baseline: the 17-page ExactLedger PRP, Section 2, FR-API-01 and
NFR-SEC-01/02 (provided as `ExactLedger_PRP.pdf`).

## Startup and local identities

Follow [local Docker setup](local-docker.md). Set separate database passwords,
`SEED_PASSWORD` (16–256 characters) and `JWT_SIGNING_KEY` in your ignored `.env`.
Generate the signing key from at least 32 random bytes, encoded as base64:

```powershell
$bytes = New-Object byte[] 32
$generator = [Security.Cryptography.RandomNumberGenerator]::Create()
$generator.GetBytes($bytes)
[Convert]::ToBase64String($bytes)
$generator.Dispose()
```

Linux/macOS alternative: `openssl rand -base64 32`. Never commit the generated key.

```sh
docker compose --profile app up --build -d
```

Startup order is PostgreSQL → migrations → identity seed → API. The seed and
migration services exit successfully; they do not run as HTTP servers. Only they
receive the migration connection. The API receives the runtime connection and
signing key, not the seed password or migration credentials.

The trusted seed command stores salted ASP.NET Core Identity password hashes,
roles and client/party bindings in PostgreSQL. The following synthetic usernames
use your chosen local SEED_PASSWORD:

| Username | Role(s) | Binding |
| --- | --- | --- |
| integrator | Integrator | demo-client-1 |
| integrator-other | Integrator | demo-client-2 |
| finance | FinanceOperator | Marketplace-wide |
| admin | ConfigurationAdministrator | Marketplace-wide configuration |
| auditor | Auditor | Marketplace-wide permitted reads |
| scheduler | Scheduler | Scheduled release / clearing / batch export |
| payee-seller-1 | Payee | Synthetic Seller One |
| payee-seller-2 | Payee | Synthetic Seller Two |
| demo-admin | ConfigurationAdministrator, FinanceOperator | Union of these two roles |

Re-seeding unchanged identities is a no-op and does not create duplicate audit
events. Explicitly changing SEED_PASSWORD updates the seeded password hashes;
disabled accounts remain disabled. Non-seed accounts are not deleted. Account
provisioning and role assignment are trusted deployment operations, never public
registration or mobile actions.

For API debugging on the host, start the seed dependency chain with
`docker compose run --build --rm seed`, then configure user secrets:

```sh
dotnet user-secrets --project src/el.Api set "ConnectionStrings:ExactLedger" "Host=localhost;Port=5432;Database=exactledger;Username=exactledger_app;Password=<APP_PASSWORD>"
dotnet user-secrets --project src/el.Api set "Authentication:Jwt:Issuer" "ExactLedger"
dotnet user-secrets --project src/el.Api set "Authentication:Jwt:Audience" "ExactLedger.Clients"
dotnet user-secrets --project src/el.Api set "Authentication:Jwt:SigningKey" "<JWT_SIGNING_KEY>"
dotnet run --project src/el.Api
```

Use your configured database port. The host does not automatically read `.env`.
Outside Compose, apply migrations first, then run
`dotnet run --project src/el.Api --no-launch-profile -- --seed-identities` with
`ConnectionStrings__ExactLedgerMigrations` and `Authentication__SeedPassword` set
in the command's environment. Remove those variables before running the API.

## HTTP contract

OpenAPI is at `/openapi/v1.json` in Development. Local Compose uses HTTP bound to
loopback; the host launch profile uses `https://localhost:7243`. Outside Development,
API requests require HTTPS: plaintext requests are rejected with 400 `HTTPS_REQUIRED`,
without redirecting or reading login credentials. Redirection is a Development
convenience only. Clients must use HTTPS from the first request; rejection cannot
undo transmission by a misconfigured HTTP client.

When deploying behind a TLS-terminating proxy, configure `UseForwardedHeaders`
before the HTTPS guard with explicit `KnownProxies` / `KnownNetworks` for your
deployment, and forward the original scheme. Untrusted forwarded headers must
never enable HTTPS or change the client IP used by rate limiting. The application
does not trust proxy headers by default. No browser/CORS flow is added for the Swift-only MVP.

| Endpoint | Access |
| --- | --- |
| POST /v1/auth/login | Anonymous credentials exchange; 20 attempts/minute/IP, 4 KiB body limit |
| GET /v1/auth/me | Any valid authenticated identity; returns its own roles and bindings |
| GET /v1/ledger/accounts/{accountId}/balance | Finance/Auditor; Payee only for an account actually owned by its signed party ID |
| GET /v1/ledger/trial-balance?currency=USD | Finance/Auditor; current all-time totals, optionally one supported currency |
| GET /v1/audit-events?offset=0&limit=50 | Finance/Admin/Auditor; limit 1–100 |
| GET /health/live | Anonymous process liveness, independent of database availability |

Login body is `{ "username": "finance", "password": "<your local password>" }`.
The response contains `accessToken`, `tokenType: "Bearer"`, `expiresIn: 1800` and
`expiresAtUtc`. It has `Cache-Control: no-store`. Send the token only in the
`Authorization: Bearer ...` header. Roles, clientId or partyId supplied in a login
request cannot change the stored identity.

Tokens use HS256 with signature, issuer, audience, expiry and not-before validation,
no alternate algorithms or grace period. Required subject/role/ownership bindings
are validated. Claims include `sub`, `name`, one or more `role` values, `jti`, `iat`,
and the stored `client_id` / `party_id` where applicable. Signing keys have no
fallback defaults. Configuration is validated before the HTTP host starts.

There is no refresh token, self-registration or public role-assignment endpoint.
Existing tokens retain their signed roles until their 30-minute expiry; disabling
an account blocks new logins, not previously issued tokens. Key rotation invalidates
old signatures. Do not treat this educational token issuer as an external OAuth provider.

Balances retain exact decimal-string/currency objects even when an aggregate
exceeds Int64 or Money's per-value range. Debit/credit totals and `netDebit` have
explicit sign conventions. `normalSide` is `Debit` or `Credit`, derived from the
stored account type. `balance` is computed on that side by the backend: a payable
with credits of 184.45 and no debits has `balance.amount: "184.45"` and
`netDebit.amount: "-184.45"`. A contra balance remains negative. This does not implement reservations or payout availability.

## Authorization and privacy

The exact operation matrix is in [ADR 0010](../architecture/0010-authentication-and-audit.md).
Mapped endpoints without an explicit policy are denied by default. Combined roles
grant a union of explicit permissions; there is no universal Admin bypass.

`ResourceAccess` accepts client/party ownership from authoritative stored data.
Never pass a caller's requested owner as the resource owner. Role permission alone
does not grant access to another client or payee. Cross-owner or missing resources
return the same 404; a denied operation role returns 403.

`PayeeSettlementProjection` is the whitelist for settlement readers:
only rows tagged to the authenticated party are included; global/other-party trace,
party identifiers, arbitrary trace payload and payout references are omitted. A
broader authorized Finance/Admin/Auditor settlement view is distinct from this slice.
For artifact downloads, use the policy for that artifact's operation and the stored
owner where applicable; never expose static files or a generic download bypass.

The implemented workflows are documented in [settlements](settlements.md),
[refunds](refunds.md), [escrow](escrow.md), [clearing](clearing.md),
[financial reports and artifact downloads](reports.md), and
[configuration](knowledge-configuration.md). Report routes require authentication,
then enforce the report kind's operation grant and stored party scope; downloads
repeat those checks against the immutable artifact. Cross-owner isolation is
verified through HTTP tests.

## Errors, logs and audit

Errors use ProblemDetails with top-level `code`, `message` and `correlationId`;
validation responses add `errors` keyed by field. Statuses are 400 malformed input,
401 missing/invalid credentials, 403 denied role, 404 unavailable resource and 409
business-state conflict. Rate limiting returns 429. Unexpected errors return a safe
500. Ledger lock timeouts return retryable 503 `LEDGER_BUSY` with
`Retry-After: 1`; retry after at least one second. Account-state conflicts remain
409 `ACCOUNT_NOT_POSTABLE`.

Generic HTTP and business error codes use `UPPER_SNAKE_CASE`, including
`INVALID_REQUEST`, `INVALID_CREDENTIALS`, `UNAUTHENTICATED`, `FORBIDDEN`,
`NOT_FOUND`, `CONFLICT`, `RATE_LIMITED` and `LEDGER_UNBALANCED`. Existing
`money.*` codes identify monetary input validation failures and retain their
dotted format. Clients should branch on `code`, not the human-readable message.

The server supplies `X-Correlation-ID`, ignoring untrusted correlation headers.
Application failure logs contain correlation/status/type metadata, not exception
messages. Passwords, bearer tokens, raw query strings and full request bodies are
not logged; EF sensitive-data logging remains disabled. Login failures
do not reveal whether a username exists or is disabled.

`IAuditTrail.RecordAsync` requires the active business transaction and accepts only
operation/target metadata. Actor ID and correlation ID come from the scoped trusted
context; time is UTC. `JournalPostingService` records `journal.posted` before the
same transaction commits. Trusted identity seeding records only changed identities.
Failures roll back both business state and audit; failures themselves are logged.
No arbitrary payload, password or token is stored in `audit_events`.

Runtime privileges are SELECT-only for identities and SELECT/INSERT for audit.
Audit UPDATE/DELETE/TRUNCATE are also blocked by protective triggers. Jobs set an
explicit service actor in their scope; non-HTTP internal calls default to `system`.
Later state-changing workflows must call the audit port in their existing unit of work.

References: [ASP.NET Core JWT authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-10.0)
and [Identity password hashing](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity-configuration?view=aspnetcore-10.0#password-hasher-options).
