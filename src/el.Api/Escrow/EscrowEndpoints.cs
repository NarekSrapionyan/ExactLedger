using el.Application.Security;
using el.Application.Settlements;
using el.Domain.Common;
using el.Domain.Settlements;

namespace el.Api.Escrow;

/// <summary>An escrow hold and its effective state (Held, Released or Refunded).</summary>
public sealed record EscrowHoldResponse(Guid Id, Guid SettlementId, Guid PartyId, Money Held, Money Released,
    string State, DateTimeOffset DueAtUtc);

/// <summary>The stored result of a full release; repeating the release returns the same result.</summary>
public sealed record EscrowReleaseResponse(Guid HoldId, Guid SettlementId, Guid PartyId, Guid JournalEntryId,
    Money Amount, DateTimeOffset ReleasedAtUtc);

/// <summary>A due hold the run skipped; it stays Held and is retried on the next run.</summary>
public sealed record SkippedEscrowReleaseResponse(Guid HoldId, string Code);

/// <summary>The result of releasing every hold due now.</summary>
public sealed record DueEscrowReleaseResponse(IReadOnlyList<EscrowReleaseResponse> Released,
    IReadOnlyList<SkippedEscrowReleaseResponse> Skipped);

public static class EscrowEndpoints
{
    public static IEndpointRouteBuilder MapEscrowEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1/escrow").WithTags("Escrow");
        group.MapGet("/holds/{id:guid}", ReadHoldAsync).WithName("ReadEscrowHold")
            .RequireAuthorization(Permission.ReadLedger.ToString()).Produces<EscrowHoldResponse>();
        group.MapGet("/holds", ListHoldsAsync).WithName("ListEscrowHolds")
            .RequireAuthorization(Permission.ReadLedger.ToString()).Produces<IReadOnlyList<EscrowHoldResponse>>();
        group.MapPost("/holds/{id:guid}/release", ReleaseAsync).WithName("ReleaseEscrowHold")
            .RequireAuthorization(Permission.ReleaseEscrow.ToString()).Produces<EscrowReleaseResponse>();
        group.MapPost("/releases", ReleaseDueAsync).WithName("ReleaseDueEscrowHolds")
            .RequireAuthorization(Permission.ReleaseEscrow.ToString()).Produces<DueEscrowReleaseResponse>();
        group.ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(503);
        return endpoints;
    }

    /// <summary>Read one hold and its effective state.</summary>
    public static async Task<IResult> ReadHoldAsync(Guid id, IEscrowStore store, CancellationToken ct)
    {
        var hold = await store.GetHoldAsync(id, ct);
        return hold is null ? Results.NotFound() : Results.Ok(ToResponse(hold));
    }

    /// <summary>List the holds created by one settlement.</summary>
    public static async Task<IResult> ListHoldsAsync(Guid settlementId, IEscrowStore store, CancellationToken ct) =>
        Results.Ok((await store.HoldsForSettlementAsync(settlementId, ct)).Select(ToResponse).ToArray());

    /// <summary>
    /// Release one due hold in full (Dr Escrow / Cr Payable). Repeating the call returns the original
    /// result; a hold that is not due or was refunded returns 409.
    /// </summary>
    public static async Task<IResult> ReleaseAsync(Guid id, EscrowService service, CancellationToken ct)
    {
        try
        {
            return Results.Ok(ToResponse(await service.ReleaseAsync(id, ct)));
        }
        catch (EscrowHoldNotFoundException)
        {
            return Results.NotFound();
        }
    }

    /// <summary>Scheduler entry point: release every hold due now. Ineligible holds are reported and stay Held.</summary>
    public static async Task<IResult> ReleaseDueAsync(EscrowService service, CancellationToken ct)
    {
        var result = await service.ReleaseDueAsync(ct);
        return Results.Ok(new DueEscrowReleaseResponse(result.Released.Select(ToResponse).ToArray(),
            result.Skipped.Select(row => new SkippedEscrowReleaseResponse(row.HoldId, row.Code)).ToArray()));
    }

    private static EscrowHoldResponse ToResponse(EscrowHold hold) => new(hold.Id, hold.SettlementId, hold.PartyId, hold.Held,
        hold.State == EscrowHoldState.Released ? hold.Held : Money.Zero(hold.Held.Currency), hold.State.ToString(), hold.DueAtUtc);

    private static EscrowReleaseResponse ToResponse(EscrowRelease release) => new(release.HoldId, release.SettlementId, release.PartyId,
        release.JournalEntryId, Money.FromMinorUnits(release.AmountMinor, CurrencyCode.Parse(release.Currency)), release.ReleasedAtUtc);
}
