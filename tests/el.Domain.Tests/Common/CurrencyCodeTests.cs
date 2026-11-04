using el.Domain.Common;

namespace el.Domain.Tests.Common;

public sealed class CurrencyCodeTests
{
    [Theory]
    [InlineData("USD")]
    [InlineData("JPY")]
    [InlineData("KWD")]
    [InlineData("EUR")]
    public void Parse_accepts_three_uppercase_ascii_letters(string value)
    {
        var code = CurrencyCode.Parse(value);

        Assert.Equal(value, code.Value);
        Assert.Equal(value, code.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("US")]
    [InlineData("USDX")]
    [InlineData("usd")]
    [InlineData("Usd")]
    [InlineData(" USD")]
    [InlineData("USD ")]
    [InlineData("U5D")]
    [InlineData("U-D")]
    [InlineData("ÜSD")]
    [InlineData("ＵＳＤ")]
    public void Parse_and_TryParse_reject_anything_else(string? value)
    {
        Assert.Throws<ArgumentException>(() => CurrencyCode.Parse(value!));

        Assert.False(CurrencyCode.TryParse(value, out var code));
        Assert.Null(code);
    }

    [Fact]
    public void Codes_are_equal_by_value()
    {
        var first = CurrencyCode.Parse("USD");
        var second = CurrencyCode.Parse("USD");

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, CurrencyCode.Parse("JPY"));
    }
}
