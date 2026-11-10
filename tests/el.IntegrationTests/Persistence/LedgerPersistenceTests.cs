using System.Numerics;
using el.Domain.Common;
using el.Domain.Ledger;
using el.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace el.IntegrationTests.Persistence;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", PostgreSqlCollection.Category)]
public sealed class LedgerPersistenceTests(PostgreSqlFixture database)
{
    private static readonly CurrencyCode Usd = CurrencyReferenceData.Usd;

    [Fact]
    public async Task Seeds_the_reference_currency_definitions()
    {
        var connectionString = await LedgerSchema.CreateAsync(database);
        await using var context = LedgerSchema.NewContext(connectionString);

        var definitions = await context.Set<CurrencyDefinition>().AsNoTracking().ToListAsync();

        Assert.Equal(
            CurrencyReferenceData.Catalog.Definitions,
            definitions.OrderBy(definition => definition.Code.Value, StringComparer.Ordinal).ThenBy(definition => definition.Version));
        Assert.Equal(3L, await Sql.ScalarAsync<long>(connectionString, "SELECT count(*) FROM ledger.currencies"));
    }

    [Fact]
    public async Task Seeds_only_synthetic_parties_and_the_baseline_account_families()
    {
        var connectionString = await LedgerSchema.CreateAsync(database);
        await using var context = LedgerSchema.NewContext(connectionString);

        var parties = await context.Set<Party>().AsNoTracking().ToListAsync();
        var accounts = await context.Set<Account>().AsNoTracking().ToListAsync();

        Assert.Equal(SyntheticSeed.Parties.Count, parties.Count);
        Assert.All(parties, party => Assert.StartsWith("Synthetic ", party.Name, StringComparison.Ordinal));
        Assert.All(parties, party => Assert.Equal(PartyStatus.Active, party.Status));

        foreach (var currency in new[] { "USD", "JPY", "KWD" })
        {
            var codes = accounts.Where(account => account.Currency.Value == currency).Select(account => account.Code.Value).Order(StringComparer.Ordinal);

            Assert.Equal(
                new[] { "CLEARING.BANK", "ESCROW.AFFILIATE-1", "ESCROW.SELLER-1", "ESCROW.SELLER-2", "PAYABLE.AFFILIATE-1", "PAYABLE.PROCESSOR", "PAYABLE.SELLER-1", "PAYABLE.SELLER-2", "PAYABLE.TAX", "REVENUE.COMMISSION" }
                    .Select(family => family + "." + currency),
                codes);
        }

        var taxPayable = accounts.Single(account => account.Code.Value == "PAYABLE.TAX.USD");
        Assert.Equal(AccountType.Payable, taxPayable.Type);
        Assert.Equal(SyntheticSeed.TaxAuthority.Id, taxPayable.PartyId);
        Assert.Null(accounts.Single(account => account.Code.Value == "CLEARING.BANK.USD").PartyId);
    }

    [Fact]
    public async Task Round_trips_parties_and_accounts_with_their_status()
    {
        var connectionString = await LedgerSchema.CreateAsync(database);
        var party = new Party(new PartyId(Guid.NewGuid()), "Synthetic Test Seller");
        var account = new Account(new AccountId(Guid.NewGuid()), AccountCode.Parse("PAYABLE.TEST.USD"), AccountType.Payable, Usd, party.Id);
        party.Suspend();
        account.Close();

        await using (var context = LedgerSchema.NewContext(connectionString))
        {
            context.AddRange(party, account);
            await context.SaveChangesAsync();
        }

        await using (var context = LedgerSchema.NewContext(connectionString))
        {
            var storedParty = await context.Set<Party>().SingleAsync(item => item.Id == party.Id);
            var storedAccount = await context.Set<Account>().SingleAsync(item => item.Id == account.Id);

            Assert.Equal("Synthetic Test Seller", storedParty.Name);
            Assert.Equal(PartyStatus.Suspended, storedParty.Status);
            Assert.Equal(AccountCode.Parse("PAYABLE.TEST.USD"), storedAccount.Code);
            Assert.Equal(AccountType.Payable, storedAccount.Type);
            Assert.Equal(Usd, storedAccount.Currency);
            Assert.Equal(party.Id, storedAccount.PartyId);
            Assert.Equal(AccountStatus.Closed, storedAccount.Status);
        }
    }

    [Fact]
    public async Task Account_codes_are_unique()
    {
        var connectionString = await LedgerSchema.CreateAsync(database);
        var duplicate = new Account(new AccountId(Guid.NewGuid()), AccountCode.Parse("CLEARING.BANK.USD"), AccountType.Clearing, Usd, null);

        await AssertRejectedAsync(connectionString, duplicate, PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task A_party_has_one_payable_account_per_currency()
    {
        var connectionString = await LedgerSchema.CreateAsync(database);
        var secondPayable = new Account(
            new AccountId(Guid.NewGuid()), AccountCode.Parse("PAYABLE.SELLER-1.USD.SECOND"), AccountType.Payable, Usd, SyntheticSeed.SellerOne.Id);

        await AssertRejectedAsync(connectionString, secondPayable, PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task Accounts_must_reference_an_existing_party()
    {
        var connectionString = await LedgerSchema.CreateAsync(database);
        var orphan = new Account(
            new AccountId(Guid.NewGuid()), AccountCode.Parse("PAYABLE.UNKNOWN.USD"), AccountType.Payable, Usd, new PartyId(Guid.NewGuid()));

        await AssertRejectedAsync(connectionString, orphan, PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task Accounts_reject_unknown_currencies()
    {
        var connectionString = await LedgerSchema.CreateAsync(database);
        var euro = new Account(
            new AccountId(Guid.NewGuid()), AccountCode.Parse("CLEARING.BANK.EUR"), AccountType.Clearing, CurrencyCode.Parse("EUR"), null);

        await AssertRejectedAsync(connectionString, euro, PostgresErrorCodes.ForeignKeyViolation);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => Sql.ExecuteAsync(
            connectionString, "INSERT INTO ledger.currency_definitions (code, version, exponent) VALUES ('EUR', 1, 2)"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    [Fact]
    public async Task Parties_with_accounts_cannot_be_deleted()
    {
        var connectionString = await LedgerSchema.CreateAsync(database);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => Sql.ExecuteAsync(
            connectionString, $"DELETE FROM ledger.parties WHERE id = '{SyntheticSeed.SellerOne.Id}'"));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    [Theory]
    [InlineData("INSERT INTO ledger.currencies (code) VALUES ('usd')")]
    [InlineData("INSERT INTO ledger.currency_definitions (code, version, exponent) VALUES ('USD', 2, 5)")]
    [InlineData("INSERT INTO ledger.currency_definitions (code, version, exponent) VALUES ('USD', 2, -1)")]
    [InlineData("INSERT INTO ledger.currency_definitions (code, version, exponent) VALUES ('USD', 0, 2)")]
    [InlineData("INSERT INTO ledger.parties (id, name, status) VALUES (gen_random_uuid(), '   ', 1)")]
    [InlineData("INSERT INTO ledger.parties (id, name, status) VALUES (gen_random_uuid(), 'Synthetic X', 9)")]
    [InlineData("INSERT INTO ledger.accounts (id, code, type, currency, party_id, status) VALUES (gen_random_uuid(), 'clearing.bank.x', 1, 'USD', NULL, 1)")]
    [InlineData("INSERT INTO ledger.accounts (id, code, type, currency, party_id, status) VALUES (gen_random_uuid(), 'CLEARING.X', 9, 'USD', NULL, 1)")]
    [InlineData("INSERT INTO ledger.accounts (id, code, type, currency, party_id, status) VALUES (gen_random_uuid(), 'CLEARING.X', 1, 'USD', NULL, 3)")]
    [InlineData("INSERT INTO ledger.accounts (id, code, type, currency, party_id, status) VALUES (gen_random_uuid(), 'PAYABLE.X', 2, 'USD', NULL, 1)")]
    [InlineData("INSERT INTO ledger.accounts (id, code, type, currency, party_id, status) SELECT gen_random_uuid(), 'CLEARING.X', 1, 'USD', id, 1 FROM ledger.parties LIMIT 1")]
    public async Task Database_constraints_repeat_the_domain_rules(string sql)
    {
        var connectionString = await LedgerSchema.CreateAsync(database);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => Sql.ExecuteAsync(connectionString, sql));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [Fact]
    public async Task Exact_aggregates_return_totals_beyond_64_bits()
    {
        var connectionString = await LedgerSchema.CreateAsync(database);
        await Sql.ExecuteAsync(connectionString, "CREATE TABLE scratch_amounts (amount_minor bigint NOT NULL, label text NOT NULL)");
        await Sql.ExecuteAsync(connectionString,
            "INSERT INTO scratch_amounts VALUES (9223372036854775807, 'a'), (9223372036854775807, 'a'), (9223372036854775807, 'a'), (-5, 'b')");
        await using var context = LedgerSchema.NewContext(connectionString);
        var label = "a";

        var total = await context.Database.QueryExactIntegerAsync($"SELECT sum(amount_minor) FROM scratch_amounts WHERE label = {label}");
        var none = await context.Database.QueryExactIntegerAsync($"SELECT sum(amount_minor) FROM scratch_amounts WHERE label = 'none'");
        var negative = await context.Database.QueryExactIntegerAsync($"SELECT sum(amount_minor) FROM scratch_amounts WHERE label = 'b'");

        Assert.Equal(new BigInteger(long.MaxValue) * 3, total);
        Assert.Equal(BigInteger.Zero, none);
        Assert.Equal(new BigInteger(-5), negative);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.Database.QueryExactIntegerAsync($"SELECT 1.5"));
    }

    private static async Task AssertRejectedAsync(string connectionString, Account account, string sqlState)
    {
        await using var context = LedgerSchema.NewContext(connectionString);
        context.Add(account);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        Assert.Equal(sqlState, Assert.IsType<PostgresException>(exception.InnerException).SqlState);
    }
}
