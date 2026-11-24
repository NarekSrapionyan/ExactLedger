# Full refunds

Task BE-012.2; FR-REF-01. A refund cancels a whole settlement before clearing. Partial refunds,
chargebacks and post-batch recovery are out of scope.

| Endpoint | Access / result |
| --- | --- |
| POST /v1/settlements/{id}/refund | Owning Integrator. Body `{"amount":{"amount":"250.00","currency":"USD"}}`, required Idempotency-Key. Returns 201; an identical retry returns the original 201 result. |
| GET /v1/settlements/{id}/refund | Finance, Admin, Auditor and the owning Integrator. Other callers and missing refunds get 404. |

## Eligibility

Checked under the shared monetary lock, in this order:

| Condition | Result |
| --- | --- |
| Same Idempotency-Key with a different amount | 409 `IDEMPOTENCY_CONFLICT` |
| Settlement missing or owned by another client | 404 |
| Settlement already refunded (with another key) | 409 `REFUND_ALREADY_EXISTS` |
| Settlement assigned to a clearing batch | 409 `SETTLEMENT_IN_BATCH` |
| Amount differs from the original gross (including currency) | 400 `REFUND_AMOUNT_MISMATCH` |
| An escrow hold of the settlement was released | 409 `HOLD_ALREADY_RELEASED` |

Batch membership comes from `ISettlementBatchMembership`. A settlement is assigned once its journal entry is in ledger.clearing_batch_entries. Refunds and the daily cut both take the
shared monetary lock, so a refund and a cut can never interleave.

## Reversal

The refund reads the original stored journal lines and posts them again with opposite sides, the same
accounts and the same amounts, linked to the original entry (`reversal_of`) under the unique reference
`REFUND:<settlement id>`. It never recalculates from current rules, so every original component,
including fees and commission, is reversed exactly; original plus reversal nets every account to zero.

The original settlement, allocations, holds and journal stay unchanged. Every hold of the settlement
gets a `Refunded` transition with the refund's journal entry, so it can no longer be released. The
posting, the refund record with its idempotency key, the hold transitions and the audit event
(`settlement.refunded`) commit together.

## Database guards

`ledger.settlement_refunds` holds one row per settlement (the primary key), with a unique key per
client and Idempotency-Key. A deferred check at commit (`EL403`) requires the refund's entry to be a
finalized reversal of the settlement's entry, to mirror every original line exactly with the opposite
side, to refund the full gross amount for the owning client, and every hold of the settlement to have
a `Refunded` transition with that entry. A `Refunded` hold transition without such a refund, or a
release of a hold whose settlement was refunded, is rejected (`EL402`). Refund rows are insert-only
for the runtime role and immutable for every role.
