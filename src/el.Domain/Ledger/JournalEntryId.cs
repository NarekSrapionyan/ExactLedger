namespace el.Domain.Ledger;

/// <summary>Identity of a journal entry. A non-empty GUID supplied by the caller.</summary>
public readonly record struct JournalEntryId
{
    public JournalEntryId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A journal entry ID must not be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString("D");
}
