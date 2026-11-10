namespace el.Domain.Ledger;

/// <summary>Identity of an account. A non-empty GUID supplied by the caller.</summary>
public readonly record struct AccountId
{
    public AccountId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An account ID must not be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString("D");
}
