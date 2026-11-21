using System.Numerics;
using el.Domain.Common;
using el.Domain.Knowledge;
using el.Domain.Ledger;
using el.Domain.Settlements;

namespace el.Domain.Tests.Settlements;

public sealed class SettlementCalculatorTests
{
    private static readonly Guid Seller = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Affiliate = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset From = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid ProfileId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid Commission = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid Processor = Guid.Parse("30000000-0000-0000-0000-000000000002");
    private static SettlementInput Input() => new("client", "T-1", From.AddMonths(1), "USD", 25000, "DEMO", ProfileId, "DEMO",
        [new("affiliate", Affiliate, "Affiliate", 0, 3_000_000), new("seller", Seller, "Remainder", 0, 0)]);
    private static SettlementSnapshot Snapshot()
    {
        var accounts = new List<SettlementAccount>
        { new(Commission, null, AccountType.Revenue, "USD"), new(Processor, Guid.NewGuid(), AccountType.Payable, "USD") };
        foreach (var party in new[] { Seller, Affiliate })
        { accounts.Add(new(Guid.NewGuid(), party, AccountType.Payable, "USD")); accounts.Add(new(Guid.NewGuid(), party, AccountType.Escrow, "USD")); }
        var profile = new CorporateFeeProfile(Guid.NewGuid(), ProfileId, 1, "client", "DEMO", "USD", 12_000_000, 2_900_000, 30,
            null, null, RoundingMode.HalfEven, 10_000_000, 7, Commission, Processor, From, From.AddYears(1), "Synthetic worked example", true, "test", From);
        return new(profile, [], accounts, "NO_DEDUCTION", 1, 2, 1, 1);
    }
    [Theory]
    [InlineData(false, 18445, 2050)]
    [InlineData(true, 17995, 2000)]
    public void PRP_worked_examples_conserve_250_USD_exactly(bool regional, long payable, long escrow)
    {
        var snapshot = Snapshot();
        if (regional)
        {
            var account = new SettlementAccount(Guid.NewGuid(), Guid.NewGuid(), AccountType.Payable, "USD");
            snapshot = snapshot with { Accounts = snapshot.Accounts.Append(account).ToArray(), Rules =
                [new DeductionRule(Guid.NewGuid(), Guid.NewGuid(), 1, "DEMO", "LEVY", "Gross", 2_000_000, 1, account.Id,
                    "USD", RoundingMode.HalfEven, From, From.AddYears(1), "Synthetic 2%", true, "test", From)], RegionalOutcome = "DEDUCTIONS_APPLIED" };
        }
        var result = SettlementCalculator.Calculate(Input(), snapshot);
        Assert.Equal(25000, result.Allocations.Sum(row => row.AmountMinor));
        Assert.Equal(755, Assert.Single(result.Allocations, row => row.Component == "Processor").AmountMinor);
        Assert.Equal(3000, Assert.Single(result.Allocations, row => row.Component == "Commission").AmountMinor);
        Assert.Equal(750, Assert.Single(result.Allocations, row => row.Component == "Affiliate").AmountMinor);
        Assert.Equal(payable, Assert.Single(result.Allocations, row => row.Component == "Payable").AmountMinor);
        Assert.Equal(escrow, Assert.Single(result.Allocations, row => row.Component == "Escrow").AmountMinor);
        var step = Assert.Single(result.Trace, row => row.Step == "escrow");
        Assert.Equal("100000000", step.Denominator); Assert.Equal("HalfEven", step.RoundingMode);
        Assert.Equal(new BigInteger(step.BaseMinor) * 10_000_000, BigInteger.Parse(step.Numerator!));
    }
    [Fact]
    public void Weighted_allocation_is_deterministic_records_remainders_and_omits_zero_holds_from_journal_inputs()
    {
        var snapshot = Snapshot();
        snapshot = snapshot with { Profile = snapshot.Profile with { CommissionRateUnits = 0, ProcessorRateUnits = 0, ProcessorFixedMinor = 0, HoldDays = 0 } };
        var input = Input() with { GrossMinor = 1, Beneficiaries = [new("B", Seller, "Weight", 1, 0), new("A", Affiliate, "Weight", 1, 0)] };
        var result = SettlementCalculator.Calculate(input, snapshot);
        Assert.Equal(1, Assert.Single(result.Allocations, row => row.LineId == "A" && row.Component == "Payable").AmountMinor);
        Assert.Equal(0, Assert.Single(result.Allocations, row => row.LineId == "B" && row.Component == "Payable").AmountMinor);
        Assert.All(result.Trace.Where(row => row.Step == "pool.weight"), row =>
        { Assert.Equal("0", row.Quotient); Assert.Equal("1", row.Remainder); Assert.Equal(new[] { "A", "B" }, row.TieBreakOrder); });
        Assert.Equal(result.Allocations, SettlementCalculator.Calculate(input with { Beneficiaries = input.Beneficiaries.Reverse().ToArray() }, snapshot).Allocations);
        var random = new Random(11011);
        snapshot = snapshot with { Profile = snapshot.Profile with { HoldDays = 30 } };
        for (var i = 0; i < 1000; i++)
        {
            input = input with { GrossMinor = random.Next(1, 1000000), Beneficiaries = [new("A", Affiliate, "Weight", random.Next(1, 1000), 0), new("B", Seller, "Weight", random.Next(1, 1000), 0)] };
            var calculation = SettlementCalculator.Calculate(input, snapshot);
            Assert.Equal(input.GrossMinor, calculation.Allocations.Sum(row => row.AmountMinor));
            Assert.All(calculation.Allocations, row => Assert.True(row.AmountMinor >= 0));
        }
    }
    public static IEnumerable<object[]> InvalidInputs()
    {
        var value = Input(); var line = value.Beneficiaries[1];
        foreach (var input in new[]
        {
            value with { ClientId = " " }, value with { ClientId = new string('c',101) }, value with { TradeId = "" }, value with { TradeId = new string('t',101) },
            value with { ProfileId = Guid.Empty }, value with { GrossMinor = 0 }, value with { OccurredAt = DateTimeOffset.MinValue },
            value with { OccurredAt = DateTimeOffset.MaxValue }, value with { OccurredAt = value.OccurredAt.ToOffset(TimeSpan.FromHours(1)) },
            value with { Beneficiaries = [] }, value with { Beneficiaries = Enumerable.Repeat(line,101).ToArray() },
            value with { Beneficiaries = [null!] }, value with { Beneficiaries = [line with { PartyId = Guid.Empty }] },
            value with { Beneficiaries = [line with { LineId = " " }] }, value with { Beneficiaries = [line with { LineId = new string('x',65) }] },
            value with { Beneficiaries = [line, line] }, value with { Beneficiaries = [line with { Kind = "Unknown" }] },
            value with { Beneficiaries = [line with { Kind = "Affiliate", Weight = 1 }] },
            value with { Beneficiaries = [line with { Kind = "Weight", Weight = -1 }] }, value with { Beneficiaries = [line with { Kind = "Weight", RateUnits = 1 }] },
            value with { Beneficiaries = [line with { Weight = 1 }] }, value with { Beneficiaries = [line with { RateUnits = 1 }] },
            value with { Beneficiaries = [line with { Kind = "Weight" }] },
        }) yield return [input];
    }
    [Theory, MemberData(nameof(InvalidInputs))]
    public void Invalid_input_is_rejected(SettlementInput input) =>
        Assert.True(Record.Exception(() => SettlementCalculator.Normalize(input)) is SettlementValidationException or ArgumentException);
    [Fact]
    public void Excess_deductions_invalid_snapshots_and_ambiguous_mappings_fail()
    {
        var snapshot = Snapshot();
        Assert.Equal("DEDUCTIONS_EXCEED_GROSS", Assert.Throws<SettlementValidationException>(() =>
            SettlementCalculator.Calculate(Input(), snapshot with { Profile = snapshot.Profile with { CommissionRateUnits = 100_000_000 } })).Code);
        foreach (var invalid in new[]
        {
            snapshot with { CalculationPolicyVersion = 2 }, snapshot with { CurrencyReferenceVersion = 2 }, snapshot with { CurrencyExponent = 3 },
            snapshot with { RoundingPolicyVersion = 2 }, snapshot with { Profile = snapshot.Profile with { ClientId = "other" } },
            snapshot with { Accounts = [] }, snapshot with { Accounts = snapshot.Accounts.Concat(snapshot.Accounts).ToArray() },
        }) Assert.Throws<SettlementValidationException>(() => SettlementCalculator.Calculate(Input(), invalid));
        var floor = snapshot with { Profile = snapshot.Profile with { ProcessorFloorMinor = 1000, ProcessorCapMinor = 1100 } };
        Assert.Equal(1000, SettlementCalculator.Calculate(Input(), floor).Allocations.Single(row => row.Component == "Processor").AmountMinor);
        Assert.Equal(1100, SettlementCalculator.Calculate(Input() with { GrossMinor = 100000 }, floor).Allocations.Single(row => row.Component == "Processor").AmountMinor);
    }
}
