using System.Numerics;

namespace el.Domain.Common;

/// <summary>
/// Largest Remainder allocation of a non-negative amount in minor units using integer weights.
/// All inputs belong to one currency, supplied by the caller. No rounding policy is applied.
/// This is not a partial-refund algorithm: allocations of increasing totals need not be monotone.
/// </summary>
public static class WeightedAllocation
{
    /// <summary>
    /// Distributes residual units by remainder descending, then allocationLineId ordinal ascending.
    /// Returns a read-only snapshot ordered by allocationLineId ordinal, independent of input order.
    /// Each share is non-negative, the shares sum to the amount, and a zero weight receives zero.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The amount is negative.</exception>
    /// <exception cref="ArgumentNullException">The sequence is null.</exception>
    /// <exception cref="ArgumentException">The sequence contains null, duplicate IDs, or no positive weight.</exception>
    public static IReadOnlyList<AllocationShare> Allocate(long amountMinorUnits, IEnumerable<AllocationLine> lines)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amountMinorUnits);
        ArgumentNullException.ThrowIfNull(lines);

        // Enumerate once: validation and calculation must use the same input snapshot.
        var snapshot = lines.ToArray();
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        var totalWeight = BigInteger.Zero;

        foreach (var line in snapshot)
        {
            if (line is null)
            {
                throw new ArgumentException("Allocation lines must not contain null.", nameof(lines));
            }

            if (!identifiers.Add(line.AllocationLineId))
            {
                throw new ArgumentException("Allocation line identifiers must be unique.", nameof(lines));
            }

            totalWeight += line.Weight;
        }

        if (totalWeight.IsZero)
        {
            throw new ArgumentException("At least one allocation line must have a positive weight.", nameof(lines));
        }

        Array.Sort(snapshot, (left, right) => StringComparer.Ordinal.Compare(left.AllocationLineId, right.AllocationLineId));

        var amounts = new long[snapshot.Length];
        var remainders = new BigInteger[snapshot.Length];
        var allocated = BigInteger.Zero;

        for (var index = 0; index < snapshot.Length; index++)
        {
            var numerator = new BigInteger(amountMinorUnits) * snapshot[index].Weight;
            var quotient = BigInteger.DivRem(numerator, totalWeight, out remainders[index]);

            // A share cannot exceed the non-negative long input amount.
            amounts[index] = (long)quotient;
            allocated += quotient;
        }

        // The sum of the fractional remainders is an integer smaller than the line count.
        // There are more positive remainders than residual units, so zero weights cannot win.
        var residual = (int)(new BigInteger(amountMinorUnits) - allocated);
        var recipients = Enumerable.Range(0, snapshot.Length)
            .OrderByDescending(index => remainders[index])
            .ThenBy(index => snapshot[index].AllocationLineId, StringComparer.Ordinal)
            .Take(residual);

        foreach (var index in recipients)
        {
            amounts[index] = checked(amounts[index] + 1);
        }

        var result = new AllocationShare[snapshot.Length];

        for (var index = 0; index < snapshot.Length; index++)
        {
            result[index] = new AllocationShare(snapshot[index].AllocationLineId, amounts[index]);
        }

        return Array.AsReadOnly(result);
    }
}
