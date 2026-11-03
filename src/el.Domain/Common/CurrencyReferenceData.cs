namespace el.Domain.Common;

/// <summary>
/// Seeded currency reference data. Published versions are immutable: add a new version
/// instead of editing an existing entry. Persistence of this catalog follows in BE-007.
/// </summary>
public static class CurrencyReferenceData
{
    public const int InitialVersion = 1;

    public static CurrencyCode Usd { get; } = CurrencyCode.Parse("USD");

    public static CurrencyCode Jpy { get; } = CurrencyCode.Parse("JPY");

    public static CurrencyCode Kwd { get; } = CurrencyCode.Parse("KWD");

    public static CurrencyCatalog Catalog { get; } = CurrencyCatalog.Create(
    [
        new CurrencyDefinition(Usd, exponent: 2, version: InitialVersion),
        new CurrencyDefinition(Jpy, exponent: 0, version: InitialVersion),
        new CurrencyDefinition(Kwd, exponent: 3, version: InitialVersion),
    ]);
}
