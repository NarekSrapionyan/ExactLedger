using System.Globalization;
using System.Numerics;
using System.Text;
using el.Domain.Common;
using el.Domain.Ledger;

namespace el.Domain.Clearing;

public sealed record PayoutCsvRow(string Reference, DateOnly BusinessDate, PartyId PartyId, Money Amount);

/// <summary>Deterministic payout file: UTF-8 without BOM, RFC 4180, rows ordered by reference.</summary>
public static class PayoutCsv
{
    private static readonly string[] Header = ["reference", "business_date", "party_id", "currency", "amount"];

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static CsvArtifact Build(CurrencyDefinition currency, IEnumerable<PayoutCsvRow> rows)
    {
        ArgumentNullException.ThrowIfNull(currency);
        ArgumentNullException.ThrowIfNull(rows);

        var ordered = rows.ToArray();

        foreach (var row in ordered)
        {
            if (row is null)
            {
                throw new ArgumentException("Rows must not contain null.", nameof(rows));
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(row.Reference);

            if (!row.Amount.IsPositive)
            {
                throw new ArgumentException("A payout amount must be positive.", nameof(rows));
            }
        }

        ordered = ordered.OrderBy(row => row.Reference, StringComparer.Ordinal).ToArray();

        for (var index = 1; index < ordered.Length; index++)
        {
            if (string.Equals(ordered[index].Reference, ordered[index - 1].Reference, StringComparison.Ordinal))
            {
                throw new ArgumentException("Payout references must be unique.", nameof(rows));
            }
        }

        var records = new List<IReadOnlyList<string>>(ordered.Length + 1) { Header };
        var total = BigInteger.Zero;

        foreach (var row in ordered)
        {
            records.Add(new[]
            {
                row.Reference,
                row.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                row.PartyId.Value.ToString("D"),
                currency.Code.ToString(),
                MoneyText.Format(row.Amount, currency),
            });
            total += row.Amount.MinorUnits;
        }

        var content = Utf8.GetBytes(Rfc4180.Write(records));

        return new CsvArtifact(content, ArtifactHash.Sha256Hex(content), ordered.Length, total, currency.Code);
    }
}
