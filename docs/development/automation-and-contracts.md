# Automation and PRP API routes

## Scheduled monetary work

The API host runs two jobs by default:

| Setting | Default | Behavior |
| --- | --- | --- |
| `Clearing:Schedule:Enabled` | `true` | Prepare daily clearing batches at `Clearing:Schedule:TimeUtc` (`00:05` UTC). |
| `Escrow:Schedule:Enabled` | `true` | Release due escrow at startup and periodically. |
| `Escrow:Schedule:IntervalSeconds` | `60` | Delay between due-release runs; valid range 1–3600 seconds. |

In Compose, use `CLEARING_SCHEDULE_ENABLED`, `ESCROW_SCHEDULE_ENABLED` and
`ESCROW_INTERVAL_SECONDS` in `.env`. For direct hosting, environment variables use
double underscores, e.g. `Escrow__Schedule__Enabled=false`. Disable both timers for
an entirely manual demo. The manual authenticated endpoints remain available.

Each escrow run handles at most 500 due holds under the existing monetary lock
and transaction. Eligible holds precede inactive beneficiaries/accounts; within
each group, oldest due time and ID determine order. Blocked holds stay Held and
are reported as skipped when selected. Remaining work is handled on later runs.
Worker audit identity is `scheduler`; retries retain the existing exactly-once
release/journal behavior. A transaction failure rolls back the run and is retried
on the next interval. Cancellation stops the timer. Logs contain counts/error
types, not financial payloads.

Each scheduled daily cut also invokes due-release first, so the resulting release
entries precede its watermark. This uses the same 500-hold bound: large backlogs
can span runs and later business dates. Enabling clearing alone still invokes this
pre-cut release even if the separate escrow timer is disabled. The daily timer
does not backfill missed days; a failed cut is logged and can be retried manually
or by the next scheduled run. Export and simulated bank confirmation remain
explicit operations. No real bank connectivity or regulated tax integration is
claimed by the educational MVP.

General integration fixtures and the performance harness explicitly disable
timers to keep their datasets deterministic. Dedicated real-PostgreSQL tests
exercise final host configuration, startup/periodic execution, rollback/retry,
cancellation, bounded selection and release-before-cut ordering with a controlled
clock.

## Canonical PRP routes

| Route | Behavior |
| --- | --- |
| `GET /v1/payees/{id}/statement` | Existing payee statement report, including ownership enforcement; optional currency/from/to/pagination filters. |
| `GET /v1/ledger/accounts` | Finance/Auditor register; optional currency, inclusive UTC `from`/`to`, offset (default 0), limit (default 50, maximum 100). |
| `GET /v1/ledger/trial-balance` | Finance/Auditor totals; optional currency and inclusive UTC `from`/`to`. |
| `GET /v1/escrow` | Optional settlementId/currency/pagination; Finance/Auditor global access, Payee restricted to stored party binding. |
| `POST /v1/escrow/{id}/release` | Finance/Scheduler full due release; retries return the stored result. |

Existing `/reports/payee-statement`, `/escrow/holds` and
`/escrow/holds/{id}/release` routes remain compatible.

The ledger register returns each account's opening balance, period debit/credit
activity and closing balance. Opening/closing amounts use the account's normal
side. Dates filter immutable posting timestamps, not the trade's earlier
`occurredAt`. The closing boundary is the start of the day after `to`. Without a
date range, the opening is zero and all finalized lines contribute to activity.
Raw journal entries without business posting timestamps are included in unfiltered
reads but cannot be attributed to a historical period. Totals use exact integer
aggregation and decimal strings, including values beyond Int64.

The older `account-balances` report remains a current snapshot including current
reservations; it still rejects historical filters. Historical register balances
do not present current reservations as historical facts.
