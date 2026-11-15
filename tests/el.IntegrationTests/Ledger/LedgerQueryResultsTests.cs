using System.Globalization;
using System.Numerics;
using el.Application.Ledger;
using el.Domain.Common;
using el.Domain.Ledger;
using el.Infrastructure.Persistence;

namespace el.IntegrationTests.Ledger;

public sealed class LedgerQueryResultsTests
{
    private static readonly AccountId AnyAccount = new(Guid.NewGuid());

    [Theory]
    [InlineData(100, 30, EntrySide.Debit, 70)]
    [InlineData(100, 30, EntrySide.Credit, -70)]
    [InlineData(30, 100, EntrySide.Credit, 70)]
    public void OnNormalSide_is_positive_on_the_normal_side(long debit, long credit, EntrySide side, long expected)
    {
        var balance = new AccountBalance(AnyAccount, CurrencyReferenceData.Usd, debit, credit);

        Assert.Equal(new BigInteger(expected), balance.OnNormalSide(side));
        Assert.Equal(new BigInteger(debit - credit), balance.NetDebit);
    }

    [Fact]
    public void OnNormalSide_rejects_an_undefined_side()
    {
        var balance = new AccountBalance(AnyAccount, CurrencyReferenceData.Usd, 1, 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => balance.OnNormalSide(default));
    }

    [Fact]
    public void A_trial_balance_row_reports_its_difference_exactly()
    {
        var huge = BigInteger.Pow(10, 30);

        Assert.True(new CurrencyTrialBalance(CurrencyReferenceData.Usd, huge, huge).IsBalanced);
        Assert.Equal(BigInteger.One, new CurrencyTrialBalance(CurrencyReferenceData.Usd, huge + 1, huge).Difference);
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("-5", "-5")]
    [InlineData("18446744073709551614", "18446744073709551614")]
    [InlineData("99999999999999999999999999999999999999", "99999999999999999999999999999999999999")]
    public void ParseExactInteger_parses_without_loss(string text, string expected)
    {
        Assert.Equal(BigInteger.Parse(expected, CultureInfo.InvariantCulture), ExactAggregateQueries.ParseExactInteger(text));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("ar-SA")]
    [InlineData("tr-TR")]
    public void ParseExactInteger_ignores_the_current_culture(string cultureName)
    {
        var original = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);

            Assert.Equal(new BigInteger(-1234567), ExactAggregateQueries.ParseExactInteger("-1234567"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original.CurrentCulture;
            CultureInfo.CurrentUICulture = original.CurrentUICulture;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.5")]
    [InlineData("1,000")]
    [InlineData("abc")]
    public void ParseExactInteger_rejects_non_integers(string text)
    {
        Assert.Throws<InvalidOperationException>(() => ExactAggregateQueries.ParseExactInteger(text));
    }

    [Fact]
    public void ParseExactInteger_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => ExactAggregateQueries.ParseExactInteger(null!));
    }
}
