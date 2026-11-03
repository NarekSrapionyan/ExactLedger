using System.Numerics;

namespace el.Domain.Common;

/// <summary>
/// Exact totals across many amounts. A total may exceed the per-value Money range,
/// so it is returned as a wider integer and never narrowed back into Money.
/// </summary>
public static class MoneyAggregation
{
    /// <exception cref="CurrencyMismatchException">A value is not in <paramref name="currency"/>.</exception>
    public static BigInteger SumMinorUnits(IEnumerable<Money> values, CurrencyCode currency)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(currency);

        var total = BigInteger.Zero;

        foreach (var value in values)
        {
            if (value is null)
            {
                throw new ArgumentException("Values must not contain null.", nameof(values));
            }

            if (value.Currency != currency)
            {
                throw new CurrencyMismatchException(currency, value.Currency);
            }

            total += value.MinorUnits;
        }

        return total;
    }
}
