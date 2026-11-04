using el.Domain.Common;

namespace el.Domain.Tests.Common;

public sealed class CurrencyCatalogTests
{
    private static readonly CurrencyCode Usd = CurrencyCode.Parse("USD");
    private static readonly CurrencyCode Jpy = CurrencyCode.Parse("JPY");
    private static readonly CurrencyCode Eur = CurrencyCode.Parse("EUR");

    [Fact]
    public void Resolve_returns_the_exact_requested_version()
    {
        var catalog = CurrencyCatalog.Create(
        [
            new CurrencyDefinition(Usd, exponent: 2, version: 1),
            new CurrencyDefinition(Usd, exponent: 3, version: 2),
        ]);

        Assert.Equal(2, catalog.Resolve(Usd, 1).Exponent);
        Assert.Equal(3, catalog.Resolve(Usd, 2).Exponent);
    }

    [Fact]
    public void Resolve_rejects_an_unknown_code()
    {
        var catalog = CurrencyCatalog.Create([new CurrencyDefinition(Usd, exponent: 2, version: 1)]);

        var exception = Assert.Throws<UnknownCurrencyException>(() => catalog.Resolve(Eur, 1));

        Assert.Equal(Eur, exception.Code);
        Assert.Equal(1, exception.Version);
        Assert.False(catalog.TryResolve(Eur, 1, out var definition));
        Assert.Null(definition);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    public void Resolve_does_not_fall_back_to_another_version(int version)
    {
        var catalog = CurrencyCatalog.Create([new CurrencyDefinition(Usd, exponent: 2, version: 1)]);

        Assert.Throws<UnknownCurrencyException>(() => catalog.Resolve(Usd, version));
        Assert.False(catalog.TryResolve(Usd, version, out _));
    }

    [Fact]
    public void Resolve_rejects_a_null_code()
    {
        var catalog = CurrencyCatalog.Create([new CurrencyDefinition(Usd, exponent: 2, version: 1)]);

        Assert.Throws<ArgumentNullException>(() => catalog.Resolve(null!, 1));
        Assert.Throws<ArgumentNullException>(() => catalog.TryResolve(null!, 1, out _));
    }

    [Fact]
    public void Create_rejects_a_conflicting_definition_for_the_same_code_and_version()
    {
        Assert.Throws<ArgumentException>(() => CurrencyCatalog.Create(
        [
            new CurrencyDefinition(Usd, exponent: 2, version: 1),
            new CurrencyDefinition(Usd, exponent: 3, version: 1),
        ]));
    }

    [Fact]
    public void Create_rejects_a_repeated_identical_definition()
    {
        Assert.Throws<ArgumentException>(() => CurrencyCatalog.Create(
        [
            new CurrencyDefinition(Usd, exponent: 2, version: 1),
            new CurrencyDefinition(Usd, exponent: 2, version: 1),
        ]));
    }

    [Fact]
    public void Create_rejects_null_input()
    {
        Assert.Throws<ArgumentNullException>(() => CurrencyCatalog.Create(null!));
        Assert.Throws<ArgumentException>(() => CurrencyCatalog.Create([null!]));
    }

    [Fact]
    public void Definitions_are_ordered_by_code_then_version()
    {
        var catalog = CurrencyCatalog.Create(
        [
            new CurrencyDefinition(Usd, exponent: 2, version: 2),
            new CurrencyDefinition(Jpy, exponent: 0, version: 1),
            new CurrencyDefinition(Usd, exponent: 2, version: 1),
        ]);

        Assert.Equal(
            new[] { ("JPY", 1), ("USD", 1), ("USD", 2) },
            catalog.Definitions.Select(definition => (definition.Code.Value, definition.Version)));
    }

    [Fact]
    public void Catalog_cannot_be_changed_after_creation()
    {
        var source = new List<CurrencyDefinition> { new(Usd, exponent: 2, version: 1) };
        var catalog = CurrencyCatalog.Create(source);

        source.Add(new CurrencyDefinition(Jpy, exponent: 0, version: 1));
        source[0] = new CurrencyDefinition(Usd, exponent: 3, version: 1);

        Assert.Single(catalog.Definitions);
        Assert.Equal(2, catalog.Resolve(Usd, 1).Exponent);
        Assert.False(catalog.TryResolve(Jpy, 1, out _));
        Assert.Throws<NotSupportedException>(
            () => ((IList<CurrencyDefinition>)catalog.Definitions)[0] = new CurrencyDefinition(Usd, exponent: 3, version: 1));
    }
}
