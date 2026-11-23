using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using el.Domain.Clearing;
using el.Domain.Common;
using el.Domain.Ledger;

namespace el.Domain.Tests.Clearing;

public sealed class PayoutCsvTests
{
    private static readonly CurrencyCode Usd = CurrencyReferenceData.Usd;
    private static readonly DateOnly Day = new(2026, 11, 21);

    private static CurrencyDefinition Definition(string code) =>
        CurrencyReferenceData.Catalog.Resolve(CurrencyCode.Parse(code), CurrencyReferenceData.InitialVersion);

    private static PartyId Party(int number) =>
        new(Guid.Parse(FormattableString.Invariant($"00000000-0000-0000-0000-{number:D12}")));

    private static PayoutCsvRow Row(string reference, int party, long minorUnits, CurrencyCode? currency = null) =>
        new(reference, Day, Party(party), Money.FromMinorUnits(minorUnits, currency ?? Usd));

    private static PayoutCsvRow[] Rows() =>
        [Row("PO-20261121-USD-000002", 2, 750), Row("PO-20261121-USD-000001", 1, 18445)];

    [Fact]
    public void Build_writes_the_frozen_rows_in_reference_order_as_utf8_rfc_4180()
    {
        var artifact = PayoutCsv.Build(Definition("USD"), Rows());

        var expected = "reference,business_date,party_id,currency,amount\r\n"
            + "PO-20261121-USD-000001,2026-11-21,00000000-0000-0000-0000-000000000001,USD,184.45\r\n"
            + "PO-20261121-USD-000002,2026-11-21,00000000-0000-0000-0000-000000000002,USD,7.50\r\n";

        Assert.Equal(expected, Encoding.UTF8.GetString(artifact.Content.Span));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(artifact.Content.Span)), artifact.Sha256);
        Assert.Matches("^[0-9a-f]{64}$", artifact.Sha256);
        Assert.Equal(2, artifact.Count);
        Assert.Equal(new BigInteger(19195), artifact.TotalMinorUnits);
        Assert.Equal(Usd, artifact.Currency);
        Assert.Equal((byte)0x72, artifact.Content.Span[0]);
    }

    [Fact]
    public void The_bytes_do_not_depend_on_the_input_order()
    {
        var forward = PayoutCsv.Build(Definition("USD"), Rows());
        var reversed = PayoutCsv.Build(Definition("USD"), Rows().OrderBy(row => row.Reference, StringComparer.Ordinal));

        Assert.Equal(forward.Content.ToArray(), reversed.Content.ToArray());
        Assert.Equal(forward.Sha256, reversed.Sha256);
    }

    [Fact]
    public void Amounts_use_the_exact_exponent_of_the_currency()
    {
        var jpy = PayoutCsv.Build(Definition("JPY"), [Row("PO-20261121-JPY-000001", 1, 1000, CurrencyReferenceData.Jpy)]);
        var kwd = PayoutCsv.Build(Definition("KWD"), [Row("PO-20261121-KWD-000001", 1, 1234, CurrencyCode.Parse("KWD"))]);

        Assert.EndsWith(",JPY,1000\r\n", Encoding.UTF8.GetString(jpy.Content.Span));
        Assert.EndsWith(",KWD,1.234\r\n", Encoding.UTF8.GetString(kwd.Content.Span));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("ar-SA")]
    [InlineData("tr-TR")]
    public void The_bytes_do_not_depend_on_the_current_culture(string cultureName)
    {
        var baseline = PayoutCsv.Build(Definition("USD"), Rows()).Content.ToArray();
        var original = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);

            Assert.Equal(baseline, PayoutCsv.Build(Definition("USD"), Rows()).Content.ToArray());
        }
        finally
        {
            CultureInfo.CurrentCulture = original.CurrentCulture;
            CultureInfo.CurrentUICulture = original.CurrentUICulture;
        }
    }

    [Fact]
    public void An_empty_batch_is_a_header_only_file()
    {
        var artifact = PayoutCsv.Build(Definition("USD"), Array.Empty<PayoutCsvRow>());

        Assert.Equal("reference,business_date,party_id,currency,amount\r\n", Encoding.UTF8.GetString(artifact.Content.Span));
        Assert.Equal(0, artifact.Count);
        Assert.Equal(BigInteger.Zero, artifact.TotalMinorUnits);
    }

    [Fact]
    public void Build_rejects_invalid_rows()
    {
        var usd = Definition("USD");

        Assert.Throws<ArgumentException>(() => PayoutCsv.Build(usd, [Row("PO-1", 1, 100), Row("PO-1", 2, 100)]));
        Assert.Throws<ArgumentException>(() => PayoutCsv.Build(usd, [Row("PO-1", 1, 0)]));
        Assert.Throws<ArgumentException>(() => PayoutCsv.Build(usd, [Row("PO-1", 1, -5)]));
        Assert.Throws<ArgumentException>(() => PayoutCsv.Build(usd, [null!]));
        Assert.ThrowsAny<ArgumentException>(() => PayoutCsv.Build(usd, [Row(" ", 1, 100)]));
        Assert.Throws<CurrencyMismatchException>(() => PayoutCsv.Build(usd, [Row("PO-1", 1, 100, CurrencyReferenceData.Jpy)]));
        Assert.Throws<ArgumentNullException>(() => PayoutCsv.Build(null!, Rows()));
        Assert.Throws<ArgumentNullException>(() => PayoutCsv.Build(usd, null!));
    }
}
