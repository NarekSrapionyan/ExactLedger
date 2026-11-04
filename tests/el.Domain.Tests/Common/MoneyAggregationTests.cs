using System.Numerics;
using el.Domain.Common;

namespace el.Domain.Tests.Common;

public sealed class MoneyAggregationTests
{
    private static readonly CurrencyCode Usd = CurrencyReferenceData.Usd;
    private static readonly CurrencyCode Jpy = CurrencyReferenceData.Jpy;

    [Fact]
    public void Total_may_exceed_the_single_value_range_without_losing_precision()
    {
        var max = Money.FromMinorUnits(Money.MaxMinorUnits, Usd);

        var total = MoneyAggregation.SumMinorUnits([max, max, max], Usd);

        Assert.Equal(new BigInteger(Money.MaxMinorUnits) * 3, total);
        Assert.True(total > Money.MaxMinorUnits);
    }

    [Fact]
    public void Total_includes_signed_values()
    {
        var total = MoneyAggregation.SumMinorUnits(
            [Money.FromMinorUnits(25000, Usd), Money.FromMinorUnits(-26000, Usd), Money.Zero(Usd)],
            Usd);

        Assert.Equal(new BigInteger(-1000), total);
    }

    [Fact]
    public void Empty_total_is_zero()
    {
        Assert.Equal(BigInteger.Zero, MoneyAggregation.SumMinorUnits([], Usd));
    }

    [Fact]
    public void Total_rejects_a_value_in_another_currency()
    {
        var exception = Assert.Throws<CurrencyMismatchException>(() => MoneyAggregation.SumMinorUnits(
            [Money.FromMinorUnits(100, Usd), Money.FromMinorUnits(100, Jpy)],
            Usd));

        Assert.Equal(Usd, exception.Expected);
        Assert.Equal(Jpy, exception.Actual);
    }

    [Fact]
    public void Total_rejects_null_input()
    {
        Assert.Throws<ArgumentNullException>(() => MoneyAggregation.SumMinorUnits(null!, Usd));
        Assert.Throws<ArgumentNullException>(() => MoneyAggregation.SumMinorUnits([], null!));
        Assert.Throws<ArgumentException>(() => MoneyAggregation.SumMinorUnits([null!], Usd));
    }
}
