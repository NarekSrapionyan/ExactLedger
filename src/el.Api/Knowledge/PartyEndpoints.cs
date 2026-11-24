using el.Api.Errors;
using el.Application.Knowledge;
using el.Application.Security;

namespace el.Api.Knowledge;

/// <summary>A synthetic participant. Reuse the same ID and content when retrying registration.</summary>
public sealed record RegisterPartyRequest(Guid Id, string Name, bool PayoutEligible = true);
public sealed record RegisteredPartyResponse(Guid Id, string Name, string Status, bool PayoutEligible);
public sealed record ProvisionedAccountResponse(Guid Id, string Code, string Type, string Currency, Guid? PartyId, string Status);

public static class PartyEndpoints
{
    public static IEndpointRouteBuilder MapPartyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1").WithTags("Configuration");
        group.MapPost("/parties", RegisterAsync).WithName("RegisterParty").RequireAuthorization(Permission.ManageConfiguration.ToString())
            .Produces<RegisteredPartyResponse>(201).Produces<RegisteredPartyResponse>();
        group.MapGet("/parties", ListAsync).WithName("ListParties").RequireAuthorization(ReadPolicy).Produces<RegisteredPartyResponse[]>();
        group.MapGet("/parties/{id:guid}", ReadAsync).WithName("ReadParty").RequireAuthorization(ReadPolicy).Produces<RegisteredPartyResponse>();
        group.MapGet("/accounts", AccountsAsync).WithName("ListConfigurationAccounts").RequireAuthorization(ReadPolicy).Produces<ProvisionedAccountResponse[]>();
        group.ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(503);
        return endpoints;
    }

    private static void ReadPolicy(Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder policy) =>
        policy.RequireAuthenticatedUser().RequireRole(IdentityRoles.ConfigurationAdministrator, IdentityRoles.Auditor);

    /// <summary>Register a party with payable/escrow accounts for USD, JPY and KWD (Admin only).</summary>
    public static async Task<IResult> RegisterAsync(RegisterPartyRequest request, PartyProvisioningService service, CancellationToken ct)
    {
        var result = await service.RegisterAsync(request.Id, request.Name, request.PayoutEligible, ct);
        return result.Created ? Results.Created("/v1/parties/" + result.Party.Id, Response(result.Party)) : Results.Ok(Response(result.Party));
    }

    /// <summary>List participants for configuration forms (Admin/Auditor).</summary>
    public static async Task<IResult> ListAsync(int? offset, int? limit, HttpContext context, IPartyProvisioningStore store, CancellationToken ct)
    {
        if (!ValidPage(offset, limit)) return Invalid(context);
        return Results.Ok((await store.ListAsync(offset ?? 0, limit ?? 50, ct)).Select(Response));
    }

    /// <summary>Read a participant and its payout eligibility (Admin/Auditor).</summary>
    public static async Task<IResult> ReadAsync(Guid id, IPartyProvisioningStore store, CancellationToken ct)
    {
        var party = await store.FindAsync(id, ct);
        return party is null ? Results.NotFound() : Results.Ok(Response(party));
    }

    /// <summary>List account mappings, optionally by party and currency (Admin/Auditor).</summary>
    public static async Task<IResult> AccountsAsync(Guid? partyId, string? currency, int? offset, int? limit,
        HttpContext context, IPartyProvisioningStore store, CancellationToken ct)
    {
        if (!ValidPage(offset, limit) || partyId == Guid.Empty || (currency is not null && currency is not ("USD" or "JPY" or "KWD")))
            return Invalid(context);
        var accounts = await store.AccountsAsync(partyId, currency, offset ?? 0, limit ?? 50, ct);
        return Results.Ok(accounts.Select(a => new ProvisionedAccountResponse(a.Id, a.Code, a.Type.ToString(), a.Currency, a.PartyId, a.Status.ToString())));
    }

    private static bool ValidPage(int? offset, int? limit) => (offset ?? 0) >= 0 && (limit ?? 50) is >= 1 and <= 100;
    private static IResult Invalid(HttpContext context) => ApiErrorResponses.Validation(context,
        new Dictionary<string, string[]> { ["filters"] = ["Use a valid party/currency, a non-negative offset and limit 1 to 100."] });
    private static RegisteredPartyResponse Response(RegisteredParty p) => new(p.Id, p.Name, p.Status.ToString(), p.PayoutEligible);
}
