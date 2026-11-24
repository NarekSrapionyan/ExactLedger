using el.Application.Settlements;
using Microsoft.EntityFrameworkCore;

namespace el.Infrastructure.Persistence;

internal sealed class SettlementQueries(ExactLedgerDbContext db) : ISettlementQueries
{
    public async Task<IReadOnlyList<SettlementListRow>> ListAsync(SettlementVisibility scope, string? currency,
        int offset, int limit, CancellationToken ct)
    {
        var query = db.Set<SettlementRecord>().AsNoTracking();
        if (!scope.All)
            query = query.Where(s => (scope.ClientId != null && s.ClientId == scope.ClientId)
                || (scope.PartyId != null && db.Set<SettlementAllocationRecord>().Any(a => a.SettlementId == s.Id && a.PartyId == scope.PartyId)));
        if (currency is not null) query = query.Where(s => s.Currency == currency);
        var rows = await query.OrderByDescending(s => s.PostedAtUtc).ThenByDescending(s => s.Id)
            .Skip(offset).Take(limit).Select(s => new { s.Id, s.PostedAtUtc, s.Currency, s.GrossMinor, s.ClientId, s.TradeId }).ToListAsync(ct);
        var ids = rows.Select(s => s.Id).ToArray();
        var own = scope.PartyId is null ? [] : await db.Set<SettlementAllocationRecord>().AsNoTracking()
            .Where(a => ids.Contains(a.SettlementId) && a.PartyId == scope.PartyId).OrderBy(a => a.Ordinal).ToListAsync(ct);
        return rows.Select(s => new SettlementListRow(s.Id, s.PostedAtUtc, s.Currency, s.GrossMinor, s.ClientId, s.TradeId,
            own.Where(a => a.SettlementId == s.Id).Select(a => new SettlementOwnAllocation(a.Component, a.AmountMinor)).ToArray())).ToArray();
    }
}
