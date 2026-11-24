using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using el.Application.Abstractions;
using el.Application.Audit;
using el.Domain.Clearing;
using el.Domain.Common;
using el.Domain.Ledger;

namespace el.Application.Reporting;

public sealed class ReportService(IReportStore store, IUnitOfWork unitOfWork,
    IMonetaryOperationLock operationLock, IAuditTrail audit, TimeProvider clock)
{
    public async Task<FinancialReport> ReadAsync(string kind, ReportFilter filter, CancellationToken ct, int offset = 0, int? limit = null)
    {
        var definition = ReportDefinitions.Get(kind);
        Validate(filter);
        var raw = await store.ReadAsync(kind, filter, ct);
        if (raw.Count > ReportDefinitions.MaximumRows) throw new ReportException(400, "REPORT_TOO_LARGE");
        var totals = raw.GroupBy(row => row["currency"], StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new ReportCurrencyTotal(group.Key, definition.TotalColumns.ToDictionary(column => column,
                column => Format(group.Aggregate(BigInteger.Zero, (sum, row) => sum + Integer(row[column])), group.Key))))
            .ToArray();
        var rows = raw.Skip(offset).Take(limit ?? raw.Count).Select(row => (IReadOnlyDictionary<string, string>)definition.Columns.ToDictionary(column => column,
            column => definition.AmountColumns.Contains(column, StringComparer.Ordinal)
                ? Format(Integer(row[column]), row["currency"])
                : column == "accountType" ? ((AccountType)int.Parse(row[column], CultureInfo.InvariantCulture)).ToString() : row[column])).ToArray();
        return new FinancialReport(kind, filter, definition.Columns, rows, raw.Count, totals);
    }

    public Task<ReportExport> ExportAsync(string kind, ReportFilter filter, string actorId, string key, CancellationToken ct)
    {
        ReportDefinitions.Get(kind);
        Validate(filter);
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || key.Any(char.IsControl))
            throw new ReportException(400, "INVALID_IDEMPOTENCY_KEY");
        var hash = ArtifactHash.Sha256Hex(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { kind, filter })));
        return unitOfWork.ExecuteAsync(async token =>
        {
            // Serialize export retries and freeze the same business state used by monetary operations.
            await operationLock.AcquireAsync(token);
            var existing = await store.FindExportAsync(actorId, key, hash, token);
            if (existing is not null) return existing;
            var report = await ReadAsync(kind, filter, token);
            var records = new List<IReadOnlyList<string>> { report.Columns };
            records.AddRange(report.Rows.Select(row => report.Columns.Select(column => row[column]).ToArray()));
            var content = new UTF8Encoding(false, true).GetBytes(Rfc4180.Write(records));
            var generatedAt = DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds());
            var export = new ReportExport(Guid.NewGuid(), kind, filter.PartyId, generatedAt,
                ArtifactHash.Sha256Hex(content), report.Rows.Count, report.Totals, content);
            await store.SaveExportAsync(export, actorId, key, hash, token);
            await audit.RecordAsync("report.exported", export.Id.ToString("D"), token);
            return export;
        }, ct);
    }

    public Task<ReportExport?> GetExportAsync(Guid id, CancellationToken ct) => store.GetExportAsync(id, ct);

    private static void Validate(ReportFilter filter)
    {
        if ((filter.Currency is not null && filter.Currency is not ("USD" or "JPY" or "KWD"))
            || filter.PartyId == Guid.Empty || filter.BatchId == Guid.Empty || filter.CutSequence < 0
            || (filter.From is { } from && filter.To is { } to && from > to))
            throw new ReportException(400, "INVALID_REPORT_FILTER");
    }

    private static BigInteger Integer(string value) => BigInteger.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

    private static string Format(BigInteger value, string currency)
    {
        var exponent = CurrencyReferenceData.Catalog.Resolve(CurrencyCode.Parse(currency), CurrencyReferenceData.InitialVersion).Exponent;
        var whole = BigInteger.DivRem(BigInteger.Abs(value), BigInteger.Pow(10, exponent), out var fraction);
        return (value.Sign < 0 ? "-" : "") + whole.ToString(CultureInfo.InvariantCulture)
            + (exponent == 0 ? "" : "." + fraction.ToString(CultureInfo.InvariantCulture).PadLeft(exponent, '0'));
    }
}
