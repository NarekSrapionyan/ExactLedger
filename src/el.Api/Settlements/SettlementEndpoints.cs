using el.Api.Authentication;
using el.Api.Errors;
using el.Api.Knowledge;
using el.Application.Knowledge;
using el.Application.Security;
using el.Application.Settlements;
using el.Domain.Common;
using el.Domain.Settlements;

namespace el.Api.Settlements;

/// <summary>A gross affiliate percentage, pool weight or unique remainder beneficiary.</summary>
public sealed record BeneficiaryRequest(string LineId, Guid PartyId, string Kind, long Weight, long RateUnits);
/// <summary>A captured trade. The authenticated identity supplies the client binding.</summary>
public sealed record TradeRequest(string TradeId, DateTimeOffset OccurredAt, string Currency, Money Gross,
    string RegionCode, Guid ProfileId, string CorporateCode, IReadOnlyList<BeneficiaryRequest> Beneficiaries);
/// <summary>One exact allocation and its authoritative ledger mapping.</summary>
public sealed record AllocationResponse(string LineId, Guid? PartyId, Guid AccountId, string Component, Money Amount);
/// <summary>Exact percentage or weighted-allocation evidence. Fractions are integer strings.</summary>
public sealed record SettlementTraceResponse(string Step, Guid? PartyId, string? LineId, Money Base, Money Result,
    long? RateUnits, string? Numerator, string? Denominator, string? RoundingMode, long? Weight,
    string? Quotient, string? Remainder, IReadOnlyList<string>? TieBreakOrder);
/// <summary>Actual immutable configuration and account content used by the calculator.</summary>
public sealed record SettlementSnapshotResponse(ProfileResponse Profile, IReadOnlyList<RuleResponse> Rules,
    IReadOnlyList<SettlementAccountResponse> Accounts, string RegionalOutcome, int CurrencyReferenceVersion,
    int CurrencyExponent, int RoundingPolicyVersion, int CalculationPolicyVersion);
public sealed record SettlementAccountResponse(Guid Id, Guid? PartyId, string Type, string Currency);
/// <summary>The original posted result, also returned for an identical command retry.</summary>
public sealed record FullSettlementResponse(Guid Id, Guid JournalEntryId, DateTimeOffset PostedAtUtc, string ClientId,
    TradeRequest Input, SettlementSnapshotResponse Snapshot, IReadOnlyList<AllocationResponse> Allocations,
    IReadOnlyList<SettlementTraceResponse> Trace, string ResultHash);
public sealed record OwnAllocationResponse(string Component, Money Amount);
public sealed record OwnTraceResponse(string Step, Money Result);
/// <summary>Payee-only whitelist: no other parties, global trace, account IDs or payout references.</summary>
public sealed record PayeeSettlementResponse(Guid Id, string Currency, IReadOnlyList<OwnAllocationResponse> Allocations,
    IReadOnlyList<OwnTraceResponse> Trace);
/// <summary>Replay uses stored input/configuration; generated request metadata is excluded from the result hash.</summary>
public sealed record SettlementReplayResponse(Guid Id, string ResultHash, bool Matches);

public static class SettlementEndpoints
{
    public static IEndpointRouteBuilder MapSettlementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1/settlements").WithTags("Settlements");
        group.MapPost("/", SubmitAsync).WithName("SubmitSettlement").RequireAuthorization(Permission.SubmitTradeOrRefund.ToString())
            .Produces<FullSettlementResponse>(201);
        group.MapGet("/{id:guid}", ReadAsync).WithName("ReadSettlement").RequireAuthorization(Permission.ReadSettlement.ToString())
            .Produces<FullSettlementResponse>().Produces<PayeeSettlementResponse>();
        group.MapGet("/{id:guid}/trace", ReadAsync).WithName("ReadSettlementTrace").RequireAuthorization(Permission.ReadSettlement.ToString())
            .Produces<FullSettlementResponse>().Produces<PayeeSettlementResponse>();
        group.MapGet("/{id:guid}/replay", ReplayAsync).RequireAuthorization(Permission.ReadSettlement.ToString())
            .Produces<SettlementReplayResponse>();
        group.ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(503);
        group.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (Exception error) when (error is ArgumentException or FormatException or OverflowException or UnknownCurrencyException)
            {
                return ApiErrorResponses.Validation(context.HttpContext, new Dictionary<string, string[]>
                { ["settlement"] = ["Check required fields, amounts, currencies, UTC time and beneficiary shares."] });
            }
        });
        return endpoints;
    }

    /// <summary>Settle a captured trade atomically. Idempotency-Key is required; identical retries return the original 201 result.</summary>
    public static async Task<IResult> SubmitAsync(TradeRequest request, HttpContext context, SettlementService service, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request.Gross); ArgumentNullException.ThrowIfNull(request.Beneficiaries);
        if (request.Gross.Currency.Value != request.Currency || request.Beneficiaries.Any(line => line is null))
            throw new SettlementValidationException("INVALID_SETTLEMENT", "Currencies and beneficiary input must be consistent.");
        var identity = RequestIdentity.Get(context);
        var keys = context.Request.Headers["Idempotency-Key"];
        if (keys.Count != 1) throw new SettlementValidationException("INVALID_IDEMPOTENCY_KEY", "One Idempotency-Key is required.");
        var input = new SettlementInput(identity.ClientId!, request.TradeId, request.OccurredAt, request.Currency, request.Gross.MinorUnits,
            request.RegionCode, request.ProfileId, request.CorporateCode,
            request.Beneficiaries.Select(line => new SettlementBeneficiary(line.LineId, line.PartyId, line.Kind, line.Weight, line.RateUnits)).ToArray());
        var result = await service.SubmitAsync(input, keys[0]!, ct);
        return Results.Created("/v1/settlements/" + result.Id, Full(result));
    }

    /// <summary>Read a settlement and trace. Integrators read their client; Payees receive only their own whitelist.</summary>
    public static async Task<IResult> ReadAsync(Guid id, HttpContext context, ISettlementStore store, CancellationToken ct)
    {
        var result = await store.GetAsync(id, ct);
        if (result is null) return Results.NotFound();
        var identity = RequestIdentity.Get(context);
        var access = Access(identity, result);
        if (access == SettlementReadAccess.None) return Results.NotFound();
        if (access == SettlementReadAccess.Full) return Results.Ok(Full(result));
        var currency = CurrencyCode.Parse(result.Input.Currency);
        var own = PayeeSettlementProjection.Create(identity.PartyId!.Value,
            result.Calculation.Allocations.Where(row => row.PartyId is not null).Select(row =>
                new PartyAllocationRow(row.PartyId!.Value, row.Component, Money.FromMinorUnits(row.AmountMinor, currency), null)),
            result.Calculation.Trace.Select(row => new PartyTraceRow(row.PartyId, row.Step, Money.FromMinorUnits(row.ResultMinor, currency))));
        return Results.Ok(new PayeeSettlementResponse(id, currency.Value,
            own.Allocations.Select(row => new OwnAllocationResponse(row.Component, row.Amount)).ToArray(),
            own.Trace.Select(row => new OwnTraceResponse(row.Step, row.Result)).ToArray()));
    }

    /// <summary>Verify the stored calculation hash by replaying the stored input and complete snapshot.</summary>
    public static async Task<IResult> ReplayAsync(Guid id, HttpContext context, ISettlementStore store, SettlementService service, CancellationToken ct)
    {
        var result = await store.GetAsync(id, ct);
        if (result is null) return Results.NotFound();
        var access = Access(RequestIdentity.Get(context), result);
        if (access == SettlementReadAccess.None) return Results.NotFound();
        if (access != SettlementReadAccess.Full) return Results.Forbid();
        return Results.Ok(new SettlementReplayResponse(id, result.ResultHash, service.ReplayMatches(result)));
    }
    private static SettlementReadAccess Access(AuthenticatedIdentity identity, SettledTrade s) => ResourceAccess.Settlement(identity,
        s.Input.ClientId, s.Calculation.Allocations.Where(row => row.PartyId is not null).Select(row => row.PartyId!.Value).Distinct().ToArray());
    private static FullSettlementResponse Full(SettledTrade s)
    {
        var currency = CurrencyCode.Parse(s.Input.Currency);
        Money Amount(long value) => Money.FromMinorUnits(value, currency);
        return new(s.Id, s.JournalEntryId, s.PostedAtUtc, s.Input.ClientId,
            new(s.Input.TradeId, s.Input.OccurredAt, s.Input.Currency, Amount(s.Input.GrossMinor), s.Input.RegionCode, s.Input.ProfileId,
                s.Input.CorporateCode, s.Input.Beneficiaries.Select(line => new BeneficiaryRequest(line.LineId, line.PartyId, line.Kind, line.Weight, line.RateUnits)).ToArray()),
            new(KnowledgeEndpoints.ToResponse(new ProfileVersion(s.Snapshot.Profile, [])),
                s.Snapshot.Rules.Select(rule => KnowledgeEndpoints.ToResponse(new RuleVersion(rule, []))).ToArray(),
                s.Snapshot.Accounts.Select(account => new SettlementAccountResponse(account.Id, account.PartyId, account.Type.ToString(), account.Currency)).ToArray(),
                s.Snapshot.RegionalOutcome, s.Snapshot.CurrencyReferenceVersion, s.Snapshot.CurrencyExponent, s.Snapshot.RoundingPolicyVersion, s.Snapshot.CalculationPolicyVersion),
            s.Calculation.Allocations.Select(row => new AllocationResponse(row.LineId, row.PartyId, row.AccountId, row.Component, Amount(row.AmountMinor))).ToArray(),
            s.Calculation.Trace.Select(row => new SettlementTraceResponse(row.Step, row.PartyId, row.LineId, Amount(row.BaseMinor), Amount(row.ResultMinor),
                row.RateUnits, row.Numerator, row.Denominator, row.RoundingMode, row.Weight, row.Quotient, row.Remainder, row.TieBreakOrder)).ToArray(), s.ResultHash);
    }
}
