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
