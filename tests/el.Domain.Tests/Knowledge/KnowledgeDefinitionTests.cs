using el.Domain.Common;
using el.Domain.Knowledge;

namespace el.Domain.Tests.Knowledge;

public sealed class KnowledgeDefinitionTests
{
    private static readonly DateTimeOffset From = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddYears(1);
    private static DeductionRule Rule() => new(Guid.NewGuid(), Guid.NewGuid(), 1, "DEMO-A", "LEVY", "Gross", 10_000_000, 1,
        Guid.NewGuid(), "USD", RoundingMode.HalfEven, From, To, "Synthetic example", true);
    private static CorporateFeeProfile Profile() => new(Guid.NewGuid(), Guid.NewGuid(), 1, "test-client", "DEMO", "USD",
        10_000_000, 2_900_000, 30, 50, 500, RoundingMode.HalfEven, 10_000_000, 7,
        Guid.NewGuid(), Guid.NewGuid(), From, To, "Synthetic example", true);

    [Fact]
    public void Calculates_separately_rounded_gross_deductions_and_beneficiary_escrow()
    {
        Assert.Equal(1000, Rule().Calculate(10000));
        Assert.Equal(new FeeCalculation(1000, 320, 800, 7200), Profile().Calculate(10000, 8000));
        Assert.Equal(new FeeCalculation(10, 50, 8, 72), Profile().Calculate(100, 80));
        Assert.Equal(500, Profile().Calculate(20000, 10000).ProcessorMinor);
        Assert.Equal(0, (Profile() with { HoldDays = 0 }).Calculate(10000, 8000).EscrowMinor);
        Assert.Equal(new FeeCalculation(0, 0, 0, 0), (Profile() with
        { CommissionRateUnits = 0, ProcessorRateUnits = 0, ProcessorFixedMinor = 0, ProcessorFloorMinor = null, ProcessorCapMinor = null, EscrowRateUnits = 0 }).Calculate(1, 0));
        Assert.Equal(10000, (Profile() with { EscrowRateUnits = 100_000_000 }).Calculate(20000, 10000).EscrowMinor);
    }

    [Theory]
    [InlineData(RoundingMode.HalfEven, 2)]
    [InlineData(RoundingMode.HalfAwayFromZero, 3)]
    [InlineData(RoundingMode.TowardZero, 2)]
    public void Regional_rounding_uses_the_declared_policy(RoundingMode mode, long expected) =>
        Assert.Equal(expected, (Rule() with { RateUnits = 50_000_000, RoundingMode = mode }).Calculate(5));

    public static IEnumerable<object[]> InvalidRules()
    {
        var r = Rule();
        foreach (var value in new[]
        {
            r with { Id = Guid.Empty }, r with { RuleId = Guid.Empty }, r with { Version = 0 },
            r with { EffectiveTo = From }, r with { EffectiveTo = From.AddDays(-1) },
            r with { EffectiveFrom = From.ToOffset(TimeSpan.FromHours(1)) }, r with { EffectiveTo = To.ToOffset(TimeSpan.FromHours(1)) },
            r with { EffectiveFrom = DateTimeOffset.MinValue }, r with { EffectiveTo = DateTimeOffset.MaxValue },
            r with { SourceNote = " " }, r with { SourceNote = new string('a', 2001) },
            r with { RegionCode = "demo" }, r with { DeductionTypeCode = "!" }, r with { RegionCode = new string('A', 65) },
            r with { RegionCode = " " }, r with { CalculationBase = "Net" }, r with { Sequence = 0 }, r with { Sequence = 1001 },
            r with { BeneficiaryAccountId = Guid.Empty }, r with { RateUnits = -1 }, r with { RateUnits = 100_000_001 }, r with { RoundingMode = 0 },
        }) yield return [value];
    }
    [Theory, MemberData(nameof(InvalidRules))]
    public void Rules_reject_invalid_content(DeductionRule value) => Assert.ThrowsAny<ArgumentException>(value.Validate);

    public static IEnumerable<object[]> InvalidProfiles()
    {
        var p = Profile();
        foreach (var value in new[]
        {
            p with { ProfileId = Guid.Empty }, p with { ClientId = " " }, p with { ClientId = new string('x', 101) },
            p with { CorporateCode = "lowercase" }, p with { CommissionRateUnits = -1 }, p with { ProcessorRateUnits = 100_000_001 },
            p with { EscrowRateUnits = -1 }, p with { ProcessorFixedMinor = -1 }, p with { ProcessorFloorMinor = -1 },
            p with { ProcessorCapMinor = Money.MaxMinorUnits + 1 }, p with { ProcessorFloorMinor = 501 },
            p with { HoldDays = -1 }, p with { HoldDays = 31 }, p with { CommissionAccountId = Guid.Empty },
            p with { ProcessorAccountId = Guid.Empty }, p with { RoundingMode = 0 },
        }) yield return [value];
    }
    [Theory, MemberData(nameof(InvalidProfiles))]
    public void Profiles_reject_invalid_content(CorporateFeeProfile value) => Assert.ThrowsAny<ArgumentException>(value.Validate);

    [Fact]
    public void Amount_ranges_currency_and_excessive_fees_fail_explicitly()
    {
        Assert.Throws<UnknownCurrencyException>(() => (Rule() with { Currency = "EUR" }).Validate());
        Assert.ThrowsAny<ArgumentException>(() => Rule().Calculate(0));
        Assert.ThrowsAny<ArgumentException>(() => Rule().Calculate(Money.MaxMinorUnits + 1));
        Assert.ThrowsAny<ArgumentException>(() => Profile().Calculate(100, 101));
        Assert.ThrowsAny<ArgumentException>(() => Profile().Calculate(100, -1));
        Assert.ThrowsAny<ArgumentException>(() => Profile().Calculate(1, 0));
        Assert.ThrowsAny<ArgumentException>(() => (Profile() with { ProcessorFixedMinor = Money.MaxMinorUnits, ProcessorCapMinor = null })
            .Calculate(Money.MaxMinorUnits, 0));
        KnowledgeValidation.Code("A0_.-Z");
    }
}
