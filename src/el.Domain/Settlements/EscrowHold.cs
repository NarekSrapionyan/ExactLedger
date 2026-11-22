using el.Domain.Common;
using el.Domain.Ledger;

namespace el.Domain.Settlements;

/// <summary>Lifecycle of an escrow hold. A hold leaves Held exactly once, to Released or Refunded.</summary>
public enum EscrowHoldState
{
    Held = 1,
    Released = 2,
    Refunded = 3,
}

/// <summary>A hold cannot change state now. <see cref="Code"/> is a stable API error code.</summary>
public sealed class EscrowHoldException(string code, string message) : Exception(message)
{
    public const string NotDue = "HOLD_NOT_DUE";
    public const string NotReleasable = "HOLD_NOT_RELEASABLE";

    public string Code { get; } = code;
}

/// <summary>
/// An escrow hold created by a settlement: the held amount of one beneficiary, the escrow and
/// payable accounts of that beneficiary, and the UTC time from which it may be released.
/// Only a full release is supported: the whole held amount moves from escrow to payable.
/// </summary>
public sealed class EscrowHold
{
    public EscrowHold(Guid id, Guid settlementId, Guid partyId, AccountId escrowAccountId, AccountId payableAccountId,
        Money held, DateTimeOffset dueAtUtc, EscrowHoldState state)
    {
        if (id == Guid.Empty) throw new ArgumentException("A hold ID must not be empty.", nameof(id));
        if (settlementId == Guid.Empty) throw new ArgumentException("A settlement ID must not be empty.", nameof(settlementId));
        if (partyId == Guid.Empty) throw new ArgumentException("A party ID must not be empty.", nameof(partyId));
        if (escrowAccountId == default) throw new ArgumentException("The escrow account must be set.", nameof(escrowAccountId));
        if (payableAccountId == default) throw new ArgumentException("The payable account must be set.", nameof(payableAccountId));
        ArgumentNullException.ThrowIfNull(held);
        if (!held.IsPositive) throw new ArgumentOutOfRangeException(nameof(held), "A hold amount must be positive.");
        if (dueAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("The due time must be UTC.", nameof(dueAtUtc));
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state), state, "The hold state is not defined.");

        Id = id;
        SettlementId = settlementId;
        PartyId = partyId;
        EscrowAccountId = escrowAccountId;
        PayableAccountId = payableAccountId;
        Held = held;
        DueAtUtc = dueAtUtc;
        State = state;
    }

    public Guid Id { get; }

    public Guid SettlementId { get; }

    public Guid PartyId { get; }

    public AccountId EscrowAccountId { get; }

    public AccountId PayableAccountId { get; }

    /// <summary>The original held amount, which a full release moves in one posting.</summary>
    public Money Held { get; }

    public DateTimeOffset DueAtUtc { get; }

    public EscrowHoldState State { get; }

    /// <summary>The unique journal reference of this hold's release, so it can be posted only once.</summary>
    public JournalReference ReleaseReference => JournalReference.Parse("ESCROW-RELEASE:" + Id.ToString("N").ToUpperInvariant());

    public bool IsDue(DateTimeOffset nowUtc) => nowUtc >= DueAtUtc;

    /// <exception cref="EscrowHoldException">The hold is no longer held or is not due yet.</exception>
    public void EnsureReleasable(DateTimeOffset nowUtc)
    {
        if (State != EscrowHoldState.Held)
        {
            throw new EscrowHoldException(EscrowHoldException.NotReleasable,
                FormattableString.Invariant($"The hold is {State} and can no longer be released."));
        }

        if (!IsDue(nowUtc))
        {
            throw new EscrowHoldException(EscrowHoldException.NotDue, "The hold is not due for release yet.");
        }
    }

    /// <summary>Dr Escrow / Cr Payable for the full held amount, after checking the hold is releasable.</summary>
    /// <exception cref="EscrowHoldException">The hold is no longer held or is not due yet.</exception>
    /// <exception cref="ArgumentException">The accounts are not this hold's escrow and payable accounts.</exception>
    public JournalEntry CreateReleaseEntry(JournalEntryId id, Account escrowAccount, Account payableAccount, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(escrowAccount);
        ArgumentNullException.ThrowIfNull(payableAccount);

        if (escrowAccount.Id != EscrowAccountId || escrowAccount.Type != AccountType.Escrow)
        {
            throw new ArgumentException("The escrow account does not belong to this hold.", nameof(escrowAccount));
        }

        if (payableAccount.Id != PayableAccountId || payableAccount.Type != AccountType.Payable)
        {
            throw new ArgumentException("The payable account does not belong to this hold.", nameof(payableAccount));
        }

        EnsureReleasable(nowUtc);

        return JournalEntry.Create(id, ReleaseReference, "Escrow release",
        [
            new JournalLineDraft(escrowAccount, EntrySide.Debit, Held),
            new JournalLineDraft(payableAccount, EntrySide.Credit, Held),
        ]);
    }
}
