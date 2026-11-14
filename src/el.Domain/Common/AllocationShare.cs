namespace el.Domain.Common;

/// <summary>A line's allocated whole minor units, in the caller's currency.</summary>
public sealed record AllocationShare
{
    internal AllocationShare(string allocationLineId, long minorUnits)
    {
        AllocationLineId = allocationLineId;
        MinorUnits = minorUnits;
    }

    public string AllocationLineId { get; }

    public long MinorUnits { get; }
}
