using System.Globalization;
using System.Numerics;
using el.Domain.Common;
using el.Domain.Knowledge;
using el.Domain.Ledger;

namespace el.Domain.Settlements;

public sealed record SettlementBeneficiary(string LineId, Guid PartyId, string Kind, long Weight, long RateUnits);
public sealed record SettlementInput(string ClientId, string TradeId, DateTimeOffset OccurredAt, string Currency,
    long GrossMinor, string RegionCode, Guid ProfileId, string CorporateCode, IReadOnlyList<SettlementBeneficiary> Beneficiaries);
public sealed record SettlementAccount(Guid Id, Guid? PartyId, AccountType Type, string Currency);
public sealed record SettlementSnapshot(CorporateFeeProfile Profile, IReadOnlyList<DeductionRule> Rules,
    IReadOnlyList<SettlementAccount> Accounts, string RegionalOutcome, int CurrencyReferenceVersion,
    int CurrencyExponent, int RoundingPolicyVersion, int CalculationPolicyVersion);
public sealed record SettlementAllocation(string LineId, Guid? PartyId, Guid AccountId, string Component, long AmountMinor);
public sealed record SettlementStep(string Step, Guid? PartyId, string? LineId, long BaseMinor, long ResultMinor,
    long? RateUnits = null, string? Numerator = null, string? Denominator = null, string? RoundingMode = null,
    long? Weight = null, string? Quotient = null, string? Remainder = null, IReadOnlyList<string>? TieBreakOrder = null);
public sealed record SettlementCalculation(IReadOnlyList<SettlementAllocation> Allocations, IReadOnlyList<SettlementStep> Trace);
public sealed class SettlementValidationException(string code, string message) : Exception(message)
{ public string Code { get; } = code; }

public static class SettlementCalculator
{
    public const int PolicyVersion = 1;

    public static SettlementInput Normalize(SettlementInput input)
    {
        if (string.IsNullOrWhiteSpace(input.ClientId) || input.ClientId.Length > 100 ||
            string.IsNullOrWhiteSpace(input.TradeId) || input.TradeId.Length > 100 || input.ProfileId == Guid.Empty)
            throw Invalid("Client, trade and profile identifiers are required.");
        KnowledgeValidation.Currency(input.Currency);
        KnowledgeValidation.Code(input.RegionCode); KnowledgeValidation.Code(input.CorporateCode);
        KnowledgeValidation.Amount(input.GrossMinor, positive: true);
        if (input.OccurredAt.Offset != TimeSpan.Zero || input.OccurredAt == DateTimeOffset.MinValue ||
            input.OccurredAt > DateTimeOffset.MaxValue.AddDays(-30)) throw Invalid("occurredAt must be finite UTC with room for the hold duration.");
        if (input.Beneficiaries is null || input.Beneficiaries.Count is < 1 or > 100) throw Invalid("Supply 1 to 100 beneficiaries.");
        var lines = input.Beneficiaries.ToArray();
        foreach (var line in lines)
        {
            if (line is null || string.IsNullOrWhiteSpace(line.LineId) || line.LineId.Length > 64 || line.PartyId == Guid.Empty)
                throw Invalid("Beneficiary line and party identifiers are required.");
            if (line.Kind == "Affiliate")
            { _ = ScaledPercentage.FromRateUnits(line.RateUnits); if (line.Weight != 0) throw Invalid("An affiliate cannot carry a pool weight."); }
            else if (line.Kind == "Weight")
            { if (line.Weight < 0 || line.RateUnits != 0) throw Invalid("Weights must be non-negative and cannot carry a percentage."); }
            else if (line.Kind == "Remainder")
            { if (line.Weight != 0 || line.RateUnits != 0) throw Invalid("A remainder line cannot carry a weight or percentage."); }
            else throw Invalid("Use Affiliate, Weight or Remainder allocation kinds.");
        }
        if (lines.Select(line => line.LineId).Distinct(StringComparer.Ordinal).Count() != lines.Length) throw Invalid("Allocation line IDs must be unique.");
        var remainders = lines.Count(line => line.Kind == "Remainder");
        var weights = lines.Where(line => line.Kind == "Weight").ToArray();
        if (!(remainders == 1 && weights.Length == 0) && !(remainders == 0 && weights.Any(line => line.Weight > 0)))
            throw Invalid("Choose exactly one remainder beneficiary or a positive-weight pool.");
        Array.Sort(lines, (a, b) => StringComparer.Ordinal.Compare(a.LineId, b.LineId));
        return input with { Beneficiaries = Array.AsReadOnly(lines) };
    }

    public static SettlementCalculation Calculate(SettlementInput supplied, SettlementSnapshot snapshot)
    {
        var input = Normalize(supplied);
        var profile = snapshot.Profile;
        profile.Validate();
        if (snapshot.CalculationPolicyVersion != PolicyVersion || snapshot.RoundingPolicyVersion != RoundingPolicy.CurrentVersion ||
            snapshot.CurrencyReferenceVersion != CurrencyReferenceData.InitialVersion || snapshot.CurrencyExponent !=
            CurrencyReferenceData.Catalog.Resolve(CurrencyCode.Parse(input.Currency), snapshot.CurrencyReferenceVersion).Exponent)
            throw Invalid("Unsupported calculation or reference version.");
        if (profile.ProfileId != input.ProfileId || profile.ClientId != input.ClientId || profile.CorporateCode != input.CorporateCode ||
            profile.Currency != input.Currency || profile.PublishedAtUtc is null || profile.EffectiveFrom > input.OccurredAt || input.OccurredAt >= profile.EffectiveTo)
            throw Invalid("The profile does not apply.");
        var rules = snapshot.Rules.OrderBy(rule => rule.Sequence).ToArray();
        if (rules.Select(rule => rule.Sequence).Distinct().Count() != rules.Length || rules.Select(rule => rule.DeductionTypeCode).Distinct().Count() != rules.Length)
            throw Invalid("Regional rules are ambiguous.");
        var allocations = new List<SettlementAllocation>(); var trace = new List<SettlementStep>();
        var remaining = input.GrossMinor;
        SettlementAccount Account(Guid? id, Guid? party, AccountType type)
        {
            var matches = snapshot.Accounts.Where(account => account.Currency == input.Currency && account.Type == type &&
                (id is not null ? account.Id == id : account.PartyId == party)).ToArray();
            if (matches.Length != 1) throw new SettlementValidationException("INVALID_ACCOUNT_MAPPING", "Exactly one matching account is required.");
            return matches[0];
        }
        void Allocate(string lineId, SettlementAccount account, string component, long amount)
        { allocations.Add(new(lineId, account.PartyId, account.Id, component, amount)); }
        long Percent(string step, Guid? party, string? lineId, long amount, long rate, RoundingMode mode)
        {
            var rounded = Rounding.Round(ScaledPercentage.FromRateUnits(rate).Multiply(amount), mode);
            var result = checked((long)rounded.Value);
            trace.Add(new(step, party, lineId, amount, result, rate, Text(rounded.Numerator), Text(rounded.Denominator), mode.ToString()));
            return result;
        }
        void Deduct(long amount)
        {
            remaining = checked(remaining - amount);
            if (remaining < 0) throw new SettlementValidationException("DEDUCTIONS_EXCEED_GROSS", "Deductions exceed gross.");
        }
        if (rules.Length == 0) trace.Add(new("regional.none", null, null, input.GrossMinor, 0));
        foreach (var rule in rules)
        {
            rule.Validate();
            if (rule.PublishedAtUtc is null || rule.RegionCode != input.RegionCode || rule.Currency != input.Currency ||
                rule.EffectiveFrom > input.OccurredAt || input.OccurredAt >= rule.EffectiveTo) throw Invalid("A regional rule does not apply.");
            var account = Account(rule.BeneficiaryAccountId, null, AccountType.Payable);
            var amount = Percent("regional." + rule.DeductionTypeCode, account.PartyId, null, input.GrossMinor, rule.RateUnits, rule.RoundingMode);
            Deduct(amount); Allocate("rule:" + rule.RuleId.ToString("D"), account, "Regional", amount);
        }
        var processorAccount = Account(profile.ProcessorAccountId, null, AccountType.Payable);
        var processor = checked(Percent("processor.percentage", processorAccount.PartyId, null, input.GrossMinor, profile.ProcessorRateUnits, profile.RoundingMode) + profile.ProcessorFixedMinor);
        if (profile.ProcessorFloorMinor is { } floor) processor = Math.Max(processor, floor);
        if (profile.ProcessorCapMinor is { } cap) processor = Math.Min(processor, cap);
        Deduct(processor); Allocate("processor", processorAccount, "Processor", processor);
        trace.Add(new("processor.final", processorAccount.PartyId, null, input.GrossMinor, processor));
        var commissionAccount = Account(profile.CommissionAccountId, null, AccountType.Revenue);
        var commission = Percent("commission", null, null, input.GrossMinor, profile.CommissionRateUnits, profile.RoundingMode);
        Deduct(commission); Allocate("commission", commissionAccount, "Commission", commission);
        foreach (var line in input.Beneficiaries.Where(line => line.Kind == "Affiliate"))
        {
            var amount = Percent("affiliate", line.PartyId, line.LineId, input.GrossMinor, line.RateUnits, profile.RoundingMode);
            Deduct(amount); Allocate(line.LineId, Account(null, line.PartyId, AccountType.Payable), "Affiliate", amount);
        }
        var pool = input.Beneficiaries.Where(line => line.Kind != "Affiliate").ToArray();
        IReadOnlyList<AllocationShare> shares;
        if (pool[0].Kind == "Remainder")
        { shares = [new(pool[0].LineId, remaining)]; trace.Add(new("pool.remainder", pool[0].PartyId, pool[0].LineId, remaining, remaining)); }
        else
        {
            shares = WeightedAllocation.Allocate(remaining, pool.Select(line => new AllocationLine(line.LineId, line.Weight)));
            var totalWeight = pool.Aggregate(BigInteger.Zero, (total, line) => total + line.Weight);
            var residualOrder = pool.OrderByDescending(line => new BigInteger(remaining) * line.Weight % totalWeight)
                .ThenBy(line => line.LineId, StringComparer.Ordinal).Select(line => line.LineId).ToArray();
            foreach (var line in pool)
            {
                var numerator = new BigInteger(remaining) * line.Weight;
                var quotient = BigInteger.DivRem(numerator, totalWeight, out var remainder);
                trace.Add(new("pool.weight", line.PartyId, line.LineId, remaining, shares.Single(share => share.AllocationLineId == line.LineId).MinorUnits,
                    Numerator: Text(numerator), Denominator: Text(totalWeight), Weight: line.Weight, Quotient: Text(quotient), Remainder: Text(remainder), TieBreakOrder: Array.AsReadOnly(residualOrder)));
            }
        }
        foreach (var share in shares)
        {
            var line = pool.Single(line => line.LineId == share.AllocationLineId);
            var escrow = profile.HoldDays == 0 ? 0 : Percent("escrow", line.PartyId, line.LineId, share.MinorUnits, profile.EscrowRateUnits, profile.RoundingMode);
            var payable = share.MinorUnits - escrow;
            Allocate(line.LineId, Account(null, line.PartyId, AccountType.Payable), "Payable", payable);
            Allocate(line.LineId, Account(null, line.PartyId, AccountType.Escrow), "Escrow", escrow);
            trace.Add(new("payable", line.PartyId, line.LineId, share.MinorUnits, payable));
        }
        if (allocations.Aggregate(BigInteger.Zero, (sum, row) => sum + row.AmountMinor) != input.GrossMinor) throw Invalid("Settlement does not conserve gross.");
        return new(Array.AsReadOnly(allocations.ToArray()), Array.AsReadOnly(trace.ToArray()));
    }
    private static string Text(BigInteger value) => value.ToString(CultureInfo.InvariantCulture);
    private static SettlementValidationException Invalid(string message) => new("INVALID_SETTLEMENT", message);
}
