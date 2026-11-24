using System.Text.Json;
using el.Application.Reporting;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace el.Infrastructure.Persistence;

internal sealed class ReportJsonRow
{
    public string Data { get; set; } = string.Empty;
}

internal sealed class ReportExportRow
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = string.Empty;
    public Guid? PartyId { get; set; }
    public string RequestHash { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public int RecordCount { get; set; }
    public string Totals { get; set; } = string.Empty;
    public byte[] Content { get; set; } = [];
}

internal sealed class ReportStore(ExactLedgerDbContext db) : IReportStore
{
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> ReadAsync(string kind, ReportFilter filter, CancellationToken ct)
    {
        var sql = kind switch
        {
            "journal-register" => ReportQueries.Journal,
            "payee-statement" => ReportQueries.Statement,
            "account-balances" => ReportQueries.Balances,
            "trial-balance" => ReportQueries.TrialBalance,
            "payout-instructions" => ReportQueries.Instructions,
            "batch-summary" => ReportQueries.Batches,
            _ => throw new ReportException(404, "REPORT_NOT_FOUND"),
        };
        var statement = ReportQueries.Common + sql;
        var rows = await db.Database.SqlQueryRaw<ReportJsonRow>(statement,
            Parameter("currency", NpgsqlDbType.Text, filter.Currency), Parameter("party", NpgsqlDbType.Uuid, filter.PartyId),
            Parameter("from", NpgsqlDbType.Date, filter.From), Parameter("to", NpgsqlDbType.Date, filter.To),
            Parameter("cut", NpgsqlDbType.Bigint, filter.CutSequence), Parameter("batch", NpgsqlDbType.Uuid, filter.BatchId)).ToListAsync(ct);
        return rows.Select(row => (IReadOnlyDictionary<string, string>)JsonSerializer.Deserialize<Dictionary<string, string>>(row.Data)!).ToArray();
    }

    private static NpgsqlParameter Parameter(string name, NpgsqlDbType type, object? value) => new(name, type) { Value = value ?? DBNull.Value };

    public async Task<ReportExport?> FindExportAsync(string actorId, string key, string requestHash, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<ReportExportRow>($"""
            SELECT id AS "Id", kind AS "Kind", party_id AS "PartyId", request_hash AS "RequestHash", generated_at_utc AS "GeneratedAtUtc",
                   content_sha256 AS "Sha256", record_count AS "RecordCount", totals_json::text AS "Totals", content AS "Content"
            FROM ledger.report_exports WHERE actor_id = {actorId} AND idempotency_key = {key}
            """).ToListAsync(ct);
        var row = rows.SingleOrDefault();
        if (row is not null && row.RequestHash != requestHash) throw new ReportException(409, "EXPORT_KEY_CONFLICT");
        return row is null ? null : Map(row);
    }

    public async Task<ReportExport?> GetExportAsync(Guid id, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<ReportExportRow>($"""
            SELECT id AS "Id", kind AS "Kind", party_id AS "PartyId", request_hash AS "RequestHash", generated_at_utc AS "GeneratedAtUtc",
                   content_sha256 AS "Sha256", record_count AS "RecordCount", totals_json::text AS "Totals", content AS "Content"
            FROM ledger.report_exports WHERE id = {id}
            """).ToListAsync(ct);
        return rows.Count == 0 ? null : Map(rows[0]);
    }

    public async Task SaveExportAsync(ReportExport export, string actorId, string key, string requestHash, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Export writes require a transaction.");
        var totals = JsonSerializer.Serialize(export.Totals);
        try
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO ledger.report_exports (id, kind, party_id, actor_id, idempotency_key, request_hash,
                    generated_at_utc, content_sha256, record_count, totals_json, content)
                VALUES ({export.Id}, {export.Kind}, {export.PartyId}, {actorId}, {key}, {requestHash},
                    {export.GeneratedAtUtc}, {export.Sha256}, {export.RecordCount}, CAST({totals} AS jsonb), {export.Content})
                """, ct);
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            throw new ReportException(404, "NOT_FOUND");
        }
    }

    private static ReportExport Map(ReportExportRow row) => new(row.Id, row.Kind, row.PartyId, row.GeneratedAtUtc,
        row.Sha256, row.RecordCount, JsonSerializer.Deserialize<ReportCurrencyTotal[]>(row.Totals)!, row.Content);
}
