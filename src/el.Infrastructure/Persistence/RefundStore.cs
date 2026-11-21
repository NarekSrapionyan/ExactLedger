using el.Application.Settlements;
using el.Domain.Ledger;
using Microsoft.EntityFrameworkCore;

namespace el.Infrastructure.Persistence;

internal sealed class RefundStore(ExactLedgerDbContext db) : IRefundStore
{
    public async Task<StoredRefundCommand?> FindCommandAsync(string clientId, string key, CancellationToken ct)
    {
        var row = await db.Set<SettlementRefundRecord>().AsNoTracking()
            .SingleOrDefaultAsync(refund => refund.ClientId == clientId && refund.IdempotencyKey == key, ct);
        return row is null ? null : new StoredRefundCommand(row.RequestHash, await ToResultAsync(row, ct));
    }

    public async Task<SettlementRefundResult?> FindRefundAsync(Guid settlementId, CancellationToken ct)
    {
        var row = await db.Set<SettlementRefundRecord>().AsNoTracking().SingleOrDefaultAsync(refund => refund.SettlementId == settlementId, ct);
        return row is null ? null : await ToResultAsync(row, ct);
    }

    public async Task<IReadOnlyList<StoredJournalLine>> JournalLinesAsync(Guid journalEntryId, CancellationToken ct)
    {
        var id = new JournalEntryId(journalEntryId);
        return await db.Set<JournalLineRecord>().AsNoTracking()
            .Where(line => line.EntryId == id)
            .OrderBy(line => line.LineNumber)
            .Select(line => new StoredJournalLine(line.AccountId, line.Side, line.AmountMinor))
            .ToListAsync(ct);
    }

    public async Task AddAsync(SettlementRefundResult refund, string key, string requestHash, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(refund);
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Refunds require a transaction.");

        db.Add(new SettlementRefundRecord
        {
            SettlementId = refund.SettlementId, ClientId = refund.ClientId, IdempotencyKey = key, RequestHash = requestHash,
            JournalEntryId = refund.JournalEntryId, Currency = refund.Currency, AmountMinor = refund.AmountMinor,
            RefundedAtUtc = refund.RefundedAtUtc,
        });

        var holds = await db.Set<EscrowHoldRecord>().AsNoTracking()
            .Where(hold => hold.SettlementId == refund.SettlementId).ToListAsync(ct);

        foreach (var hold in holds)
        {
            db.Add(new EscrowHoldTransitionRecord
            {
                HoldId = hold.Id, State = "Refunded", JournalEntryId = refund.JournalEntryId,
                Currency = hold.Currency, AmountMinor = hold.OriginalHeldMinor, OccurredAtUtc = refund.RefundedAtUtc,
            });
        }
    }

    private async Task<SettlementRefundResult> ToResultAsync(SettlementRefundRecord row, CancellationToken ct)
    {
        var settlement = await db.Set<SettlementRecord>().AsNoTracking().SingleAsync(item => item.Id == row.SettlementId, ct);
        var holdIds = await (from transition in db.Set<EscrowHoldTransitionRecord>().AsNoTracking()
                             join hold in db.Set<EscrowHoldRecord>().AsNoTracking() on transition.HoldId equals hold.Id
                             where hold.SettlementId == row.SettlementId && transition.State == "Refunded"
                             orderby hold.AllocationOrdinal
                             select hold.Id).ToListAsync(ct);

        return new SettlementRefundResult(row.SettlementId, row.ClientId, row.JournalEntryId, settlement.JournalEntryId,
            row.Currency, row.AmountMinor, row.RefundedAtUtc.ToUniversalTime(), holdIds.AsReadOnly());
    }
}

/// <summary>
/// No clearing batches exist before BE-013, so no settlement is assigned to one. BE-013.1 replaces this
/// registration with a check against its batch membership; the shared monetary lock already keeps a
/// refund and a batch cut from interleaving.
/// </summary>
internal sealed class NoSettlementBatchMembership : ISettlementBatchMembership
{
    public Task<bool> IsAssignedAsync(Guid settlementId, CancellationToken ct) => Task.FromResult(false);
}
