# Clearing (BE-013)

| Method | Route | Permission |
|---|---|---|
| POST | /v1/clearing-batches | RunClearingAndExport (201 new, 200 retry, 204 nothing to cut) |
| GET | /v1/clearing-batches, /v1/clearing-batches/{id} | RunClearingAndExport |
| POST | /v1/clearing-batches/{id}/export | RunClearingAndExport |
| GET | /v1/clearing-batches/{id}/export | RunClearingAndExport (text/csv, X-Content-SHA256) |
| POST | /v1/clearing-batches/{id}/simulate-statement | ConfirmSimulatedBank (Finance only) |
| POST | /v1/clearing-batches/{id}/confirm-simulated | ConfirmSimulatedBank (Finance only, text/csv body) |

Payout CSV: `reference,business_date,party_id,currency,amount`. Statement CSV: `reference,currency,amount,source` (source `SYNTHETIC`).
Errors: UNSUPPORTED_CURRENCY, INVALID_BUSINESS_DATE, UNCONFIRMED_BATCH_EXISTS, CLEARING_VERIFICATION_FAILED, BATCH_NOT_EXPORTED, STATEMENT_INVALID, STATEMENT_MISMATCH, STATEMENT_CONFLICT.
Configuration: `Clearing:Schedule:Enabled`, `Clearing:Schedule:TimeUtc`.

Confirmation sends the CSV as the raw `text/csv` request body; OpenAPI declares this
required body explicitly. Exporting or simulating a statement never posts payment.
An identical statement retry returns the original confirmation; different bytes
after confirmation return `STATEMENT_CONFLICT`.

Refund and cut use real database batch membership and the same monetary lock.
Integration tests queue both HTTP operations behind PostgreSQL's lock and verify
both orderings: refund first produces no payable instructions; cut first rejects
the refund with `SETTLEMENT_IN_BATCH` and keeps the reserved amounts intact.
