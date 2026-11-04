namespace el.Domain.Common;

/// <summary>
/// Immutable reference definition of a currency at one reference-data version.
/// The exponent is the number of minor-unit digits (USD 2, JPY 0, KWD 3).
/// A changed exponent is published as a new version; existing versions never change.
/// </summary>
public sealed record CurrencyDefinition
{
    public const int MinExponent = 0;
    public const int MaxExponent = 4;
    public const int MinVersion = 1;

    public CurrencyDefinition(CurrencyCode code, int exponent, int version)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentOutOfRangeException.ThrowIfLessThan(exponent, MinExponent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(exponent, MaxExponent);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, MinVersion);

        Code = code;
        Exponent = exponent;
        Version = version;
    }

    public CurrencyCode Code { get; }

    public int Exponent { get; }

    public int Version { get; }
}
