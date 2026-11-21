using el.Api.Errors;
using el.Application.Knowledge;
using el.Application.Security;
using el.Domain.Common;
using el.Domain.Knowledge;

namespace el.Api.Knowledge;

public static class KnowledgeEndpoints
{
    public static IEndpointRouteBuilder MapKnowledgeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1").WithTags("Configuration");
        group.MapGet("/regions", RegionsAsync).RequireAuthorization(ReadPolicy);
        group.MapPost("/regions", AddRegionAsync).RequireAuthorization(Permission.ManageConfiguration.ToString());
        group.MapGet("/deduction-types", TypesAsync).RequireAuthorization(ReadPolicy);
        group.MapPost("/deduction-types", AddTypeAsync).RequireAuthorization(Permission.ManageConfiguration.ToString());
        group.MapGet("/rules", RulesAsync).RequireAuthorization(ReadPolicy);
        group.MapPost("/rules", CreateRuleAsync).RequireAuthorization(Permission.ManageConfiguration.ToString());
        group.MapPut("/rules/{id:guid}", UpdateRuleAsync).RequireAuthorization(Permission.ManageConfiguration.ToString());
        group.MapPost("/rules/{id:guid}/validate", ValidateRuleAsync).RequireAuthorization(Permission.ManageConfiguration.ToString());
        group.MapPost("/rules/{id:guid}/publish", PublishRuleAsync).RequireAuthorization(Permission.PublishConfiguration.ToString());
        group.MapGet("/fee-profiles", ProfilesAsync).RequireAuthorization(ReadPolicy);
        group.MapPost("/fee-profiles", CreateProfileAsync).RequireAuthorization(Permission.ManageConfiguration.ToString());
        group.MapPut("/fee-profiles/{id:guid}", UpdateProfileAsync).RequireAuthorization(Permission.ManageConfiguration.ToString());
        group.MapPost("/fee-profiles/{id:guid}/validate", ValidateProfileAsync).RequireAuthorization(Permission.ManageConfiguration.ToString());
        group.MapPost("/fee-profiles/{id:guid}/publish", PublishProfileAsync).RequireAuthorization(Permission.PublishConfiguration.ToString());
        group.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (Exception error) when (error is ArgumentException or FormatException or OverflowException or UnknownCurrencyException)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                { ["configuration"] = ["Check rates, amounts, currencies, intervals and required fields."] },
                    extensions: ApiErrorResponses.Create(context.HttpContext, 400, "INVALID_CONFIGURATION").Extensions);
            }
        });
        group.WithOpenApiErrors();
        return endpoints;
    }

    private static void ReadPolicy(Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder policy) =>
        policy.RequireAuthenticatedUser().RequireRole(IdentityRoles.ConfigurationAdministrator, IdentityRoles.Auditor);

    private static void WithOpenApiErrors(this RouteGroupBuilder group) =>
        group.ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(503);

    /// <summary>List known regions (Admin/Auditor).</summary>
    public static async Task<RegionRequest[]> RegionsAsync(IKnowledgeStore store, CancellationToken ct) =>
        (await store.RegionsAsync(ct)).Select(item => new RegionRequest(item.Code, item.Name)).ToArray();
    /// <summary>Add a region; a region without applicable rules has no deduction.</summary>
    public static async Task<IResult> AddRegionAsync(RegionRequest request, KnowledgeService service, CancellationToken ct)
    { await service.AddRegionAsync(new(request.Code, request.Name), ct); return Results.Created("/v1/regions", request); }
    /// <summary>List deduction categories (Admin/Auditor).</summary>
    public static async Task<DeductionTypeRequest[]> TypesAsync(IKnowledgeStore store, CancellationToken ct) =>
        (await store.TypesAsync(ct)).Select(item => new DeductionTypeRequest(item.Code, item.Name, item.Description)).ToArray();
    /// <summary>Add a deduction category.</summary>
    public static async Task<IResult> AddTypeAsync(DeductionTypeRequest request, KnowledgeService service, CancellationToken ct)
    { await service.AddTypeAsync(new(request.Code, request.Name, request.Description), ct); return Results.Created("/v1/deduction-types", request); }
    /// <summary>List rule versions and their examples (Admin/Auditor).</summary>
    public static async Task<RuleResponse[]> RulesAsync(IKnowledgeStore store, CancellationToken ct) =>
        (await store.RulesAsync(ct)).Select(ToResponse).ToArray();
    /// <summary>Create a regional rule draft.</summary>
    public static async Task<IResult> CreateRuleAsync(RuleRequest request, KnowledgeService service, CancellationToken ct)
    {
        var id = Guid.NewGuid(); var version = ToVersion(id, request);
        await service.SaveRuleAsync(version, ct);
        return Results.Created("/v1/rules", ToResponse(version));
    }
    /// <summary>Edit an existing regional rule draft.</summary>
    public static async Task<IResult> UpdateRuleAsync(Guid id, RuleRequest request, IKnowledgeStore store, KnowledgeService service, CancellationToken ct)
    {
        if (!(await store.RulesAsync(ct)).Any(item => item.Definition.Id == id)) return Results.NotFound();
        var version = ToVersion(id, request); await service.SaveRuleAsync(version, ct); return Results.Ok(ToResponse(version));
    }
    /// <summary>Run a rule's attached expected-result examples.</summary>
    public static async Task<FixtureValidationResponse> ValidateRuleAsync(Guid id, KnowledgeService service, CancellationToken ct)
    { await service.ValidateRuleAsync(id, ct); return new(true); }
    /// <summary>Validate and publish a rule version atomically.</summary>
    public static async Task<IResult> PublishRuleAsync(Guid id, KnowledgeService service, CancellationToken ct)
    { await service.PublishRuleAsync(id, ct); return Results.NoContent(); }
    /// <summary>List corporate profile versions and examples (Admin/Auditor).</summary>
    public static async Task<ProfileResponse[]> ProfilesAsync(IKnowledgeStore store, CancellationToken ct) =>
        (await store.ProfilesAsync(ct)).Select(ToResponse).ToArray();
    /// <summary>Create a seller-borne fee and escrow profile draft.</summary>
    public static async Task<IResult> CreateProfileAsync(ProfileRequest request, KnowledgeService service, CancellationToken ct)
    {
        var id = Guid.NewGuid(); var version = ToVersion(id, request);
        await service.SaveProfileAsync(version, ct); return Results.Created("/v1/fee-profiles", ToResponse(version));
    }
    /// <summary>Edit an existing corporate profile draft.</summary>
    public static async Task<IResult> UpdateProfileAsync(Guid id, ProfileRequest request, IKnowledgeStore store, KnowledgeService service, CancellationToken ct)
    {
        if (!(await store.ProfilesAsync(ct)).Any(item => item.Definition.Id == id)) return Results.NotFound();
        var version = ToVersion(id, request); await service.SaveProfileAsync(version, ct); return Results.Ok(ToResponse(version));
    }
    /// <summary>Run a profile's attached expected-result examples.</summary>
    public static async Task<FixtureValidationResponse> ValidateProfileAsync(Guid id, KnowledgeService service, CancellationToken ct)
    { await service.ValidateProfileAsync(id, ct); return new(true); }
    /// <summary>Validate and publish a corporate profile version atomically.</summary>
    public static async Task<IResult> PublishProfileAsync(Guid id, KnowledgeService service, CancellationToken ct)
    { await service.PublishProfileAsync(id, ct); return Results.NoContent(); }

    private static RuleVersion ToVersion(Guid id, RuleRequest r)
    {
        ArgumentNullException.ThrowIfNull(r.Fixtures);
        if (r.Fixtures.Any(item => item is null)) throw new ArgumentException("A fixture cannot be null.");
        return new(new(id, r.RuleId, r.Version, r.RegionCode, r.DeductionTypeCode, r.CalculationBase, r.RateUnits, r.Sequence,
            r.BeneficiaryAccountId, r.Currency, Mode(r.RoundingMode), r.EffectiveFrom, r.EffectiveTo, r.SourceNote, r.IsSynthetic),
            r.Fixtures.Select(f => new RuleFixture(Guid.NewGuid(), id, f.Note, Minor(f.Gross, r.Currency), Minor(f.ExpectedDeduction, r.Currency))).ToArray());
    }
    private static ProfileVersion ToVersion(Guid id, ProfileRequest p)
    {
        ArgumentNullException.ThrowIfNull(p.Fixtures);
        if (p.Fixtures.Any(item => item is null)) throw new ArgumentException("A fixture cannot be null.");
        return new(new(id, p.ProfileId, p.Version, p.ClientId, p.CorporateCode, p.Currency, p.CommissionRateUnits, p.ProcessorRateUnits,
            Minor(p.ProcessorFixed, p.Currency), p.ProcessorFloor is null ? null : Minor(p.ProcessorFloor, p.Currency),
            p.ProcessorCap is null ? null : Minor(p.ProcessorCap, p.Currency), Mode(p.RoundingMode), p.EscrowRateUnits, p.HoldDays,
            p.CommissionAccountId, p.ProcessorAccountId, p.EffectiveFrom, p.EffectiveTo, p.SourceNote, p.IsSynthetic),
            p.Fixtures.Select(f => new ProfileFixture(Guid.NewGuid(), id, f.Note, Minor(f.Gross, p.Currency), Minor(f.Share, p.Currency),
                Minor(f.ExpectedCommission, p.Currency), Minor(f.ExpectedProcessor, p.Currency), Minor(f.ExpectedEscrow, p.Currency), Minor(f.ExpectedPayable, p.Currency))).ToArray());
    }
    private static long Minor(Money value, string currency)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Currency.Value != currency) throw new ArgumentException("Fixture/fee currency must match the configuration currency.");
        return value.MinorUnits;
    }
    private static RoundingMode Mode(string value) => value switch
    { "HalfEven" => RoundingMode.HalfEven, "HalfAwayFromZero" => RoundingMode.HalfAwayFromZero, "TowardZero" => RoundingMode.TowardZero, _ => throw new ArgumentException("Unknown rounding mode.") };
    private static Money Money(long value, string currency) => el.Domain.Common.Money.FromMinorUnits(value, CurrencyCode.Parse(currency));
    internal static RuleResponse ToResponse(RuleVersion v)
    {
        var r = v.Definition;
        return new(r.Id, new(r.RuleId, r.Version, r.RegionCode, r.DeductionTypeCode, r.CalculationBase, r.RateUnits, r.Sequence,
            r.BeneficiaryAccountId, r.Currency, r.RoundingMode.ToString(), r.EffectiveFrom, r.EffectiveTo, r.SourceNote, r.IsSynthetic,
            v.Fixtures.Select(f => new RuleExampleRequest(f.Note, Money(f.GrossMinor, r.Currency), Money(f.ExpectedMinor, r.Currency))).ToArray()), r.PublishedBy, r.PublishedAtUtc);
    }
    internal static ProfileResponse ToResponse(ProfileVersion v)
    {
        var p = v.Definition;
        return new(p.Id, new(p.ProfileId, p.Version, p.ClientId, p.CorporateCode, p.Currency, p.CommissionRateUnits, p.ProcessorRateUnits,
            Money(p.ProcessorFixedMinor, p.Currency), p.ProcessorFloorMinor is { } floor ? Money(floor, p.Currency) : null,
            p.ProcessorCapMinor is { } cap ? Money(cap, p.Currency) : null, p.RoundingMode.ToString(), p.EscrowRateUnits, p.HoldDays,
            p.CommissionAccountId, p.ProcessorAccountId, p.EffectiveFrom, p.EffectiveTo, p.SourceNote, p.IsSynthetic,
            v.Fixtures.Select(f => new ProfileExampleRequest(f.Note, Money(f.GrossMinor, p.Currency), Money(f.ShareMinor, p.Currency),
                Money(f.ExpectedCommissionMinor, p.Currency), Money(f.ExpectedProcessorMinor, p.Currency), Money(f.ExpectedEscrowMinor, p.Currency), Money(f.ExpectedPayableMinor, p.Currency))).ToArray()), p.PublishedBy, p.PublishedAtUtc);
    }
}
