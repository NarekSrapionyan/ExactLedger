namespace el.Domain.Common;

/// <summary>
/// An immutable allocation-line identifier and non-negative integer weight.
/// Identifiers are opaque, case-sensitive strings; they are never trimmed or normalized.
/// </summary>
public sealed record AllocationLine
{
    public AllocationLine(string allocationLineId, long weight)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(allocationLineId);
        ArgumentOutOfRangeException.ThrowIfNegative(weight);

        AllocationLineId = allocationLineId;
        Weight = weight;
    }

    public string AllocationLineId { get; }

    public long Weight { get; }
}
