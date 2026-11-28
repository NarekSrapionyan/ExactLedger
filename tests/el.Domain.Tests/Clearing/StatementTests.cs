using System.Numerics;
using System.Text;
using el.Domain.Clearing;
using el.Domain.Common;

namespace el.Domain.Tests.Clearing;

public sealed class StatementTests
{
    private const string Header = "reference,currency,amount,source";
    private const string Ref1 = "PO-20261121-USD-000001";
    private const string Ref2 = "PO-20261121-USD-000002";

    private static readonly CurrencyCode Usd = CurrencyReferenceData.Usd;
    private static readonly CurrencyDefinition UsdDefinition = CurrencyReferenceData.Catalog.Resolve(Usd, CurrencyReferenceData.InitialVersion);

    private static ExportedInstruction[] Expected() =>
        [new(Ref1, Money.FromMinorUnits(18445, Usd)), new(Ref2, Money.FromMinorUnits(750, Usd))];

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static StatementRow Row(int line, string reference, string amount, string currency = "USD", string source = "SYNTHETIC") =>
        new(line, reference, currency, amount, source);

    private static StatementRow[] Exact() => [Row(2, Ref1, "184.45"), Row(3, Ref2, "7.50")];

    private static StatementProblem[] Problems(params StatementRow[] rows) =>
        StatementMatcher.Match(UsdDefinition, Expected(), rows).Problems.ToArray();

    [Fact]
    public void Simulate_builds_a_deterministic_synthetic_statement_in_reference_order()
    {
        var statement = StatementCsv.Simulate(UsdDefinition, Expected());

        Assert.Equal(
            Header + "\r\n" + Ref1 + ",USD,184.45,SYNTHETIC\r\n" + Ref2 + ",USD,7.50,SYNTHETIC\r\n",
            Encoding.UTF8.GetString(statement.Content.Span));
        Assert.Equal(2, statement.Count);
        Assert.Equal(new BigInteger(19195), statement.TotalMinorUnits);
        Assert.Equal(ArtifactHash.Sha256Hex(statement.Content.Span), statement.Sha256);

        var reversed = StatementCsv.Simulate(UsdDefinition, Expected().OrderByDescending(item => item.Reference, StringComparer.Ordinal));

        Assert.Equal(statement.Content.ToArray(), reversed.Content.ToArray());
    }

    [Fact]
    public void Simulate_rejects_invalid_input()
    {
        var money = Money.FromMinorUnits(1, Usd);

        Assert.Throws<ArgumentException>(() => StatementCsv.Simulate(UsdDefinition, [new(Ref1, money), new(Ref1, money)]));
        Assert.Throws<ArgumentException>(() => StatementCsv.Simulate(UsdDefinition, [null!]));
        Assert.Throws<ArgumentNullException>(() => StatementCsv.Simulate(null!, Expected()));
        Assert.Throws<ArgumentNullException>(() => StatementCsv.Simulate(UsdDefinition, null!));
    }

    [Fact]
    public void Parse_reads_the_simulated_statement_back()
    {
        var statement = StatementCsv.Simulate(UsdDefinition, Expected());

        var result = StatementCsv.Parse(statement.Content.Span);

        Assert.True(result.IsValid);
        Assert.Equal(
            new[]
            {
                new StatementRow(2, Ref1, "USD", "184.45", "SYNTHETIC"),
                new StatementRow(3, Ref2, "USD", "7.50", "SYNTHETIC"),
            },
            result.Rows.ToArray());
    }

    [Theory]
    [InlineData(Header + "\r\n")]
    [InlineData(Header)]
    public void Parse_accepts_a_header_only_statement(string text)
    {
        var result = StatementCsv.Parse(Bytes(text));

        Assert.True(result.IsValid);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void Parse_accepts_lf_line_endings()
    {
        var result = StatementCsv.Parse(Bytes(Header + "\n" + Ref1 + ",USD,184.45,SYNTHETIC\n"));

        Assert.True(result.IsValid);
        Assert.Equal(new StatementRow(2, Ref1, "USD", "184.45", "SYNTHETIC"), Assert.Single(result.Rows));
    }

    [Theory]
    [InlineData("ref,currency,amount,source\r\n", StatementParseError.InvalidHeader, 1)]
    [InlineData("reference,currency,amount\r\n", StatementParseError.InvalidHeader, 1)]
    [InlineData("\r\n", StatementParseError.InvalidHeader, 1)]
    [InlineData(Header + "\r\n\"open", StatementParseError.InvalidCsv, 2)]
    [InlineData(Header + "\r\nPO-1,USD,1.00\r\n", StatementParseError.InvalidColumnCount, 2)]
    [InlineData(Header + "\r\n\r\n", StatementParseError.InvalidColumnCount, 2)]
    [InlineData(Header + "\r\n" + Ref1 + ",USD,1.00,SYNTHETIC,extra\r\n", StatementParseError.InvalidColumnCount, 2)]
    public void Parse_rejects_malformed_statements(string text, StatementParseError error, int line)
    {
        var result = StatementCsv.Parse(Bytes(text));

        Assert.False(result.IsValid);
        Assert.Empty(result.Rows);
        Assert.Equal(error, result.Error);
        Assert.Equal(line, result.ErrorLine);
    }

    [Fact]
    public void Parse_rejects_empty_oversized_misencoded_and_bom_prefixed_files()
    {
        Assert.Equal(StatementParseError.Empty, StatementCsv.Parse(Array.Empty<byte>()).Error);
        Assert.Equal(StatementParseError.TooLarge, StatementCsv.Parse(new byte[StatementCsv.MaxBytes + 1]).Error);
        Assert.Equal(StatementParseError.InvalidEncoding, StatementCsv.Parse(new byte[] { 0xC3, 0x28 }).Error);

        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Bytes(Header + "\r\n")).ToArray();
        var bom = StatementCsv.Parse(withBom);

        Assert.Equal(StatementParseError.InvalidHeader, bom.Error);
        Assert.Equal(1, bom.ErrorLine);
    }

    [Fact]
    public void An_exact_statement_matches_whatever_the_row_order()
    {
        Assert.True(StatementMatcher.Match(UsdDefinition, Expected(), Exact()).IsMatch);
        Assert.True(StatementMatcher.Match(UsdDefinition, Expected(),
            Exact().OrderByDescending(row => row.Reference, StringComparer.Ordinal)).IsMatch);
    }

    [Fact]
    public void An_empty_statement_matches_an_empty_batch()
    {
        Assert.True(StatementMatcher.Match(UsdDefinition, Array.Empty<ExportedInstruction>(), Array.Empty<StatementRow>()).IsMatch);
    }

    [Fact]
    public void A_missing_row_is_reported()
    {
        Assert.Equal(
            new[] { new StatementProblem(StatementProblemKind.MissingInstruction, Ref2, 0) },
            Problems(Row(2, Ref1, "184.45")));
    }

    [Fact]
    public void An_unknown_reference_is_reported()
    {
        Assert.Equal(
            new[] { new StatementProblem(StatementProblemKind.UnknownReference, "PO-OTHER", 4) },
            Problems(Row(2, Ref1, "184.45"), Row(3, Ref2, "7.50"), Row(4, "PO-OTHER", "1.00")));
    }

    [Fact]
    public void A_duplicate_row_is_reported_and_the_first_one_still_counts()
    {
        Assert.Equal(
            new[] { new StatementProblem(StatementProblemKind.DuplicateReference, Ref1, 4) },
            Problems(Row(2, Ref1, "184.45"), Row(3, Ref2, "7.50"), Row(4, Ref1, "184.45")));
    }

    [Fact]
    public void A_duplicate_unknown_reference_is_unknown_then_duplicate()
    {
        Assert.Equal(
            new[]
            {
                new StatementProblem(StatementProblemKind.UnknownReference, "PO-X", 4),
                new StatementProblem(StatementProblemKind.DuplicateReference, "PO-X", 5),
            },
            Problems(Row(2, Ref1, "184.45"), Row(3, Ref2, "7.50"), Row(4, "PO-X", "1.00"), Row(5, "PO-X", "1.00")));
    }

    [Theory]
    [InlineData("184.46")]
    [InlineData("184.450")]
    [InlineData("184.4")]
    [InlineData("0184.45")]
    [InlineData(" 184.45")]
    public void Any_amount_that_is_not_the_exact_canonical_text_is_a_mismatch(string amount)
    {
        Assert.Equal(
            new[] { new StatementProblem(StatementProblemKind.AmountMismatch, Ref1, 2) },
            Problems(Row(2, Ref1, amount), Row(3, Ref2, "7.50")));
    }

    [Fact]
    public void A_different_currency_is_reported_separately_from_the_amount()
    {
        Assert.Equal(
            new[] { new StatementProblem(StatementProblemKind.CurrencyMismatch, Ref1, 2) },
            Problems(Row(2, Ref1, "184.45", currency: "JPY"), Row(3, Ref2, "7.50")));
        Assert.Equal(
            new[]
            {
                new StatementProblem(StatementProblemKind.CurrencyMismatch, Ref1, 2),
                new StatementProblem(StatementProblemKind.AmountMismatch, Ref1, 2),
            },
            Problems(Row(2, Ref1, "1000", currency: "JPY"), Row(3, Ref2, "7.50")));
    }

    [Fact]
    public void A_row_that_is_not_marked_synthetic_is_reported()
    {
        Assert.Equal(
            new[] { new StatementProblem(StatementProblemKind.NotSynthetic, Ref1, 2) },
            Problems(Row(2, Ref1, "184.45", source: "REAL"), Row(3, Ref2, "7.50")));
    }

    [Fact]
    public void Row_problems_come_first_in_row_order_then_missing_instructions_in_reference_order()
    {
        Assert.Equal(
            new[]
            {
                new StatementProblem(StatementProblemKind.UnknownReference, "PO-X", 2),
                new StatementProblem(StatementProblemKind.MissingInstruction, Ref1, 0),
                new StatementProblem(StatementProblemKind.MissingInstruction, Ref2, 0),
            },
            Problems(Row(2, "PO-X", "1.00")));
    }

    [Fact]
    public void Match_rejects_invalid_arguments()
    {
        var money = Money.FromMinorUnits(1, Usd);

        Assert.Throws<ArgumentException>(() => StatementMatcher.Match(UsdDefinition, [new(Ref1, money), new(Ref1, money)], Exact()));
        Assert.Throws<ArgumentNullException>(() => StatementMatcher.Match(null!, Expected(), Exact()));
        Assert.Throws<ArgumentNullException>(() => StatementMatcher.Match(UsdDefinition, null!, Exact()));
        Assert.Throws<ArgumentNullException>(() => StatementMatcher.Match(UsdDefinition, Expected(), null!));
        Assert.Throws<ArgumentNullException>(() => StatementMatcher.Match(UsdDefinition, [null!], Exact()));
        Assert.Throws<ArgumentNullException>(() => StatementMatcher.Match(UsdDefinition, Expected(), [null!]));
    }
}
