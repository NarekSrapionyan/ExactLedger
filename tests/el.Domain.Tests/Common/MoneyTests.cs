using el.Domain.Common;

namespace el.Domain.Tests.Common;

public sealed class MoneyTests
{
    private static readonly CurrencyCode Usd = CurrencyReferenceData.Usd;
    private static readonly CurrencyCode Jpy = CurrencyReferenceData.Jpy;

    private static Money UsdMinor(long minorUnits) => Money.FromMinorUnits(minorUnits, Usd);

    [Theory]
    [InlineData(Money.MaxMinorUnits)]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(Money.MinMinorUnits)]
    public void FromMinorUnits_accepts_values_within_the_range(long minorUnits)
    {
        var money = UsdMinor(minorUnits);

        Assert.Equal(minorUnits, money.MinorUnits);
        Assert.Equal(Usd, money.Currency);
    }

    [Theory]
    [InlineData(Money.MaxMinorUnits + 1)]
    [InlineData(Money.MinMinorUnits - 1)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void FromMinorUnits_rejects_values_outside_the_range(long minorUnits)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UsdMinor(minorUnits));
    }

    [Fact]
    public void FromMinorUnits_rejects_a_null_currency()
    {
        Assert.Throws<ArgumentNullException>(() => Money.FromMinorUnits(100, null!));
    }

    [Theory]
    [InlineData(25000, false, true, false)]
    [InlineData(0, true, false, false)]
    [InlineData(-25000, false, false, true)]
    public void Zero_and_negative_amounts_are_valid_money(long minorUnits, bool isZero, bool isPositive, bool isNegative)
    {
        var money = UsdMinor(minorUnits);

        Assert.Equal(isZero, money.IsZero);
        Assert.Equal(isPositive, money.IsPositive);
        Assert.Equal(isNegative, money.IsNegative);
    }

    [Fact]
    public void Zero_creates_a_zero_amount_in_the_currency()
    {
        Assert.Equal(UsdMinor(0), Money.Zero(Usd));
        Assert.Equal(Jpy, Money.Zero(Jpy).Currency);
    }

    [Fact]
    public void Equality_uses_amount_and_currency()
    {
        var first = UsdMinor(25000);
        var second = UsdMinor(25000);

        var equalByOperator = first == second;
        var differentByOperator = first != Money.FromMinorUnits(25000, Jpy);

        Assert.Equal(first, second);
        Assert.True(equalByOperator);
        Assert.True(differentByOperator);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, UsdMinor(25001));
        Assert.NotEqual(first, Money.FromMinorUnits(25000, Jpy));
    }

    [Theory]
    [InlineData(25000, 1000, 26000)]
    [InlineData(25000, -26000, -1000)]
    [InlineData(-1, 1, 0)]
    [InlineData(Money.MaxMinorUnits - 1, 1, Money.MaxMinorUnits)]
    [InlineData(Money.MinMinorUnits + 1, -1, Money.MinMinorUnits)]
    [InlineData(Money.MaxMinorUnits, Money.MinMinorUnits, 0)]
    public void Add_returns_the_exact_sum(long left, long right, long expected)
    {
        Assert.Equal(UsdMinor(expected), UsdMinor(left).Add(UsdMinor(right)));
        Assert.Equal(UsdMinor(expected), UsdMinor(left) + UsdMinor(right));
    }

    [Theory]
    [InlineData(25000, 1000, 24000)]
    [InlineData(1000, 25000, -24000)]
    [InlineData(0, Money.MaxMinorUnits, Money.MinMinorUnits)]
    [InlineData(Money.MaxMinorUnits - 1, -1, Money.MaxMinorUnits)]
    public void Subtract_returns_the_exact_difference(long left, long right, long expected)
    {
        Assert.Equal(UsdMinor(expected), UsdMinor(left).Subtract(UsdMinor(right)));
        Assert.Equal(UsdMinor(expected), UsdMinor(left) - UsdMinor(right));
    }

    [Theory]
    [InlineData(Money.MaxMinorUnits, 1)]
    [InlineData(Money.MinMinorUnits, -1)]
    [InlineData(Money.MaxMinorUnits, Money.MaxMinorUnits)]
    [InlineData(Money.MinMinorUnits, Money.MinMinorUnits)]
    public void Add_rejects_results_outside_the_range(long left, long right)
    {
        Assert.Throws<OverflowException>(() => UsdMinor(left).Add(UsdMinor(right)));
        Assert.Throws<OverflowException>(() => UsdMinor(left) + UsdMinor(right));
    }

    [Theory]
    [InlineData(Money.MinMinorUnits, 1)]
    [InlineData(Money.MaxMinorUnits, -1)]
    [InlineData(Money.MaxMinorUnits, Money.MinMinorUnits)]
    public void Subtract_rejects_results_outside_the_range(long left, long right)
    {
        Assert.Throws<OverflowException>(() => UsdMinor(left).Subtract(UsdMinor(right)));
        Assert.Throws<OverflowException>(() => UsdMinor(left) - UsdMinor(right));
    }

    [Theory]
    [InlineData(25000, -25000)]
    [InlineData(0, 0)]
    [InlineData(Money.MaxMinorUnits, Money.MinMinorUnits)]
    [InlineData(Money.MinMinorUnits, Money.MaxMinorUnits)]
    public void Negate_flips_the_sign_within_the_range(long minorUnits, long expected)
    {
        Assert.Equal(UsdMinor(expected), UsdMinor(minorUnits).Negate());
        Assert.Equal(UsdMinor(expected), -UsdMinor(minorUnits));
    }

    [Fact]
    public void Arithmetic_returns_new_values_and_leaves_operands_unchanged()
    {
        var left = UsdMinor(25000);
        var right = UsdMinor(1000);

        var sum = left + right;

        Assert.Equal(26000, sum.MinorUnits);
        Assert.Equal(25000, left.MinorUnits);
        Assert.Equal(1000, right.MinorUnits);
    }

    [Fact]
    public void Arithmetic_rejects_different_currencies()
    {
        var usd = UsdMinor(1000);
        var jpy = Money.FromMinorUnits(1000, Jpy);

        var exception = Assert.Throws<CurrencyMismatchException>(() => usd.Add(jpy));
        Assert.Equal(Usd, exception.Expected);
        Assert.Equal(Jpy, exception.Actual);

        Assert.Throws<CurrencyMismatchException>(() => usd.Subtract(jpy));
        Assert.Throws<CurrencyMismatchException>(() => usd + jpy);
        Assert.Throws<CurrencyMismatchException>(() => usd - jpy);
    }

    [Fact]
    public void Ordered_comparison_rejects_different_currencies()
    {
        var usd = UsdMinor(1000);
        var jpy = Money.FromMinorUnits(1000, Jpy);

        Assert.Throws<CurrencyMismatchException>(() => usd.CompareTo(jpy));
        Assert.Throws<CurrencyMismatchException>(() => usd < jpy);
        Assert.Throws<CurrencyMismatchException>(() => usd > jpy);
        Assert.Throws<CurrencyMismatchException>(() => usd <= jpy);
        Assert.Throws<CurrencyMismatchException>(() => usd >= jpy);
    }

    [Fact]
    public void Ordered_comparison_uses_the_signed_amount()
    {
        var negative = UsdMinor(-1);
        var zero = UsdMinor(0);
        var positive = UsdMinor(1);

        Assert.True(negative < zero);
        Assert.True(positive > zero);
        Assert.True(zero <= UsdMinor(0));
        Assert.True(zero >= UsdMinor(0));
        Assert.False(positive < negative);
        Assert.Equal(0, zero.CompareTo(UsdMinor(0)));
        Assert.True(negative.CompareTo(positive) < 0);
        Assert.True(positive.CompareTo(negative) > 0);
    }

    [Fact]
    public void Operations_reject_null_operands()
    {
        var usd = UsdMinor(1000);

        Assert.Throws<ArgumentNullException>(() => usd.Add(null!));
        Assert.Throws<ArgumentNullException>(() => usd.Subtract(null!));
        Assert.Throws<ArgumentNullException>(() => usd.CompareTo(null));
        Assert.Throws<ArgumentNullException>(() => null! + usd);
        Assert.Throws<ArgumentNullException>(() => usd < null!);
    }

    [Fact]
    public void ToString_shows_minor_units_and_currency()
    {
        Assert.Equal("-25000 USD minor units", UsdMinor(-25000).ToString());
    }
}
