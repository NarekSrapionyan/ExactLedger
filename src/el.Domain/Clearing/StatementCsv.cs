using System.Numerics;
using System.Text;
using el.Domain.Common;

namespace el.Domain.Clearing;

public sealed record ExportedInstruction(string Reference, Money Amount);

/// <summary>One statement row exactly as sent. Line is the 1-based record number; the header is line 1.</summary>
public sealed record StatementRow(int Line, string Reference, string Currency, string Amount, string Source);

public enum StatementParseError
{
    None = 0,
    Empty,
    TooLarge,
    InvalidEncoding,
    InvalidCsv,
    InvalidHeader,
    InvalidColumnCount,
}

public sealed record StatementParseResult(IReadOnlyList<StatementRow> Rows, StatementParseError Error, int ErrorLine)
{
    public bool IsValid => Error == StatementParseError.None;
}

public static class StatementCsv
{
    public const string SyntheticSource = "SYNTHETIC";

    public const int MaxBytes = 1_048_576;

    private static readonly string[] Header = ["reference", "currency", "amount", "source"];

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static CsvArtifact Simulate(CurrencyDefinition currency, IEnumerable<ExportedInstruction> instructions)
    {
        ArgumentNullException.ThrowIfNull(currency);
        ArgumentNullException.ThrowIfNull(instructions);

        var ordered = instructions.ToArray();

        if (ordered.Any(instruction => instruction is null))
        {
            throw new ArgumentException("Instructions must not contain null.", nameof(instructions));
        }

        ordered = ordered.OrderBy(instruction => instruction.Reference, StringComparer.Ordinal).ToArray();

        for (var index = 1; index < ordered.Length; index++)
        {
            if (string.Equals(ordered[index].Reference, ordered[index - 1].Reference, StringComparison.Ordinal))
            {
                throw new ArgumentException("Instruction references must be unique.", nameof(instructions));
            }
        }

        var records = new List<IReadOnlyList<string>>(ordered.Length + 1) { Header };
        var total = BigInteger.Zero;

        foreach (var instruction in ordered)
        {
            records.Add(new[]
            {
                instruction.Reference,
                currency.Code.ToString(),
                MoneyText.Format(instruction.Amount, currency),
                SyntheticSource,
            });
            total += instruction.Amount.MinorUnits;
        }

        var content = Utf8.GetBytes(Rfc4180.Write(records));

        return new CsvArtifact(content, ArtifactHash.Sha256Hex(content), ordered.Length, total, currency.Code);
    }

    public static StatementParseResult Parse(ReadOnlySpan<byte> content)
    {
        if (content.IsEmpty)
        {
            return Failed(StatementParseError.Empty, 0);
        }

        if (content.Length > MaxBytes)
        {
            return Failed(StatementParseError.TooLarge, 0);
        }

        string text;

        try
        {
            text = Utf8.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return Failed(StatementParseError.InvalidEncoding, 0);
        }

        if (!Rfc4180.TryParse(text, out var records, out var errorRecord))
        {
            return Failed(StatementParseError.InvalidCsv, errorRecord);
        }

        if (records.Count == 0 || !records[0].SequenceEqual(Header, StringComparer.Ordinal))
        {
            return Failed(StatementParseError.InvalidHeader, 1);
        }

        var rows = new List<StatementRow>();

        for (var index = 1; index < records.Count; index++)
        {
            var record = records[index];

            if (record.Count != Header.Length)
            {
                return Failed(StatementParseError.InvalidColumnCount, index + 1);
            }

            rows.Add(new StatementRow(index + 1, record[0], record[1], record[2], record[3]));
        }

        return new StatementParseResult(rows, StatementParseError.None, 0);
    }

    private static StatementParseResult Failed(StatementParseError error, int line) => new([], error, line);
}
