using el.Application.Abstractions;
using el.Application.Audit;
using el.Domain.Common;
using el.Domain.Knowledge;

namespace el.Application.Knowledge;

public sealed class KnowledgeService(IKnowledgeStore store, IUnitOfWork unitOfWork,
    IMonetaryOperationLock operationLock, IAuditTrail audit, AuditContext actor, TimeProvider clock)
{
    public Task SaveRuleAsync(RuleVersion version, CancellationToken ct) => WriteAsync(async token =>
    {
        var rule = version.Definition;
        rule.Validate();
        if (rule.PublishedBy is not null || rule.PublishedAtUtc is not null) throw Invalid("Publication metadata is server-owned.");
        var versions = await store.RulesAsync(token);
        if (versions.Any(item => item.Definition.Id != rule.Id && item.Definition.RuleId == rule.RuleId && item.Definition.Version == rule.Version))
            throw Conflict("This rule version already exists.");
        var existing = versions.SingleOrDefault(item => item.Definition.Id == rule.Id);
        EnsureDraft(existing?.Definition.PublishedAtUtc);
        if (existing is not null && (existing.Definition.RuleId != rule.RuleId || existing.Definition.Version != rule.Version))
            throw Conflict("Version identity cannot change.");
        await ValidateRuleMappingAsync(rule, token);
        ValidateRuleFixtures(version, run: false);
        await store.SaveRuleAsync(version, token);
        await audit.RecordAsync("rule.draft-saved", rule.Id.ToString(), token);
    }, ct);

    public Task SaveProfileAsync(ProfileVersion version, CancellationToken ct) => WriteAsync(async token =>
    {
        var profile = version.Definition;
        profile.Validate();
        if (profile.PublishedBy is not null || profile.PublishedAtUtc is not null) throw Invalid("Publication metadata is server-owned.");
        var versions = await store.ProfilesAsync(token);
        if (versions.Any(item => item.Definition.Id != profile.Id && item.Definition.ProfileId == profile.ProfileId && item.Definition.Version == profile.Version))
            throw Conflict("This profile version already exists.");
        var existing = versions.SingleOrDefault(item => item.Definition.Id == profile.Id);
        EnsureDraft(existing?.Definition.PublishedAtUtc);
        if (existing is not null && (existing.Definition.ProfileId != profile.ProfileId || existing.Definition.Version != profile.Version))
            throw Conflict("Version identity cannot change.");
        await ValidateProfileMappingAsync(profile, token);
        ValidateProfileFixtures(version, run: false);
        await store.SaveProfileAsync(version, token);
        await audit.RecordAsync("profile.draft-saved", profile.Id.ToString(), token);
    }, ct);

    public Task PublishRuleAsync(Guid id, CancellationToken ct) => WriteAsync(async token =>
    {
        var versions = await store.RulesAsync(token);
        var version = versions.SingleOrDefault(item => item.Definition.Id == id) ?? throw Missing();
        var rule = version.Definition;
        EnsureDraft(rule.PublishedAtUtc);
        rule.Validate();
        await ValidateRuleMappingAsync(rule, token);
        var published = versions.Select(item => item.Definition).Where(item => item.PublishedAtUtc is not null).ToArray();
        var previous = published.Where(item => item.RuleId == rule.RuleId).OrderByDescending(item => item.Version).FirstOrDefault();
        if (previous is not null && (previous.RegionCode != rule.RegionCode || previous.DeductionTypeCode != rule.DeductionTypeCode || previous.Currency != rule.Currency))
            throw Conflict("A stable rule cannot change region, type or currency.");
        EnsureReplacement(rule.Version, rule.EffectiveFrom, previous?.Version, previous?.EffectiveTo);
        if (published.Any(item => item.RegionCode == rule.RegionCode && Overlap(item.EffectiveFrom, item.EffectiveTo, rule.EffectiveFrom, rule.EffectiveTo) &&
            (item.DeductionTypeCode == rule.DeductionTypeCode || item.Sequence == rule.Sequence)))
            throw Conflict("Published regional rules overlap or duplicate a sequence position.");
        ValidateRuleFixtures(version, run: true);
        await store.SaveRuleAsync(version with { Definition = rule with { PublishedBy = actor.ActorId, PublishedAtUtc = clock.GetUtcNow() } }, token);
        await audit.RecordAsync("rule.published", id.ToString(), token);
    }, ct);

    public Task PublishProfileAsync(Guid id, CancellationToken ct) => WriteAsync(async token =>
    {
        var versions = await store.ProfilesAsync(token);
        var version = versions.SingleOrDefault(item => item.Definition.Id == id) ?? throw Missing();
        var profile = version.Definition;
        EnsureDraft(profile.PublishedAtUtc);
        profile.Validate();
        await ValidateProfileMappingAsync(profile, token);
        var published = versions.Select(item => item.Definition).Where(item => item.PublishedAtUtc is not null).ToArray();
        var previous = published.Where(item => item.ProfileId == profile.ProfileId).OrderByDescending(item => item.Version).FirstOrDefault();
        if (previous is not null && (previous.ClientId != profile.ClientId || previous.CorporateCode != profile.CorporateCode || previous.Currency != profile.Currency))
            throw Conflict("A stable profile cannot change client, corporate code or currency.");
        EnsureReplacement(profile.Version, profile.EffectiveFrom, previous?.Version, previous?.EffectiveTo);
        if (published.Any(item => item.ClientId == profile.ClientId && item.CorporateCode == profile.CorporateCode && item.Currency == profile.Currency &&
            Overlap(item.EffectiveFrom, item.EffectiveTo, profile.EffectiveFrom, profile.EffectiveTo)))
            throw Conflict("Published corporate profiles overlap.");
        ValidateProfileFixtures(version, run: true);
        await store.SaveProfileAsync(version with { Definition = profile with { PublishedBy = actor.ActorId, PublishedAtUtc = clock.GetUtcNow() } }, token);
        await audit.RecordAsync("profile.published", id.ToString(), token);
    }, ct);

    public Task AddRegionAsync(Region region, CancellationToken ct) => WriteAsync(async token =>
    {
        KnowledgeValidation.Code(region.Code);
        Note(region.Name, 200);
        if ((await store.RegionsAsync(token)).Any(item => item.Code == region.Code)) throw Conflict("Region already exists.");
        await store.AddRegionAsync(region, token);
        await audit.RecordAsync("region.created", region.Code, token);
    }, ct);

    public Task AddTypeAsync(DeductionType type, CancellationToken ct) => WriteAsync(async token =>
    {
        KnowledgeValidation.Code(type.Code);
        Note(type.Name, 200);
        Note(type.Description, 2000);
        if ((await store.TypesAsync(token)).Any(item => item.Code == type.Code)) throw Conflict("Deduction type already exists.");
        await store.AddTypeAsync(type, token);
        await audit.RecordAsync("deduction-type.created", type.Code, token);
    }, ct);

    public Task ValidateRuleAsync(Guid id, CancellationToken ct) => WriteAsync(async token =>
    {
        var version = (await store.RulesAsync(token)).SingleOrDefault(item => item.Definition.Id == id) ?? throw Missing();
        version.Definition.Validate();
        await ValidateRuleMappingAsync(version.Definition, token);
        ValidateRuleFixtures(version, run: true);
    }, ct);

    public Task ValidateProfileAsync(Guid id, CancellationToken ct) => WriteAsync(async token =>
    {
        var version = (await store.ProfilesAsync(token)).SingleOrDefault(item => item.Definition.Id == id) ?? throw Missing();
        version.Definition.Validate();
        await ValidateProfileMappingAsync(version.Definition, token);
        ValidateProfileFixtures(version, run: true);
    }, ct);

    public Task<KnowledgeSelection> SelectAsync(string region, Guid profileId, string clientId,
        string corporateCode, string currency, DateTimeOffset occurredAt, CancellationToken ct) =>
        unitOfWork.ExecuteAsync(async token =>
        {
            await operationLock.AcquireAsync(token);
            return await SelectCoreAsync(region, profileId, clientId, corporateCode, currency, occurredAt, token);
        }, ct);

    private async Task<KnowledgeSelection> SelectCoreAsync(string region, Guid profileId, string clientId,
        string corporateCode, string currency, DateTimeOffset occurredAt, CancellationToken ct)
    {
        if (occurredAt.Offset != TimeSpan.Zero) throw Invalid("occurredAt must be UTC.");
        if (!(await store.RegionsAsync(ct)).Any(item => item.Code == region))
            throw new KnowledgeException(KnowledgeFailure.Invalid, "UNKNOWN_REGION", "The region is unknown.");
        var profiles = (await store.ProfilesAsync(ct)).Select(item => item.Definition).Where(item => item.PublishedAtUtc is not null &&
            item.ProfileId == profileId && item.ClientId == clientId && item.CorporateCode == corporateCode && item.Currency == currency &&
            item.EffectiveFrom <= occurredAt && occurredAt < item.EffectiveTo).ToArray();
        if (profiles.Length != 1) throw new KnowledgeException(KnowledgeFailure.Invalid, "PROFILE_NOT_APPLICABLE", "Exactly one published profile must apply.");
        var rules = (await store.RulesAsync(ct)).Select(item => item.Definition).Where(item => item.PublishedAtUtc is not null &&
            item.RegionCode == region && item.EffectiveFrom <= occurredAt && occurredAt < item.EffectiveTo).OrderBy(item => item.Sequence).ToArray();
        if (rules.Any(item => item.Currency != currency)) throw Invalid("A regional beneficiary mapping has a different currency.");
        if (rules.Select(item => item.Sequence).Distinct().Count() != rules.Length || rules.Select(item => item.DeductionTypeCode).Distinct().Count() != rules.Length)
            throw Conflict("Regional rules are ambiguous.");
        // Immutable records and a read-only copied collection form the input snapshot for BE-011.
        return new(profiles[0], Array.AsReadOnly(rules), rules.Length == 0 ? "NO_DEDUCTION" : "DEDUCTIONS_APPLIED",
            RoundingPolicy.CurrentVersion, CurrencyReferenceData.InitialVersion);
    }

    private async Task WriteAsync(Func<CancellationToken, Task> work, CancellationToken ct) =>
        await unitOfWork.ExecuteAsync(async token =>
        {
            await operationLock.AcquireAsync(token);
            await work(token);
            return true;
        }, ct);

    private async Task ValidateRuleMappingAsync(DeductionRule rule, CancellationToken ct)
    {
        if (!(await store.RegionsAsync(ct)).Any(item => item.Code == rule.RegionCode)) throw Invalid("Unknown region.");
        if (!(await store.TypesAsync(ct)).Any(item => item.Code == rule.DeductionTypeCode)) throw Invalid("Unknown deduction type.");
        await store.ValidateAccountAsync(rule.BeneficiaryAccountId, rule.Currency, commission: false, ct);
    }

    private async Task ValidateProfileMappingAsync(CorporateFeeProfile profile, CancellationToken ct)
    {
        await store.ValidateAccountAsync(profile.CommissionAccountId, profile.Currency, commission: true, ct);
        await store.ValidateAccountAsync(profile.ProcessorAccountId, profile.Currency, commission: false, ct);
    }

    private static void ValidateRuleFixtures(RuleVersion version, bool run)
    {
        if (version.Fixtures.Count is < 1 or > 100) throw Invalid("Attach 1 to 100 expected-result fixtures.");
        foreach (var fixture in version.Fixtures)
        {
            if (fixture.Id == Guid.Empty || fixture.RuleVersionId != version.Definition.Id) throw Invalid("Fixture version does not match.");
            Note(fixture.Note, 2000);
            KnowledgeValidation.Amount(fixture.GrossMinor, positive: true);
            KnowledgeValidation.Amount(fixture.ExpectedMinor);
            if (run && version.Definition.Calculate(fixture.GrossMinor) != fixture.ExpectedMinor) throw FixtureFailed();
        }
    }

    private static void ValidateProfileFixtures(ProfileVersion version, bool run)
    {
        if (version.Fixtures.Count is < 1 or > 100) throw Invalid("Attach 1 to 100 expected-result fixtures.");
        foreach (var fixture in version.Fixtures)
        {
            if (fixture.Id == Guid.Empty || fixture.ProfileVersionId != version.Definition.Id) throw Invalid("Fixture version does not match.");
            Note(fixture.Note, 2000);
            KnowledgeValidation.Amount(fixture.GrossMinor, positive: true);
            foreach (var amount in new[] { fixture.ShareMinor, fixture.ExpectedCommissionMinor, fixture.ExpectedProcessorMinor, fixture.ExpectedEscrowMinor, fixture.ExpectedPayableMinor })
                KnowledgeValidation.Amount(amount);
            if (fixture.ShareMinor > fixture.GrossMinor) throw Invalid("Fixture share exceeds gross.");
            if (run && version.Definition.Calculate(fixture.GrossMinor, fixture.ShareMinor) !=
                new FeeCalculation(fixture.ExpectedCommissionMinor, fixture.ExpectedProcessorMinor, fixture.ExpectedEscrowMinor, fixture.ExpectedPayableMinor)) throw FixtureFailed();
        }
    }

    private static void EnsureReplacement(int version, DateTimeOffset from, int? previousVersion, DateTimeOffset? previousTo)
    {
        if (version != (previousVersion ?? 0) + 1 || (previousTo is { } end && from != end))
            throw Conflict("Publish consecutive versions with adjacent finite intervals.");
    }
    private static bool Overlap(DateTimeOffset a, DateTimeOffset b, DateTimeOffset c, DateTimeOffset d) => a < d && c < b;
    private static void EnsureDraft(DateTimeOffset? published) { if (published is not null) throw Conflict("Published versions are immutable."); }
    private static void Note(string value, int max) { if (string.IsNullOrWhiteSpace(value) || value.Length > max) throw Invalid("A non-empty note/name within the length limit is required."); }
    private static KnowledgeException Invalid(string message) => new(KnowledgeFailure.Invalid, "INVALID_CONFIGURATION", message);
    private static KnowledgeException Conflict(string message) => new(KnowledgeFailure.Conflict, "CONFIGURATION_CONFLICT", message);
    private static KnowledgeException Missing() => new(KnowledgeFailure.NotFound, "NOT_FOUND", "Configuration version not found.");
    private static KnowledgeException FixtureFailed() => new(KnowledgeFailure.Invalid, "FIXTURE_FAILED", "An expected-result fixture failed; publication was rolled back.");
}
