# Signed rounding policies

Task: BE-004 — Explicit Signed Rounding Policies

## Decision

`Rounding` in `el.Domain/Common` rounds an exact fraction — an integer numerator over a positive integer denominator, such as the `PercentageProduct` from BE-003 — to a whole number of minor units. The mode is always an explicit argument: there is no default mode and no overload without one. `RoundingMode` values start at 1, so `default(RoundingMode)` (0) is rejected rather than treated as a policy.

Rounding happens only at the declared fee, deduction and escrow points. Parsing (`MoneyText`) is unchanged and stays strict: `"2.345"` is still `ExcessPrecision` for USD and nothing in `MoneyText` accepts a rounding mode.

No `ExactAmount` or general rational type is introduced. The domain, application and architecture rules are unchanged: only `BigInteger` quotient and remainder are used, with no decimal division, floating point, current time or current culture.

## Algorithm

For numerator `N` and denominator `D > 0`:

1. `sign` is the sign of `N`. Work on `|N|`.
2. `q, r = BigInteger.DivRem(|N|, D)`. Both are exact.
3. Decide whether to add one to `q`, comparing `2r` with `D` (an exact comparison with one half):

| Mode | `q` becomes `q + 1` when |
|---|---|
| `HalfEven` | `2r > D`, or `2r == D` and `q` is odd |
| `HalfAwayFromZero` | `2r >= D` |
| `TowardZero` | never |

4. The result is `sign * (q or q + 1)`. Zero has no sign, so `-1/3` rounds to `0` in every mode.

| Exact value | HalfEven | HalfAwayFromZero | TowardZero |
|---|---|---|---|
| 2.5 | 2 | 3 | 2 |
| 3.5 | 4 | 4 | 3 |
| -2.5 | -2 | -3 | -2 |
| -3.5 | -4 | -4 | -3 |
| 2.6 | 3 | 3 | 2 |
| -2.6 | -3 | -3 | -2 |

Example: USD 2.345 is 234.5 minor units, the fraction `2345/10` (`q = 234`, `r = 5`, `2r = D`). HalfEven gives 234 (USD 2.34), HalfAwayFromZero gives 235 (USD 2.35), TowardZero gives 234. For `-2345/10` the results are -234, -235 and -234.

## Policy and trace

`RoundingPolicy.For(mode)` exposes the mode, a stable `Name` (`"HalfEven"`, `"HalfAwayFromZero"`, `"TowardZero"`) and `Version` (currently 1). Persist the name and version, not the enum number. A change to what any mode does requires a new version so a stored trace can be replayed against the definition it used.

`Rounding.Round` returns a `RoundingResult` with the exact input and output of one step:

| Property | Meaning |
|---|---|
| `Numerator`, `Denominator` | The exact input, as given |
| `Policy` | Mode name and version |
| `AbsoluteQuotient`, `AbsoluteRemainder` | `q` and `r` of `|Numerator|` over the denominator |
| `MagnitudeIncremented` | Whether one was added to `q` |
| `Value` | The rounded whole number with the sign restored |

`RoundingResult` is a plain domain value. Mapping it to JSON or database columns is the responsibility of Application/Infrastructure.

## Money conversion

`RoundingResult.ToMoney(currency)` narrows `Value` into `Money`. The range is checked before narrowing, so a value outside ±99,999,999,999 minor units, including one that does not fit in 64 bits, throws `OverflowException`, the same exception as out-of-range Money arithmetic. Rounding can itself cross the limit: `99,999,999,999.5` rounds up to `100,000,000,000` under HalfEven and HalfAwayFromZero and is rejected, while TowardZero returns `99,999,999,999`.

## Errors

| Condition | Result |
|---|---|
| Denominator zero or negative | `ArgumentOutOfRangeException` (`denominator`) |
| Mode not defined, including `default` | `ArgumentOutOfRangeException` (`mode`) |
| Null `PercentageProduct` or currency | `ArgumentNullException` |
| Rounded value outside the Money range | `OverflowException` |

The denominator is checked first, then the mode. An undefined mode is rejected even when the value needs no rounding. Messages never echo input.

## Usage at a rounding point

```csharp
var product = ScaledPercentage.FromRateUnits(10_000_000).Multiply(sellerShare.MinorUnits);
var escrow = Rounding.Round(product, RoundingMode.HalfEven).ToMoney(sellerShare.Currency);
```

Which mode applies at which point (regional deduction, processor percentage, commission, affiliate, escrow) comes from configuration in later tasks. The processor fixed amount and floor/cap are added after rounding and are not part of this task. Example A escrow: 10% of USD 204.95 is `20495 * 10_000_000 / 100_000_000 = 2049.5`, which is 2050 (USD 20.50) under HalfEven and HalfAwayFromZero and 2049 under TowardZero. Example B: 10% of USD 199.95 is 1999.5, which is 2000 (USD 20.00) under HalfEven.

## Boundaries

Scaled percentage rates and the exact product are BE-003. Weighted allocation, the settlement waterfall, rule/profile selection and the full calculation trace (base, rate units and so on) are later tasks. A general rational or `ExactAmount` framework is outside this MVP.
