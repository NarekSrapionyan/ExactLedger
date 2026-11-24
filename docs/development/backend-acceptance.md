# Backend acceptance contracts

This follow-up closes concrete FR-MOB-01 / FR-REF-01 gaps after BE-001–BE-014.

## Recent settlements

`GET /v1/settlements?offset=0&limit=50&currency=USD` supplies dashboard summaries.
Currency is optional; limit is 1–100. Response: `items`, `offset`, `limit`, `hasMore`.
Order is posting time descending, then immutable ID descending. Follow the next
offset when `hasMore` is true; use each ID with existing detail/trace endpoints.

Finance/Admin/Auditor see global summaries. Integrators see their client only.
Payees see participating settlements and only their own allocations; gross,
client/trade IDs and other parties are omitted. Combined client/Payee roles use
the same union of grants as detail. Caller-supplied client/party query parameters
never change the server-derived scope.

## Participant setup

| Route | Access / purpose |
| --- | --- |
| `POST /v1/parties` | Admin: register participant and accounts |
| `GET /v1/parties?offset=0&limit=50` | Admin/Auditor: participants and payout eligibility |
| `GET /v1/parties/{id}` | Admin/Auditor: one participant |
| `GET /v1/accounts?partyId={id}&currency=USD&offset=0&limit=50` | Admin/Auditor: ledger mapping lookup |

```json
{"id":"<new-client-generated-guid>","name":"Synthetic New Seller","payoutEligible":true}
```

Generate the ID once and retain it for retries. Registration returns 201, an
identical ID/name/eligibility returns 200 without duplicate accounts/audit, and
changed content under that ID returns 409 `PARTY_CONFLICT`. Name is trimmed,
non-empty, at most 200 characters, with no control characters.

One transaction under the monetary lock creates the participant, one payable and
one escrow account per currency (six accounts), optional payout eligibility and
the `party.registered` audit. Server-generated account IDs/codes are obtained via
the account lookup; callers cannot set arbitrary balances or account types.
`payoutEligible` defaults to true for payees; false is for other beneficiaries
whose liabilities are not paid by the simulated payee clearing workflow. Existing
eligibility cannot be edited through registration. Runtime gets SELECT/INSERT on
the allowlist; history guards reject UPDATE/DELETE/TRUNCATE.

Participant registration does not create a login or grant roles. Identity/role
provisioning remains trusted per PRP Section 2.2. This closes Admin party setup and
reference lookup, not identity administration. Lookup pagination uses limit 1–100.

## Refund reason and legacy history

New refund commands require `reason` alongside the original gross amount. The
trimmed reason must be 1–500 characters without control characters; otherwise
400 `INVALID_REFUND_REASON`. Normalized reason participates in the command hash;
changing it with the same key returns 409 `IDEMPOTENCY_CONFLICT`.

Reason is stored in the immutable refund row and returned on creation, retry and
authorized read. Actor/time/correlation remain in transactional audit. Reasons
are not copied into logs. Existing refunds keep NULL: no historical reason is
invented. Their original command hashes can still be retried without a reason;
missing reason is rejected for a new command.

Migration `20261124180700_BackendAcceptanceContracts` adds the nullable historical
column and restricted allowlist privileges/guards. EF target model and snapshot
are updated together. Tests downgrade/reapply around a genuine legacy reversal
and verify its original retry result.

The representation boundary is recorded in [ADR 0015](../architecture/0015-backend-acceptance-boundary.md).
The system continues to use synthetic rules and explicit simulated confirmation.
Enable the daily timer with `Clearing__Schedule__Enabled=true`; default is manual
demonstration. Swift UI/device acceptance remains separate.
