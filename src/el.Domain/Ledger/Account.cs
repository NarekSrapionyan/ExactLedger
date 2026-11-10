using el.Domain.Common;

namespace el.Domain.Ledger;

/// <summary>
/// A ledger account in exactly one currency. Its ID, code, type, currency and party never change.
/// Payable and escrow accounts belong to one party, so each party has separate payable and escrow
/// accounts per currency; clearing and revenue accounts are system accounts without a party.
/// Uniqueness of a party's account per type and currency is enforced by persistence (BE-007).
/// Balances are derived from journal lines (BE-008), not stored on the account.
/// </summary>
public sealed class Account : Entity<AccountId>
{
    public Account(AccountId id, AccountCode code, AccountType type, CurrencyCode currency, PartyId? partyId)
        : base(id)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(currency);

        var partyOwned = AccountTypes.IsPartyOwned(type);

        if (partyOwned && partyId is null)
        {
            throw new ArgumentException(
                FormattableString.Invariant($"A {type} account must belong to a party."), nameof(partyId));
        }

        if (!partyOwned && partyId is not null)
        {
            throw new ArgumentException(
                FormattableString.Invariant($"A {type} account must not belong to a party."), nameof(partyId));
        }

        if (partyId == default(PartyId))
        {
            throw new ArgumentException("The party ID must not be empty.", nameof(partyId));
        }

        Code = code;
        Type = type;
        Currency = currency;
        PartyId = partyId;
        Status = AccountStatus.Active;
    }

    public AccountCode Code { get; }

    public AccountType Type { get; }

    public CurrencyCode Currency { get; }

    public PartyId? PartyId { get; }

    public AccountStatus Status { get; private set; }

    public bool IsActive => Status == AccountStatus.Active;

    public EntrySide NormalSide => AccountTypes.NormalSide(Type);

    /// <exception cref="InvalidOperationException">The account is already closed.</exception>
    public void Close()
    {
        if (Status == AccountStatus.Closed)
        {
            throw new InvalidOperationException("The account is already closed.");
        }

        Status = AccountStatus.Closed;
    }
}
