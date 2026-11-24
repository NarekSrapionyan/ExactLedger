using System.Numerics;
using el.Domain.Clearing;
using el.Domain.Common;
using el.Domain.Ledger;

namespace el.Domain.Tests.Clearing;

public sealed class PayoutPlannerTests
{
    private static readonly CurrencyCode Usd = CurrencyReferenceData.Usd;
    private static readonly DateOnly Day = new(2026, 11, 21);

    private static PartyId Party(int number) =>
        new(Guid.Parse(FormattableString.Invariant($"00000000-0000-0000-0000-{number:D12}")));

    private static string PartyText(int number) => Party(number).Value.ToString("D");

    private static PartyPayable Payable(int party, long credit, long debit = 0, long reserved = 0) =>
        new(Party(party), credit, debit, reserved);

    [Fact]
    public void Plans_the_prp_example_payees_in_party_order_with_contiguous_references()
    {
        var plan = PayoutPlanner.Plan(Day, Usd, [Payable(2, 750), Payable(1, 18445)]);

        Assert.Empty(plan.Findings);
        Assert.Equal(
            new[] { "PO-20261121-USD-000001", "PO-20261121-USD-000002" },
            plan.Instructions.Select(instruction => instruction.Reference).ToArray());
        Assert.Equal(new[] { Party(1), Party(2) }, plan.Instructions.Select(instruction => instruction.PartyId).ToArray());
        Assert.Equal(new[] { 18445L, 750L }, plan.Instructions.Select(instruction => instruction.Amount.MinorUnits).ToArray());
        Assert.Equal(new BigInteger(19195), plan.TotalMinorUnits);
    }

    [Fact]
    public void The_plan_does_not_depend_on_input_order()
    {
        var forward = PayoutPlanner.Plan(Day, Usd, [Payable(1, 10), Payable(2, 20), Payable(3, 30)]);
        var shuffled = PayoutPlanner.Plan(Day, Usd, [Payable(3, 30), Payable(1, 10), Payable(2, 20)]);

        Assert.Equal(forward.Instructions.ToArray(), shuffled.Instructions.ToArray());
    }

    [Fact]
    public void Parties_without_available_funds_get_no_instruction_and_no_finding()
    {
        var plan = PayoutPlanner.Plan(Day, Usd, [Payable(1, 500, 500), Payable(2, 1000, 0, 1000), Payable(3, 0)]);

        Assert.Empty(plan.Instructions);
        Assert.Empty(plan.Findings);
        Assert.Equal(BigInteger.Zero, plan.TotalMinorUnits);
    }

    [Fact]
    public void Active_reservations_reduce_the_available_amount()
    {
        var plan = PayoutPlanner.Plan(Day, Usd, [Payable(1, 10000, 2000, 3000)]);

        Assert.Equal(5000, Assert.Single(plan.Instructions).Amount.MinorUnits);
    }

    [Fact]
    public void A_debit_balance_is_a_negative_payable_and_gets_no_instruction()
    {
        var plan = PayoutPlanner.Plan(Day, Usd, [Payable(1, 100, 200), Payable(2, 500)]);

        Assert.Equal(
            new[] { new ClearingFinding(ClearingFindingCode.NegativePayable, PartyText(1)) },
            plan.Findings.ToArray());

        var instruction = Assert.Single(plan.Instructions);

        Assert.Equal("PO-20261121-USD-000001", instruction.Reference);
        Assert.Equal(Party(2), instruction.PartyId);
    }

    [Fact]
    public void Reservations_above_the_balance_are_negative_availability()
    {
        var plan = PayoutPlanner.Plan(Day, Usd, [Payable(1, 100, 0, 150)]);

        Assert.Empty(plan.Instructions);
        Assert.Equal(
            new[] { new ClearingFinding(ClearingFindingCode.NegativeAvailability, PartyText(1)) },
            plan.Findings.ToArray());
    }

    [Fact]
    public void An_amount_above_the_money_range_is_a_finding_not_an_exception()
    {
        var huge = new PartyPayable(Party(1), BigInteger.Pow(10, 30), BigInteger.Zero, BigInteger.Zero);

        var plan = PayoutPlanner.Plan(Day, Usd, [huge, Payable(2, Money.MaxMinorUnits + 1), Payable(3, Money.MaxMinorUnits)]);

        Assert.Equal(
            new[]
            {
                new ClearingFinding(ClearingFindingCode.PayoutAmountOutOfRange, PartyText(1)),
                new ClearingFinding(ClearingFindingCode.PayoutAmountOutOfRange, PartyText(2)),
            },
            plan.Findings.ToArray());

        var instruction = Assert.Single(plan.Instructions);

        Assert.Equal(Money.MaxMinorUnits, instruction.Amount.MinorUnits);
        Assert.Equal("PO-20261121-USD-000001", instruction.Reference);
    }

    [Fact]
    public void Plan_rejects_invalid_arguments()
    {
        Assert.Throws<ArgumentException>(() => PayoutPlanner.Plan(Day, Usd, [Payable(1, 10), Payable(1, 20)]));
        Assert.Throws<ArgumentException>(() => PayoutPlanner.Plan(Day, Usd, [Payable(1, 10), null!]));
        Assert.Throws<ArgumentNullException>(() => PayoutPlanner.Plan(Day, null!, [Payable(1, 10)]));
        Assert.Throws<ArgumentNullException>(() => PayoutPlanner.Plan(Day, Usd, null!));
    }
}
