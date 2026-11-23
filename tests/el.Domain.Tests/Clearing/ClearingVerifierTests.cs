using System.Numerics;
using el.Domain.Clearing;
using el.Domain.Common;
using el.Domain.Ledger;

namespace el.Domain.Tests.Clearing;

public sealed class ClearingVerifierTests
{
    private static readonly CurrencyCode Usd = CurrencyReferenceData.Usd;
    private static readonly DateOnly Day = new(2026, 11, 21);

    private static PartyId Party(int number) =>
        new(Guid.Parse(FormattableString.Invariant($"00000000-0000-0000-0000-{number:D12}")));

    private static JournalEntryId Entry(int number) =>
        new(Guid.Parse(FormattableString.Invariant($"00000000-0000-0000-0001-{number:D12}")));

    private static readonly PayoutPlan Plan = PayoutPlanner.Plan(
        Day, Usd, [new PartyPayable(Party(1), 18445, 0, 0), new PartyPayable(Party(2), 750, 0, 0)]);

    private static SelectedEntryFacts Good(int number) => new(Entry(number), Usd, true, 2, 25000, 25000);

    private static ClearingVerificationInput Input(
        IReadOnlyList<CurrencyTotals>? trial = null,
        IReadOnlyList<SelectedEntryFacts>? entries = null,
        PayoutPlan? plan = null,
        BigInteger? instructionTotal = null,
        BigInteger? reservationTotal = null)
    {
        var effective = plan ?? Plan;
        var planned = effective.TotalMinorUnits;

        return new ClearingVerificationInput(
            Usd,
            trial ?? new[] { new CurrencyTotals(Usd, 25000, 25000) },
            entries ?? new[] { Good(1) },
            effective,
            instructionTotal ?? planned,
            reservationTotal ?? planned);
    }

    [Fact]
    public void A_consistent_cut_is_valid()
    {
        var report = ClearingVerifier.Verify(Input());

        Assert.True(report.IsValid);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void Unbalanced_currencies_are_reported_in_currency_order()
    {
        var trial = new[]
        {
            new CurrencyTotals(Usd, 3, 4),
            new CurrencyTotals(CurrencyReferenceData.Jpy, 5, 5),
            new CurrencyTotals(CurrencyCode.Parse("KWD"), 9, 1),
        };

        var report = ClearingVerifier.Verify(Input(trial: trial));

        Assert.Equal(
            new[]
            {
                new ClearingFinding(ClearingFindingCode.TrialBalanceUnbalanced, "KWD"),
                new ClearingFinding(ClearingFindingCode.TrialBalanceUnbalanced, "USD"),
            },
            report.Findings.ToArray());
    }

    [Fact]
    public void Planner_findings_are_part_of_the_report()
    {
        var plan = PayoutPlanner.Plan(Day, Usd, [new PartyPayable(Party(3), 100, 200, 0)]);

        var report = ClearingVerifier.Verify(Input(plan: plan));

        Assert.False(report.IsValid);
        Assert.Equal(
            new[] { new ClearingFinding(ClearingFindingCode.NegativePayable, Party(3).Value.ToString("D")) },
            report.Findings.ToArray());
    }

    [Theory]
    [InlineData(1, 19195)]
    [InlineData(19195, 1)]
    [InlineData(19196, 19196)]
    public void Persisted_instruction_and_reservation_totals_must_equal_the_plan(long instructions, long reservations)
    {
        var report = ClearingVerifier.Verify(Input(instructionTotal: instructions, reservationTotal: reservations));

        Assert.Equal(
            new[] { new ClearingFinding(ClearingFindingCode.ReservationTotalMismatch, "USD") },
            report.Findings.ToArray());
    }

    [Fact]
    public void An_incompletely_posted_entry_reports_every_broken_check_in_order()
    {
        var broken = new SelectedEntryFacts(Entry(7), CurrencyReferenceData.Jpy, false, 1, 10, 20);
        var subject = Entry(7).ToString();

        var report = ClearingVerifier.Verify(Input(entries: new[] { broken }));

        Assert.Equal(
            new[]
            {
                new ClearingFinding(ClearingFindingCode.EntryNotFinalized, subject),
                new ClearingFinding(ClearingFindingCode.EntryTooFewLines, subject),
                new ClearingFinding(ClearingFindingCode.EntryUnbalanced, subject),
                new ClearingFinding(ClearingFindingCode.EntryCurrencyMismatch, subject),
            },
            report.Findings.ToArray());
    }

    [Fact]
    public void Entry_findings_follow_the_entry_id_order_not_the_input_order()
    {
        var entries = new[]
        {
            new SelectedEntryFacts(Entry(2), Usd, false, 2, 5, 5),
            new SelectedEntryFacts(Entry(1), Usd, false, 2, 5, 5),
        };

        var report = ClearingVerifier.Verify(Input(entries: entries));

        Assert.Equal(
            new[]
            {
                new ClearingFinding(ClearingFindingCode.EntryNotFinalized, Entry(1).ToString()),
                new ClearingFinding(ClearingFindingCode.EntryNotFinalized, Entry(2).ToString()),
            },
            report.Findings.ToArray());
    }

    [Fact]
    public void Verify_rejects_null_input()
    {
        Assert.Throws<ArgumentNullException>(() => ClearingVerifier.Verify(null!));
    }
}
