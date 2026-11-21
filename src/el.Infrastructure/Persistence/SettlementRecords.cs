namespace el.Infrastructure.Persistence;

internal sealed class SettlementRecord
{
    public Guid Id { get; set; }
    public string ClientId { get; set; } = null!;
    public string TradeId { get; set; } = null!;
    public string Currency { get; set; } = null!;
    public long GrossMinor { get; set; }
    public Guid JournalEntryId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset PostedAtUtc { get; set; }
    public string InputJson { get; set; } = null!;
    public string SnapshotJson { get; set; } = null!;
    public string ResultJson { get; set; } = null!;
    public string ResultHash { get; set; } = null!;
}
internal sealed class SettlementAllocationRecord
{
    public Guid SettlementId { get; set; }
    public int Ordinal { get; set; }
    public string LineId { get; set; } = null!;
    public Guid? PartyId { get; set; }
    public Guid AccountId { get; set; }
    public string Currency { get; set; } = null!;
    public string Component { get; set; } = null!;
    public long AmountMinor { get; set; }
}
internal sealed class EscrowHoldRecord
{
    public Guid Id { get; set; }
    public Guid SettlementId { get; set; }
    public int AllocationOrdinal { get; set; }
    public Guid PartyId { get; set; }
    public Guid EscrowAccountId { get; set; }
    public Guid PayableAccountId { get; set; }
    public string Currency { get; set; } = null!;
    public long OriginalHeldMinor { get; set; }
    public long ReleasedMinor { get; set; }
    public string State { get; set; } = "Held";
    public DateTimeOffset DueAtUtc { get; set; }
}
internal sealed class SettlementCommandRecord
{
    public string ClientId { get; set; } = null!;
    public string Operation { get; set; } = "settlement.create";
    public string Key { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public Guid SettlementId { get; set; }
}
