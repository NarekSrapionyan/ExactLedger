using el.Api.Authentication;
using el.Api.Errors;
using el.Application.Reporting;
using el.Application.Security;

namespace el.Api.Reporting;

/// <summary>A report page; totals and record count cover the full filtered report.</summary>
public sealed record ReportResponse(string Kind, ReportFilter Filter, IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, string>> Rows, int RecordCount, int Offset, int Limit,
    IReadOnlyList<ReportCurrencyTotal> Totals);

/// <summary>Persisted CSV metadata; the download returns the original bytes.</summary>
public sealed record ReportExportResponse(Guid Id, string Kind, DateTimeOffset GeneratedAtUtc, string Sha256,
    int RecordCount, IReadOnlyList<ReportCurrencyTotal> Totals, string DownloadUrl);

public static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1/reports").WithTags("Reports").RequireAuthorization();
        group.MapGet("/{kind}", ReadAsync).WithName("ReadFinancialReport").Produces<ReportResponse>();
        group.MapPost("/{kind}/exports", ExportAsync).WithName("ExportFinancialReport").Produces<ReportExportResponse>(201);
        group.MapGet("/exports/{id:guid}", MetadataAsync).WithName("ReadReportExport").Produces<ReportExportResponse>();
        group.MapGet("/exports/{id:guid}/download", DownloadAsync).WithName("DownloadReportExport").Produces(200, contentType: "text/csv");
        group.ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(503);
        return endpoints;
    }

    /// <summary>Read a filtered report. Finance/Auditor read ledger reports; Payees read only their party's balances, statement and instructions.</summary>
    /// <remarks>Kinds: account-balances, trial-balance, journal-register, payee-statement, payout-instructions, batch-summary.</remarks>
    public static async Task<IResult> ReadAsync(string kind, string? currency, Guid? partyId, DateOnly? from, DateOnly? to,
        long? cutSequence, Guid? batchId, int? offset, int? limit, HttpContext context, ReportService service, CancellationToken ct)
    {
        var start = offset ?? 0;
        var count = limit ?? 50;
        if (start < 0 || count is < 1 or > 1000) return ApiErrorResponses.Validation(context,
            new Dictionary<string, string[]> { ["pagination"] = ["Offset must be non-negative; limit must be 1 to 1000."] });
        var filter = Scope(kind, new(currency, partyId, from, to, cutSequence, batchId), context);
        var report = await service.ReadAsync(kind, filter, ct, start, count);
        return Results.Ok(new ReportResponse(kind, filter, report.Columns, report.Rows,
            report.RecordCount, start, count, report.Totals));
    }

    /// <summary>Freeze a complete CSV report. Idempotency-Key retries return the original artifact, timestamp and hash.</summary>
    /// <remarks>Kinds: account-balances, trial-balance, journal-register, payee-statement, payout-instructions, batch-summary.</remarks>
    public static async Task<IResult> ExportAsync(string kind, ReportFilter request, HttpContext context, ReportService service, CancellationToken ct)
    {
        var filter = Scope(kind, request, context);
        var keys = context.Request.Headers["Idempotency-Key"];
        if (keys.Count != 1) throw new ReportException(400, "INVALID_IDEMPOTENCY_KEY");
        var export = await service.ExportAsync(kind, filter, RequestIdentity.Get(context).Id, keys[0]!, ct);
        return Results.Created("/v1/reports/exports/" + export.Id, Metadata(export));
    }

    /// <summary>Read frozen CSV metadata, including totals by currency.</summary>
    public static async Task<IResult> MetadataAsync(Guid id, HttpContext context, ReportService service, CancellationToken ct) =>
        Results.Ok(Metadata(await AuthorizedExportAsync(id, context, service, ct)));

    /// <summary>Download the original UTF-8 CSV bytes with their SHA-256 ETag.</summary>
    public static async Task<IResult> DownloadAsync(Guid id, HttpContext context, ReportService service, CancellationToken ct)
    {
        var export = await AuthorizedExportAsync(id, context, service, ct);
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.ETag = "\"" + export.Sha256 + "\"";
        return Results.File(export.Content, "text/csv; charset=utf-8", export.Kind + "-" + export.Id + ".csv");
    }

    private static ReportFilter Scope(string kind, ReportFilter filter, HttpContext context)
    {
        var identity = RequestIdentity.Get(context);
        var definition = ReportDefinitions.Get(kind);
        var permission = definition.LedgerOnly ? Permission.ReadLedger : Permission.ReadBalancesAndPayouts;
        if (!identity.Roles.Any(PermissionMatrix.RolesFor(permission).Contains)) throw new ReportException(403, "FORBIDDEN");
        var global = identity.Roles.Any(role => role is IdentityRoles.FinanceOperator or IdentityRoles.Auditor);
        if (!global)
        {
            if (identity.PartyId is null || (filter.PartyId is not null && filter.PartyId != identity.PartyId))
                throw new ReportException(404, "NOT_FOUND");
            filter = filter with { PartyId = identity.PartyId };
        }
        // A trial balance and a batch reconciliation must never become a misleading partial-party total.
        if (definition.LedgerOnly && filter.PartyId is not null) throw new ReportException(400, "INVALID_REPORT_FILTER");
        if (kind == "payee-statement" && filter.PartyId is null) throw new ReportException(400, "PARTY_REQUIRED");
        // Mutable reservation/export states do not have a historical journal sequence of their own.
        // Historical cuts apply only to the immutable journal and trial balance.
        if (filter.CutSequence is not null && kind is not ("journal-register" or "trial-balance"))
            throw new ReportException(400, "INVALID_REPORT_FILTER");
        if (kind == "account-balances" && (filter.From is not null || filter.To is not null || filter.BatchId is not null))
            throw new ReportException(400, "INVALID_REPORT_FILTER");
        return filter;
    }

    private static async Task<ReportExport> AuthorizedExportAsync(Guid id, HttpContext context, ReportService service, CancellationToken ct)
    {
        var export = await service.GetExportAsync(id, ct) ?? throw new ReportException(404, "NOT_FOUND");
        var scope = Scope(export.Kind, new(PartyId: export.PartyId), context);
        if (scope.PartyId != export.PartyId) throw new ReportException(404, "NOT_FOUND");
        return export;
    }

    private static ReportExportResponse Metadata(ReportExport export) => new(export.Id, export.Kind, export.GeneratedAtUtc,
        export.Sha256, export.RecordCount, export.Totals, $"/v1/reports/exports/{export.Id}/download");
}
