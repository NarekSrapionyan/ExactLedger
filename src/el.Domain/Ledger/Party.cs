using el.Domain.Common;

namespace el.Domain.Ledger;

/// <summary>
/// A participant that can own payable and escrow accounts, such as a seller, affiliate or
/// processor. Parties are entities: equality is by <see cref="PartyId"/>, not by name or status.
/// </summary>
public sealed class Party : Entity<PartyId>
{
    public const int MaxNameLength = 200;

    public Party(PartyId id, string name)
        : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(name.Length, MaxNameLength, nameof(name));

        Name = name;
        Status = PartyStatus.Active;
    }

    public string Name { get; }

    public PartyStatus Status { get; private set; }

    public bool IsActive => Status == PartyStatus.Active;

    /// <exception cref="InvalidOperationException">The party is not active.</exception>
    public void Suspend()
    {
        EnsureStatus(PartyStatus.Active, "suspended");
        Status = PartyStatus.Suspended;
    }

    /// <exception cref="InvalidOperationException">The party is not suspended.</exception>
    public void Reactivate()
    {
        EnsureStatus(PartyStatus.Suspended, "reactivated");
        Status = PartyStatus.Active;
    }

    /// <exception cref="InvalidOperationException">The party is already closed.</exception>
    public void Close()
    {
        if (Status == PartyStatus.Closed)
        {
            throw new InvalidOperationException("The party is already closed.");
        }

        Status = PartyStatus.Closed;
    }

    private void EnsureStatus(PartyStatus required, string action)
    {
        if (Status != required)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"A {Status} party cannot be {action}; it must be {required}."));
        }
    }
}
