# Money JSON contract

Task: BE-002.4 — Money JSON Contract & Converter

Every monetary value in the API is a JSON object with a decimal **string** amount and its currency:

```json
{ "amount": "250.00", "currency": "USD" }
```

- `amount` is a string in the canonical form for the currency's exponent (see `docs/architecture/0004-money-text-format.md`): USD and other 2-decimal currencies `"250.00"`, JPY `"1000"`, KWD `"1.234"`. Exactly that many decimal places, ASCII digits, an optional leading `-`, no grouping, exponents, whitespace or leading zeros, and no `"-0.00"`.
- `currency` is an uppercase three-letter code supported by the API: currently USD, JPY and KWD.
- No other properties are allowed, property names are case-sensitive, and each property appears once.
- JSON numbers are rejected for money, both as the whole value and as the amount.
- Money itself cannot be `null`, whether it is the root value, a request property, or an array element. Reading or writing a null Money fails with `money.not_object`; use an explicit zero amount when the value is zero. Required or omitted request fields are validated by their request DTO/use case.
- Responses always use the same canonical form.

## Fixtures

- `valid-money.json`: accepted values with their exact integer minor units.
- `invalid-money.json`: rejected values with the error code the API reports.

Both files are loaded by `el.IntegrationTests`, so they always match the server's behaviour.

## Error codes

| Code | Meaning |
|---|---|
| `money.numeric_not_allowed` | Money or its amount was sent as a JSON number |
| `money.not_object` | Money is null or is not a JSON object |
| `money.missing_amount` / `money.missing_currency` | A required property is missing |
| `money.amount_not_string` | The amount is null, a boolean, an object or an array |
| `money.invalid_currency` | The currency is not a three-letter uppercase string |
| `money.unknown_currency` | The currency is not supported |
| `money.unknown_property` / `money.duplicate_property` | Extra or repeated properties |
| `money.amount_empty` | The amount is an empty string |
| `money.amount_invalid_format` | Not a plain decimal (sign, grouping, exponent, whitespace, symbols) |
| `money.amount_leading_zeros` | Leading zeros such as `"0250.00"` |
| `money.amount_negative_zero` | `"-0.00"` |
| `money.amount_missing_precision` / `money.amount_excess_precision` | Wrong number of decimal places for the currency |
| `money.amount_out_of_range` | Outside ±99,999,999,999 minor units |

The HTTP response format for these errors (ProblemDetails with code, message and correlationId) is defined with the API foundation in BE-009.

## Swift client guidance

- Decode `amount` as `String`; never let `JSONDecoder` read money as `Double` or `Decimal` from a JSON number.
- Keep financial calculations on the backend. For display-only formatting, use exact integer minor units or `Decimal(string:locale:)` with `Locale(identifier: "en_US_POSIX")`. Never use the device locale for API values.
- Format display values for the user separately from the API value.
- Send amounts back in the canonical form, with exactly the currency's decimal places.
