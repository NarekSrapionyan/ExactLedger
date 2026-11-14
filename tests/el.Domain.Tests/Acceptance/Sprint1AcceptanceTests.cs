using System.Numerics;
using el.Domain.Common;
using el.Domain.Ledger;

namespace el.Domain.Tests.Acceptance;

/// <summary>
/// In-memory composition of BE-002 through BE-006. These scenarios do not exercise
/// persistence, transactional posting, production settlement APIs or real regional rules.
/// </summary>
public sealed class Sprint1AcceptanceTests
{
    [Theory]
    [InlineData(false, "204.95", "20.50", "184.45", 6)]
    [InlineData(true, "199.95", "20.00", "179.95", 7)]
    public void Worked_usd_examples_produce_exact_balanced_domain_journals(
        bool withSyntheticRegionalDeduction, string expectedShare, string expectedEscrow,
        string expectedPayable, int expectedLines)
    {
        var usd = Definition("USD");
        var gross = MoneyText.Parse("250.00", usd);
        var seller = new Party(new PartyId(Id(1)), "Synthetic seller");
        var processorParty = new Party(new PartyId(Id(2)), "Synthetic processor");
        var affiliateParty = new Party(new PartyId(Id(3)), "Synthetic affiliate");
        var regionalParty = new Party(new PartyId(Id(4)), "Synthetic regional beneficiary");
        var clearing = Account(1, "CLEARING.USD", AccountType.Clearing, usd.Code);
        var commissionAccount = Account(2, "COMMISSION.USD", AccountType.Revenue, usd.Code);
        var processorAccount = Account(3, "PROCESSOR.USD", AccountType.Payable, usd.Code, processorParty.Id);
        var affiliateAccount = Account(4, "AFFILIATE.USD", AccountType.Payable, usd.Code, affiliateParty.Id);
        var regionalAccount = Account(5, "REGIONAL.USD", AccountType.Payable, usd.Code, regionalParty.Id);
        var payableAccount = Account(6, "SELLER.PAYABLE.USD", AccountType.Payable, usd.Code, seller.Id);
        var escrowAccount = Account(7, "SELLER.ESCROW.USD", AccountType.Escrow, usd.Code, seller.Id);

        var regional = withSyntheticRegionalDeduction ? Percentage(gross, "2") : Money.Zero(usd.Code);
        var processor = Percentage(gross, "2.900000") + MoneyText.Parse("0.30", usd);
        var commission = Percentage(gross, "12");
        var affiliate = Percentage(gross, "3");
        var sellerShare = gross - regional - processor - commission - affiliate;
        var escrow = Percentage(sellerShare, "10");
        var payable = sellerShare - escrow;

        var entry = JournalEntry.FromGeneratedComponents(new JournalEntryId(Id(100)), "Sprint 1 synthetic example",
        [
            new(clearing, EntrySide.Debit, gross),
            new(regionalAccount, EntrySide.Credit, regional),
            new(processorAccount, EntrySide.Credit, processor),
            new(commissionAccount, EntrySide.Credit, commission),
            new(affiliateAccount, EntrySide.Credit, affiliate),
            new(escrowAccount, EntrySide.Credit, escrow),
            new(payableAccount, EntrySide.Credit, payable),
        ]);

        Assert.Equal(expectedShare, MoneyText.Format(sellerShare, usd));
        Assert.Equal(expectedLines, entry.Lines.Count);
        AssertCredit(entry, processorAccount, "7.55", usd);
        AssertCredit(entry, commissionAccount, "30.00", usd);
        AssertCredit(entry, affiliateAccount, "7.50", usd);
        AssertCredit(entry, escrowAccount, expectedEscrow, usd);
        AssertCredit(entry, payableAccount, expectedPayable, usd);

        if (withSyntheticRegionalDeduction)
        {
            AssertCredit(entry, regionalAccount, "5.00", usd);
        }
        else
        {
            Assert.DoesNotContain(entry.Lines, line => line.AccountId == regionalAccount.Id);
        }

        AssertBalanced(entry, gross);
    }

    [Theory]
    [InlineData("USD", "0.05", "0.03", "0.01", "0.01")]
    [InlineData("JPY", "5", "3", "1", "1")]
    [InlineData("KWD", "0.005", "0.003", "0.001", "0.001")]
    public void Exact_weighted_allocations_become_balanced_journals_in_each_currency(
        string code, string totalText, string expectedA, string expectedB, string expectedC)
    {
        var currency = Definition(code);
        var amount = MoneyText.Parse(totalText, currency);
        var clearing = Account(1, "CLEARING", AccountType.Clearing, currency.Code);
        var accounts = new Dictionary<string, Account>(StringComparer.Ordinal)
        {
            ["A"] = Account(2, "PAYABLE.A", AccountType.Payable, currency.Code, new PartyId(Id(1))),
            ["B"] = Account(3, "PAYABLE.B", AccountType.Payable, currency.Code, new PartyId(Id(2))),
            ["C"] = Account(4, "PAYABLE.C", AccountType.Payable, currency.Code, new PartyId(Id(3))),
        };
        var shares = WeightedAllocation.Allocate(amount.MinorUnits, [new("C", 20), new("A", 50), new("B", 30)]);
        var lines = shares.Select(share => new JournalLineDraft(accounts[share.AllocationLineId], EntrySide.Credit,
            Money.FromMinorUnits(share.MinorUnits, currency.Code)));
        var entry = JournalEntry.Create(new JournalEntryId(Id(100)), "Sprint 1 weighted example",
            lines.Prepend(new JournalLineDraft(clearing, EntrySide.Debit, amount)));

        Assert.Equal(5, amount.MinorUnits);
        AssertCredit(entry, accounts["A"], expectedA, currency);
        AssertCredit(entry, accounts["B"], expectedB, currency);
        AssertCredit(entry, accounts["C"], expectedC, currency);
        AssertBalanced(entry, amount);
    }

    [Fact]
    public void Zero_allocations_are_omitted_from_generated_journals_but_submitted_zeros_are_rejected()
    {
        var currency = Definition("USD");
        var amount = MoneyText.Parse("0.01", currency);
        var clearing = Account(1, "CLEARING", AccountType.Clearing, currency.Code);
        var payableA = Account(2, "PAYABLE.A", AccountType.Payable, currency.Code, new PartyId(Id(1)));
        var payableB = Account(3, "PAYABLE.B", AccountType.Payable, currency.Code, new PartyId(Id(2)));
        var shares = WeightedAllocation.Allocate(amount.MinorUnits, [new("A", 1), new("B", 0)]);
        var components = new JournalLineDraft[]
        {
            new(clearing, EntrySide.Debit, amount),
            new(payableA, EntrySide.Credit, Money.FromMinorUnits(shares[0].MinorUnits, currency.Code)),
            new(payableB, EntrySide.Credit, Money.FromMinorUnits(shares[1].MinorUnits, currency.Code)),
        };

        var generated = JournalEntry.FromGeneratedComponents(new JournalEntryId(Id(100)), "Generated allocation", components);

        Assert.Equal(2, generated.Lines.Count);
        Assert.DoesNotContain(generated.Lines, line => line.AccountId == payableB.Id);
        AssertBalanced(generated, amount);
        Assert.Equal(JournalError.NonPositiveAmount, Assert.Throws<JournalValidationException>(
            () => JournalEntry.Create(new JournalEntryId(Id(101)), "Submitted allocation", components)).Error);
    }

    private static CurrencyDefinition Definition(string code) =>
        CurrencyReferenceData.Catalog.Resolve(CurrencyCode.Parse(code), CurrencyReferenceData.InitialVersion);

    private static Money Percentage(Money amount, string text)
    {
        Assert.True(PercentageText.TryParse(text, out var rate, out _));
        return Rounding.Round(rate.Multiply(amount.MinorUnits), RoundingMode.HalfEven).ToMoney(amount.Currency);
    }

    private static Account Account(int id, string code, AccountType type, CurrencyCode currency, PartyId? party = null) =>
        new(new AccountId(Id(id)), AccountCode.Parse(code), type, currency, party);

    private static Guid Id(int value) => new(value, 0, 0, new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 });

    private static void AssertCredit(JournalEntry entry, Account account, string expected, CurrencyDefinition currency)
    {
        var line = Assert.Single(entry.Lines, line => line.AccountId == account.Id);
        Assert.Equal(EntrySide.Credit, line.Side);
        Assert.Equal(expected, MoneyText.Format(line.Amount, currency));
    }

    private static void AssertBalanced(JournalEntry entry, Money expected)
    {
        var debit = entry.Lines.Where(line => line.Side == EntrySide.Debit)
            .Aggregate(BigInteger.Zero, (sum, line) => sum + line.Amount.MinorUnits);
        var credit = entry.Lines.Where(line => line.Side == EntrySide.Credit)
            .Aggregate(BigInteger.Zero, (sum, line) => sum + line.Amount.MinorUnits);

        Assert.Equal(new BigInteger(expected.MinorUnits), debit);
        Assert.Equal(debit, credit);
        Assert.Equal(debit, entry.TotalMinorUnits);
        Assert.Equal(expected.Currency, entry.Currency);
        Assert.All(entry.Lines, line =>
        {
            Assert.True(line.Amount.IsPositive);
            Assert.Equal(expected.Currency, line.Amount.Currency);
        });
    }
}
