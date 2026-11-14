namespace el.Domain.Ledger;

/// <summary>Side of a journal line. Zero is deliberately not a valid value.</summary>
public enum EntrySide
{
    Debit = 1,
    Credit = 2,
}
