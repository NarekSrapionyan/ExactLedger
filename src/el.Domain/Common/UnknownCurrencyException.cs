namespace el.Domain.Common;

/// <summary>
/// Thrown when a currency code and version pair is not defined in the reference catalog.
/// </summary>
public sealed class UnknownCurrencyException : Exception
{
    public UnknownCurrencyException(CurrencyCode code, int version)
        : base(FormattableString.Invariant(
            $"Currency {code} version {version} is not defined in the currency reference catalog."))
    {
        Code = code;
        Version = version;
    }

    public CurrencyCode Code { get; }

    public int Version { get; }
}
