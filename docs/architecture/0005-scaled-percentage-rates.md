# Scaled percentage rates

Task: BE-003 — Scaled Percentage Rates

## Decision

`ScaledPercentage` in `el.Domain/Common` is an immutable value holding one `long` of rate units: the percentage multiplied by 1,000,000. 2.9% is `2_900_000` and 100% is `100_000_000`. The smallest non-zero rate is 0.000001%. It has no persistence or serialization dependencies, and it is deliberately not Money: it holds no currency and never takes or returns Money.

Every product of a base and a rate has the same denominator, `100_000_000` (100 percent times the scale of 10^6).

## Rules

- Valid rate units are 0 to 100,000,000 inclusive. `ScaledPercentage.FromRateUnits` throws `ArgumentOutOfRangeException` outside that range.
- `PercentageText.TryParse` accepts `(0|[1-9][0-9]*)` optionally followed by `.` and one to six ASCII digits: `"2.9"`, `"2.900000"`, `"100"`. Fewer than six fractional digits are padded with zeros; this differs from Money, which requires the exact exponent.
- Rejected: empty text, any sign, whitespace, `%`, grouping separators, decimal commas, exponents, `".5"`, `"5."`, non-ASCII digits, leading zeros, more than six fractional digits and values above 100.
- Errors are reported as `PercentageTextError` (`Empty`, `InvalidFormat`, `LeadingZeros`, `ExcessPrecision`, `OutOfRange`), checked in that order, so `"101.1234567"` is `ExcessPrecision`.
- Parsing uses integer arithmetic only, never rounds and never reads the current culture.

## Exact product

`ScaledPercentage.Multiply(long baseMinorUnits)` returns a `PercentageProduct` with:

- `Numerator = baseMinorUnits * RateUnits`, computed with `BigInteger`;
- `Denominator = 100_000_000`.

The fraction is not reduced, divided or rounded. The base may be zero or negative; the sign stays on the numerator and the denominator stays positive. The product can exceed 64 bits even for valid Money: 99,999,999,999 minor units at 100% is 9,999,999,999,900,000,000, which is larger than `long.MaxValue`.

Example: 2.9% of USD 250.00 is base `25000`, numerator `72_500_000_000`, denominator `100_000_000`, which is exactly 725 minor units (USD 7.25).

## Boundaries

Rounding the fraction to minor units (HalfEven, HalfAwayFromZero, TowardZero) is BE-004. Weighted allocation is a separate task. A general rational or `ExactAmount` framework is outside this MVP.
