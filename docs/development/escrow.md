# Escrow release

Task BE-012.1; FR-ESC-01. Holds are created by settlements (BE-011, see [settlements](settlements.md));
this task adds the full release of due holds.

| Endpoint | Access / result |
| --- | --- |
| POST /v1/escrow/holds/{id}/release | Finance Operator or Scheduler. Releases one due hold in full; repeating returns the original result (200). |
| POST /v1/escrow/releases | Finance Operator or Scheduler. Scheduler entry point: releases every hold due now, oldest first (up to 500 per run). |
| GET /v1/escrow/holds/{id} | Finance Operator/Auditor, or owning Payee. One hold and its effective state; foreign holds return 404. |
| GET /v1/escrow/holds?settlementId={id} | Finance Operator/Auditor; Payee sees only their own holds, filtered by the signed party binding. |

## Rules

- A hold is released only when its state is Held and the server's UTC time has reached `dueAtUtc`
  (the trade's `occurredAt` plus the profile's hold days). Earlier calls return 409 `HOLD_NOT_DUE`;
  a hold refunded with its settlement ([refunds](refunds.md)) returns 409 `HOLD_NOT_RELEASABLE`.
- The beneficiary party and its escrow and payable accounts must be active. Otherwise the manual
  call returns 409 `PARTY_NOT_ACTIVE` or `ACCOUNT_NOT_POSTABLE`, and the scheduler run skips the
  hold, reports it under `skipped` and retries it on the next run.
- Release is always full: Dr Escrow / Cr Payable for the original held amount, with the unique
  journal reference `ESCROW-RELEASE:<hold id>`.
- The monetary lock, the journal entry, the release record and the audit event (`escrow.released`,
  plus `escrow.release-run` for scheduler runs) commit together. Concurrent or repeated releases of a
  hold produce one posting and return the same result, so no Idempotency-Key is needed.
- A settlement whose escrow amount is zero creates no hold, so there is nothing to release.

## Persistence

Hold rows stay immutable. Each state change is one insert-only row in `ledger.escrow_hold_transitions`,
keyed by hold ID, so a hold can leave Held only once. A deferred database check verifies at commit that
a release has exactly two lines (Dr the hold's escrow account, Cr its payable account) for exactly the
held amount, is not earlier than the due time, uses a finalized, non-reversal entry and its settlement
was not refunded (`EL402`). `Refunded` transitions are written by [refunds](refunds.md); unknown states are
rejected (`EL401`). The runtime role
can only insert and read transitions; updates and deletes are rejected for every role.

`ledger.escrow_hold_status` shows each hold's effective state (`Held`, `Released` or `Refunded`)
and released amount. At any time, each escrow account's balance equals the sum of its holds that are
still Held; the integration tests check this after every release.
