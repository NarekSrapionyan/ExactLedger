namespace el.Domain.Ledger;

/// <summary>Lifecycle of an account. Only active accounts accept new journal lines; Closed is final.</summary>
public enum AccountStatus
{
    Active = 1,
    Closed = 2,
}
