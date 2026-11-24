using el.Application.Abstractions;
using el.Application.Audit;
using el.Domain.Clearing;
using el.Domain.Common;
using el.Domain.Ledger;

namespace el.Application.Clearing;

public sealed record StoredExport(byte[] Content, ExportMetadata Metadata);

public sealed record ExportResult(StoredExport Export, ClearingBatch Batch, bool Created);

public interface IClearingExportStore
{
    Task<StoredExport?> FindAsync(Guid batchId, CancellationToken ct);

    Task AddAsync(Guid batchId, string currency, byte[] content, ExportMetadata metadata, CancellationToken ct);
}

/// <summary>
/// Persists the deterministic payout CSV once and derives Exported from its row. Exporting posts nothing,
/// debits no payable and releases no reservation.
/// </summary>
public sealed class ClearingExportService(IClearingStore store, IClearingExportStore exports, IUnitOfWork unitOfWork,
    IMonetaryOperationLock operationLock, IAuditTrail audit, TimeProvider clock)
{
    public Task<ExportResult> ExportAsync(Guid batchId, CancellationToken ct) =>
        unitOfWork.ExecuteAsync(async token =>
        {
            await operationLock.AcquireAsync(token);

            var batch = await store.FindBatchAsync(batchId, token) ?? throw ClearingErrors.NotFound();
            var stored = await exports.FindAsync(batchId, token);

            if (stored is not null)
            {
                return new ExportResult(stored, batch, false);
            }

            var definition = ClearingSupport.Definition(batch.Currency);
            var artifact = PayoutCsv.Build(definition, batch.Instructions.Select(item => new PayoutCsvRow(
                item.Reference, batch.BusinessDate, new PartyId(item.PartyId), Money.FromMinorUnits(item.AmountMinor, definition.Code))));
            var metadata = new ExportMetadata(artifact.Sha256, artifact.Count, artifact.TotalMinorUnits, Now());
            var content = artifact.Content.ToArray();

            await exports.AddAsync(batch.Id, batch.Currency, content, metadata, token);
            await audit.RecordAsync("clearing.batch.exported", batch.Id.ToString(), token);

            return new ExportResult(new StoredExport(content, metadata), (await store.FindBatchAsync(batch.Id, token))!, true);
        }, ct);

    public async Task<StoredExport> DownloadAsync(Guid batchId, CancellationToken ct)
    {
        _ = await store.FindBatchAsync(batchId, ct) ?? throw ClearingErrors.NotFound();

        return await exports.FindAsync(batchId, ct)
            ?? throw ClearingErrors.Conflict("BATCH_NOT_EXPORTED", "The batch has not been exported yet.");
    }

    private DateTimeOffset Now()
    {
        var now = clock.GetUtcNow();

        return new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
    }
}
