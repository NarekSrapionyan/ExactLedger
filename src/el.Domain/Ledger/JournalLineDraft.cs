using el.Domain.Common;

namespace el.Domain.Ledger;

/// <summary>A proposed journal line before the entry is validated.</summary>
public sealed record JournalLineDraft
{
    public JournalLineDraft(Account account, EntrySide side, Money amount)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(amount);

        if (!Enum.IsDefined(side))
        {
            throw new ArgumentOutOfRangeException(nameof(side), side, "The entry side is not defined.");
        }

        Account = account;
        Side = side;
        Amount = amount;
    }

    public Account Account { get; }

    public EntrySide Side { get; }

    public Money Amount { get; }
}
