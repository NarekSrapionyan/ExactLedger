# Money text format

Task: BE-002.3 — Money String Parsing & Formatting

## Canonical form

`MoneyText` in `el.Domain/Common` converts between `Money` and plain decimal strings using the exponent of an explicitly resolved `CurrencyDefinition`. Input and output use one canonical form:

```text
-?(0|[1-9][0-9]*)      when the exponent is 0           JPY  "1000"
-?(0|[1-9][0-9]*)\.d{e} when the exponent e is above 0   USD  "250.00", KWD "1.234"
```

- Exactly `e` fractional digits: USD "250" and "250.0" are missing precision, USD "2.345" and JPY "1000.0" are excess precision.
- Only a leading ASCII `-` is allowed as a sign; `+`, trailing signs, parentheses and the Unicode minus are rejected.
- Only ASCII digits and `.` are allowed: no whitespace, grouping separators, decimal commas, exponents, currency symbols or non-ASCII digits.
- No leading zeros (`01.00`, `007`).
- Zero is never negative: `-0.00` is rejected, and formatting writes `0.00`.
- Values must be within the Money range of ±99,999,999,999 minor units.

Parsing never rounds and never depends on the current culture; it uses integer arithmetic only. A rejected value reports a `MoneyTextError` (`Empty`, `InvalidFormat`, `LeadingZeros`, `NegativeZero`, `MissingPrecision`, `ExcessPrecision`, `OutOfRange`) so the API can return structured validation errors. Error messages do not include the rejected input.

## Boundaries

The JSON contract and HTTP validation responses are BE-002.4. Explicit rounding of calculated amounts is BE-004.
