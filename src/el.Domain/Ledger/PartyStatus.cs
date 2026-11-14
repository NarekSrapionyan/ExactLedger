namespace el.Domain.Ledger;

/// <summary>Lifecycle of a party. Closed is final.</summary>
public enum PartyStatus
{
    Active = 1,
    Suspended = 2,
    Closed = 3,
}
