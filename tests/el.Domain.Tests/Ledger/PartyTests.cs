using el.Domain.Ledger;
using static el.Domain.Tests.Ledger.LedgerFixtures;

namespace el.Domain.Tests.Ledger;

public sealed class PartyTests
{
    [Fact]
    public void New_party_is_active_and_keeps_its_name()
    {
        var party = new Party(SellerId, "Demo Seller");

        Assert.Equal(SellerId, party.Id);
        Assert.Equal("Demo Seller", party.Name);
        Assert.Equal(PartyStatus.Active, party.Status);
        Assert.True(party.IsActive);
    }

    [Fact]
    public void Rejects_a_missing_or_too_long_name()
    {
        Assert.Throws<ArgumentNullException>(() => new Party(SellerId, null!));
        Assert.Throws<ArgumentException>(() => new Party(SellerId, ""));
        Assert.Throws<ArgumentException>(() => new Party(SellerId, "   "));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Party(SellerId, new string('a', Party.MaxNameLength + 1)));
        Assert.Equal(Party.MaxNameLength, new Party(SellerId, new string('a', Party.MaxNameLength)).Name.Length);
    }

    [Fact]
    public void Can_be_suspended_reactivated_and_closed()
    {
        var party = new Party(SellerId, "Seller");

        party.Suspend();
        Assert.Equal(PartyStatus.Suspended, party.Status);
        Assert.False(party.IsActive);

        party.Reactivate();
        Assert.Equal(PartyStatus.Active, party.Status);

        party.Suspend();
        party.Close();
        Assert.Equal(PartyStatus.Closed, party.Status);
    }

    [Fact]
    public void Rejects_invalid_status_transitions()
    {
        var active = new Party(SellerId, "Seller");
        Assert.Throws<InvalidOperationException>(active.Reactivate);

        var suspended = new Party(SellerId, "Seller");
        suspended.Suspend();
        Assert.Throws<InvalidOperationException>(suspended.Suspend);

        var closed = new Party(SellerId, "Seller");
        closed.Close();
        Assert.Throws<InvalidOperationException>(closed.Suspend);
        Assert.Throws<InvalidOperationException>(closed.Reactivate);
        Assert.Throws<InvalidOperationException>(closed.Close);
        Assert.Equal(PartyStatus.Closed, closed.Status);
    }
}
