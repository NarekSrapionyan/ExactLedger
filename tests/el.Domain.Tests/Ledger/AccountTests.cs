using el.Domain.Ledger;
using static el.Domain.Tests.Ledger.LedgerFixtures;

namespace el.Domain.Tests.Ledger;

public sealed class AccountTests
{
    [Theory]
    [InlineData("PAYABLE.SELLER-001.USD")]
    [InlineData("A")]
    [InlineData("ESCROW_2")]
    public void AccountCode_accepts_uppercase_ascii_codes(string value)
    {
        var code = AccountCode.Parse(value);

        Assert.Equal(value, code.Value);
        Assert.Equal(value, code.ToString());
        Assert.Equal(code, AccountCode.Parse(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("payable.seller")]
    [InlineData("PAYABLE SELLER")]
    [InlineData(" PAYABLE")]
    [InlineData("PAYABLE/SELLER")]
    [InlineData("ÄSCROW")]
    public void AccountCode_rejects_other_values(string? value)
    {
        Assert.Throws<ArgumentException>(() => AccountCode.Parse(value!));
        Assert.False(AccountCode.TryParse(value, out var code));
        Assert.Null(code);
    }

    [Fact]
    public void AccountCode_limits_the_length()
    {
        Assert.True(AccountCode.TryParse(new string('A', AccountCode.MaxLength), out _));
        Assert.False(AccountCode.TryParse(new string('A', AccountCode.MaxLength + 1), out _));
    }

    [Fact]
    public void Party_accounts_keep_their_immutable_identity_and_start_active()
    {
        var id = NewAccountId();
        var account = new Account(id, AccountCode.Parse("PAYABLE.SELLER.USD"), AccountType.Payable, Usd, SellerId);

        Assert.Equal(id, account.Id);
        Assert.Equal("PAYABLE.SELLER.USD", account.Code.Value);
        Assert.Equal(AccountType.Payable, account.Type);
        Assert.Equal(Usd, account.Currency);
        Assert.Equal(SellerId, account.PartyId);
        Assert.Equal(AccountStatus.Active, account.Status);
        Assert.True(account.IsActive);
    }

    [Fact]
    public void Payable_and_escrow_are_separate_accounts_per_party_and_currency()
    {
        var usdPayable = Payable();
        var usdEscrow = Escrow();
        var jpyPayable = Payable(currency: Jpy);

        Assert.NotEqual(usdPayable, usdEscrow);
        Assert.NotEqual(usdPayable, jpyPayable);
        Assert.Equal(AccountType.Escrow, usdEscrow.Type);
        Assert.Equal(Jpy, jpyPayable.Currency);
        Assert.Equal(usdPayable.PartyId, usdEscrow.PartyId);
    }

    [Theory]
    [InlineData(AccountType.Payable)]
    [InlineData(AccountType.Escrow)]
    public void Party_owned_types_require_a_party(AccountType type)
    {
        Assert.True(AccountTypes.IsPartyOwned(type));
        Assert.Equal("partyId", Assert.Throws<ArgumentException>(
            () => new Account(NewAccountId(), AccountCode.Parse("X"), type, Usd, null)).ParamName);
        Assert.Equal("partyId", Assert.Throws<ArgumentException>(
            () => new Account(NewAccountId(), AccountCode.Parse("X"), type, Usd, default(PartyId))).ParamName);
    }

    [Theory]
    [InlineData(AccountType.Clearing)]
    [InlineData(AccountType.Revenue)]
    public void System_types_must_not_have_a_party(AccountType type)
    {
        Assert.False(AccountTypes.IsPartyOwned(type));
        Assert.Equal("partyId", Assert.Throws<ArgumentException>(
            () => new Account(NewAccountId(), AccountCode.Parse("X"), type, Usd, SellerId)).ParamName);
        Assert.Null(new Account(NewAccountId(), AccountCode.Parse("X"), type, Usd, null).PartyId);
    }

    [Theory]
    [InlineData(AccountType.Clearing, EntrySide.Debit)]
    [InlineData(AccountType.Payable, EntrySide.Credit)]
    [InlineData(AccountType.Escrow, EntrySide.Credit)]
    [InlineData(AccountType.Revenue, EntrySide.Credit)]
    public void Each_type_has_a_normal_side(AccountType type, EntrySide expected)
    {
        Assert.Equal(expected, AccountTypes.NormalSide(type));
    }

    [Fact]
    public void Account_exposes_the_normal_side_of_its_type()
    {
        Assert.Equal(EntrySide.Debit, Clearing().NormalSide);
        Assert.Equal(EntrySide.Credit, Payable().NormalSide);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    public void Undefined_account_types_are_rejected(int value)
    {
        var type = (AccountType)value;

        Assert.Throws<ArgumentOutOfRangeException>(() => AccountTypes.IsPartyOwned(type));
        Assert.Throws<ArgumentOutOfRangeException>(() => AccountTypes.NormalSide(type));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new Account(NewAccountId(), AccountCode.Parse("X"), type, Usd, null));
    }

    [Fact]
    public void Rejects_missing_code_or_currency()
    {
        Assert.Throws<ArgumentNullException>(
            () => new Account(NewAccountId(), null!, AccountType.Clearing, Usd, null));
        Assert.Throws<ArgumentNullException>(
            () => new Account(NewAccountId(), AccountCode.Parse("X"), AccountType.Clearing, null!, null));
    }

    [Fact]
    public void Close_is_final()
    {
        var account = Payable();

        account.Close();

        Assert.Equal(AccountStatus.Closed, account.Status);
        Assert.False(account.IsActive);
        Assert.Throws<InvalidOperationException>(account.Close);
    }
}
