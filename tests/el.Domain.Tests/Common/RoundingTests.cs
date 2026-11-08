using System.Globalization;
using System.Numerics;
using System.Reflection;
using el.Domain.Common;

namespace el.Domain.Tests.Common;

public sealed class RoundingTests
{
    private static readonly CurrencyCode Usd = CurrencyReferenceData.Usd;

    private static CurrencyDefinition Definition(string code) =>
        CurrencyReferenceData.Catalog.Resolve(CurrencyCode.Parse(code), CurrencyReferenceData.InitialVersion);

    // One fee or escrow point: exact percentage of a Money amount, rounded with an explicit mode.
    private static Money Percentage(Money amount, long rateUnits, RoundingMode mode) =>
        Rounding.Round(ScaledPercentage.FromRateUnits(rateUnits).Multiply(amount.MinorUnits), mode)
            .ToMoney(amount.Currency);

    // ---- required USD examples (AC4) ----

    [Theory]
    [InlineData(RoundingMode.HalfEven, 2345, 234, "2.34")]
    [InlineData(RoundingMode.HalfAwayFromZero, 2345, 235, "2.35")]
    [InlineData(RoundingMode.TowardZero, 2345, 234, "2.34")]
    [InlineData(RoundingMode.HalfEven, -2345, -234, "-2.34")]
    [InlineData(RoundingMode.HalfAwayFromZero, -2345, -235, "-2.35")]
    [InlineData(RoundingMode.TowardZero, -2345, -234, "-2.34")]
    public void Usd_2_345_rounds_to_two_decimal_places_from_an_exact_fraction(
        RoundingMode mode, long numerator, long expectedMinorUnits, string expectedText)
    {
        // USD 2.345 is 234.5 minor units, the exact fraction 2345/10.
        var usd = Definition("USD");

        var money = Rounding.Round(numerator, 10, mode).ToMoney(usd.Code);

        Assert.Equal(expectedMinorUnits, money.MinorUnits);
        Assert.Equal(expectedText, MoneyText.Format(money, usd));
    }

    [Theory]
    [InlineData(RoundingMode.HalfEven, 23450, 234, "2.34")]
    [InlineData(RoundingMode.HalfAwayFromZero, 23450, 235, "2.35")]
    [InlineData(RoundingMode.HalfEven, -23450, -234, "-2.34")]
    [InlineData(RoundingMode.HalfAwayFromZero, -23450, -235, "-2.35")]
    public void Usd_2_345_rounds_to_two_decimal_places_from_a_percentage_product(
        RoundingMode mode, long baseMinorUnits, long expectedMinorUnits, string expectedText)
    {
        // 1% of USD 234.50 is USD 2.345.
        var usd = Definition("USD");
        var product = ScaledPercentage.FromRateUnits(1_000_000).Multiply(baseMinorUnits);

        var money = Rounding.Round(product, mode).ToMoney(usd.Code);

        Assert.Equal(new BigInteger(baseMinorUnits) * 1_000_000, product.Numerator);
        Assert.Equal(expectedMinorUnits, money.MinorUnits);
        Assert.Equal(expectedText, MoneyText.Format(money, usd));
    }

    [Theory]
    [InlineData("2.345")]
    [InlineData("-2.345")]
    public void Default_money_parsing_stays_strict_and_never_rounds(string text)
    {
        var usd = Definition("USD");

        Assert.False(MoneyText.TryParse(text, usd, out var money, out var error));
        Assert.Null(money);
        Assert.Equal(MoneyTextError.ExcessPrecision, error);
        Assert.Equal(MoneyTextError.ExcessPrecision, Assert.Throws<MoneyFormatException>(() => MoneyText.Parse(text, usd)).Error);
    }

    [Fact]
    public void Money_text_has_no_rounding_mode_parameter()
    {
        var parameters = typeof(MoneyText)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SelectMany(method => method.GetParameters());

        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(RoundingMode));
    }

    // ---- explicit mode, invalid arguments (AC5) ----

    [Fact]
    public void Every_public_round_method_requires_an_explicit_mode()
    {
        var methods = typeof(Rounding).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);

        Assert.NotEmpty(methods);

        foreach (var method in methods)
        {
            var mode = Assert.Single(method.GetParameters(), parameter => parameter.ParameterType == typeof(RoundingMode));

            Assert.False(mode.HasDefaultValue);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100_000_000)]
    [InlineData(long.MinValue)]
    public void Round_rejects_a_denominator_that_is_not_positive(long denominator)
    {
        foreach (var mode in Enum.GetValues<RoundingMode>())
        {
            var exception = Assert.Throws<ArgumentOutOfRangeException>(() => Rounding.Round(25000, denominator, mode));

            Assert.Equal("denominator", exception.ParamName);
        }
    }

    [Fact]
    public void Round_rejects_a_product_with_an_invalid_denominator()
    {
        var zero = new PercentageProduct(1, 0);
        var negative = new PercentageProduct(1, -100_000_000);

        Assert.Throws<ArgumentOutOfRangeException>(() => Rounding.Round(zero, RoundingMode.HalfEven));
        Assert.Throws<ArgumentOutOfRangeException>(() => Rounding.Round(negative, RoundingMode.TowardZero));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void Round_rejects_a_mode_that_is_not_defined_even_when_nothing_needs_rounding(int value)
    {
        var mode = (RoundingMode)value;

        // 10/5 is exact and 0/1 is zero; the mode is still validated.
        Assert.Equal("mode", Assert.Throws<ArgumentOutOfRangeException>(() => Rounding.Round(10, 5, mode)).ParamName);
        Assert.Equal("mode", Assert.Throws<ArgumentOutOfRangeException>(() => Rounding.Round(0, 1, mode)).ParamName);
        Assert.Equal("mode", Assert.Throws<ArgumentOutOfRangeException>(() => Rounding.Round(7, 2, mode)).ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => Rounding.Round(new PercentageProduct(7, 2), mode));
    }

    [Fact]
    public void Round_rejects_a_null_product()
    {
        Assert.Throws<ArgumentNullException>(() => Rounding.Round(null!, RoundingMode.HalfEven));
    }

    // ---- trace contract ----

    [Fact]
    public void Result_exposes_exact_input_policy_working_values_and_output()
    {
        var result = Rounding.Round(-2345, 10, RoundingMode.HalfAwayFromZero);

        Assert.Equal(new BigInteger(-2345), result.Numerator);
        Assert.Equal(new BigInteger(10), result.Denominator);
        Assert.Equal(RoundingPolicy.For(RoundingMode.HalfAwayFromZero), result.Policy);
        Assert.Equal("HalfAwayFromZero", result.Policy.Name);
        Assert.Equal(1, result.Policy.Version);
        Assert.Equal(new BigInteger(234), result.AbsoluteQuotient);
        Assert.Equal(new BigInteger(5), result.AbsoluteRemainder);
        Assert.True(result.MagnitudeIncremented);
        Assert.Equal(new BigInteger(-235), result.Value);
    }

    [Fact]
    public void Result_records_when_the_magnitude_was_not_incremented()
    {
        var halfEven = Rounding.Round(-2345, 10, RoundingMode.HalfEven);
        var exact = Rounding.Round(72_500_000_000, 100_000_000, RoundingMode.HalfEven);
        var truncated = Rounding.Round(2346, 10, RoundingMode.TowardZero);

        Assert.False(halfEven.MagnitudeIncremented);
        Assert.Equal(new BigInteger(-234), halfEven.Value);

        Assert.False(exact.MagnitudeIncremented);
        Assert.Equal(BigInteger.Zero, exact.AbsoluteRemainder);
        Assert.Equal(new BigInteger(725), exact.Value);

        Assert.False(truncated.MagnitudeIncremented);
        Assert.Equal(new BigInteger(6), truncated.AbsoluteRemainder);
        Assert.Equal(new BigInteger(234), truncated.Value);
    }

    [Fact]
    public void Result_reconciles_numerator_quotient_remainder_and_value()
    {
        var result = Rounding.Round(-204_950_000_000, 100_000_000, RoundingMode.HalfEven);

        Assert.Equal(
            BigInteger.Abs(result.Numerator),
            (result.AbsoluteQuotient * result.Denominator) + result.AbsoluteRemainder);
        Assert.Equal(new BigInteger(2049), result.AbsoluteQuotient);
        Assert.Equal(new BigInteger(50_000_000), result.AbsoluteRemainder);
        Assert.True(result.MagnitudeIncremented);
        Assert.Equal(new BigInteger(-2050), result.Value);
    }

    [Fact]
    public void Rounding_is_deterministic_and_results_compare_by_value()
    {
        var first = Rounding.Round(-2345, 10, RoundingMode.HalfEven);
        var second = Rounding.Round(-2345, 10, RoundingMode.HalfEven);

        var equalByOperator = first == second;

        Assert.Equal(first, second);
        Assert.True(equalByOperator);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, Rounding.Round(-2345, 10, RoundingMode.HalfAwayFromZero));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("tr-TR")]
    [InlineData("ar-SA")]
    [InlineData("fa-IR")]
    [InlineData("hy-AM")]
    public void Rounding_and_trace_text_ignore_the_current_culture(string cultureName)
    {
        var original = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        var culture = CultureInfo.GetCultureInfo(cultureName);

        try
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;

            var result = Rounding.Round(-2345, 10, RoundingMode.HalfEven);

            Assert.Equal(new BigInteger(-234), result.Value);
            Assert.Equal("-2345/10 rounded HalfEven v1 = -234", result.ToString());
            Assert.Equal("HalfEven v1", result.Policy.ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = original.CurrentCulture;
            CultureInfo.CurrentUICulture = original.CurrentUICulture;
        }
    }

    // ---- Money conversion (AC5) ----

    [Theory]
    [InlineData(RoundingMode.HalfEven)]
    [InlineData(RoundingMode.HalfAwayFromZero)]
    [InlineData(RoundingMode.TowardZero)]
    public void ToMoney_accepts_the_range_limits(RoundingMode mode)
    {
        Assert.Equal(Money.MaxMinorUnits, Rounding.Round(Money.MaxMinorUnits, 1, mode).ToMoney(Usd).MinorUnits);
        Assert.Equal(Money.MinMinorUnits, Rounding.Round(Money.MinMinorUnits, 1, mode).ToMoney(Usd).MinorUnits);
    }

    [Theory]
    [InlineData(RoundingMode.HalfEven)]
    [InlineData(RoundingMode.HalfAwayFromZero)]
    public void ToMoney_rejects_a_value_that_rounding_pushed_past_the_range(RoundingMode mode)
    {
        // 99,999,999,999.5 rounds up to 100,000,000,000, one past the largest Money.
        var up = Rounding.Round(199_999_999_999, 2, mode);
        var down = Rounding.Round(-199_999_999_999, 2, mode);

        Assert.Equal(new BigInteger(100_000_000_000), up.Value);
        Assert.Equal(new BigInteger(-100_000_000_000), down.Value);
        Assert.Throws<OverflowException>(() => up.ToMoney(Usd));
        Assert.Throws<OverflowException>(() => down.ToMoney(Usd));
    }

    [Fact]
    public void ToMoney_accepts_the_same_fraction_rounded_toward_zero()
    {
        var up = Rounding.Round(199_999_999_999, 2, RoundingMode.TowardZero).ToMoney(Usd);
        var down = Rounding.Round(-199_999_999_999, 2, RoundingMode.TowardZero).ToMoney(Usd);

        Assert.Equal(Money.MaxMinorUnits, up.MinorUnits);
        Assert.Equal(Money.MinMinorUnits, down.MinorUnits);
    }

    [Theory]
    [InlineData(RoundingMode.HalfEven)]
    [InlineData(RoundingMode.HalfAwayFromZero)]
    [InlineData(RoundingMode.TowardZero)]
    public void ToMoney_rejects_values_that_do_not_fit_in_64_bits(RoundingMode mode)
    {
        var huge = BigInteger.Pow(new BigInteger(10), 40);

        Assert.Throws<OverflowException>(() => Rounding.Round(huge, 1, mode).ToMoney(Usd));
        Assert.Throws<OverflowException>(() => Rounding.Round(-huge, 1, mode).ToMoney(Usd));
        Assert.Throws<OverflowException>(() => Rounding.Round(long.MaxValue, 1, mode).ToMoney(Usd));
        Assert.Throws<OverflowException>(() => Rounding.Round(long.MinValue, 1, mode).ToMoney(Usd));
    }

    [Fact]
    public void ToMoney_rejects_a_null_currency()
    {
        Assert.Throws<ArgumentNullException>(() => Rounding.Round(1, 1, RoundingMode.HalfEven).ToMoney(null!));
    }

    [Fact]
    public void ToMoney_keeps_the_currency()
    {
        var jpy = CurrencyReferenceData.Jpy;

        Assert.Equal(Money.FromMinorUnits(3, jpy), Rounding.Round(5, 2, RoundingMode.HalfAwayFromZero).ToMoney(jpy));
    }

    // ---- products beyond 64 bits feed rounding exactly ----

    [Theory]
    [InlineData(RoundingMode.HalfEven, "4611686018427387904")]
    [InlineData(RoundingMode.HalfAwayFromZero, "4611686018427387904")]
    [InlineData(RoundingMode.TowardZero, "4611686018427387903")]
    public void A_product_beyond_64_bits_rounds_exactly(RoundingMode mode, string expected)
    {
        // long.MaxValue * 50% is 4611686018427387903.5; the numerator itself exceeds a long.
        var product = ScaledPercentage.FromRateUnits(50_000_000).Multiply(long.MaxValue);

        var result = Rounding.Round(product, mode);

        Assert.True(product.Numerator > long.MaxValue);
        Assert.Equal(BigInteger.Parse(expected, CultureInfo.InvariantCulture), result.Value);
    }

    [Theory]
    [InlineData(RoundingMode.HalfEven)]
    [InlineData(RoundingMode.HalfAwayFromZero)]
    [InlineData(RoundingMode.TowardZero)]
    public void The_largest_money_at_100_percent_rounds_back_to_itself(RoundingMode mode)
    {
        var product = ScaledPercentage.FromRateUnits(ScaledPercentage.MaxRateUnits).Multiply(Money.MaxMinorUnits);

        var result = Rounding.Round(product, mode);

        Assert.Equal(new BigInteger(Money.MaxMinorUnits), result.Value);
        Assert.Equal(BigInteger.Zero, result.AbsoluteRemainder);
    }

    // ---- explicit policies at the fee and escrow points (Examples A and B) ----

    [Fact]
    public void Example_A_rounds_each_declared_point_explicitly_and_conserves_the_gross()
    {
        var usd = Definition("USD");
        var gross = Money.FromMinorUnits(25000, usd.Code);

        var processor = Percentage(gross, 2_900_000, RoundingMode.HalfEven) + Money.FromMinorUnits(30, usd.Code);
        var commission = Percentage(gross, 12_000_000, RoundingMode.HalfEven);
        var affiliate = Percentage(gross, 3_000_000, RoundingMode.HalfEven);
        var sellerShare = gross - processor - commission - affiliate;
        var escrow = Percentage(sellerShare, 10_000_000, RoundingMode.HalfEven);
        var payable = sellerShare - escrow;

        Assert.Equal("7.55", MoneyText.Format(processor, usd));
        Assert.Equal("30.00", MoneyText.Format(commission, usd));
        Assert.Equal("7.50", MoneyText.Format(affiliate, usd));
        Assert.Equal("204.95", MoneyText.Format(sellerShare, usd));
        Assert.Equal("20.50", MoneyText.Format(escrow, usd));
        Assert.Equal("184.45", MoneyText.Format(payable, usd));
        Assert.Equal(gross, processor + commission + affiliate + payable + escrow);
    }

    [Fact]
    public void Example_B_with_a_regional_deduction_rounds_escrow_to_20_00()
    {
        var usd = Definition("USD");
        var gross = Money.FromMinorUnits(25000, usd.Code);

        var regional = Percentage(gross, 2_000_000, RoundingMode.HalfEven);
        var processor = Percentage(gross, 2_900_000, RoundingMode.HalfEven) + Money.FromMinorUnits(30, usd.Code);
        var commission = Percentage(gross, 12_000_000, RoundingMode.HalfEven);
        var affiliate = Percentage(gross, 3_000_000, RoundingMode.HalfEven);
        var sellerShare = gross - regional - processor - commission - affiliate;
        var escrow = Percentage(sellerShare, 10_000_000, RoundingMode.HalfEven);
        var payable = sellerShare - escrow;

        Assert.Equal("5.00", MoneyText.Format(regional, usd));
        Assert.Equal("199.95", MoneyText.Format(sellerShare, usd));
        Assert.Equal("20.00", MoneyText.Format(escrow, usd));
        Assert.Equal("179.95", MoneyText.Format(payable, usd));
        Assert.Equal(gross, regional + processor + commission + affiliate + payable + escrow);
    }

    [Theory]
    [InlineData(RoundingMode.HalfEven, 2050)]
    [InlineData(RoundingMode.HalfAwayFromZero, 2050)]
    [InlineData(RoundingMode.TowardZero, 2049)]
    public void The_chosen_mode_decides_the_example_A_escrow(RoundingMode mode, long expectedMinorUnits)
    {
        var sellerShare = Money.FromMinorUnits(20495, Usd);

        Assert.Equal(expectedMinorUnits, Percentage(sellerShare, 10_000_000, mode).MinorUnits);
    }

    // ---- properties ----

    [Fact]
    public void Results_respect_their_error_bounds_and_are_symmetric_in_sign()
    {
        var random = new Random(20261105);

        for (var i = 0; i < 2000; i++)
        {
            var numerator = new BigInteger(random.NextInt64(-1_000_000_000_000_000_000, 1_000_000_000_000_000_000))
                * random.NextInt64(1, 1_000_000_000_000);
            var denominator = new BigInteger(i % 3 == 0 ? 100_000_000 : random.NextInt64(1, i % 3 == 1 ? 20 : 1_000_000_000));
            var absolute = BigInteger.Abs(numerator);

            var toward = Rounding.Round(numerator, denominator, RoundingMode.TowardZero).Value;
            var halfEven = Rounding.Round(numerator, denominator, RoundingMode.HalfEven).Value;
            var halfAway = Rounding.Round(numerator, denominator, RoundingMode.HalfAwayFromZero).Value;

            // TowardZero is the truncated magnitude with the numerator's sign.
            Assert.True(BigInteger.Abs(toward) * denominator <= absolute);
            Assert.True(absolute < (BigInteger.Abs(toward) + 1) * denominator);
            Assert.True(toward.IsZero || toward.Sign == numerator.Sign);

            // The half modes are within half a unit of the exact value.
            Assert.True(2 * BigInteger.Abs((halfEven * denominator) - numerator) <= denominator);
            Assert.True(2 * BigInteger.Abs((halfAway * denominator) - numerator) <= denominator);

            // The half modes differ only on an exact midpoint.
            var isMidpoint = 2 * BigInteger.Remainder(absolute, denominator) == denominator;

            if (isMidpoint)
            {
                Assert.True(halfEven.IsEven);
                Assert.Equal(BigInteger.Abs(toward) + 1, BigInteger.Abs(halfAway));
            }
            else
            {
                Assert.Equal(halfEven, halfAway);
            }

            // Rounding is symmetric around zero.
            Assert.Equal(-toward, Rounding.Round(-numerator, denominator, RoundingMode.TowardZero).Value);
            Assert.Equal(-halfEven, Rounding.Round(-numerator, denominator, RoundingMode.HalfEven).Value);
            Assert.Equal(-halfAway, Rounding.Round(-numerator, denominator, RoundingMode.HalfAwayFromZero).Value);
        }
    }

    [Fact]
    public void Exact_multiples_are_returned_unchanged_by_every_mode()
    {
        var random = new Random(20261106);

        for (var i = 0; i < 500; i++)
        {
            var multiple = new BigInteger(random.NextInt64(-1_000_000_000_000, 1_000_000_000_000));
            var denominator = new BigInteger(random.NextInt64(1, 1_000_000_000_000));
            var numerator = multiple * denominator;

            foreach (var mode in Enum.GetValues<RoundingMode>())
            {
                var result = Rounding.Round(numerator, denominator, mode);

                Assert.Equal(multiple, result.Value);
                Assert.Equal(BigInteger.Zero, result.AbsoluteRemainder);
                Assert.False(result.MagnitudeIncremented);
            }
        }
    }

    [Fact]
    public void Exact_midpoints_follow_parity_for_both_signs()
    {
        var random = new Random(20261107);

        for (var i = 0; i < 1000; i++)
        {
            var half = random.NextInt64(1, 1_000_000);
            var denominator = new BigInteger(half) * 2;
            var quotient = random.NextInt64(0, 1_000_000_000_000);
            var magnitude = (new BigInteger(quotient) * denominator) + half;
            var sign = i % 2 == 0 ? 1 : -1;
            var numerator = sign * magnitude;
            var evenNeighbour = quotient % 2 == 0 ? quotient : quotient + 1;

            Assert.Equal(sign * new BigInteger(evenNeighbour), Rounding.Round(numerator, denominator, RoundingMode.HalfEven).Value);
            Assert.Equal(sign * new BigInteger(quotient + 1), Rounding.Round(numerator, denominator, RoundingMode.HalfAwayFromZero).Value);
            Assert.Equal(sign * new BigInteger(quotient), Rounding.Round(numerator, denominator, RoundingMode.TowardZero).Value);
        }
    }
}
