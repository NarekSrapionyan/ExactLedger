using System.Text.RegularExpressions;
using el.Domain.Clearing;
using el.Domain.Common;

namespace el.Domain.Tests.Clearing;

public sealed class PayoutReferenceTests
{
    [Theory]
    [InlineData("USD", 1, "PO-20261121-USD-000001")]
    [InlineData("JPY", 42, "PO-20261121-JPY-000042")]
    [InlineData("KWD", 999999, "PO-20261121-KWD-999999")]
    [InlineData("USD", 1234567, "PO-20261121-USD-1234567")]
    public void Create_formats_date_currency_and_a_six_digit_ordinal(string code, int ordinal, string expected)
    {
        Assert.Equal(expected, PayoutReference.Create(new DateOnly(2026, 11, 21), CurrencyCode.Parse(code), ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_rejects_an_ordinal_below_one(int ordinal)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PayoutReference.Create(new DateOnly(2026, 11, 21), CurrencyReferenceData.Usd, ordinal));
    }

    [Fact]
    public void Create_rejects_a_null_currency()
    {
        Assert.Throws<ArgumentNullException>(() => PayoutReference.Create(new DateOnly(2026, 11, 21), null!, 1));
    }

    [Fact]
    public void A_payout_journal_reference_fits_the_ledger_reference_pattern()
    {
        var reference = PayoutReference.Create(new DateOnly(2026, 11, 21), CurrencyReferenceData.Usd, 1);

        Assert.Matches("^[A-Z0-9.:_-]{1,100}$", "PAYOUT:" + reference);
    }
}
