using el.Application.Abstractions;
using el.Application.Settlements;
using el.Domain.Common;
using el.Domain.Ledger;
using el.Domain.Settlements;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace el.Infrastructure.Persistence;

internal sealed class EscrowStore(ExactLedgerDbContext db, MonetaryOperationLockOptions? options = null) : IEscrowStore
{
    public async Task<EscrowHold?> GetHoldAsync(Guid holdId, CancellationToken ct) =>
        (await HoldsAsync(hold => hold.Id == holdId, ct)).SingleOrDefault();

    public Task<IReadOnlyList<EscrowHold>> HoldsForSettlementAsync(Guid settlementId, CancellationToken ct) =>
        HoldsAsync(hold => hold.SettlementId == settlementId, ct);

    public async Task<IReadOnlyList<Guid>> DueHoldIdsAsync(DateTimeOffset asOfUtc, int limit, CancellationToken ct) =>
        await db.Set<EscrowHoldRecord>().AsNoTracking()
            .Where(hold => hold.DueAtUtc <= asOfUtc && !db.Set<EscrowHoldTransitionRecord>().Any(row => row.HoldId == hold.Id))
            .OrderBy(hold => hold.DueAtUtc).ThenBy(hold => hold.Id)
            .Select(hold => hold.Id).Take(limit).ToListAsync(ct);

    public async Task<EscrowRelease?> FindReleaseAsync(Guid holdId, CancellationToken ct) =>
        await (from transition in db.Set<EscrowHoldTransitionRecord>().AsNoTracking()
               join hold in db.Set<EscrowHoldRecord>().AsNoTracking() on transition.HoldId equals hold.Id
               where transition.HoldId == holdId && transition.State == "Released"
               select new EscrowRelease(hold.Id, hold.SettlementId, hold.PartyId, transition.JournalEntryId,
                   transition.Currency, transition.AmountMinor, transition.OccurredAtUtc))
            .SingleOrDefaultAsync(ct);

    public Task<Account?> GetAccountAsync(AccountId accountId, CancellationToken ct) =>
        db.Set<Account>().AsNoTracking().SingleOrDefaultAsync(account => account.Id == accountId, ct);

    public async Task<bool> IsPartyActiveAsync(Guid partyId, CancellationToken ct)
    {
        try
        {
            // Keep the party from being suspended between this check and commit.
            var rows = await db.Database.SqlQuery<AccountStatusRow>($"""
                SELECT id AS "Id", status AS "Status" FROM ledger.parties WHERE id = {partyId} FOR SHARE
                """).ToListAsync(ct);
            return rows.Count == 1 && rows[0].Status == (int)PartyStatus.Active;
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            throw new LedgerLockTimeoutException((options ?? new MonetaryOperationLockOptions()).WaitTimeout, error);
        }
    }

    public Task AddReleaseAsync(EscrowRelease release, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(release);
        ct.ThrowIfCancellationRequested();
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Escrow releases require a transaction.");

        db.Add(new EscrowHoldTransitionRecord
        {
            HoldId = release.HoldId, State = "Released", JournalEntryId = release.JournalEntryId,
            Currency = release.Currency, AmountMinor = release.AmountMinor, OccurredAtUtc = release.ReleasedAtUtc,
        });

        return Task.CompletedTask;
    }

    private async Task<IReadOnlyList<EscrowHold>> HoldsAsync(System.Linq.Expressions.Expression<Func<EscrowHoldRecord, bool>> filter, CancellationToken ct)
    {
        var rows = await (from hold in db.Set<EscrowHoldRecord>().AsNoTracking().Where(filter)
                          join transition in db.Set<EscrowHoldTransitionRecord>().AsNoTracking() on hold.Id equals transition.HoldId into transitions
                          from transition in transitions.DefaultIfEmpty()
                          orderby hold.AllocationOrdinal
                          select new { Hold = hold, State = transition == null ? null : transition.State })
            .ToListAsync(ct);

        return rows.Select(row => new EscrowHold(row.Hold.Id, row.Hold.SettlementId, row.Hold.PartyId,
                new AccountId(row.Hold.EscrowAccountId), new AccountId(row.Hold.PayableAccountId),
                Money.FromMinorUnits(row.Hold.OriginalHeldMinor, CurrencyCode.Parse(row.Hold.Currency)),
                row.Hold.DueAtUtc.ToUniversalTime(),
                row.State is null ? EscrowHoldState.Held : Enum.Parse<EscrowHoldState>(row.State)))
            .ToArray();
    }
}
