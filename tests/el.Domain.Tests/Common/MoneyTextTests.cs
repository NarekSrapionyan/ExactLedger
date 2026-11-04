using System.Globalization;
using el.Domain.Common;

namespace el.Domain.Tests.Common;

public sealed class MoneyTextTests
{
    private static CurrencyDefinition Definition(string code) =>
        CurrencyReferenceData.Catalog.Resolve(CurrencyCode.Parse(code), CurrencyReferenceData.InitialVersion);

    [Theory]
    [InlineData("USD", "250.00", 25000)]
    [InlineData("JPY", "1000", 1000)]
    [InlineData("KWD", "1.234", 1234)]
    [InlineData("USD", "0.00", 0)]
    [InlineData("USD", "0.01", 1)]
    [InlineData("USD", "-0.01", -1)]
    [InlineData("USD", "-250.00", -25000)]
    [InlineData("USD", "999999999.99", Money.MaxMinorUnits)]
    [InlineData("USD", "-999999999.99", Money.MinMinorUnits)]
    [InlineData("JPY", "0", 0)]
    [InlineData("JPY", "-1", -1)]
    [InlineData("JPY", "99999999999", Money.MaxMinorUnits)]
    [InlineData("JPY", "-99999999999", Money.MinMinorUnits)]
    [InlineData("KWD", "0.000", 0)]
    [InlineData("KWD", "0.001", 1)]
    [InlineData("KWD", "99999999.999", Money.MaxMinorUnits)]
    [InlineData("KWD", "-99999999.999", Money.MinMinorUnits)]
    public void Parses_and_formats_canonical_amounts_exactly(string code, string text, long minorUnits)
    {
        var currency = Definition(code);
        var expected = Money.FromMinorUnits(minorUnits, currency.Code);

        Assert.Equal(expected, MoneyText.Parse(text, currency));
        Assert.Equal(text, MoneyText.Format(expected, currency));

        Assert.True(MoneyText.TryParse(text, currency, out var parsed, out var error));
        Assert.Equal(expected, parsed);
        Assert.Equal(MoneyTextError.None, error);
    }

    [Theory]
    [InlineData("USD", "", MoneyTextError.Empty)]
    [InlineData("USD", " 250.00", MoneyTextError.InvalidFormat)]
    [InlineData("USD", "250.00 ", MoneyTextError.InvalidFormat)]
    [InlineData("USD", "+250.00", MoneyTextError.InvalidFormat)]
    [InlineData("USD", "--250.00", MoneyTextError.InvalidFormat)]
    [InlineData("USD", "-", MoneyTextError.InvalidFormat)]
    [InlineData("USD", "250.00-", MoneyTextError.InvalidFormat)]
    [InlineData("USD", "(250.00)", MoneyTextError.InvalidFormat)]
    [InlineData("USD", "2.50e2", MoneyTextError.InvalidFormat)]
    [InlineData("USD", "2.50E+2", MoneyTextError.InvalidFormat)]
    [InlineData("USD", "1,000.00", MoneyTextError.InvalidFormat)]
    [InlineData("USD", "1 000.00", MoneyTextError.InvalidFormat)]
    [InlineData("USD", "1_000.00", MoneyTextError.InvalidFormat)]
    [InlineData("USD", "250,00", MoneyTextError.InvalidFormat)]
    [InlineData("USD", ".50", MoneyTextError.InvalidFormat)]
    [InlineData("USD", "-.50", MoneyTextError.InvalidFormat)]
    [InlineData("USD", "250.", MoneyTextError.InvalidFormat)]
    [InlineData("USD", "250.00.00", MoneyTextError.InvalidFormat)]
    [InlineData("USD", "$250.00", MoneyTextError.InvalidFormat)]
    [InlineData("USD", "250.00USD", MoneyTextError.InvalidFormat)]
    [InlineData("USD", "0x10", MoneyTextError.InvalidFormat)]
    [InlineData("USD", "NaN", MoneyTextError.InvalidFormat)]
    [InlineData("USD", "Infinity", MoneyTextError.InvalidFormat)]
    [InlineData("USD", "٢٥٠.٠٠", MoneyTextError.InvalidFormat)]
    [InlineData("USD", "２５０.００", MoneyTextError.InvalidFormat)]
    [InlineData("USD", "−250.00", MoneyTextError.InvalidFormat)]
    [InlineData("JPY", "1000.", MoneyTextError.InvalidFormat)]
    [InlineData("USD", "00.00", MoneyTextError.LeadingZeros)]
    [InlineData("USD", "01.00", MoneyTextError.LeadingZeros)]
    [InlineData("USD", "-01.00", MoneyTextError.LeadingZeros)]
    [InlineData("JPY", "007", MoneyTextError.LeadingZeros)]
    [InlineData("USD", "-0.00", MoneyTextError.NegativeZero)]
    [InlineData("JPY", "-0", MoneyTextError.NegativeZero)]
    [InlineData("KWD", "-0.000", MoneyTextError.NegativeZero)]
    [InlineData("USD", "250", MoneyTextError.MissingPrecision)]
    [InlineData("USD", "250.0", MoneyTextError.MissingPrecision)]
    [InlineData("KWD", "1.23", MoneyTextError.MissingPrecision)]
    [InlineData("KWD", "1", MoneyTextError.MissingPrecision)]
    [InlineData("USD", "2.345", MoneyTextError.ExcessPrecision)]
    [InlineData("USD", "250.000", MoneyTextError.ExcessPrecision)]
    [InlineData("USD", "0.001", MoneyTextError.ExcessPrecision)]
    [InlineData("JPY", "1000.0", MoneyTextError.ExcessPrecision)]
    [InlineData("JPY", "1000.00", MoneyTextError.ExcessPrecision)]
    [InlineData("KWD", "1.2340", MoneyTextError.ExcessPrecision)]
    [InlineData("USD", "1000000000.00", MoneyTextError.OutOfRange)]
    [InlineData("USD", "-1000000000.00", MoneyTextError.OutOfRange)]
    [InlineData("JPY", "100000000000", MoneyTextError.OutOfRange)]
    [InlineData("KWD", "100000000.000", MoneyTextError.OutOfRange)]
    [InlineData("USD", "9223372036854775807.00", MoneyTextError.OutOfRange)]
    [InlineData("USD", "99999999999999999999999999999999999999.00", MoneyTextError.OutOfRange)]
    public void Rejects_non_canonical_text_without_rounding(string code, string text, MoneyTextError expected)
    {
        var currency = Definition(code);

        var exception = Assert.Throws<MoneyFormatException>(() => MoneyText.Parse(text, currency));
        Assert.Equal(expected, exception.Error);
        Assert.Equal(currency.Code, exception.Currency);

        Assert.False(MoneyText.TryParse(text, currency, out var money, out var error));
        Assert.Null(money);
        Assert.Equal(expected, error);
    }

    [Fact]
    public void Rejects_null_text_as_empty()
    {
        var currency = Definition("USD");

        var exception = Assert.Throws<MoneyFormatException>(() => MoneyText.Parse(null!, currency));
        Assert.Equal(MoneyTextError.Empty, exception.Error);
        Assert.False(MoneyText.TryParse(null, currency, out _, out var error));
        Assert.Equal(MoneyTextError.Empty, error);
    }

    [Fact]
    public void Error_messages_do_not_echo_the_input()
    {
        var exception = Assert.Throws<MoneyFormatException>(() => MoneyText.Parse("12345.678", Definition("USD")));

        Assert.Equal("A USD amount must have exactly 2 decimal places.", exception.Message);
        Assert.DoesNotContain("12345", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_never_writes_a_negative_zero()
    {
        var zero = Money.Zero(CurrencyReferenceData.Usd).Negate();

        Assert.Equal("0.00", MoneyText.Format(zero, Definition("USD")));
    }

    [Fact]
    public void Format_rejects_a_different_currency()
    {
        var jpy = Money.FromMinorUnits(1000, CurrencyReferenceData.Jpy);

        var exception = Assert.Throws<CurrencyMismatchException>(() => MoneyText.Format(jpy, Definition("USD")));
        Assert.Equal(CurrencyReferenceData.Usd, exception.Expected);
        Assert.Equal(CurrencyReferenceData.Jpy, exception.Actual);
    }

    [Fact]
    public void Rejects_null_arguments()
    {
        var usd = Definition("USD");

        Assert.Throws<ArgumentNullException>(() => MoneyText.Parse("1.00", null!));
        Assert.Throws<ArgumentNullException>(() => MoneyText.TryParse("1.00", null!, out _, out _));
        Assert.Throws<ArgumentNullException>(() => MoneyText.Format(null!, usd));
        Assert.Throws<ArgumentNullException>(() => MoneyText.Format(Money.Zero(usd.Code), null!));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("tr-TR")]
    [InlineData("ar-SA")]
    [InlineData("fa-IR")]
    [InlineData("hy-AM")]
    public void Parsing_and_formatting_ignore_the_current_culture(string cultureName)
    {
        var original = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        var culture = CultureInfo.GetCultureInfo(cultureName);

        try
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;

            var usd = Definition("USD");
            var money = Money.FromMinorUnits(-123456789, usd.Code);

            Assert.Equal("-1234567.89", MoneyText.Format(money, usd));
            Assert.Equal(money, MoneyText.Parse("-1234567.89", usd));
            Assert.False(MoneyText.TryParse("-1234567,89", usd, out _, out _));
            Assert.False(MoneyText.TryParse("-1.234.567,89", usd, out _, out _));
        }
        finally
        {
            CultureInfo.CurrentCulture = original.CurrentCulture;
            CultureInfo.CurrentUICulture = original.CurrentUICulture;
        }
    }

    [Theory]
    [InlineData("USD")]
    [InlineData("JPY")]
    [InlineData("KWD")]
    public void Format_then_parse_round_trips_across_the_range(string code)
    {
        var currency = Definition(code);
        var random = new Random(20261103);
        var values = new List<long> { 0, 1, -1, 9, -9, 10, -10, 999, 1000, Money.MaxMinorUnits, Money.MinMinorUnits };

        for (var i = 0; i < 1000; i++)
        {
            values.Add(random.NextInt64(Money.MinMinorUnits, Money.MaxMinorUnits + 1));
        }

        foreach (var minorUnits in values)
        {
            var money = Money.FromMinorUnits(minorUnits, currency.Code);

            var text = MoneyText.Format(money, currency);

            Assert.Equal(money, MoneyText.Parse(text, currency));
        }
    }
}
