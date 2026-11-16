# Atomic journal posting, monetary lock and ledger queries

Task: BE-008 — Atomic Journal Posting & Trial Balance (FR-LED-01, FR-LED-02, NFR-REL-01)

## Decision

Journal entries are posted all-or-nothing inside one unit of work, under one database-backed monetary-operation lock that every money-changing workflow takes. Balances and the trial balance are derived from finalized journal lines with exact SQL sums and returned as `BigInteger`. No schema change, migration or new privilege is needed.

## Monetary-operation lock

`IMonetaryOperationLock` (Application) is implemented by `MonetaryOperationLock` (Infrastructure) with a PostgreSQL transaction-scoped advisory lock, `pg_advisory_xact_lock`, on one fixed key (`0x454C4D4F4E455931`, "ELMONEY1"). The key is reserved: do not use other advisory-lock keys in this database.

| Property | Behaviour |
|---|---|
| Scope | One lock per database, shared by every app instance connected to it. It serializes all money-changing workflows, not only posting |
| Release | Automatic at COMMIT, ROLLBACK or connection loss. There is no release method; a crashed worker cannot leave it held |
| Precondition | An active transaction on the same DbContext as the work (`InvalidOperationException` otherwise) at READ COMMITTED. Under a stricter level the snapshot would predate the lock wait, so it is rejected |
| Re-entrancy | Taking it again in the same transaction is allowed and changes nothing |
| Waiting | `lock_timeout` (default 10 s, `MonetaryOperationLockOptions.WaitTimeout`). On expiry: `LedgerLockTimeoutException`; nothing was applied and the operation can be retried. Keep the wait below the Npgsql command timeout (30 s default) |
| Cancellation | Canceling the token cancels the wait and throws `OperationCanceledException`; the transaction rolls back |
| Bounded lifetime | `idle_in_transaction_session_timeout` (default 30 s, `MaxIdleInTransaction`): a hung holder is disconnected by the server and the lock is released |
| Pooling | Transaction-scoped, so it is safe with connection pooling and transaction-mode poolers |

Both limits use `SET LOCAL` semantics, so they end with the transaction.

Rules for every workflow that uses the lock:

1. Open the unit of work, then acquire the lock first, before reading any state the decision depends on.
2. Keep the transaction short: no HTTP calls, file generation or other slow work between acquisition and commit.
3. Do not retry silently. There is no retrying execution strategy.

Workflows that must use the same lock: journal posting (BE-008), settlement, escrow release, full refund, daily cut and reservation, payout confirmation, and account or party closure. Reports and CSV export read committed data and take no write lock.

Because all of these serialize, ledger inserts commit in the order they take the lock. A daily cut that captures its journal-sequence watermark under the same lock therefore sees a gap-free prefix.

## Atomic posting

`JournalPostingService.PostAsync` runs one unit of work: acquire the lock, re-check the accounts, write and finalize the entry through `IJournalEntryStore`, commit. A failure at any point (before any write, after the header, after some lines, after finalization, at the deferred commit check, cancellation, connection loss) rolls the whole transaction back. Workflows that already run in a unit of work call `PostWithinTransactionAsync`, because units of work do not nest.

Account status: the entry was validated against an earlier snapshot, so the account status is re-checked under the lock with `SELECT … FOR SHARE`. A missing or closed account throws `AccountNotPostableException`. The row lock also makes a concurrent `UPDATE accounts SET status` wait until the post ends, so closure cannot slip in before the commit. A party-level activity check belongs to the posting use case.

Duplicates: the unique journal reference is the only guard for direct posts. A duplicate fails the whole transaction with a unique violation. If a client loses the commit response, the outcome is unknown; a retry with the same reference fails as a duplicate, which means "already posted". Settlement, refund and release add their own idempotency records on top.

Currency consistency and the balanced, finalized, immutable checks stay in the domain and in the database guards (EL101 to EL105).

## Balances and trial balance

`ILedgerBalanceQueries` reads finalized lines only, with one SQL statement per query, so a concurrent commit cannot make related totals disagree.

- `SUM` over BIGINT is NUMERIC in PostgreSQL. It is cast to text and parsed into BigInteger with ExactAggregateQueries.ParseExactInteger; nothing is narrowed to `long` or `Money`.
- `AccountBalance` returns `DebitTotal` and `CreditTotal`, plus `NetDebit` (debits minus credits) and `OnNormalSide(side)` (positive on the account's normal side). Payee availability is `OnNormalSide(Credit)` minus reservations. Callers pick the side from the domain account: balance.OnNormalSide(account.NormalSide)
- An unknown account returns `null`; an account without postings returns zeros. Closed accounts keep their history.
- `GetTrialBalanceAsync` returns one row per currency that has finalized lines, ordered by code, never netted across currencies, optionally filtered to one currency. `IsBalanced` is `DebitTotal == CreditTotal`.
- No public HTTP endpoint is added; routes belong to the reporting tasks.

## Privileges

No migration is needed. `pg_advisory_xact_lock` is executable by PUBLIC. `FOR SHARE` on `ledger.accounts` needs UPDATE on at least one column, which `exactledger_runtime` has through `UPDATE (status)`.

## Boundaries

Settlement, escrow, refund, clearing and confirmation use this lock in later tasks. Posting time, the journal sequence and period filters are not part of this task.
