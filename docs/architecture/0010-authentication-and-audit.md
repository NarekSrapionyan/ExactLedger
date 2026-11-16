# Authentication, operation permissions and transactional audit

Task: BE-009. Source: ExactLedger PRP (17 pages), Section 2.2, FR-API-01,
NFR-SEC-01/02, architecture Section 5.3.

Use ASP.NET Core bearer authentication and named operation policies. Infrastructure
implements token issuance, credential verification, identity persistence and audit;
Application owns the boundary contracts and ownership decisions. Do not add a full
account-registration framework, external identity provider, refresh-token system,
new project or universal Administrator bypass for this educational MVP.

## Exact role grants

| Operation | Integrator | Finance | Admin | Payee | Auditor | Scheduler |
| --- | --- | --- | --- | --- | --- | --- |
| Submit trade / eligible refund | Yes | — | — | — | — | — |
| Read settlement / trace | Client | Yes | Yes | Own | Yes | — |
| Read balances / payouts | — | Yes | — | Own | Yes | — |
| Read journal / trial balance | — | Yes | — | — | Yes | — |
| Manage parties / draft configuration | — | — | Yes | — | — | — |
| Publish valid version | — | — | Yes | — | — | — |
| Release due escrow | — | Yes | — | — | — | Yes |
| Run clearing / export batch | — | Yes | — | — | — | Yes |
| Confirm simulated bank result | — | Yes | — | — | — | — |
| Read operational audit | — | Yes | Yes | — | Yes | — |

Client/Own grants add authoritative resource checks. Payee output is a whitelist
of own allocations/trace with no other party identifiers or payout references.
Combined roles grant a union; Scheduler alone never confirms bank execution,
Admin alone never reads ledger balances, and no role edits posted journal history.
Downloads must enforce the corresponding operation and ownership policy.

## Seed and transaction boundaries

Persist synthetic identity IDs, salted password hashes, roles and client/party
bindings in PostgreSQL. Only a trusted seed/provisioning command has write rights.
The API's runtime login cannot create identities or assign roles. A separate local
Compose seed step uses the existing API image, exits before API startup and shares
neither provisioning credentials nor the seed password with the API service.

Business audit is an immutable actor/operation/target/UTC/correlation record in
the same DbContext transaction as the change. Posting automatically appends audit;
later workflows append their other state transitions through the same port.
Operational failure logs are separate and contain safe metadata only.

## Verification

HTTP tests cover all 60 role/operation combinations independently of the policy
implementation, combined roles, default denial, client isolation, Payee filtering
and protected downloads. Real PostgreSQL HTTP tests cover login, stored ownership,
hashing, disabled accounts, rate limiting and real ledger/audit routes. Transaction
tests verify audit commit/rollback and runtime/owner immutability. Token tests reject
expired, unsigned, wrong-key, wrong-issuer/audience, alternate-algorithm and tampered
tokens. Compose verifies migration, seeding, authenticated reads and restart persistence.

Business endpoints whose storage does not yet exist are not fabricated for this task;
the HTTP contract harness proves the shared gates for their later implementations.
See [startup and API contract](../development/authentication.md).
