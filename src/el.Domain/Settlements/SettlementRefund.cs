using el.Domain.Common;
using el.Domain.Ledger;

namespace el.Domain.Settlements;

/// <summary>A settlement cannot be refunded. <see cref="Code"/> is a stable API error code.</summary>
public sealed class RefundException(string code, string message) : Exception(message)
{
    public const string AmountMismatch = "REFUND_AMOUNT_MISMATCH";
    public const string AlreadyRefunded = "REFUND_ALREADY_EXISTS";
    public const string HoldReleased = "HOLD_ALREADY_RELEASED";
    public const string InBatch = "SETTLEMENT_IN_BATCH";

    public string Code { get; } = code;
}

/// <summary>
/// Full pre-clearing refund of a settlement. The refund reverses the original stored journal lines
/// (same accounts and amounts, opposite sides) and links to the original entry. It never recalculates
/// from current rules, so every original component, including fees, is reversed exactly.
/// </summary>
public static class SettlementRefund
{
    /// <summary>The unique journal reference of a settlement's refund, so it can be posted only once.</summary>
    public static JournalReference Reference(Guid settlementId) =>
        JournalReference.Parse("REFUND:" + settlementId.ToString("N").ToUpperInvariant());

    /// <summary>A refund must request exactly the gross amount, and no hold may have left Held.</summary>
    /// <exception cref="RefundException">The amount differs from gross, or a hold was released or refunded.</exception>
    public static void EnsureEligible(Money requested, Money gross, IEnumerable<EscrowHold> holds)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(gross);
        ArgumentNullException.ThrowIfNull(holds);

        if (requested != gross)
        {
            throw new RefundException(RefundException.AmountMismatch, "Only a full refund of the original gross amount is supported.");
        }

        var states = holds.Select(hold => hold.State).ToArray();

        if (states.Contains(EscrowHoldState.Released))
        {
            throw new RefundException(RefundException.HoldReleased, "An escrow hold of this settlement was already released.");
        }

        if (states.Contains(EscrowHoldState.Refunded))
        {
            throw new RefundException(RefundException.AlreadyRefunded, "This settlement was already refunded.");
        }
    }

    /// <summary>Mirrors the original lines with opposite sides and links the entry to the original.</summary>
    /// <exception cref="JournalValidationException">The mirrored lines break a ledger invariant, such as an inactive account.</exception>
    public static JournalEntry CreateReversal(JournalEntryId id, Guid settlementId, JournalEntryId originalEntryId,
        IReadOnlyList<JournalLineDraft> originalLines)
    {
        ArgumentNullException.ThrowIfNull(originalLines);
        if (settlementId == Guid.Empty) throw new ArgumentException("A settlement ID must not be empty.", nameof(settlementId));

        var mirrored = originalLines.Select(line =>
        {
            ArgumentNullException.ThrowIfNull(line, nameof(originalLines));
            return new JournalLineDraft(line.Account, line.Side == EntrySide.Debit ? EntrySide.Credit : EntrySide.Debit, line.Amount);
        }).ToArray();

        return JournalEntry.Create(id, Reference(settlementId), "Full refund of settlement", mirrored, reversalOf: originalEntryId);
    }
}
