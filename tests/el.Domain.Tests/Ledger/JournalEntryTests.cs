using System.Numerics;
using el.Domain.Common;
using el.Domain.Ledger;
using static el.Domain.Tests.Ledger.LedgerFixtures;

namespace el.Domain.Tests.Ledger;

public sealed class JournalEntryTests
{
    [Fact]
    public void Creates_a_balanced_entry_with_numbered_immutable_lines()
    {
        var clearing = Clearing();
        var payable = Payable();
        var revenue = Revenue();

        var entry = JournalEntry.Create(EntryId(), "Trade T-1 capture",
            [Debit(clearing, 25000), Credit(payable, 22500), Credit(revenue, 2500)]);

        Assert.Equal(EntryId(), entry.Id);
        Assert.Equal("Trade T-1 capture", entry.Description);
        Assert.Equal(Usd, entry.Currency);
        Assert.Equal(new BigInteger(25000), entry.TotalMinorUnits);
        Assert.Equal(new[] { 1, 2, 3 }, entry.Lines.Select(line => line.LineNumber));
        Assert.Equal(new[] { clearing.Id, payable.Id, revenue.Id }, entry.Lines.Select(line => line.AccountId));
        Assert.Equal(new[] { EntrySide.Debit, EntrySide.Credit, EntrySide.Credit }, entry.Lines.Select(line => line.Side));
        Assert.Equal(Amount(22500), entry.Lines[1].Amount);

        var list = Assert.IsAssignableFrom<IList<JournalLine>>(entry.Lines);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Clear());
    }

    [Fact]
    public void Escrow_release_moves_funds_from_escrow_to_payable()
    {
        var entry = JournalEntry.Create(EntryId(), "Escrow release", [Debit(Escrow(), 5000), Credit(Payable(), 5000)]);

        Assert.Equal(new BigInteger(5000), entry.TotalMinorUnits);
    }

    [Fact]
    public void Totals_beyond_a_single_money_value_stay_exact()
    {
        var clearing = Clearing();
        var payable = Payable();

        var entry = JournalEntry.Create(EntryId(), "Large batch",
        [
            Debit(clearing, Money.MaxMinorUnits),
            Debit(clearing, Money.MaxMinorUnits),
            Credit(payable, Money.MaxMinorUnits),
            Credit(payable, Money.MaxMinorUnits),
        ]);

        Assert.Equal(new BigInteger(Money.MaxMinorUnits) * 2, entry.TotalMinorUnits);
    }

    [Theory]
    [InlineData(25000, 24999)]
    [InlineData(25000, 25001)]
    [InlineData(1, 2)]
    public void Rejects_unbalanced_entries(long debit, long credit)
    {
        var exception = Assert.Throws<JournalValidationException>(
            () => JournalEntry.Create(EntryId(), "Unbalanced", [Debit(Clearing(), debit), Credit(Payable(), credit)]));

        Assert.Equal(JournalError.Unbalanced, exception.Error);
        Assert.Null(exception.LineNumber);
    }

    [Fact]
    public void Rejects_lines_that_are_all_on_one_side()
    {
        var exception = Assert.Throws<JournalValidationException>(
            () => JournalEntry.Create(EntryId(), "One-sided", [Debit(Clearing(), 100), Debit(Clearing(), 100)]));

        Assert.Equal(JournalError.Unbalanced, exception.Error);
    }

    [Fact]
    public void Rejects_fewer_than_two_lines()
    {
        Assert.Equal(JournalError.TooFewLines, Assert.Throws<JournalValidationException>(
            () => JournalEntry.Create(EntryId(), "Empty", [])).Error);
        Assert.Equal(JournalError.TooFewLines, Assert.Throws<JournalValidationException>(
            () => JournalEntry.Create(EntryId(), "Single", [Debit(Clearing(), 100)])).Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void Rejects_submitted_zero_or_negative_lines(long minorUnits)
    {
        var exception = Assert.Throws<JournalValidationException>(() => JournalEntry.Create(EntryId(), "Bad line",
            [Debit(Clearing(), 100), Credit(Payable(), 100), Credit(Revenue(), minorUnits)]));

        Assert.Equal(JournalError.NonPositiveAmount, exception.Error);
        Assert.Equal(3, exception.LineNumber);
    }

    [Fact]
    public void Rejects_mixed_currencies()
    {
        var exception = Assert.Throws<JournalValidationException>(() => JournalEntry.Create(EntryId(), "Mixed",
            [Debit(Clearing(), 1000), Credit(Payable(currency: Jpy), 1000)]));

        Assert.Equal(JournalError.MixedCurrencies, exception.Error);
        Assert.Equal(2, exception.LineNumber);
    }

    [Fact]
    public void Rejects_an_amount_in_a_different_currency_from_its_account()
    {
        var exception = Assert.Throws<JournalValidationException>(() => JournalEntry.Create(EntryId(), "Wrong account",
            [Debit(Clearing(), 1000), Credit(Payable(currency: Jpy), 1000, currency: Usd)]));

        Assert.Equal(JournalError.AccountCurrencyMismatch, exception.Error);
        Assert.Equal(2, exception.LineNumber);
    }

    [Fact]
    public void Rejects_inactive_accounts()
    {
        var closed = Payable();
        closed.Close();

        var exception = Assert.Throws<JournalValidationException>(() => JournalEntry.Create(EntryId(), "Closed account",
            [Debit(Clearing(), 1000), Credit(closed, 1000)]));

        Assert.Equal(JournalError.InactiveAccount, exception.Error);
        Assert.Equal(2, exception.LineNumber);
        Assert.Contains("line 2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_components_omit_zero_values_before_validation()
    {
        var clearing = Clearing();
        var payable = Payable();

        var entry = JournalEntry.FromGeneratedComponents(EntryId(), "Waterfall",
        [
            Debit(clearing, 25000),
            Credit(Revenue(), 0),
            Credit(payable, 25000),
            Credit(Escrow(), 0),
        ]);

        Assert.Equal(new[] { clearing.Id, payable.Id }, entry.Lines.Select(line => line.AccountId));
        Assert.Equal(new[] { 1, 2 }, entry.Lines.Select(line => line.LineNumber));
    }

    [Fact]
    public void Generated_components_still_reject_negative_values_and_too_few_lines()
    {
        Assert.Equal(JournalError.NonPositiveAmount, Assert.Throws<JournalValidationException>(
            () => JournalEntry.FromGeneratedComponents(EntryId(), "Negative",
                [Debit(Clearing(), 100), Credit(Payable(), 200), Credit(Revenue(), -100)])).Error);

        Assert.Equal(JournalError.TooFewLines, Assert.Throws<JournalValidationException>(
            () => JournalEntry.FromGeneratedComponents(EntryId(), "All zero but one",
                [Debit(Clearing(), 100), Credit(Payable(), 0)])).Error);
    }

    [Fact]
    public void Rejects_invalid_arguments()
    {
        var lines = new[] { Debit(Clearing(), 100), Credit(Payable(), 100) };

        Assert.Throws<ArgumentException>(() => JournalEntry.Create(default, "Entry", lines));
        Assert.Throws<ArgumentNullException>(() => JournalEntry.Create(EntryId(), null!, lines));
        Assert.Throws<ArgumentException>(() => JournalEntry.Create(EntryId(), " ", lines));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => JournalEntry.Create(EntryId(), new string('d', JournalEntry.MaxDescriptionLength + 1), lines));
        Assert.Equal("lines", Assert.Throws<ArgumentNullException>(
            () => JournalEntry.Create(EntryId(), "Entry", null!)).ParamName);
        Assert.Equal("lines", Assert.Throws<ArgumentException>(
            () => JournalEntry.Create(EntryId(), "Entry", [lines[0], null!])).ParamName);
        Assert.Equal("components", Assert.Throws<ArgumentNullException>(
            () => JournalEntry.FromGeneratedComponents(EntryId(), "Entry", null!)).ParamName);
    }

    [Fact]
    public void Draft_rejects_missing_values_and_undefined_sides()
    {
        Assert.Throws<ArgumentNullException>(() => new JournalLineDraft(null!, EntrySide.Debit, Amount(1)));
        Assert.Throws<ArgumentNullException>(() => new JournalLineDraft(Clearing(), EntrySide.Debit, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JournalLineDraft(Clearing(), (EntrySide)0, Amount(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JournalLineDraft(Clearing(), (EntrySide)3, Amount(1)));
    }

    [Theory]
    [InlineData(JournalError.TooFewLines, null, "A journal entry needs at least two lines with a positive amount.")]
    [InlineData(JournalError.NonPositiveAmount, 4, "Journal line 4 must have a positive amount.")]
    [InlineData(JournalError.MixedCurrencies, 2, "Journal line 2 is in a different currency from the entry.")]
    [InlineData(JournalError.AccountCurrencyMismatch, 3, "Journal line 3 is in a different currency from its account.")]
    [InlineData(JournalError.InactiveAccount, 1, "Journal line 1 targets an account that is not active.")]
    [InlineData(JournalError.Unbalanced, null, "Total debits must exactly equal total credits.")]
    [InlineData((JournalError)0, null, "The journal entry is invalid.")]
    public void Validation_errors_have_clear_messages(JournalError error, int? lineNumber, string message)
    {
        Assert.Equal(message, new JournalValidationException(error, lineNumber).Message);
    }
}
