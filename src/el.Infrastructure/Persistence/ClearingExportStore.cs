using System.Globalization;
using el.Application.Clearing;
using Microsoft.EntityFrameworkCore;

namespace el.Infrastructure.Persistence;

internal sealed class ClearingExportStore(ExactLedgerDbContext db) : IClearingExportStore
{
    public async Task<StoredExport?> FindAsync(Guid batchId, CancellationToken ct)
    {
        var row = (await db.Database.SqlQuery<ExportContentRow>($"""
            SELECT e.content AS "Content", e.content_sha256 AS "Sha256", e.record_count AS "RecordCount",
                   e.total_minor::text AS "TotalMinor", e.generated_at_utc AS "GeneratedAtUtc"
            FROM ledger.clearing_batch_exports e WHERE e.batch_id = {batchId}
            """).ToListAsync(ct)).SingleOrDefault();

        return row is null ? null : new StoredExport(row.Content, new ExportMetadata(row.Sha256, row.RecordCount,
            ExactAggregateQueries.ParseExactInteger(row.TotalMinor), row.GeneratedAtUtc));
    }

    public async Task AddAsync(Guid batchId, string currency, byte[] content, ExportMetadata metadata, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Clearing writes require a transaction.");
        }

        var total = metadata.TotalMinorUnits.ToString(CultureInfo.InvariantCulture);

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO ledger.clearing_batch_exports (batch_id, content, content_sha256, record_count, total_minor, currency, generated_at_utc)
            VALUES ({batchId}, {content}, {metadata.Sha256}, {metadata.RecordCount}, CAST({total} AS numeric), {currency}, {metadata.GeneratedAtUtc})
            """, ct);
    }
}
