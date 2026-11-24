using el.Domain.Common;

namespace el.Domain.Clearing;

/// <summary>The immutable payout instruction reference: PO-yyyyMMdd-CUR-nnnnnn.</summary>
public static class PayoutReference
{
    public static string Create(DateOnly businessDate, CurrencyCode currency, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(currency);
        ArgumentOutOfRangeException.ThrowIfLessThan(ordinal, 1);

        return FormattableString.Invariant($"PO-{businessDate:yyyyMMdd}-{currency}-{ordinal:D6}");
    }
}
