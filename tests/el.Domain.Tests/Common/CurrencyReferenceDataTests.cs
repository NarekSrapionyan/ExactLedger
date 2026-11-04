using el.Domain.Common;

namespace el.Domain.Tests.Common;

public sealed class CurrencyReferenceDataTests
{
    [Theory]
    [InlineData("USD", 2)]
    [InlineData("JPY", 0)]
    [InlineData("KWD", 3)]
    public void Seeds_the_initial_currency_exponents(string code, int exponent)
    {
        var definition = CurrencyReferenceData.Catalog.Resolve(
            CurrencyCode.Parse(code),
            CurrencyReferenceData.InitialVersion);

        Assert.Equal(exponent, definition.Exponent);
        Assert.Equal(1, definition.Version);
    }

    [Fact]
    public void Seeds_only_the_initial_currencies()
    {
        Assert.Equal(
            new[] { ("JPY", 0, 1), ("KWD", 3, 1), ("USD", 2, 1) },
            CurrencyReferenceData.Catalog.Definitions.Select(
                definition => (definition.Code.Value, definition.Exponent, definition.Version)));
    }

    [Fact]
    public void Exposes_the_seeded_codes()
    {
        Assert.Equal("USD", CurrencyReferenceData.Usd.Value);
        Assert.Equal("JPY", CurrencyReferenceData.Jpy.Value);
        Assert.Equal("KWD", CurrencyReferenceData.Kwd.Value);
    }
}
