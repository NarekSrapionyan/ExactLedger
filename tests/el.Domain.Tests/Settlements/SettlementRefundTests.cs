using el.Domain.Common;
using el.Domain.Ledger;
using el.Domain.Settlements;

namespace el.Domain.Tests.Settlements;

public sealed class SettlementRefundTests
{
    private static readonly Guid SettlementId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    private static readonly JournalEntryId Original = new(Guid.Parse("eeeeeeee-0000-0000-0000-000000000001"));
    private static readonly PartyId Seller = new(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"));
    private static readonly Account Clearing = Account("CLEARING.BANK.USD", AccountType.Clearing, null);
    private static readonly Account Revenue = Account("REVENUE.COMMISSION.USD", AccountType.Revenue, null);
    private static readonly Account Payable = Account("PAYABLE.S.USD", AccountType.Payable, Seller);
    private static readonly Account Escrow = Account("ESCROW.S.USD", AccountType.Escrow, Seller);

    private static Account Account(string code, AccountType type, PartyId? party) =>
        new(new AccountId(Guid.NewGuid()), AccountCode.Parse(code), type, CurrencyReferenceData.Usd, party);

    private static Money Usd(long minor) => Money.FromMinorUnits(minor, CurrencyReferenceData.Usd);

    private static EscrowHold Hold(EscrowHoldState state) => new(Guid.NewGuid(), SettlementId, Seller.Value, Escrow.Id, Payable.Id,
        Usd(2050), new DateTimeOffset(2026, 6, 8, 0, 0, 0, TimeSpan.Zero), state);

    private static readonly JournalLineDraft[] OriginalLines =
    [
        new(Clearing, EntrySide.Debit, Usd(25000)),
        new(Revenue, EntrySide.Credit, Usd(3000)),
        new(Payable, EntrySide.Credit, Usd(19950)),
        new(Escrow, EntrySide.Credit, Usd(2050)),
    ];

    [Fact]
    public void Reversal_mirrors_the_original_lines_and_links_the_original_entry()
    {
        var reversal = SettlementRefund.CreateReversal(new JournalEntryId(Guid.NewGuid()), SettlementId, Original, OriginalLines);

        Assert.Equal(Original, reversal.ReversalOf);
        Assert.Equal("REFUND:DDDDDDDD000000000000000000000001", reversal.Reference.Value);
        Assert.Equal(
            OriginalLines.Select(line => (line.Account.Id, line.Side == EntrySide.Debit ? EntrySide.Credit : EntrySide.Debit, line.Amount)),
            reversal.Lines.Select(line => (line.AccountId, line.Side, line.Amount)));
        Assert.Equal(25000, (long)reversal.TotalMinorUnits);
    }

    [Fact]
    public void Original_plus_reversal_nets_every_account_to_exactly_zero()
    {
        var original = JournalEntry.Create(Original, JournalReference.Parse("SETTLEMENT:1"), "Capture", OriginalLines);
        var reversal = SettlementRefund.CreateReversal(new JournalEntryId(Guid.NewGuid()), SettlementId, Original, OriginalLines);

        var net = original.Lines.Concat(reversal.Lines)
            .GroupBy(line => line.AccountId)
            .Select(group => group.Sum(line => line.Side == EntrySide.Debit ? line.Amount.MinorUnits : -line.Amount.MinorUnits));

        Assert.All(net, total => Assert.Equal(0, total));
    }

    [Fact]
    public void A_full_refund_of_a_settlement_with_held_or_no_holds_is_eligible()
    {
        SettlementRefund.EnsureEligible(Usd(25000), Usd(25000), [Hold(EscrowHoldState.Held)]);
        SettlementRefund.EnsureEligible(Usd(25000), Usd(25000), []);
    }

    [Theory]
    [InlineData(24999)]
    [InlineData(25001)]
    public void Only_the_exact_gross_amount_can_be_refunded(long requested)
    {
        var exception = Assert.Throws<RefundException>(() => SettlementRefund.EnsureEligible(Usd(requested), Usd(25000), []));

        Assert.Equal(RefundException.AmountMismatch, exception.Code);
    }

    [Fact]
    public void A_refund_in_another_currency_is_not_the_gross_amount()
    {
        var exception = Assert.Throws<RefundException>(() => SettlementRefund.EnsureEligible(
            Money.FromMinorUnits(25000, CurrencyReferenceData.Jpy), Usd(25000), []));

        Assert.Equal(RefundException.AmountMismatch, exception.Code);
    }

    [Theory]
    [InlineData(EscrowHoldState.Released, RefundException.HoldReleased)]
    [InlineData(EscrowHoldState.Refunded, RefundException.AlreadyRefunded)]
    public void A_released_or_refunded_hold_blocks_the_refund(EscrowHoldState state, string code)
    {
        var exception = Assert.Throws<RefundException>(() => SettlementRefund.EnsureEligible(Usd(25000), Usd(25000),
            [Hold(EscrowHoldState.Held), Hold(state)]));

        Assert.Equal(code, exception.Code);
    }

    [Fact]
    public void Rejects_invalid_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => SettlementRefund.EnsureEligible(null!, Usd(1), []));
        Assert.Throws<ArgumentNullException>(() => SettlementRefund.EnsureEligible(Usd(1), null!, []));
        Assert.Throws<ArgumentNullException>(() => SettlementRefund.EnsureEligible(Usd(1), Usd(1), null!));
        Assert.Throws<ArgumentNullException>(() => SettlementRefund.CreateReversal(new JournalEntryId(Guid.NewGuid()), SettlementId, Original, null!));
        Assert.Throws<ArgumentNullException>(() => SettlementRefund.CreateReversal(new JournalEntryId(Guid.NewGuid()), SettlementId, Original, [null!]));
        Assert.Throws<ArgumentException>(() => SettlementRefund.CreateReversal(new JournalEntryId(Guid.NewGuid()), Guid.Empty, Original, OriginalLines));
    }
}
