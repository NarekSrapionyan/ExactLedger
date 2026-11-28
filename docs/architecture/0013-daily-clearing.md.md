# 0013 Daily clearing: cut, export and simulated confirmation

Status: Accepted (educational MVP)

## Decision
- Batches are one per UTC business date and currency. The ID is derived from the date and currency, so a retry finds the same batch.
- The cut runs under the shared monetary lock. Watermark: `ledger.journal_entries.sequence` (identity). Every posting path takes the lock before its header insert and holds it to commit, so sequence order equals commit order for API paths. Rollbacks leave gaps. Raw SQL inserts that bypass the lock are outside this guarantee.
- Included: finalized settlement, refund and escrow-release entries with `sequence <= watermark` that are in no batch. Later commits go to a later run regardless of occurredAt.
- Payout instructions: payable balance through the watermark minus active reservations, positive only, for parties in `ledger.payout_parties` (sellers and affiliates; processor and tax are never paid out).
- Preparation posts nothing and debits no payable. One unconfirmed batch per currency.
- State is derived from insert-only rows: Prepared, Exported (export row), Confirmed (confirmation row).
- Export stores UTF-8 RFC 4180 bytes and their SHA-256 once; downloads return the stored bytes.
- Confirmation requires an exact statement match for every instruction, then posts Dr Payee Payable / Cr Simulated Bank Clearing per instruction and consumes the reservations, all in one transaction.
- Database guards EL501-EL504; history tables are insert-only.

## Consequences
Refunds are rejected once the settlement is in a batch (`SETTLEMENT_IN_BATCH`). Scheduler: `Clearing:Schedule:Enabled` (default true), `TimeUtc` (default `00:05`).

Automation follow-up: periodic escrow release also defaults to enabled, runs at
startup and every 60 seconds, and is invoked before each scheduled cut captures
its watermark. Both timers can be disabled for a manual demonstration. Bank
export/confirmation remain explicit simulated operations; see
[operating controls](../development/automation-and-contracts.md).
