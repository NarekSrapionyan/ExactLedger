# 0012: Settlement waterfall, snapshots and retained command keys

Status: Accepted for BE-011. Requirements: FR-SPL-01/02/03 and PRP section 5.4.

## Calculation boundary

The Domain calculator receives validated trade input and immutable configuration
and account content. It has no persistence, JSON, HTTP or clock dependency.
The fixed order is regional Gross deductions by sequence, seller-borne processor
percentage plus fixed fee/floor/cap, gross platform commission, gross affiliate
percentages, remainder/weighted pool, then beneficiary escrow/payable.

An affiliate line has a gross percentage and no weight; a pool has either exactly
one remainder beneficiary or integer weights with a positive total. Line IDs are
unique and sorted ordinally. Affiliates are directly payable; the corporate escrow
policy applies to remaining-pool beneficiaries. This reproduces the PRP examples
without applying seller escrow to the affiliate's independent gross percentage.
Excess deductions fail; no component is silently reduced. Weighted allocation uses
the existing Largest Remainder implementation and records quotient, remainder and
the complete tie-break order. Every percentage records its exact fraction and
rounding policy. Zero components remain explainable but create no journal line or
hold. Escrow plus payable exactly equals each beneficiary's original share.

## One transaction

The application takes the shared monetary lock, checks retained commands/trades,
selects published configuration in that transaction, validates active parties and
accounts, calculates, posts the controlled journal, and stores the settlement,
allocations, initial holds, command key and audit together. A deferred PostgreSQL
constraint verifies gross conservation, per-account credit agreement with the
journal, clearing debit, command presence and escrow/account mappings. Fault tests
exercise failure after journal insertion and failure at the deferred commit check.

All new history tables are insert-only at runtime and reject ordinary owner
UPDATE/DELETE/TRUNCATE. An internal server-generated transaction ID prevents new
allocation/hold/command rows being appended to a settlement after commit. Existing
migrations and their immutable history protections remain unchanged.

Holds start Held with original amount, released amount zero and dueAt equal to
occurredAt plus the profile's UTC hold duration. BE-012 adds controlled release and
refund transitions; BE-011 exposes neither operation and grants no unrestricted
hold updates. No broker/outbox or background worker is introduced.

## Canonical input and result

The input hash uses a fixed typed representation with ordinally sorted beneficiary
lines, integer money/rates and UTC time. Client identity comes from the signed
token. Keys are scoped by client and operation, retained with the demo dataset and
backed by a unique composite key. Same key/input returns the original 201 business
result; changed input returns 409 IDEMPOTENCY_CONFLICT. Independent permanent
client/trade uniqueness rejects reposting with a new key. Lock wait expiration
returns retryable 503 LEDGER_BUSY / Retry-After: 1; there is no partial public result.

Input, full selected rule/profile content, reference/policy versions, account
mappings, ordered allocations and trace are stored. The result hash covers the
canonical ordered calculation only: generated settlement/journal/hold IDs, posted
time, audit/correlation metadata and the external trade label are excluded.
Request identity and external trade uniqueness use the separate input hash/key.
JSONB key reordering is harmless: replay deserializes into typed records and
recalculates from stored content without reselecting today's configuration.
Posted timestamps are normalized to PostgreSQL microsecond precision before the
first response so retries reproduce it exactly.
Changing canonical fields/order or arithmetic requires a new calculation-policy
version and preservation of the earlier replay implementation.

## Reads and privacy

Finance, Admin and Auditor can inspect settlements; Integrators can inspect only
their stored client; a Payee can inspect only a participating signed party.
Cross-owner and missing resources share 404. Payee output uses the existing strict
projection: own component/result money only, without global fractions, input,
snapshots, account/party/payout identifiers or another beneficiary's values.
Full snapshot/hash replay is limited to full-access readers. Authorization is
enforced on both settlement and trace routes.
