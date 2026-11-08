# Deterministic weighted allocation

Task: [BE-005 — Deterministic Weighted Allocation](https://app.clickup.com/t/z8r3fdzh4x), FR-MON-03.

## Contract

`WeightedAllocation.Allocate(amountMinorUnits, lines)` allocates a non-negative `long` amount
in whole minor units. Each immutable `AllocationLine` has a non-blank `AllocationLineId`
and a non-negative `long` weight. Weights are relative integers, not percentages, and need
not sum to 100. Their total and all intermediate products use `BigInteger`.

Identifiers are opaque, case-sensitive strings. They are compared with `StringComparer.Ordinal`,
never normalized, trimmed or compared with the current culture. Callers must supply stable IDs
for reproducible results across requests and replays. Duplicate IDs, null entries, empty input
and an all-zero weight total are rejected with `ArgumentException`, including for a zero amount.
Null input is `ArgumentNullException`; negative amounts/weights are `ArgumentOutOfRangeException`.

All inputs belong to one currency; the caller retains that currency. The allocator does not
convert currencies, construct Money or enforce Money's narrower per-value limit. It accepts
the full non-negative `long` range. A caller converting a share to Money uses its existing
range-checked factory.

## Largest Remainder

For amount `A`, weights `w_i` and total weight `W > 0`:

1. Compute `q_i, r_i = BigInteger.DivRem(A * w_i, W)`.
2. Allocate each line its floor `q_i`.
3. Distribute the remaining `A - sum(q_i)` units, one each, by remainder descending,
   then allocationLineId ordinal ascending. Zero-weight lines receive zero.

The residual is strictly smaller than the line count, so it fits in an array index. Each
share fits in `long` because it cannot exceed `A`. The result is a read-only snapshot of
immutable `AllocationShare` values sorted by ordinal ID, independent of input order.
The input is enumerated once and is not modified.

Example: 0.05 USD is `A = 5`, with IDs A/B/C and weights 50/30/20. Floors are 2/1/1,
remainders are 50/50/0 over 100. The remaining unit goes to A, producing 3/1/1 minor units.
This algorithm uses neither floating point nor decimal division nor a rounding policy.

## Verification and boundaries

Golden vectors cover zero, ties, zero weights, a single recipient and `long.MaxValue` amounts,
products and weight totals beyond 64 bits. Tests also cover invalid inputs, culture, shuffled
input and read-only output. 10,000 reproducible property cases verify conservation,
non-negativity, repeatability, permutation independence, zero-weight exclusion and exact
share error strictly below one minor unit using integer comparisons.

Largest Remainder is not monotone in the total (the Alabama paradox). With weights
1500/1500/900/500/500/200, totals 25 and 26 allocate 7/7/4/3/3/1 and 8/8/5/2/2/1.
Some recipients lose a unit when the total grows. Do not compute partial refunds by subtracting
cumulative allocations. Refund eligibility and reversal of stored postings are separate tasks.
