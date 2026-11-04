using System.Globalization;
using el.Domain.Common;

namespace el.Domain.Tests.Common;

public sealed class PercentageTextTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("0.0", 0)]
    [InlineData("0.000000", 0)]
    [InlineData("0.000001", 1)]
    [InlineData("0.5", 500_000)]
    [InlineData("2.9", 2_900_000)]
    [InlineData("2.900000", 2_900_000)]
    [InlineData("3", 3_000_000)]
    [InlineData("10", 10_000_000)]
    [InlineData("12", 12_000_000)]
    [InlineData("12.0", 12_000_000)]
    [InlineData("99.999999", 99_999_999)]
    [InlineData("100", 100_000_000)]
    [InlineData("100.0", 100_000_000)]
    [InlineData("100.000000", 100_000_000)]
    public void Parses_valid_percentages_exactly(string text, long rateUnits)
    {
        Assert.True(PercentageText.TryParse(text, out var percentage, out var error));
        Assert.Equal(ScaledPercentage.FromRateUnits(rateUnits), percentage);
        Assert.Equal(PercentageTextError.None, error);
    }

    [Theory]
    [InlineData("", PercentageTextError.Empty)]
    [InlineData("-1", PercentageTextError.InvalidFormat)]
    [InlineData("-0", PercentageTextError.InvalidFormat)]
    [InlineData("+1", PercentageTextError.InvalidFormat)]
    [InlineData("-", PercentageTextError.InvalidFormat)]
    [InlineData(" 2.9", PercentageTextError.InvalidFormat)]
    [InlineData("2.9 ", PercentageTextError.InvalidFormat)]
    [InlineData("2,9", PercentageTextError.InvalidFormat)]
    [InlineData("1,000", PercentageTextError.InvalidFormat)]
    [InlineData("2.9%", PercentageTextError.InvalidFormat)]
    [InlineData("%", PercentageTextError.InvalidFormat)]
    [InlineData("1e1", PercentageTextError.InvalidFormat)]
    [InlineData("1E+1", PercentageTextError.InvalidFormat)]
    [InlineData(".5", PercentageTextError.InvalidFormat)]
    [InlineData("5.", PercentageTextError.InvalidFormat)]
    [InlineData(".", PercentageTextError.InvalidFormat)]
    [InlineData("1.2.3", PercentageTextError.InvalidFormat)]
    [InlineData("1_0", PercentageTextError.InvalidFormat)]
    [InlineData("0x10", PercentageTextError.InvalidFormat)]
    [InlineData("NaN", PercentageTextError.InvalidFormat)]
    [InlineData("Infinity", PercentageTextError.InvalidFormat)]
    [InlineData("٢.٩", PercentageTextError.InvalidFormat)]
    [InlineData("２.９", PercentageTextError.InvalidFormat)]
    [InlineData("−1", PercentageTextError.InvalidFormat)]
    [InlineData("00", PercentageTextError.LeadingZeros)]
    [InlineData("02.9", PercentageTextError.LeadingZeros)]
    [InlineData("007", PercentageTextError.LeadingZeros)]
    [InlineData("0.0000001", PercentageTextError.ExcessPrecision)]
    [InlineData("2.9000001", PercentageTextError.ExcessPrecision)]
    [InlineData("100.0000000", PercentageTextError.ExcessPrecision)]
    [InlineData("101.1234567", PercentageTextError.ExcessPrecision)]
    [InlineData("100.000001", PercentageTextError.OutOfRange)]
    [InlineData("101", PercentageTextError.OutOfRange)]
    [InlineData("999", PercentageTextError.OutOfRange)]
    [InlineData("1000", PercentageTextError.OutOfRange)]
    [InlineData("9223372036854775807", PercentageTextError.OutOfRange)]
    [InlineData("99999999999999999999999999999999999999", PercentageTextError.OutOfRange)]
    public void Rejects_invalid_text_without_rounding(string text, PercentageTextError expected)
    {
        Assert.False(PercentageText.TryParse(text, out var percentage, out var error));
        Assert.Null(percentage);
        Assert.Equal(expected, error);
    }

    [Fact]
    public void Rejects_null_text_as_empty()
    {
        Assert.False(PercentageText.TryParse(null, out var percentage, out var error));
        Assert.Null(percentage);
        Assert.Equal(PercentageTextError.Empty, error);
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("tr-TR")]
    [InlineData("ar-SA")]
    [InlineData("fa-IR")]
    [InlineData("hy-AM")]
    public void Parsing_ignores_the_current_culture(string cultureName)
    {
        var original = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        var culture = CultureInfo.GetCultureInfo(cultureName);

        try
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;

            Assert.True(PercentageText.TryParse("2.9", out var percentage, out _));
            Assert.Equal(2_900_000, percentage.RateUnits);
            Assert.True(PercentageText.TryParse("12.345678", out percentage, out _));
            Assert.Equal(12_345_678, percentage.RateUnits);
            Assert.False(PercentageText.TryParse("2,9", out _, out _));
            Assert.False(PercentageText.TryParse("12,345678", out _, out _));
            Assert.False(PercentageText.TryParse("1.000,5", out _, out _));
        }
        finally
        {
            CultureInfo.CurrentCulture = original.CurrentCulture;
            CultureInfo.CurrentUICulture = original.CurrentUICulture;
        }
    }

    [Fact]
    public void Every_sampled_rate_unit_value_round_trips_through_its_six_digit_text()
    {
        var random = new Random(20261104);
        var values = new List<long> { 0, 1, 9, 10, 999_999, 1_000_000, 2_900_000, 99_999_999, 100_000_000 };

        for (var i = 0; i < 1000; i++)
        {
            values.Add(random.NextInt64(0, ScaledPercentage.MaxRateUnits + 1));
        }

        foreach (var rateUnits in values)
        {
            var whole = rateUnits / ScaledPercentage.Scale;
            var fraction = rateUnits % ScaledPercentage.Scale;
            var text = FormattableString.Invariant($"{whole}.{fraction:D6}");

            Assert.True(PercentageText.TryParse(text, out var percentage, out _));
            Assert.Equal(rateUnits, percentage.RateUnits);
        }
    }
}
