using el.Domain.Knowledge;

namespace el.Application.Knowledge;

public sealed record RuleVersion(DeductionRule Definition, IReadOnlyList<RuleFixture> Fixtures);
public sealed record ProfileVersion(CorporateFeeProfile Definition, IReadOnlyList<ProfileFixture> Fixtures);
public sealed record KnowledgeSelection(CorporateFeeProfile Profile, IReadOnlyList<DeductionRule> Rules,
    string RegionalOutcome, int RoundingPolicyVersion, int CurrencyReferenceVersion);

public enum KnowledgeFailure { Invalid, Conflict, NotFound }
public sealed class KnowledgeException(KnowledgeFailure failure, string code, string message) : Exception(message)
{
    public KnowledgeFailure Failure { get; } = failure;
    public string Code { get; } = code;
}

public interface IKnowledgeStore
{
    Task<IReadOnlyList<Region>> RegionsAsync(CancellationToken ct);
    Task<IReadOnlyList<DeductionType>> TypesAsync(CancellationToken ct);
    Task<IReadOnlyList<RuleVersion>> RulesAsync(CancellationToken ct);
    Task<IReadOnlyList<ProfileVersion>> ProfilesAsync(CancellationToken ct);
    Task SaveRuleAsync(RuleVersion version, CancellationToken ct);
    Task SaveProfileAsync(ProfileVersion version, CancellationToken ct);
    Task AddRegionAsync(Region region, CancellationToken ct);
    Task AddTypeAsync(DeductionType type, CancellationToken ct);
    Task ValidateAccountAsync(Guid id, string currency, bool commission, CancellationToken ct);
}
