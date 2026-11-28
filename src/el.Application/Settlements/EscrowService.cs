using el.Application.Abstractions;
using el.Application.Audit;
using el.Application.Ledger;
using el.Domain.Ledger;
using el.Domain.Settlements;

namespace el.Application.Settlements;

/// <summary>The stored result of a hold's full release.</summary>
public sealed record EscrowRelease(Guid HoldId, Guid SettlementId, Guid PartyId, Guid JournalEntryId,
    string Currency, long AmountMinor, DateTimeOffset ReleasedAtUtc);

/// <summary>A due hold the scheduler skipped, with the reason. It stays Held and is retried next run.</summary>
public sealed record SkippedEscrowRelease(Guid HoldId, string Code);

public sealed record DueEscrowReleaseResult(IReadOnlyList<EscrowRelease> Released, IReadOnlyList<SkippedEscrowRelease> Skipped);

/// <summary>The hold does not exist.</summary>
public sealed class EscrowHoldNotFoundException(Guid holdId) : Exception("The escrow hold was not found.")
{
    public Guid HoldId { get; } = holdId;
}

public interface IEscrowStore
{
    Task<EscrowHold?> GetHoldAsync(Guid holdId, CancellationToken ct);

    Task<IReadOnlyList<EscrowHold>> HoldsForSettlementAsync(Guid settlementId, CancellationToken ct);

    /// <summary>Due Held IDs: eligible work first, then oldest due; blocked holds cannot starve a bounded run.</summary>
    Task<IReadOnlyList<Guid>> DueHoldIdsAsync(DateTimeOffset asOfUtc, int limit, CancellationToken ct);

    Task<EscrowRelease?> FindReleaseAsync(Guid holdId, CancellationToken ct);

    Task<Account?> GetAccountAsync(AccountId accountId, CancellationToken ct);

    /// <summary>True when the party exists and is active; the row is share-locked until commit.</summary>
    Task<bool> IsPartyActiveAsync(Guid partyId, CancellationToken ct);

    Task AddReleaseAsync(EscrowRelease release, CancellationToken ct);
}

public interface IEscrowQueries
{
    Task<IReadOnlyList<EscrowHold>> ListAsync(Guid? settlementId, Guid? partyId, string? currency,
        int offset, int limit, CancellationToken ct);
}

/// <summary>
/// Full release of due escrow holds: Dr Escrow / Cr Payable under the shared monetary lock, with
/// the journal entry, the release record and the audit event committed together. A hold is
/// released at most once; repeating the release returns the stored result.
/// </summary>
public sealed class EscrowService(IEscrowStore store, IUnitOfWork unitOfWork, IMonetaryOperationLock operationLock,
    JournalPostingService posting, IAuditTrail audit, TimeProvider clock)
{
    public const int MaxReleasesPerRun = 500;
    public const string PartyNotActive = "PARTY_NOT_ACTIVE";
    public const string AccountNotActive = "ACCOUNT_NOT_POSTABLE";

    /// <summary>Manual release of one hold by a Finance Operator or the scheduler.</summary>
    /// <exception cref="EscrowHoldNotFoundException">The hold does not exist.</exception>
    /// <exception cref="EscrowHoldException">The hold is not due, or it was refunded.</exception>
    public Task<EscrowRelease> ReleaseAsync(Guid holdId, CancellationToken ct) =>
        unitOfWork.ExecuteAsync(async token =>
        {
            await operationLock.AcquireAsync(token);

            var existing = await store.FindReleaseAsync(holdId, token);
            if (existing is not null) return existing;

            var hold = await store.GetHoldAsync(holdId, token) ?? throw new EscrowHoldNotFoundException(holdId);
            var now = Now();
            hold.EnsureReleasable(now);

            var problem = await EligibilityProblemAsync(hold, token);
            if (problem is not null) throw new EscrowHoldException(problem.Value.Code, problem.Value.Message);

            return await PostReleaseAsync(hold, now, token);
        }, ct);

    /// <summary>
    /// Scheduler entry point: processes up to <see cref="MaxReleasesPerRun"/> due holds, eligible first, then oldest due.
    /// Holds whose party or accounts are not active are skipped and stay Held.
    /// </summary>
    public Task<DueEscrowReleaseResult> ReleaseDueAsync(CancellationToken ct) =>
        unitOfWork.ExecuteAsync(async token =>
        {
            await operationLock.AcquireAsync(token);

            var now = Now();
            var released = new List<EscrowRelease>();
            var skipped = new List<SkippedEscrowRelease>();

            foreach (var holdId in await store.DueHoldIdsAsync(now, MaxReleasesPerRun, token))
            {
                var hold = await store.GetHoldAsync(holdId, token) ?? throw new EscrowHoldNotFoundException(holdId);
                var problem = await EligibilityProblemAsync(hold, token);

                if (problem is not null)
                {
                    skipped.Add(new SkippedEscrowRelease(hold.Id, problem.Value.Code));
                    continue;
                }

                released.Add(await PostReleaseAsync(hold, now, token));
            }

            await audit.RecordAsync("escrow.release-run", FormattableString.Invariant($"{released.Count}:{skipped.Count}"), token);

            return new DueEscrowReleaseResult(released.AsReadOnly(), skipped.AsReadOnly());
        }, ct);

    private async Task<(string Code, string Message)?> EligibilityProblemAsync(EscrowHold hold, CancellationToken ct)
    {
        if (!await store.IsPartyActiveAsync(hold.PartyId, ct))
            return (PartyNotActive, "The beneficiary is not active, so the hold cannot be released.");

        var escrow = await store.GetAccountAsync(hold.EscrowAccountId, ct);
        var payable = await store.GetAccountAsync(hold.PayableAccountId, ct);
        if (escrow is not { IsActive: true } || payable is not { IsActive: true })
            return (AccountNotActive, "The hold's escrow or payable account is not active.");

        return null;
    }

    private async Task<EscrowRelease> PostReleaseAsync(EscrowHold hold, DateTimeOffset now, CancellationToken ct)
    {
        var escrow = await store.GetAccountAsync(hold.EscrowAccountId, ct) ?? throw new EscrowHoldNotFoundException(hold.Id);
        var payable = await store.GetAccountAsync(hold.PayableAccountId, ct) ?? throw new EscrowHoldNotFoundException(hold.Id);
        var entry = hold.CreateReleaseEntry(new JournalEntryId(Guid.NewGuid()), escrow, payable, now);

        await posting.PostWithinTransactionAsync(entry, ct);

        var release = new EscrowRelease(hold.Id, hold.SettlementId, hold.PartyId, entry.Id.Value,
            hold.Held.Currency.Value, hold.Held.MinorUnits, now);
        await store.AddReleaseAsync(release, ct);
        await audit.RecordAsync("escrow.released", hold.Id.ToString(), ct);

        return release;
    }

    // PostgreSQL keeps microseconds; normalize so the first response equals every later read.
    private DateTimeOffset Now()
    {
        var now = clock.GetUtcNow();
        return new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
    }
}
