using System.Security.Cryptography;
using System.Text;
using el.Application.Abstractions;
using el.Application.Audit;
using el.Application.Ledger;
using el.Domain.Common;
using el.Domain.Ledger;
using el.Domain.Settlements;

namespace el.Application.Settlements;

/// <summary>The stored result of a full refund, also returned for an identical retry.</summary>
public sealed record SettlementRefundResult(Guid SettlementId, string ClientId, Guid JournalEntryId, Guid OriginalJournalEntryId,
    string Currency, long AmountMinor, DateTimeOffset RefundedAtUtc, IReadOnlyList<Guid> RefundedHoldIds);

public sealed record StoredRefundCommand(string RequestHash, SettlementRefundResult Refund);

/// <summary>One stored line of a posted journal entry.</summary>
public sealed record StoredJournalLine(AccountId AccountId, EntrySide Side, long AmountMinor);

/// <summary>The settlement does not exist or belongs to another client.</summary>
public sealed class SettlementNotFoundException() : Exception("The settlement was not found.");

/// <summary>
/// Whether a settlement's business entry is already assigned to a clearing batch. Refunds and the
/// daily cut both run under the shared monetary lock, so the answer cannot change while a refund
/// is deciding. BE-013.1 implements this against its batch membership.
/// </summary>
public interface ISettlementBatchMembership
{
    Task<bool> IsAssignedAsync(Guid settlementId, CancellationToken ct);
}

public interface IRefundStore
{
    Task<StoredRefundCommand?> FindCommandAsync(string clientId, string key, CancellationToken ct);

    Task<SettlementRefundResult?> FindRefundAsync(Guid settlementId, CancellationToken ct);

    Task<IReadOnlyList<StoredJournalLine>> JournalLinesAsync(Guid journalEntryId, CancellationToken ct);

    /// <summary>Stores the refund and its idempotency key and marks every hold of the settlement Refunded.</summary>
    Task AddAsync(SettlementRefundResult refund, string key, string requestHash, CancellationToken ct);
}

/// <summary>
/// Full pre-clearing refund of a settlement: under the shared monetary lock, the original stored
/// journal lines are reversed and linked to the original entry, holds are marked Refunded, and the
/// refund, its idempotency key, the posting and the audit event commit together.
/// </summary>
public sealed class RefundService(IRefundStore refunds, ISettlementStore settlements, IEscrowStore escrow,
    ISettlementBatchMembership batches, IUnitOfWork unitOfWork, IMonetaryOperationLock operationLock,
    JournalPostingService posting, IAuditTrail audit, TimeProvider clock)
{
    /// <exception cref="SettlementValidationException">The idempotency key is invalid.</exception>
    /// <exception cref="SettlementNotFoundException">The settlement does not exist or belongs to another client.</exception>
    /// <exception cref="SettlementConflictException">The key was used with a different request.</exception>
    /// <exception cref="RefundException">The settlement is not eligible for a full refund.</exception>
    public Task<SettlementRefundResult> RefundAsync(string clientId, Guid settlementId, Money amount, string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(amount);
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || !key.All(character => character is >= '!' and <= '~'))
            throw new SettlementValidationException("INVALID_IDEMPOTENCY_KEY", "Supply a visible ASCII Idempotency-Key of 1 to 128 characters.");

        var requestHash = RequestHash(settlementId, amount);

        return unitOfWork.ExecuteAsync(async token =>
        {
            await operationLock.AcquireAsync(token);

            var command = await refunds.FindCommandAsync(clientId, key, token);
            if (command is not null)
            {
                if (command.RequestHash != requestHash) throw new SettlementConflictException("IDEMPOTENCY_CONFLICT");
                return command.Refund;
            }

            var settlement = await settlements.GetAsync(settlementId, token);
            if (settlement is null || !string.Equals(settlement.Input.ClientId, clientId, StringComparison.Ordinal))
                throw new SettlementNotFoundException();

            if (await refunds.FindRefundAsync(settlementId, token) is not null)
                throw new RefundException(RefundException.AlreadyRefunded, "This settlement was already refunded.");

            if (await batches.IsAssignedAsync(settlementId, token))
                throw new RefundException(RefundException.InBatch, "The settlement is already assigned to a clearing batch.");

            var currency = CurrencyCode.Parse(settlement.Input.Currency);
            var holds = await escrow.HoldsForSettlementAsync(settlementId, token);
            SettlementRefund.EnsureEligible(amount, Money.FromMinorUnits(settlement.Input.GrossMinor, currency), holds);

            var lines = new List<JournalLineDraft>();
            foreach (var line in await refunds.JournalLinesAsync(settlement.JournalEntryId, token))
            {
                var account = await escrow.GetAccountAsync(line.AccountId, token)
                    ?? throw new InvalidOperationException("A posted journal line references a missing account.");
                lines.Add(new JournalLineDraft(account, line.Side, Money.FromMinorUnits(line.AmountMinor, currency)));
            }

            var entry = SettlementRefund.CreateReversal(new JournalEntryId(Guid.NewGuid()), settlementId,
                new JournalEntryId(settlement.JournalEntryId), lines);
            await posting.PostWithinTransactionAsync(entry, token);

            var refund = new SettlementRefundResult(settlementId, clientId, entry.Id.Value, settlement.JournalEntryId,
                currency.Value, settlement.Input.GrossMinor, Now(), holds.Select(hold => hold.Id).ToArray());
            await refunds.AddAsync(refund, key, requestHash, token);
            await audit.RecordAsync("settlement.refunded", settlementId.ToString(), token);

            return refund;
        }, ct);
    }

    private static string RequestHash(Guid settlementId, Money amount) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            FormattableString.Invariant($"refund|{settlementId:N}|{amount.Currency.Value}|{amount.MinorUnits}"))));

    // PostgreSQL keeps microseconds; normalize so the first response equals every later read.
    private DateTimeOffset Now()
    {
        var now = clock.GetUtcNow();
        return new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
    }
}
