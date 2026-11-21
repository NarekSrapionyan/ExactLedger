using el.Application.Knowledge;
using el.Domain.Common;
using el.Domain.Knowledge;
using el.Domain.Ledger;
using Microsoft.EntityFrameworkCore;

namespace el.Infrastructure.Persistence;

internal sealed class KnowledgeStore(ExactLedgerDbContext db) : IKnowledgeStore
{
    public async Task<IReadOnlyList<Region>> RegionsAsync(CancellationToken ct) =>
        await db.Set<Region>().AsNoTracking().OrderBy(item => item.Code).ToListAsync(ct);
    public async Task<IReadOnlyList<DeductionType>> TypesAsync(CancellationToken ct) =>
        await db.Set<DeductionType>().AsNoTracking().OrderBy(item => item.Code).ToListAsync(ct);
    public async Task<IReadOnlyList<RuleVersion>> RulesAsync(CancellationToken ct)
    {
        var rules = await db.Set<DeductionRule>().AsNoTracking().OrderBy(item => item.RuleId).ThenBy(item => item.Version).ToListAsync(ct);
        var fixtures = await db.Set<RuleFixture>().AsNoTracking().OrderBy(item => item.Id).ToListAsync(ct);
        return rules.Select(rule => new RuleVersion(rule, fixtures.Where(item => item.RuleVersionId == rule.Id).ToArray())).ToArray();
    }
    public async Task<IReadOnlyList<ProfileVersion>> ProfilesAsync(CancellationToken ct)
    {
        var profiles = await db.Set<CorporateFeeProfile>().AsNoTracking().OrderBy(item => item.ProfileId).ThenBy(item => item.Version).ToListAsync(ct);
        var fixtures = await db.Set<ProfileFixture>().AsNoTracking().OrderBy(item => item.Id).ToListAsync(ct);
        return profiles.Select(profile => new ProfileVersion(profile, fixtures.Where(item => item.ProfileVersionId == profile.Id).ToArray())).ToArray();
    }

    public async Task SaveRuleAsync(RuleVersion version, CancellationToken ct)
    {
        var existing = await db.Set<DeductionRule>().FindAsync([version.Definition.Id], ct);
        if (existing is null) db.Add(version.Definition);
        else db.Entry(existing).CurrentValues.SetValues(version.Definition);
        var fixtures = await db.Set<RuleFixture>().Where(item => item.RuleVersionId == version.Definition.Id).ToListAsync(ct);
        if (fixtures.OrderBy(item => item.Id).SequenceEqual(version.Fixtures.OrderBy(item => item.Id))) return;
        db.RemoveRange(fixtures);
        db.AddRange(version.Fixtures);
    }

    public async Task SaveProfileAsync(ProfileVersion version, CancellationToken ct)
    {
        var existing = await db.Set<CorporateFeeProfile>().FindAsync([version.Definition.Id], ct);
        if (existing is null) db.Add(version.Definition);
        else db.Entry(existing).CurrentValues.SetValues(version.Definition);
        var fixtures = await db.Set<ProfileFixture>().Where(item => item.ProfileVersionId == version.Definition.Id).ToListAsync(ct);
        if (fixtures.OrderBy(item => item.Id).SequenceEqual(version.Fixtures.OrderBy(item => item.Id))) return;
        db.RemoveRange(fixtures);
        db.AddRange(version.Fixtures);
    }

    public Task AddRegionAsync(Region region, CancellationToken ct) { ct.ThrowIfCancellationRequested(); db.Add(region); return Task.CompletedTask; }
    public Task AddTypeAsync(DeductionType type, CancellationToken ct) { ct.ThrowIfCancellationRequested(); db.Add(type); return Task.CompletedTask; }

    public async Task ValidateAccountAsync(Guid id, string currency, bool commission, CancellationToken ct)
    {
        var account = await db.Set<Account>().AsNoTracking().SingleOrDefaultAsync(item => item.Id == new AccountId(id), ct);
        if (account is null || !account.IsActive || account.Currency != CurrencyCode.Parse(currency) ||
            account.Type != (commission ? AccountType.Revenue : AccountType.Payable)) throw InvalidMapping();
        if (account.PartyId is { } party && !await db.Set<Party>().AnyAsync(item => item.Id == party && item.Status == PartyStatus.Active, ct))
            throw InvalidMapping();
    }
    private static KnowledgeException InvalidMapping() => new(KnowledgeFailure.Invalid, "INVALID_ACCOUNT_MAPPING", "Use an active same-currency beneficiary account of the required type and an active party.");
}
