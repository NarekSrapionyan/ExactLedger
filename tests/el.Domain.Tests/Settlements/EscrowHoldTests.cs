using el.Domain.Common;
using el.Domain.Ledger;
using el.Domain.Settlements;

namespace el.Domain.Tests.Settlements;

public sealed class EscrowHoldTests
{
    private static readonly DateTimeOffset Due = new(2026, 6, 8, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid HoldId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly PartyId Seller = new(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"));
    private static readonly Account Escrow = new(new AccountId(Guid.Parse("cccccccc-0000-0000-0000-000000000001")),
        AccountCode.Parse("ESCROW.S.USD"), AccountType.Escrow, CurrencyReferenceData.Usd, Seller);
    private static readonly Account Payable = new(new AccountId(Guid.Parse("cccccccc-0000-0000-0000-000000000002")),
        AccountCode.Parse("PAYABLE.S.USD"), AccountType.Payable, CurrencyReferenceData.Usd, Seller);

    private static EscrowHold Hold(EscrowHoldState state = EscrowHoldState.Held, long minor = 2050) =>
        new(HoldId, Guid.Parse("dddddddd-0000-0000-0000-000000000001"), Seller.Value, Escrow.Id, Payable.Id,
            Money.FromMinorUnits(minor, CurrencyReferenceData.Usd), Due, state);

    [Fact]
    public void Release_entry_moves_the_full_held_amount_from_escrow_to_payable()
    {
        var entry = Hold().CreateReleaseEntry(new JournalEntryId(Guid.NewGuid()), Escrow, Payable, Due);

        Assert.Equal("ESCROW-RELEASE:AAAAAAAA000000000000000000000001", entry.Reference.Value);
        Assert.Collection(entry.Lines,
            line => { Assert.Equal(Escrow.Id, line.AccountId); Assert.Equal(EntrySide.Debit, line.Side); Assert.Equal(2050, line.Amount.MinorUnits); },
            line => { Assert.Equal(Payable.Id, line.AccountId); Assert.Equal(EntrySide.Credit, line.Side); Assert.Equal(2050, line.Amount.MinorUnits); });
        Assert.Null(entry.ReversalOf);
    }

    [Fact]
    public void A_hold_is_due_from_its_due_time_and_not_a_tick_earlier()
    {
        Assert.True(Hold().IsDue(Due));
        Assert.True(Hold().IsDue(Due.AddDays(30)));
        Assert.False(Hold().IsDue(Due.AddTicks(-1)));

        var early = Assert.Throws<EscrowHoldException>(() => Hold().CreateReleaseEntry(new JournalEntryId(Guid.NewGuid()), Escrow, Payable, Due.AddTicks(-1)));
        Assert.Equal(EscrowHoldException.NotDue, early.Code);
    }

    [Theory]
    [InlineData(EscrowHoldState.Released)]
    [InlineData(EscrowHoldState.Refunded)]
    public void Released_or_refunded_holds_cannot_be_released_again(EscrowHoldState state)
    {
        var exception = Assert.Throws<EscrowHoldException>(() => Hold(state).EnsureReleasable(Due.AddDays(1)));

        Assert.Equal(EscrowHoldException.NotReleasable, exception.Code);
        Assert.Contains(state.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_requires_this_holds_own_escrow_and_payable_accounts()
    {
        var other = new Account(new AccountId(Guid.NewGuid()), AccountCode.Parse("PAYABLE.X.USD"), AccountType.Payable, CurrencyReferenceData.Usd, Seller);

        Assert.Equal("escrowAccount", Assert.Throws<ArgumentException>(() => Hold().CreateReleaseEntry(new JournalEntryId(Guid.NewGuid()), Payable, Payable, Due)).ParamName);
        Assert.Equal("payableAccount", Assert.Throws<ArgumentException>(() => Hold().CreateReleaseEntry(new JournalEntryId(Guid.NewGuid()), Escrow, other, Due)).ParamName);
        Assert.Equal("payableAccount", Assert.Throws<ArgumentException>(() => Hold().CreateReleaseEntry(new JournalEntryId(Guid.NewGuid()), Escrow, Escrow, Due)).ParamName);
        Assert.Throws<ArgumentNullException>(() => Hold().CreateReleaseEntry(new JournalEntryId(Guid.NewGuid()), null!, Payable, Due));
        Assert.Throws<ArgumentNullException>(() => Hold().CreateReleaseEntry(new JournalEntryId(Guid.NewGuid()), Escrow, null!, Due));
    }

    [Fact]
    public void Exposes_its_values()
    {
        var hold = Hold();

        Assert.Equal(HoldId, hold.Id);
        Assert.Equal(Guid.Parse("dddddddd-0000-0000-0000-000000000001"), hold.SettlementId);
        Assert.Equal(Seller.Value, hold.PartyId);
        Assert.Equal(Escrow.Id, hold.EscrowAccountId);
        Assert.Equal(Payable.Id, hold.PayableAccountId);
        Assert.Equal(Due, hold.DueAtUtc);
        Assert.Equal(EscrowHoldState.Held, hold.State);
    }

    [Fact]
    public void Rejects_invalid_hold_values()
    {
        var settlement = Guid.NewGuid();
        var usd = Money.FromMinorUnits(1, CurrencyReferenceData.Usd);

        Assert.Throws<ArgumentException>(() => new EscrowHold(Guid.Empty, settlement, Seller.Value, Escrow.Id, Payable.Id, usd, Due, EscrowHoldState.Held));
        Assert.Throws<ArgumentException>(() => new EscrowHold(HoldId, Guid.Empty, Seller.Value, Escrow.Id, Payable.Id, usd, Due, EscrowHoldState.Held));
        Assert.Throws<ArgumentException>(() => new EscrowHold(HoldId, settlement, Guid.Empty, Escrow.Id, Payable.Id, usd, Due, EscrowHoldState.Held));
        Assert.Throws<ArgumentException>(() => new EscrowHold(HoldId, settlement, Seller.Value, default, Payable.Id, usd, Due, EscrowHoldState.Held));
        Assert.Throws<ArgumentException>(() => new EscrowHold(HoldId, settlement, Seller.Value, Escrow.Id, default, usd, Due, EscrowHoldState.Held));
        Assert.Throws<ArgumentNullException>(() => new EscrowHold(HoldId, settlement, Seller.Value, Escrow.Id, Payable.Id, null!, Due, EscrowHoldState.Held));
        Assert.Throws<ArgumentOutOfRangeException>(() => Hold(minor: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Hold(minor: -1));
        Assert.Throws<ArgumentException>(() => new EscrowHold(HoldId, settlement, Seller.Value, Escrow.Id, Payable.Id, usd, new DateTimeOffset(2026, 6, 8, 0, 0, 0, TimeSpan.FromHours(4)), EscrowHoldState.Held));
        Assert.Throws<ArgumentOutOfRangeException>(() => Hold((EscrowHoldState)0));
    }
}
