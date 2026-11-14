using el.Domain.Common;

namespace el.Domain.Tests.Common;

public sealed class CurrencyDefinitionTests
{
    private static readonly CurrencyCode Usd = CurrencyCode.Parse("USD");

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, int.MaxValue)]
    public void Accepts_exponents_zero_to_four_and_positive_versions(int exponent, int version)
    {
        var definition = new CurrencyDefinition(Usd, exponent, version);

        Assert.Equal(Usd, definition.Code);
        Assert.Equal(exponent, definition.Exponent);
        Assert.Equal(version, definition.Version);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Rejects_invalid_exponents(int exponent)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CurrencyDefinition(Usd, exponent, version: 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Rejects_versions_below_one(int version)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CurrencyDefinition(Usd, exponent: 2, version));
    }

    [Fact]
    public void Rejects_a_null_code()
    {
        Assert.Throws<ArgumentNullException>(() => new CurrencyDefinition(null!, exponent: 2, version: 1));
    }

    [Fact]
    public void Definitions_are_equal_by_code_exponent_and_version()
    {
        var definition = new CurrencyDefinition(Usd, exponent: 2, version: 1);

        Assert.Equal(definition, new CurrencyDefinition(CurrencyCode.Parse("USD"), exponent: 2, version: 1));
        Assert.NotEqual(definition, new CurrencyDefinition(Usd, exponent: 3, version: 1));
        Assert.NotEqual(definition, new CurrencyDefinition(Usd, exponent: 2, version: 2));
        Assert.NotEqual(definition, new CurrencyDefinition(CurrencyCode.Parse("JPY"), exponent: 2, version: 1));
    }
}
