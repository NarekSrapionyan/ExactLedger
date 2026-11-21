using el.Domain.Common;
using System.Text.Json.Serialization;

namespace el.Api.Knowledge;

/// <summary>A regional deduction example with exact expected money.</summary>
public sealed record RuleExampleRequest(string Note, Money Gross, Money ExpectedDeduction);
/// <summary>A draft gross-based regional deduction version.</summary>
public sealed record RuleRequest(Guid RuleId, int Version, string RegionCode, string DeductionTypeCode,
    string CalculationBase, long RateUnits, int Sequence, Guid BeneficiaryAccountId, string Currency,
    string RoundingMode, DateTimeOffset EffectiveFrom, DateTimeOffset EffectiveTo, string SourceNote,
    bool IsSynthetic, IReadOnlyList<RuleExampleRequest> Fixtures);
/// <summary>A corporate profile example; share is the amount before escrow.</summary>
public sealed record ProfileExampleRequest(string Note, Money Gross, Money Share, Money ExpectedCommission,
    Money ExpectedProcessor, Money ExpectedEscrow, Money ExpectedPayable);
/// <summary>A seller-borne fee and escrow profile. Rate units are percent times 1,000,000.</summary>
public sealed record ProfileRequest(Guid ProfileId, int Version, string ClientId, string CorporateCode,
    string Currency, long CommissionRateUnits, long ProcessorRateUnits, Money ProcessorFixed,
    [property: JsonConverter(typeof(OptionalMoneyJsonConverter))] Money? ProcessorFloor,
    [property: JsonConverter(typeof(OptionalMoneyJsonConverter))] Money? ProcessorCap,
    string RoundingMode, long EscrowRateUnits, int HoldDays,
    Guid CommissionAccountId, Guid ProcessorAccountId, DateTimeOffset EffectiveFrom,
    DateTimeOffset EffectiveTo, string SourceNote, bool IsSynthetic, IReadOnlyList<ProfileExampleRequest> Fixtures);
/// <summary>A saved rule version with server-owned publication metadata.</summary>
public sealed record RuleResponse(Guid Id, RuleRequest Content, string? PublishedBy, DateTimeOffset? PublishedAtUtc);
/// <summary>A saved corporate profile version with server-owned publication metadata.</summary>
public sealed record ProfileResponse(Guid Id, ProfileRequest Content, string? PublishedBy, DateTimeOffset? PublishedAtUtc);
/// <summary>A known region, including explicit no-deduction regions.</summary>
public sealed record RegionRequest(string Code, string Name);
/// <summary>A typed deduction category.</summary>
public sealed record DeductionTypeRequest(string Code, string Name, string Description);
/// <summary>All attached expected-result fixtures passed.</summary>
public sealed record FixtureValidationResponse(bool Passed);
