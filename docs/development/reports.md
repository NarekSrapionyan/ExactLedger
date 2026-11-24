# Financial reports and CSV

BE-014 implements FR-RPT-01. Existing settlement detail/trace endpoints and
`/v1/ledger/accounts/{id}/balance` remain available.

## API

`GET /v1/reports/{kind}` returns `columns`, rectangular `rows` (objects keyed by
column), `recordCount` and `totals` grouped by currency. Every cell is a string;
monetary cells use the currency's exact precision, never JSON numbers. Aggregate
amounts are not narrowed to Money/Int64. Totals cover the **whole filtered report**,
even when `offset`/`limit` select a page (default 50, maximum 1,000).

| Kind | Access | Meaning |
| --- | --- | --- |
| `account-balances` | Finance, Auditor, own Payee | Current finalized debits/credits, normal-side balance, active reservation and available payable |
| `trial-balance` | Finance, Auditor | Debits, credits and difference per currency |
| `journal-register` | Finance, Auditor | Finalized journal lines, immutable references, reversals and ledger mapping |
| `payee-statement` | Finance, Auditor, own Payee | One party's allocations, refund deductions, escrow movements, reservations and confirmed payout postings |
| `payout-instructions` | Finance, Auditor, own Payee | Frozen instruction, currently reserved amount and posted paid amount |
| `batch-summary` | Finance, Auditor | Instructions = reserved + paid; paid = payable debits = simulated bank credits |

Filters: `currency` (USD/JPY/KWD), `partyId`, `from`/`to` (inclusive ISO dates),
`batchId`, and `cutSequence`. Finance/Auditor must supply `partyId` for a payee
statement. Payee identity supplies its stored party automatically; a different
party returns 404. Journal/trial/batch reports reject partial-party filtering.
Admin, Integrator and Scheduler do not receive financial report access.

Journal/statement periods use actual UTC posting timestamps. Instruction/batch
periods use their assigned UTC business dates and show **current** reservation and
payment states. They are not historical end-of-day balances. Current account
balances reject dates and batch filters. Only the immutable journal/trial reports
accept `cutSequence` (inclusive); mutable reservation/export states cannot be
reconstructed reliably from a journal sequence alone. A batch filter includes
its original business entries and its payout journals. Older generic journals
without a business posting timestamp appear in unfiltered/cut reports but not
date-filtered reports.

The payee statement's `grossAllocation` is the party's stored allocation **before
escrow**, after the upstream waterfall. It is not the entire trade gross.
`deductions` are reversals of that party's allocations on an eligible full refund.
Global regional/processor/commission deductions happen before the seller pool;
their original amounts and calculation trace remain in authorized settlement
detail. They are never invented as per-seller charges or exposed to other parties.
`heldEscrow` is the signed escrow movement in the selected period; `releasedEscrow`
counts the release once on its escrow debit. Reservation rows are informational,
have zero journal debit/credit and never count as `confirmedPayouts`.

## Immutable exports

```http
POST /v1/reports/journal-register/exports
Authorization: Bearer <finance-or-auditor-token>
Idempotency-Key: demo-journal-1
Content-Type: application/json

{"currency":"USD","batchId":"<batch-id>"}
```

The 201 response provides ID, original generation time, SHA-256, record count,
totals by currency and `downloadUrl`. The corresponding metadata endpoint is
`GET /v1/reports/exports/{id}`. Download returns `text/csv; charset=utf-8`, the
original bytes and a SHA-256 ETag. Exporting a report does not mark a clearing
batch Exported; use the existing clearing export operation for that workflow.

CSV uses RFC 4180 escaping, CRLF, UTF-8 without BOM, fixed columns and immutable-ID
row order (journal ID then line number; currency for trial balance). No metadata
footer breaks the rectangular table. The complete filtered report is exported,
not the current page. More than 100,000 rows fails with `REPORT_TOO_LARGE`; narrow
the period instead of silently accepting a truncated file.

An actor's identical key/input returns the stored artifact even after new postings
or confirmation. A changed input with the same key returns 409
`EXPORT_KEY_CONFLICT`; use a new key for a fresh snapshot. Generation and audit
share a transaction under the existing monetary lock. Bytes, hash and metadata
are immutable; runtime receives SELECT/INSERT only and database triggers also
reject owner UPDATE/DELETE/TRUNCATE. Stored party scope and current role grants
are checked again on metadata and download; knowing an artifact ID grants no access.

See [delivery runbook](delivery-runbook.md) and [performance evidence](performance.md).
