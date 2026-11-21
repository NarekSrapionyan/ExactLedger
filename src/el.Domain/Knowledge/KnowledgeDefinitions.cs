using el.Domain.Common;

namespace el.Domain.Knowledge;

public sealed record Region(string Code, string Name);
public sealed record DeductionType(string Code, string Name, string Description);

/// <summary>A gross-based deduction. Each ID identifies one immutable published version.</summary>
public sealed record DeductionRule(Guid Id, Guid RuleId, int Version, string RegionCode, string DeductionTypeCode,
    string CalculationBase, long RateUnits, int Sequence, Guid BeneficiaryAccountId, string Currency,
    RoundingMode RoundingMode, DateTimeOffset EffectiveFrom, DateTimeOffset EffectiveTo, string SourceNote,
    bool IsSynthetic, string? PublishedBy = null, DateTimeOffset? PublishedAtUtc = null)
{
    public void Validate()
    {
        KnowledgeValidation.Version(Id, RuleId, Version, EffectiveFrom, EffectiveTo, SourceNote);
        KnowledgeValidation.Code(RegionCode);
        KnowledgeValidation.Code(DeductionTypeCode);
        if (CalculationBase != "Gross") throw new ArgumentException("Only the Gross calculation base is supported.");
        if (Sequence < 1 || Sequence > 1000) throw new ArgumentException("Sequence must be 1 to 1000.");
        if (BeneficiaryAccountId == Guid.Empty) throw new ArgumentException("A beneficiary account is required.");
        KnowledgeValidation.Currency(Currency);
        _ = ScaledPercentage.FromRateUnits(RateUnits);
        _ = RoundingPolicy.For(RoundingMode);
    }

    public long Calculate(long grossMinor)
    {
        Validate();
        KnowledgeValidation.Amount(grossMinor, positive: true);
        return KnowledgeValidation.Percentage(grossMinor, RateUnits, RoundingMode);
    }
}

/// <summary>Seller-borne processor fees and beneficiary escrow; all amounts are exact minor units.</summary>
public sealed record CorporateFeeProfile(Guid Id, Guid ProfileId, int Version, string ClientId, string CorporateCode,
    string Currency, long CommissionRateUnits, long ProcessorRateUnits, long ProcessorFixedMinor,
    long? ProcessorFloorMinor, long? ProcessorCapMinor, RoundingMode RoundingMode, long EscrowRateUnits,
    int HoldDays, Guid CommissionAccountId, Guid ProcessorAccountId, DateTimeOffset EffectiveFrom,
    DateTimeOffset EffectiveTo, string SourceNote, bool IsSynthetic,
    string? PublishedBy = null, DateTimeOffset? PublishedAtUtc = null)
{
    public void Validate()
    {
        KnowledgeValidation.Version(Id, ProfileId, Version, EffectiveFrom, EffectiveTo, SourceNote);
        ArgumentException.ThrowIfNullOrWhiteSpace(ClientId);
        if (ClientId.Length > 100) throw new ArgumentException("Client ID is too long.");
        KnowledgeValidation.Code(CorporateCode);
        KnowledgeValidation.Currency(Currency);
        foreach (var rate in new[] { CommissionRateUnits, ProcessorRateUnits, EscrowRateUnits })
            _ = ScaledPercentage.FromRateUnits(rate);
        foreach (var amount in new long?[] { ProcessorFixedMinor, ProcessorFloorMinor, ProcessorCapMinor })
            if (amount is { } value) KnowledgeValidation.Amount(value);
        if (ProcessorFloorMinor > ProcessorCapMinor) throw new ArgumentException("Processor floor exceeds cap.");
        if (HoldDays is < 0 or > 30) throw new ArgumentException("Hold duration must be 0 to 30 days.");
        if (CommissionAccountId == Guid.Empty || ProcessorAccountId == Guid.Empty)
            throw new ArgumentException("Commission and processor accounts are required.");
        _ = RoundingPolicy.For(RoundingMode);
    }

    public FeeCalculation Calculate(long grossMinor, long beneficiaryShareMinor)
    {
        Validate();
        KnowledgeValidation.Amount(grossMinor, positive: true);
        KnowledgeValidation.Amount(beneficiaryShareMinor);
        if (beneficiaryShareMinor > grossMinor) throw new ArgumentException("Beneficiary share exceeds gross.");
        var commission = KnowledgeValidation.Percentage(grossMinor, CommissionRateUnits, RoundingMode);
        var processor = checked(KnowledgeValidation.Percentage(grossMinor, ProcessorRateUnits, RoundingMode) + ProcessorFixedMinor);
        if (ProcessorFloorMinor is { } floor) processor = Math.Max(processor, floor);
        if (ProcessorCapMinor is { } cap) processor = Math.Min(processor, cap);
        KnowledgeValidation.Amount(processor);
        if (checked(commission + processor) > grossMinor) throw new ArgumentException("Fees exceed gross.");
        var escrow = HoldDays == 0 ? 0 : KnowledgeValidation.Percentage(beneficiaryShareMinor, EscrowRateUnits, RoundingMode);
        return new(commission, processor, escrow, beneficiaryShareMinor - escrow);
    }
}

public sealed record FeeCalculation(long CommissionMinor, long ProcessorMinor, long EscrowMinor, long PayableMinor);
public sealed record RuleFixture(Guid Id, Guid RuleVersionId, string Note, long GrossMinor, long ExpectedMinor);
public sealed record ProfileFixture(Guid Id, Guid ProfileVersionId, string Note, long GrossMinor, long ShareMinor,
    long ExpectedCommissionMinor, long ExpectedProcessorMinor, long ExpectedEscrowMinor, long ExpectedPayableMinor);

public static class KnowledgeValidation
{
    public static void Code(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 64 ||
            !code.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.'))
            throw new ArgumentException("Codes must contain 1 to 64 uppercase ASCII letters, digits, '.', '_' or '-'.");
    }

    public static void Currency(string currency)
    {
        _ = CurrencyReferenceData.Catalog.Resolve(CurrencyCode.Parse(currency), CurrencyReferenceData.InitialVersion);
    }

    public static void Version(Guid id, Guid stableId, int version, DateTimeOffset from, DateTimeOffset to, string source)
    {
        if (id == Guid.Empty || stableId == Guid.Empty || version < 1) throw new ArgumentException("Version identifiers and a positive version are required.");
        if (from.Offset != TimeSpan.Zero || to.Offset != TimeSpan.Zero || from >= to ||
            from == DateTimeOffset.MinValue || to == DateTimeOffset.MaxValue)
            throw new ArgumentException("A finite non-empty half-open UTC interval is required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (source.Length > 2000) throw new ArgumentException("Source note is too long.");
    }

    public static void Amount(long amount, bool positive = false)
    {
        if (amount < (positive ? 1 : 0) || amount > Money.MaxMinorUnits)
            throw new ArgumentException("The amount is outside the supported positive/non-negative Money range.");
    }

    public static long Percentage(long amount, long rate, RoundingMode mode) =>
        checked((long)Rounding.Round(ScaledPercentage.FromRateUnits(rate).Multiply(amount), mode).Value);
}
