# ADR 0015: Complete mobile contracts within the settlement MVP

Status: Proposed in the backend acceptance follow-up.

## Decision

Add scoped recent-settlement summaries, Admin participant registration with
currency accounts and optional payout eligibility, and immutable refund reasons.
Reuse permissions, the monetary lock, unit of work and audit. A client-generated
participant ID permits safe retries; registration grants no identity or role.
Keep legacy refund rows and their original reason-less retry hashes intact.

Retain existing settlement-specific account purposes: Clearing is an asset;
Payable/Escrow are liabilities; Revenue is revenue. The MVP does not implement
the PRP's general Equity/Expense categories or a separate account-name field.
Account codes identify mappings. These unused categories do not improve the
required marketplace financial flows.

Posting time and operation kind remain authoritative on immutable settlement,
refund, escrow-transition and payout-confirmation records, linked to their
journals. Reports derive those fields through the links and retain journal
references/reversal relations. Generic journal objects do not duplicate business
operation/timestamp fields. There is no public manual-journal endpoint. Older
generic test journals without a business timestamp are excluded from date-filtered
reports, as already documented in reporting contracts.

These are explicit representational departures from literal FR-LED-01 AC1/AC2,
not a claim of a general-purpose accounting system. Accepting this scoped decision
does not weaken conservation, balanced immutable journals or ownership checks.
General bookkeeping, real tax-law content, bank execution and sub-minor accrual
remain separate requirements. Swift/device acceptance remains in mobile tasks.

## Evidence

HTTP/PostgreSQL tests cover list pagination/ownership, registration retry/conflict,
atomic account/payout/audit rollback, a new party's settlement and clearing,
refund-reason validation/immutability and migration-safe legacy retries.
