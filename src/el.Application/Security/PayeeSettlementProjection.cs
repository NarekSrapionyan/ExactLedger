using el.Domain.Common;

namespace el.Application.Security;

/// <summary>Normalized structured rows supplied by the settlement reader; global trace rows have no party ID.</summary>
public sealed record PartyAllocationRow(Guid PartyId, string Component, Money Amount, string? PayoutReference);
public sealed record PartyTraceRow(Guid? PartyId, string Step, Money Result);

// Whitelisted output deliberately has no party identifiers, payout references,
// arbitrary trace payload or another participant's financial data.
public sealed record OwnAllocation(string Component, Money Amount);
public sealed record OwnTraceStep(string Step, Money Result);
public sealed record PayeeSettlementSlice(IReadOnlyList<OwnAllocation> Allocations, IReadOnlyList<OwnTraceStep> Trace);

public static class PayeeSettlementProjection
{
    public static PayeeSettlementSlice Create(Guid partyId, IEnumerable<PartyAllocationRow> allocations, IEnumerable<PartyTraceRow> trace)
    {
        if (partyId == Guid.Empty) throw new ArgumentException("An authenticated party ID is required.", nameof(partyId));
        return new(
            allocations.Where(row => row.PartyId == partyId).Select(row => new OwnAllocation(row.Component, row.Amount)).ToArray(),
            trace.Where(row => row.PartyId == partyId).Select(row => new OwnTraceStep(row.Step, row.Result)).ToArray());
    }
}
