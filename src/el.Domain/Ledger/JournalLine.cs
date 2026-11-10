using el.Domain.Common;

namespace el.Domain.Ledger;

/// <summary>
/// An immutable line of a validated journal entry. The amount is always positive; the side says
/// whether it debits or credits the account. Line numbers are 1-based and unique within the entry.
/// </summary>
public sealed record JournalLine
{
    internal JournalLine(int lineNumber, AccountId accountId, EntrySide side, Money amount)
    {
        LineNumber = lineNumber;
        AccountId = accountId;
        Side = side;
        Amount = amount;
    }

    public int LineNumber { get; }

    public AccountId AccountId { get; }

    public EntrySide Side { get; }

    public Money Amount { get; }
}
