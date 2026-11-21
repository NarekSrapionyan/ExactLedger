# Settlement API and worked examples

Task BE-011; FR-SPL-01/02/03. See [ADR 0012](../architecture/0012-settlement-snapshots-and-idempotency.md).
Start through [Docker Compose](local-docker.md); provision profiles/rules through
the [configuration API](knowledge-configuration.md). Sign in as an Integrator to
submit a trade. The server uses the identity's client binding, never a body field.

| Endpoint | Access / result |
| --- | --- |
| POST /v1/settlements | Integrator; required Idempotency-Key; original or safely retried result returns 201 |
| GET /v1/settlements/{id} | Finance/Admin/Auditor, owning Integrator, participating Payee |
| GET /v1/settlements/{id}/trace | Same access; full calculation for full readers, own whitelist for Payee |
| GET /v1/settlements/{id}/replay | Full-access readers; re-run stored content and compare the result hash |

OpenAPI documents the required key and full vs Payee response variants. Money
always uses exact decimal-string/currency objects. Rates are percentage × 1,000,000.
Fractions and weighted quotient/remainder values are integer strings; weights and
rates remain integers. Never convert money through Double on mobile.

```json
{
  "tradeId": "T-1",
  "occurredAt": "2026-06-01T00:00:00Z",
  "currency": "USD",
  "gross": {"amount":"250.00","currency":"USD"},
  "regionCode": "DEMO-NONE",
  "profileId": "<stable published profile ID for your client>",
  "corporateCode": "WORKED-USD",
  "beneficiaries": [
    {"lineId":"affiliate","partyId":"<affiliate ID>","kind":"Affiliate","weight":0,"rateUnits":3000000},
    {"lineId":"seller","partyId":"<seller ID>","kind":"Remainder","weight":0,"rateUnits":0}
  ]
}
```

Send `Authorization: Bearer ...` and `Idempotency-Key: trade-T-1-attempt-1`.
Reuse the same key and business input after a network error. Reordering beneficiary
lines is equivalent; changing money, shares, time, region or profile is a different
input. A changed payload under the same key returns 409 IDEMPOTENCY_CONFLICT.
A previously posted client/trade under a different key returns 409
TRADE_ALREADY_SETTLED. Different clients have separate key/trade namespaces.
Missing/invalid keys and business input return 400; wrong roles return 403 and
foreign/missing results return 404. Concurrent identical commands wait and return
the same committed result; a lock timeout returns 503 LEDGER_BUSY with Retry-After: 1.
The stored result survives later provisioning/configuration changes.

For the PRP worked example, create/publish a USD profile with 12% commission,
processor 2.9% + USD 0.30, no floor/cap, HalfEven, 10% escrow for 7 days. The
BE-010 DEMO-STANDARD seed has 10% commission and a cap, so it is deliberately
different; do not silently change published seed content. Attach independent
expected fixtures and use a new stable profile/corporate code for the example.

| Component | No regional deduction | Synthetic 2% gross deduction |
| --- | --- | --- |
| Regional payable | 0.00 | 5.00 |
| Processor payable | 7.55 | 7.55 |
| Commission revenue | 30.00 | 30.00 |
| Affiliate payable | 7.50 | 7.50 |
| Seller share before escrow | 204.95 | 199.95 |
| Seller escrow | 20.50 | 20.00 |
| Seller payable | 184.45 | 179.95 |
| Total credits / clearing debit | 250.00 | 250.00 |

For the second column, publish a 2% synthetic rule in a separate region with a
matching tax payable mapping. DEMO-A/DEMO-B remain their existing 10%/5% teaching
examples. HTTP/PostgreSQL integration tests provision these worked fixtures,
exercise both examples, and verify JPY/KWD independently.

Full responses include stable input, complete configuration/account snapshot,
ordered allocations, trace, journal reference, posted time and canonical result
hash. A Payee receives only settlement ID, currency, own allocations and own trace
results; no global input or snapshot is exposed. Snapshot profile/rule fixture
arrays are empty in the response because examples are not calculation inputs;
their published originals remain in the configuration history. Hash replay uses
stored version content, not current selection. The tests publish an adjacent
profile version and prove earlier trades/retries remain unchanged while a newly
eligible trade uses the new commission.

Escrow holds are created atomically and are not available as payable balance.
Release/refund commands and lifecycle screens belong to BE-012; clearing, payouts
and statements belong to BE-013. BE-011 adds no arbitrary journal-posting endpoint.

```sh
dotnet restore ExactLedger.sln --locked-mode
dotnet build ExactLedger.sln -c Release --no-restore -warnaserror
dotnet test ExactLedger.sln -c Release --no-build --no-restore --collect "XPlat Code Coverage" --settings coverage.runsettings
```

Docker is required for PostgreSQL tests. Tests include ten concurrent identical
commands, changed-payload/permanent-trade conflicts, client/Payee isolation,
configuration-change replay, and rollback after journal insertion or a failing
deferred integrity check. No real bank or external payment service is called.
