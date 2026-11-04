using System.Globalization;
using System.Numerics;
using el.Domain.Common;

namespace el.Domain.Tests.Common;

public sealed class ScaledPercentageTests
{
    // Expected products can exceed long, so they are written as text and parsed invariantly.
    private static BigInteger Big(string digits) => BigInteger.Parse(digits, CultureInfo.InvariantCulture);

    private static ScaledPercentage Rate(long rateUnits) => ScaledPercentage.FromRateUnits(rateUnits);

    [Fact]
    public void Constants_define_six_fractional_digits_over_a_denominator_of_100_million()
    {
        Assert.Equal(6, ScaledPercentage.FractionalDigits);
        Assert.Equal(1_000_000L, ScaledPercentage.Scale);
        Assert.Equal(100_000_000L, ScaledPercentage.Denominator);
        Assert.Equal(100_000_000L, ScaledPercentage.MaxRateUnits);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2_900_000)]
    [InlineData(12_000_000)]
    [InlineData(99_999_999)]
    [InlineData(100_000_000)]
    public void FromRateUnits_accepts_values_from_0_to_100_percent(long rateUnits)
    {
        Assert.Equal(rateUnits, Rate(rateUnits).RateUnits);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100_000_001)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void FromRateUnits_rejects_values_outside_0_to_100_percent(long rateUnits)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Rate(rateUnits));
    }

    [Fact]
    public void Equality_uses_the_rate_units()
    {
        var first = Rate(2_900_000);
        var second = Rate(2_900_000);

        var equalByOperator = first == second;
        var differentByOperator = first != Rate(2_900_001);

        Assert.Equal(first, second);
        Assert.True(equalByOperator);
        Assert.True(differentByOperator);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, Rate(2_900_001));
    }

    [Fact]
    public void ToString_shows_the_rate_units()
    {
        Assert.Equal("2900000 rate units", Rate(2_900_000).ToString());
    }

    [Fact]
    public void A_rate_is_not_money_and_has_no_currency()
    {
        var properties = typeof(ScaledPercentage).GetProperties();
        var multiply = typeof(ScaledPercentage).GetMethod(nameof(ScaledPercentage.Multiply))!;

        Assert.DoesNotContain(properties, p => p.PropertyType == typeof(Money) || p.PropertyType == typeof(CurrencyCode));
        Assert.Equal(typeof(long), Assert.Single(multiply.GetParameters()).ParameterType);
    }

    [Theory]
    [InlineData(25000, 2_900_000, "72500000000")]           // Example A processor percentage
    [InlineData(25000, 12_000_000, "300000000000")]         // Example A commission
    [InlineData(25000, 3_000_000, "75000000000")]           // Example A affiliate
    [InlineData(20495, 10_000_000, "204950000000")]         // Example A escrow (a midpoint)
    [InlineData(1, 1, "1")]
    [InlineData(25000, 100_000_000, "2500000000000")]
    public void Multiply_returns_the_exact_numerator(long baseMinor, long rateUnits, string numerator)
    {
        var product = Rate(rateUnits).Multiply(baseMinor);

        Assert.Equal(Big(numerator), product.Numerator);
        Assert.Equal(new BigInteger(100_000_000), product.Denominator);
    }

    [Theory]
    [InlineData(0, 2_900_000, "0")]
    [InlineData(25000, 0, "0")]
    [InlineData(0, 0, "0")]
    [InlineData(-25000, 2_900_000, "-72500000000")]
    [InlineData(-1, 1, "-1")]
    [InlineData(-25000, 0, "0")]
    public void Multiply_handles_zero_and_negative_bases(long baseMinor, long rateUnits, string numerator)
    {
        var product = Rate(rateUnits).Multiply(baseMinor);

        Assert.Equal(Big(numerator), product.Numerator);
        Assert.Equal(new BigInteger(100_000_000), product.Denominator);
    }

    [Theory]
    [InlineData(Money.MaxMinorUnits, 100_000_000, "9999999999900000000")]
    [InlineData(Money.MinMinorUnits, 100_000_000, "-9999999999900000000")]
    [InlineData(long.MaxValue, 100_000_000, "922337203685477580700000000")]
    [InlineData(long.MinValue, 100_000_000, "-922337203685477580800000000")]
    public void Multiply_is_exact_beyond_the_64_bit_range(long baseMinor, long rateUnits, string numerator)
    {
        var product = Rate(rateUnits).Multiply(baseMinor);

        Assert.Equal(Big(numerator), product.Numerator);
        Assert.Equal(new BigInteger(100_000_000), product.Denominator);
    }

    [Fact]
    public void The_largest_money_at_100_percent_does_not_fit_in_a_long()
    {
        long maxMoney = Money.MaxMinorUnits;
        long fullRate = ScaledPercentage.MaxRateUnits;

        var product = Rate(fullRate).Multiply(maxMoney);

        Assert.True(product.Numerator > long.MaxValue);
        Assert.Throws<OverflowException>(() => checked(maxMoney * fullRate));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2_900_000)]
    [InlineData(50_000_000)]
    [InlineData(100_000_000)]
    public void Multiply_never_reduces_or_changes_the_denominator(long rateUnits)
    {
        Assert.Equal(new BigInteger(100_000_000), Rate(rateUnits).Multiply(25000).Denominator);
        Assert.Equal(new BigInteger(100_000_000), Rate(rateUnits).Multiply(-25000).Denominator);
        Assert.Equal(new BigInteger(100_000_000), Rate(rateUnits).Multiply(0).Denominator);
    }

    [Fact]
    public void The_fraction_is_the_exact_unrounded_value()
    {
        var exact = Rate(2_900_000).Multiply(25000);
        var midpoint = Rate(10_000_000).Multiply(20495);

        var exactQuotient = BigInteger.DivRem(exact.Numerator, exact.Denominator, out var exactRemainder);
        var midpointQuotient = BigInteger.DivRem(midpoint.Numerator, midpoint.Denominator, out var midpointRemainder);

        // 2.9% of 250.00 USD is exactly 7.25.
        Assert.Equal(new BigInteger(725), exactQuotient);
        Assert.Equal(BigInteger.Zero, exactRemainder);

        // 10% of 204.95 USD is 20.495: quotient 2049 and remainder exactly half of the denominator.
        Assert.Equal(new BigInteger(2049), midpointQuotient);
        Assert.Equal(new BigInteger(50_000_000), midpointRemainder);
    }

    [Fact]
    public void Negating_the_base_negates_only_the_numerator()
    {
        var random = new Random(20261104);

        for (var i = 0; i < 1000; i++)
        {
            var rate = Rate(random.NextInt64(0, ScaledPercentage.MaxRateUnits + 1));
            var baseMinor = random.NextInt64(Money.MinMinorUnits, Money.MaxMinorUnits + 1);

            var positive = rate.Multiply(baseMinor);
            var negative = rate.Multiply(-baseMinor);

            Assert.Equal(-positive.Numerator, negative.Numerator);
            Assert.Equal(positive.Denominator, negative.Denominator);
        }
    }
}
