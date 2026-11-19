using System.Globalization;
using System.Numerics;
using el.Api.Ledger;
using el.Domain.Common;

namespace el.IntegrationTests;

public sealed class AggregateMoneyContractTests
{
    [Theory]
    [InlineData("USD", "18446744073709551614", "184467440737095516.14")]
    [InlineData("JPY", "18446744073709551614", "18446744073709551614")]
    [InlineData("KWD", "-1234", "-1.234")]
    [InlineData("USD", "-1", "-0.01")]
    [InlineData("KWD", "0", "0.000")]
    public void Aggregate_strings_preserve_precision_beyond_Int64_and_Money_limits(string currency, string minorUnits, string expected)
    {
        var result = LedgerEndpoints.Format(BigInteger.Parse(minorUnits, CultureInfo.InvariantCulture), CurrencyCode.Parse(currency));
        Assert.Equal(expected, result.Amount);
        Assert.Equal(currency, result.Currency);
    }
}
