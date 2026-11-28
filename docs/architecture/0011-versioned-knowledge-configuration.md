# 0011: Versioned knowledge configuration

Status: Accepted for BE-010. Requirements: FR-KEL-01/02, PRP section 5.1.

## Decision

Keep a typed, deterministic knowledge layer in the existing modular monolith.
Domain records represent regions, deduction types, gross-based deduction versions,
corporate fee/escrow profile versions and expected-result examples. Rates reuse
ScaledPercentage and amounts remain integer minor units. Rounding uses the existing
named, versioned policies. No expression language or configurable waterfall is added.

The Application service validates mappings, controls draft changes, runs fixtures,
publishes and selects effective versions. EF mappings, migrations and repositories
remain in Infrastructure. HTTP DTOs are separate from application/domain records;
money uses the existing exact decimal-string contract.

## Publication and concurrency

Each version has a row ID and a stable rule/profile ID. Versions start at 1 and are
consecutive. Publication requires a finite UTC interval `[effectiveFrom, effectiveTo)`,
at least one passing fixture, a source/assumption note and active same-currency ledger
mappings. The publishing actor and injected UTC clock time are server-owned.

Regional rules cannot overlap for the same region/type or share a sequence position
within an overlapping interval. Profile applicability is client, corporate code and
currency; overlapping published profiles are rejected. Stable IDs retain those keys.
Replacement intervals must begin exactly at the preceding version's effectiveTo;
published intervals are never shortened to make room for a replacement.

Configuration writes and selection use the existing transaction-scoped monetary
lock. This serializes publication checks and keeps a selection consistent across
profile and regional queries. Publication and its audit event commit together.
PostgreSQL constraints, foreign keys and triggers independently protect intervals,
overlap/order, account mappings, adjacent versions and immutable published content
and fixtures. Fixture arithmetic runs in the Application/Domain layer before commit;
direct SQL provisioning is a trusted operation, not a second publication API.

## Replay boundary

Published rows and their examples remain permanently available. Selection returns
immutable profile/rule content, ordered rules, reference/policy versions and an
explicit NO_DEDUCTION outcome when a known region has no applicable rule. Unknown
regions and profiles that do not apply fail explicitly. These values are the input
snapshot for BE-011; settlement persistence, result hashing and full-waterfall replay
belong to that task. BE-010 verifies old selections remain unchanged after adjacent
versions are published; it does not claim to create or replay a persisted trade.

The reference dataset uses synthetic USD mappings and finite 2026 intervals. Rules
with a beneficiary account in another currency are rejected, never silently skipped
or converted. Additional currencies require matching accounts and configuration;
there is no FX conversion or real-world tax interpretation.

Processor fees are always seller-borne: rounded percentage plus fixed fee, then
optional floor/cap. Escrow is rounded from a beneficiary share; payable is the exact
remainder. Zero hold duration produces no escrow. Combining deductions, affiliate
shares and ledger postings remains the fixed BE-011 settlement waterfall.
