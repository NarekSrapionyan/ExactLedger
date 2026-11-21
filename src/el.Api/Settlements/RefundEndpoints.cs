using el.Api.Authentication;
using el.Application.Security;
using el.Application.Settlements;
using el.Domain.Common;
using el.Domain.Settlements;

namespace el.Api.Settlements;

/// <summary>A full refund request. The amount must equal the settlement's gross amount.</summary>
public sealed record RefundRequest(Money Amount);

/// <summary>The stored refund, also returned for an identical retry with the same Idempotency-Key.</summary>
public sealed record RefundResponse(Guid SettlementId, Guid JournalEntryId, Guid OriginalJournalEntryId, Money Amount,
    DateTimeOffset RefundedAtUtc, IReadOnlyList<Guid> RefundedHoldIds);

public static class RefundEndpoints
{
    public static IEndpointRouteBuilder MapRefundEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1/settlements").WithTags("Settlements");
        group.MapPost("/{id:guid}/refund", RefundAsync).WithName("RefundSettlement")
            .RequireAuthorization(Permission.SubmitTradeOrRefund.ToString()).Produces<RefundResponse>(201);
        group.MapGet("/{id:guid}/refund", ReadRefundAsync).WithName("ReadSettlementRefund")
            .RequireAuthorization(Permission.ReadSettlement.ToString()).Produces<RefundResponse>();
        group.ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(503);
        return endpoints;
    }

    /// <summary>
    /// Fully refund a settlement before clearing by reversing its original journal lines. Idempotency-Key is
    /// required; an identical retry returns the original 201 result.
    /// </summary>
    public static async Task<IResult> RefundAsync(Guid id, RefundRequest request, HttpContext context, RefundService service, CancellationToken ct)
    {
        if (request?.Amount is null)
            throw new SettlementValidationException("INVALID_REFUND", "A refund amount is required.");
        var keys = context.Request.Headers["Idempotency-Key"];
        if (keys.Count != 1) throw new SettlementValidationException("INVALID_IDEMPOTENCY_KEY", "One Idempotency-Key is required.");
        var identity = RequestIdentity.Get(context);

        try
        {
            var refund = await service.RefundAsync(identity.ClientId!, id, request.Amount, keys[0]!, ct);
            return Results.Created($"/v1/settlements/{id}/refund", ToResponse(refund));
        }
        catch (SettlementNotFoundException)
        {
            return Results.NotFound();
        }
    }

    /// <summary>Read a settlement's refund. Full readers and the owning Integrator only.</summary>
    public static async Task<IResult> ReadRefundAsync(Guid id, HttpContext context, ISettlementStore settlements, IRefundStore refunds,
        CancellationToken ct)
    {
        var settlement = await settlements.GetAsync(id, ct);
        if (settlement is null) return Results.NotFound();
        var access = ResourceAccess.Settlement(RequestIdentity.Get(context), settlement.Input.ClientId,
            settlement.Calculation.Allocations.Where(row => row.PartyId is not null).Select(row => row.PartyId!.Value).Distinct().ToArray());
        if (access != SettlementReadAccess.Full) return Results.NotFound();
        var refund = await refunds.FindRefundAsync(id, ct);
        return refund is null ? Results.NotFound() : Results.Ok(ToResponse(refund));
    }

    private static RefundResponse ToResponse(SettlementRefundResult refund) => new(refund.SettlementId, refund.JournalEntryId,
        refund.OriginalJournalEntryId, Money.FromMinorUnits(refund.AmountMinor, CurrencyCode.Parse(refund.Currency)),
        refund.RefundedAtUtc, refund.RefundedHoldIds);
}
