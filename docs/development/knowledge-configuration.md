# Regional rules and corporate profiles

Task: BE-010. Baseline: FR-KEL-01/02 in the supplied 17-page ExactLedger PRP.
See [ADR 0011](../architecture/0011-versioned-knowledge-configuration.md).

## Start and inspect

Use the [local Docker setup](local-docker.md) and sign in as `admin` using your
configured seed password. `auditor` can inspect configuration but cannot change it.
The combined `demo-admin` identity has the administrator permission as well.

Participant registration and account mapping lookup are documented in
[backend acceptance contracts](backend-acceptance.md). Admin can create new
participants with complete currency accounts; this does not grant login roles.

| Routes | Access and behavior |
| --- | --- |
| GET /v1/regions, /v1/deduction-types | Admin/Auditor: list taxonomy |
| POST /v1/regions, /v1/deduction-types | Admin: add taxonomy |
| GET /v1/rules, /v1/fee-profiles | Admin/Auditor: list full version content, examples and publication metadata |
| POST /v1/rules, /v1/fee-profiles | Admin: create draft; returns server-generated version row ID |
| PUT /v1/rules/{id}, /v1/fee-profiles/{id} | Admin: replace content/examples of an existing draft |
| POST /v1/rules/{id}/validate, /v1/fee-profiles/{id}/validate | Admin: run attached examples; no publication side effect |
| POST /v1/rules/{id}/publish, /v1/fee-profiles/{id}/publish | Admin: validate and publish atomically; returns 204 |

All routes require a bearer token. Errors use the shared code/message/correlationId
contract. Invalid input/mappings or failed examples return 400; immutable content,
duplicate versions, non-adjacent intervals and ambiguous publication return 409.
Missing versions return 404; unauthenticated/forbidden requests return 401/403.
Lock timeouts return retryable 503 LEDGER_BUSY with Retry-After: 1.
OpenAPI `/openapi/v1.json` in Development includes concise endpoint descriptions,
explicit request/response DTOs and the actual `{amount, currency}` money schema.

## Synthetic reference data

Migrations seed the following, without external legal or bank dependencies:

* DEMO-A: one synthetic 10% gross levy, paid into PAYABLE.TAX.USD.
* DEMO-B: one synthetic 5% gross levy with the same account family.
* DEMO-NONE: known region with an explicit no-deduction selection result.
* DEMO-STANDARD / demo-client-1 / USD: 10% commission; seller-borne processor fee
  2.9% + USD 0.30, floor USD 0.50, cap USD 5.00; 10% escrow for 7 days.
* DEMO-SIMPLE / demo-client-1 / USD: no commission; 1% seller-borne processor fee;
  no floor, cap or escrow.

All reference intervals are `[2026-01-01T00:00:00Z, 2027-01-01T00:00:00Z)`.
Rows are marked synthetic and carry an explicit source/assumption and
`synthetic-seed` publication identity. Each has an independent expected-result
fixture. These values are teaching examples, not actual legal deductions.
IDs are deterministic; read them from GET responses. No additional credentials
or provisioning command are required. Existing local databases receive the new
data when the migration service runs; ledger history is untouched.

## Create and publish

A rule draft request looks like this; replace the account ID with the matching
same-currency payable account from your deployment:

```json
{
  "ruleId": "82b4bc41-6c56-41bb-9f7b-51961d50bb88",
  "version": 1,
  "regionCode": "DEMO-NONE",
  "deductionTypeCode": "DEMO-LEVY",
  "calculationBase": "Gross",
  "rateUnits": 5000000,
  "sequence": 1,
  "beneficiaryAccountId": "<PAYABLE.TAX.USD account ID>",
  "currency": "USD",
  "roundingMode": "HalfEven",
  "effectiveFrom": "2026-01-01T00:00:00Z",
  "effectiveTo": "2027-01-01T00:00:00Z",
  "sourceNote": "Synthetic demonstration of a 5% gross levy.",
  "isSynthetic": true,
  "fixtures": [{
    "note": "Synthetic USD 100.00 example",
    "gross": {"amount": "100.00", "currency": "USD"},
    "expectedDeduction": {"amount": "5.00", "currency": "USD"}
  }]
}
```

The response is `{id, content, publishedBy, publishedAtUtc}`; content is the saved
request. Use the returned **id** in validate/publish/PUT routes. `ruleId` is instead
the stable identity reused by versions 2, 3, etc. Profiles use the equivalent
`profileId`. Each version row ID and stable ID is distinct.

To create a profile draft, copy a profile's `content`, set a new profileId and
corporateCode, keep version 1 and edit fee/escrow settings and examples. Rates are
integer percent × 1,000,000: 2.9% is 2,900,000 units; 100% is 100,000,000.
`processorFixed`, optional `processorFloor` and `processorCap`, and all fixture
amounts use the same exact money objects. Floor/cap may be omitted or null. JSON
numbers are rejected as money. Round modes are HalfEven, HalfAwayFromZero or
TowardZero. Hold days are 0–30; zero means no escrow.

Profile fixtures supply gross, beneficiary share before escrow and four expected
amounts: expectedCommission, expectedProcessor, expectedEscrow and expectedPayable.
They test fee and escrow components; they are not a substitute for BE-011's complete
trade-conservation fixtures. Attach 1–100 non-empty, labeled examples per version.

Validate to diagnose example failures, then publish. Publication re-runs validation
and examples under the transaction lock. A failure leaves the draft unpublished
and writes no successful-publication audit event. Drafts remain editable; published
versions and their examples cannot be edited, deleted or truncated, including by
ordinary schema-owner SQL. Runtime has no schema-changing permissions.

For a replacement, copy published content into a new draft with the same stable ID,
version + 1, effectiveFrom equal to the preceding effectiveTo, and a later finite
effectiveTo. Change the settings and expected results. Do not PUT the published
row or attempt to shorten its existing interval. Server-owned actor/time metadata
cannot be supplied through these request DTOs.

## Selection and verification

The backend selects by explicit stable profile ID, trusted client binding,
corporate code, currency, region and UTC trade occurredAt. Intervals exclude their
upper boundary. Unknown regions and absent/wrong-client/wrong-currency profiles
fail; a known region without an applicable deduction returns NO_DEDUCTION.
Newly eligible selection uses the new version; earlier selection retains the old
immutable content. Settlement endpoints, persisted transaction snapshots, replay
hashes and the complete waterfall will be added in BE-011.

```sh
dotnet restore ExactLedger.sln --locked-mode
dotnet build ExactLedger.sln -c Release --no-restore -warnaserror
dotnet test ExactLedger.sln -c Release --no-build --no-restore --collect "XPlat Code Coverage" --settings coverage.runsettings
```

Docker must be running for PostgreSQL integration tests. The knowledge HTTP tests
cover role denial, example failures, draft correction, overlapping and concurrent
publication, adjacency, historical selection, optional money bounds and database
immutability. Standard CI also runs Windows checks and the Compose startup smoke.
