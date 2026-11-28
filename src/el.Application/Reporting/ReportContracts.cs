namespace el.Application.Reporting;

public sealed record ReportFilter(string? Currency = null, Guid? PartyId = null,
    DateOnly? From = null, DateOnly? To = null, long? CutSequence = null, Guid? BatchId = null);

/// <summary>Rectangular financial records. All monetary cells are exact decimal strings.</summary>
public sealed record FinancialReport(string Kind, ReportFilter Filter, IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, string>> Rows, int RecordCount, IReadOnlyList<ReportCurrencyTotal> Totals);

public sealed record ReportCurrencyTotal(string Currency, IReadOnlyDictionary<string, string> Amounts);

public sealed record ReportExport(Guid Id, string Kind, Guid? PartyId, DateTimeOffset GeneratedAtUtc,
    string Sha256, int RecordCount, IReadOnlyList<ReportCurrencyTotal> Totals, byte[] Content);

public sealed class ReportException(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public sealed record ReportDefinition(string[] Columns, string[] AmountColumns, string[] TotalColumns, bool LedgerOnly);

public static class ReportDefinitions
{
    public const int MaximumRows = 100_000;

    public static ReportDefinition Get(string kind) => kind switch
    {
        "journal-register" => new(
            ["journalEntryId", "lineNumber", "sequence", "reference", "description", "postedAtUtc", "reversalOf", "accountId", "partyId", "accountType", "currency", "debit", "credit"],
            ["debit", "credit"], ["debit", "credit"], true),
        "payee-statement" => new(
            ["id", "event", "postedAtUtc", "journalEntryId", "accountId", "accountType", "partyId", "currency", "debit", "credit", "grossAllocation", "deductions", "heldEscrow", "releasedEscrow", "reserved", "confirmedPayouts"],
            ["debit", "credit", "grossAllocation", "deductions", "heldEscrow", "releasedEscrow", "reserved", "confirmedPayouts"],
            ["grossAllocation", "deductions", "heldEscrow", "releasedEscrow", "reserved", "confirmedPayouts"], false),
        "account-balances" => new(
            ["accountId", "partyId", "accountType", "currency", "debit", "credit", "balance", "reserved", "available"],
            ["debit", "credit", "balance", "reserved", "available"], ["debit", "credit", "reserved"], false),
        "trial-balance" => new(["currency", "debit", "credit", "difference"],
            ["debit", "credit", "difference"], ["debit", "credit", "difference"], true),
        "payout-instructions" => new(
            ["instructionId", "batchId", "reference", "businessDate", "partyId", "currency", "amount", "reserved", "paid"],
            ["amount", "reserved", "paid"], ["amount", "reserved", "paid"], false),
        "batch-summary" => new(
            ["batchId", "businessDate", "currency", "state", "entryCount", "instructionCount", "instructions", "reserved", "paid", "payableDebits", "bankCredits", "isReconciled"],
            ["instructions", "reserved", "paid", "payableDebits", "bankCredits"],
            ["instructions", "reserved", "paid", "payableDebits", "bankCredits"], true),
        _ => throw new ReportException(404, "REPORT_NOT_FOUND"),
    };
}

public interface IReportStore
{
    Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> ReadAsync(string kind, ReportFilter filter, CancellationToken ct);
    Task<ReportExport?> FindExportAsync(string actorId, string key, string requestHash, CancellationToken ct);
    Task<ReportExport?> GetExportAsync(Guid id, CancellationToken ct);
    Task SaveExportAsync(ReportExport export, string actorId, string key, string requestHash, CancellationToken ct);
}
