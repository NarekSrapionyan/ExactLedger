using el.Domain.Common;
using el.Domain.Ledger;
using static el.Domain.Tests.Ledger.LedgerFixtures;

namespace el.Domain.Tests.Ledger;

public sealed class IdentityTests
{
    private static readonly Guid SomeGuid = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

    [Fact]
    public void Ids_reject_an_empty_guid()
    {
        Assert.Throws<ArgumentException>(() => new PartyId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new AccountId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new JournalEntryId(Guid.Empty));
    }

    [Fact]
    public void Ids_are_equal_by_value_and_format_invariantly()
    {
        Assert.Equal(new PartyId(SomeGuid), new PartyId(SomeGuid));
        Assert.Equal(new AccountId(SomeGuid), new AccountId(SomeGuid));
        Assert.Equal(new JournalEntryId(SomeGuid), new JournalEntryId(SomeGuid));
        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", new PartyId(SomeGuid).ToString());
        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", new AccountId(SomeGuid).ToString());
        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", new JournalEntryId(SomeGuid).ToString());
        Assert.Equal(SomeGuid, new PartyId(SomeGuid).Value);
    }

    [Fact]
    public void Entities_reject_a_default_id()
    {
        Assert.Throws<ArgumentException>(() => new Party(default, "Seller"));
        Assert.Throws<ArgumentException>(
            () => new Account(default, AccountCode.Parse("CLEARING.BANK"), AccountType.Clearing, Usd, null));
    }

    [Fact]
    public void Entities_with_the_same_id_are_the_same_entity_whatever_their_state()
    {
        var first = new Party(SellerId, "Seller");
        var renamed = new Party(SellerId, "Seller (renamed)");
        renamed.Suspend();

        var sameByOperator = first == renamed;
        var differentByOperator = first != renamed;

        Assert.Equal(first, renamed);
        Assert.True(sameByOperator);
        Assert.False(differentByOperator);
        Assert.Equal(first.GetHashCode(), renamed.GetHashCode());
    }

    [Fact]
    public void Entities_with_equal_state_but_different_ids_are_different_entities()
    {
        var seller = new Party(SellerId, "Same name");
        var affiliate = new Party(AffiliateId, "Same name");

        var differentByOperator = seller != affiliate;

        Assert.NotEqual(seller, affiliate);
        Assert.True(differentByOperator);
    }

    [Fact]
    public void Entities_of_different_types_are_never_equal()
    {
        var guid = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var party = new Party(new PartyId(guid), "Seller");
        var account = new Account(new AccountId(guid), AccountCode.Parse("CLEARING.BANK"), AccountType.Clearing, Usd, null);

        Assert.False(party.Equals((object)account));
        Assert.False(party.Equals((object?)null));
        Assert.False(party.Equals(null));
    }

    [Fact]
    public void Null_entities_compare_by_reference()
    {
        Party? missing = null;
        var party = new Party(SellerId, "Seller");

        var bothNull = missing == null;
        var leftNull = missing == party;
        var rightNull = party == missing;

        Assert.True(bothNull);
        Assert.False(leftNull);
        Assert.False(rightNull);
    }

    [Fact]
    public void Value_objects_are_equal_by_value_unlike_entities()
    {
        // Two separately created Money values with the same amount and currency are the same value.
        Assert.Equal(Money.FromMinorUnits(25000, Usd), Money.FromMinorUnits(25000, Usd));

        // Two accounts with identical data are still different entities when their IDs differ.
        Assert.NotEqual(Clearing(), Clearing());
    }
}
