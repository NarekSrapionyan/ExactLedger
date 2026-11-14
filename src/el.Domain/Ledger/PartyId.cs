namespace el.Domain.Ledger;

/// <summary>Identity of a party. A non-empty GUID supplied by the caller.</summary>
public readonly record struct PartyId
{
    public PartyId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A party ID must not be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString("D");
}
