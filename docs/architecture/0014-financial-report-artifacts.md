# ADR 0014: Ledger-backed reports and immutable CSV artifacts

Status: Proposed in BE-014.

## Decision

Use fixed, parameterized SQL projections over finalized journal lines and stored
clearing instructions/confirmations. Each report is a single PostgreSQL statement
with a consistent snapshot. Aggregate NUMERIC values are read as integer strings
and formatted through BigInteger; currencies are never netted together.

Return a rectangular schema (`columns` plus keyed string cells) shared with CSV.
Financial metrics remain named and documented. Read pagination does not change
whole-report totals. Bound a report to 100,000 rows and reject larger results.

Persist CSV bytes/hash/count/currency totals and the original timestamp in a
SQL-backed `report_exports` history table. Reuse the existing unit of work,
monetary lock, role matrix, stored ownership and RFC 4180 writer. Serialize rare
export generation with postings to freeze state and coordinate idempotent retries;
ordinary reads acquire no monetary lock. One migration adds the artifact table
without modifying the EF model or any existing financial history.

## Consequences

Exports consume storage and briefly hold the monetary lock, so narrow filters
are appropriate. Separate artifacts avoid changing the meaning of BE-013's
operational payout export. No generic report engine, new background worker or
external file storage is needed for the educational dataset.

Journal cut filters apply only to immutable journal/trial reports. Current
reservation state and business-date periods are labelled explicitly instead of
pretending to provide historical reservation states without recorded state times.
Payee statements expose only the party's own ledger movements; upstream global
fees remain in the authorized settlement trace, without arbitrary apportionment.
