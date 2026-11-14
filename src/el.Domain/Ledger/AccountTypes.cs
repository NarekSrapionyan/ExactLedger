namespace el.Domain.Ledger;

/// <summary>Rules that follow from an account's type.</summary>
public static class AccountTypes
{
    /// <summary>Payable and escrow accounts belong to exactly one party; system accounts belong to none.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The type is not defined.</exception>
    public static bool IsPartyOwned(AccountType type) => type switch
    {
        AccountType.Payable or AccountType.Escrow => true,
        AccountType.Clearing or AccountType.Revenue => false,
        _ => throw Undefined(type),
    };

    /// <summary>The side on which the account's balance normally increases.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The type is not defined.</exception>
    public static EntrySide NormalSide(AccountType type) => type switch
    {
        AccountType.Clearing => EntrySide.Debit,
        AccountType.Payable or AccountType.Escrow or AccountType.Revenue => EntrySide.Credit,
        _ => throw Undefined(type),
    };

    private static ArgumentOutOfRangeException Undefined(AccountType type) =>
        new(nameof(type), type, "The account type is not defined.");
}
