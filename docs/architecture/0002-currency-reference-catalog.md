# Currency reference catalog

Task: BE-002.1 — Currency Definitions & Reference Catalog

## Decision

Currency metadata lives in `el.Domain/Common` as pure, immutable types with no persistence or serialization dependencies:

- `CurrencyCode`: exactly three uppercase ASCII letters. Input is validated, never normalized (`usd` is rejected, not upper-cased).
- `CurrencyDefinition`: code, exponent (minor-unit digits, 0–4 as in ISO 4217) and reference-data version (1 or greater).
- `CurrencyCatalog`: a read-only set of definitions keyed by code and version.
- `CurrencyReferenceData`: the seeded version 1 catalog with USD/2, JPY/0 and KWD/3.

## Rules

- Callers resolve an explicit code and version. There is no "latest version" fallback, so a stored snapshot replays against exactly the definition it used.
- An unknown code or version fails with `UnknownCurrencyException`.
- Each code/version pair is defined once. Repeated or conflicting definitions are rejected when the catalog is created.
- Published versions do not change. A different exponent for an existing code is introduced as a new version.

## Boundaries

Money arithmetic, string parsing and JSON are BE-002.2–BE-002.4. Database persistence and seeding of the catalog follow in BE-007; Infrastructure will load definitions and build the same `CurrencyCatalog`.
