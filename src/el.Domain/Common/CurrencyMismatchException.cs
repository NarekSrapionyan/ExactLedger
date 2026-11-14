namespace el.Domain.Common;

/// <summary>
/// Thrown when an operation combines or orders amounts in different currencies.
/// </summary>
public sealed class CurrencyMismatchException : Exception
{
    public CurrencyMismatchException(CurrencyCode expected, CurrencyCode actual)
        : base(FormattableString.Invariant(
            $"Expected an amount in {expected} but got {actual}; currencies are never converted implicitly."))
    {
        Expected = expected;
        Actual = actual;
    }

    public CurrencyCode Expected { get; }

    public CurrencyCode Actual { get; }
}
