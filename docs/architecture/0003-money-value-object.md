# Money value object

Task: BE-002.2 — Money Value Object & Arithmetic

## Decision

`Money` in `el.Domain/Common` is an immutable value object holding signed `long` minor units and a `CurrencyCode`. USD 250.00 is `25000` USD minor units. It has no identity, persistence or serialization dependencies.

- Equality and hash code use the amount and currency. Amounts in different currencies are simply not equal.
- A single value is limited to ±99,999,999,999 minor units. Creating a value outside that range throws `ArgumentOutOfRangeException`; an arithmetic result outside it throws `OverflowException`.
- Addition, subtraction and ordered comparison (`<`, `>`, `CompareTo`) require the same currency and otherwise throw `CurrencyMismatchException`. There is no implicit conversion.
- Zero and negative values are valid `Money`. A command that requires a positive amount, such as a captured trade gross, validates that separately.

## Aggregates

Totals across many values can exceed the per-value range. `MoneyAggregation.SumMinorUnits` returns an exact `BigInteger` for one currency and never narrows the result back into `Money`.

## Boundaries

Decimal-string parsing and formatting are BE-002.3, the JSON contract is BE-002.4, percentage rates are BE-003 and rounding is BE-004.
