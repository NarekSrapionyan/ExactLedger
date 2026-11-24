namespace el.Application.Settlements;

/// <summary>Server-derived scope; a combined Integrator/Payee may read the union of both grants.</summary>
public sealed record SettlementVisibility(bool All, string? ClientId, Guid? PartyId);
public sealed record SettlementListRow(Guid Id, DateTimeOffset PostedAtUtc, string Currency, long GrossMinor,
    string ClientId, string TradeId, IReadOnlyList<SettlementOwnAllocation> OwnAllocations);
public sealed record SettlementOwnAllocation(string Component, long AmountMinor);

public interface ISettlementQueries
{
    Task<IReadOnlyList<SettlementListRow>> ListAsync(SettlementVisibility scope, string? currency,
        int offset, int limit, CancellationToken ct);
}
