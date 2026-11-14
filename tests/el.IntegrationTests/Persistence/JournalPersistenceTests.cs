using System.Numerics;
using el.Application.Abstractions;
using el.Domain.Common;
using el.Domain.Ledger;
using el.Infrastructure;
using el.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace el.IntegrationTests.Persistence;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", PostgreSqlCollection.Category)]
public sealed class JournalPersistenceTests(PostgreSqlFixture database)
{
    private static readonly Account ClearingUsd = SeedAccount("CLEARING.BANK.USD");
    private static readonly Account SellerPayableUsd = SeedAccount("PAYABLE.SELLER-1.USD");
    private static readonly Account SellerPayableJpy = SeedAccount("PAYABLE.SELLER-1.JPY");

    [Fact]
    public async Task Migrations_build_the_full_schema_from_an_empty_database()
    {
        var admin = await LedgerSchema.CreateAsync(database);
        await using var context = LedgerSchema.NewContext(admin);

        Assert.Equal(
            new[] { "20261110203211_InitialLedgerSchema", "20261110203212_LedgerSafetyGuards" },
            await context.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.True(await Sql.ScalarAsync<bool>(
            admin, $"SELECT EXISTS (SELECT FROM pg_roles WHERE rolname = '{LedgerDatabase.RuntimeRole}')"));
        Assert.Equal(30L, await Sql.ScalarAsync<long>(admin, "SELECT count(*) FROM ledger.accounts"));
    }

    [Fact]
    public async Task Runtime_role_posts_a_balanced_entry_through_the_store()
    {
        var admin = await LedgerSchema.CreateAsync(database);
        var runtime = await LedgerSchema.CreateRuntimeLoginAsync(admin);
        var entry = Entry("TEST:POST-1", (ClearingUsd, EntrySide.Debit, 25000), (SellerPayableUsd, EntrySide.Credit, 25000));

        await using (var services = new ServiceCollection().AddInfrastructure(runtime).BuildServiceProvider())
        await using (var scope = services.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var store = scope.ServiceProvider.GetRequiredService<IJournalEntryStore>();

            await unitOfWork.ExecuteAsync(async cancellationToken =>
            {
                await store.AddAsync(entry, cancellationToken);
                return true;
            }, CancellationToken.None);
        }

        await using var context = LedgerSchema.NewContext(admin);
        Assert.True(await Sql.ScalarAsync<bool>(admin, $"SELECT finalized FROM ledger.journal_entries WHERE id = '{entry.Id}'"));
        Assert.Equal(2L, await Sql.ScalarAsync<long>(admin, $"SELECT count(*) FROM ledger.journal_lines WHERE entry_id = '{entry.Id}'"));
        Assert.Equal(new BigInteger(25000), await context.Database.QueryExactIntegerAsync(
            $"SELECT sum(amount_minor) FROM ledger.journal_lines WHERE account_id = {ClearingUsd.Id.Value} AND side = 1"));
    }

    [Fact]
    public async Task Store_posts_only_inside_a_unit_of_work()
    {
        var admin = await LedgerSchema.CreateAsync(database);
        await using var services = new ServiceCollection().AddInfrastructure(admin).BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IJournalEntryStore>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AddAsync(
            Entry("TEST:NO-UOW", (ClearingUsd, EntrySide.Debit, 1), (SellerPayableUsd, EntrySide.Credit, 1)), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.AddAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task Database_rejects_unbalanced_entries_at_commit_independently_of_the_domain()
    {
        var admin = await LedgerSchema.CreateAsync(database);
        var runtime = await LedgerSchema.CreateRuntimeLoginAsync(admin);
        var id = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => Sql.ExecuteAsync(runtime,
            InsertEntry(id, "TEST:UNBALANCED") + InsertLine(id, 1, ClearingUsd, 1, 100) + InsertLine(id, 2, SellerPayableUsd, 2, 99) + Finalize(id)));

        Assert.Equal(LedgerDatabase.UnbalancedEntry, exception.SqlState);
        Assert.Equal(0L, await Sql.ScalarAsync<long>(admin, "SELECT count(*) FROM ledger.journal_entries"));
    }

    [Fact]
    public async Task Database_rejects_completed_entries_with_fewer_than_two_lines()
    {
        var admin = await LedgerSchema.CreateAsync(database);
        var runtime = await LedgerSchema.CreateRuntimeLoginAsync(admin);
        var id = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => Sql.ExecuteAsync(runtime,
            InsertEntry(id, "TEST:ONE-LINE") + InsertLine(id, 1, ClearingUsd, 1, 100) + Finalize(id)));

        Assert.Equal(LedgerDatabase.UnbalancedEntry, exception.SqlState);
    }

    [Fact]
    public async Task Database_rejects_entries_that_are_not_finalized_by_commit()
    {
        var admin = await LedgerSchema.CreateAsync(database);
        var runtime = await LedgerSchema.CreateRuntimeLoginAsync(admin);
        var id = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => Sql.ExecuteAsync(runtime,
            InsertEntry(id, "TEST:OPEN") + InsertLine(id, 1, ClearingUsd, 1, 100) + InsertLine(id, 2, SellerPayableUsd, 2, 100)));

        Assert.Equal(LedgerDatabase.EntryNotFinalized, exception.SqlState);
    }

    [Fact]
    public async Task Finalization_happens_once_and_then_no_lines_can_be_added()
    {
        var admin = await LedgerSchema.CreateAsync(database);
        var runtime = await LedgerSchema.CreateRuntimeLoginAsync(admin);
        var id = Guid.NewGuid();
        await PostBalancedAsync(runtime, id, "TEST:FINAL");

        var append = await Assert.ThrowsAsync<PostgresException>(() => Sql.ExecuteAsync(runtime,
            InsertLine(id, 3, ClearingUsd, 1, 1) + InsertLine(id, 4, SellerPayableUsd, 2, 1)));
        var refinalize = await Assert.ThrowsAsync<PostgresException>(() => Sql.ExecuteAsync(runtime, Finalize(id)));

        Assert.Equal(LedgerDatabase.LineAddedAfterFinalization, append.SqlState);
        Assert.Equal(LedgerDatabase.InvalidFinalization, refinalize.SqlState);
        Assert.Equal(2L, await Sql.ScalarAsync<long>(admin, $"SELECT count(*) FROM ledger.journal_lines WHERE entry_id = '{id}'"));
    }

    [Theory]
    [InlineData("UPDATE ledger.journal_entries SET description = 'Changed'")]
    [InlineData("UPDATE ledger.journal_entries SET finalized = false")]
    [InlineData("DELETE FROM ledger.journal_entries")]
    [InlineData("UPDATE ledger.journal_lines SET amount_minor = 1")]
    [InlineData("DELETE FROM ledger.journal_lines")]
    [InlineData("TRUNCATE ledger.journal_lines")]
    [InlineData("UPDATE ledger.accounts SET code = 'CHANGED.CODE'")]
    [InlineData("DELETE FROM ledger.accounts")]
    [InlineData("DELETE FROM ledger.parties")]
    [InlineData("INSERT INTO ledger.currencies (code) VALUES ('EUR')")]
    [InlineData("UPDATE ledger.currency_definitions SET exponent = 3")]
    [InlineData("CREATE TABLE ledger.shadow_ledger (id integer)")]
    public async Task Runtime_role_cannot_change_posted_history_reference_data_or_schema(string sql)
    {
        var admin = await LedgerSchema.CreateAsync(database);
        var runtime = await LedgerSchema.CreateRuntimeLoginAsync(admin);
        await PostBalancedAsync(runtime, Guid.NewGuid(), "TEST:HISTORY");

        var exception = await Assert.ThrowsAsync<PostgresException>(() => Sql.ExecuteAsync(runtime, sql));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    [Theory]
    [InlineData("UPDATE ledger.journal_entries SET description = 'Changed'")]
    [InlineData("UPDATE ledger.journal_entries SET finalized = false")]
    [InlineData("DELETE FROM ledger.journal_entries")]
    [InlineData("TRUNCATE ledger.journal_entries CASCADE")]
    [InlineData("UPDATE ledger.journal_lines SET amount_minor = 1")]
    [InlineData("DELETE FROM ledger.journal_lines")]
    [InlineData("TRUNCATE ledger.journal_lines")]
    public async Task Posted_history_is_immutable_even_for_the_schema_owner(string sql)
    {
        var admin = await LedgerSchema.CreateAsync(database);
        await PostBalancedAsync(admin, Guid.NewGuid(), "TEST:OWNER");

        var exception = await Assert.ThrowsAsync<PostgresException>(() => Sql.ExecuteAsync(admin, sql));

        Assert.Equal(LedgerDatabase.HistoryImmutable, exception.SqlState);
    }

    [Fact]
    public async Task Runtime_role_can_change_only_party_and_account_status()
    {
        var admin = await LedgerSchema.CreateAsync(database);
        var runtime = await LedgerSchema.CreateRuntimeLoginAsync(admin);

        await using (var context = LedgerSchema.NewContext(runtime))
        {
            var account = await context.Set<Account>().SingleAsync(item => item.Id == SellerPayableJpy.Id);
            account.Close();
            await context.SaveChangesAsync();
        }

        Assert.Equal(2, await Sql.ScalarAsync<int>(admin, $"SELECT status FROM ledger.accounts WHERE id = '{SellerPayableJpy.Id}'"));
    }

    [Fact]
    public async Task Accounts_with_postings_cannot_be_deleted()
    {
        var admin = await LedgerSchema.CreateAsync(database);
        await PostBalancedAsync(admin, Guid.NewGuid(), "TEST:POSTINGS");

        var exception = await Assert.ThrowsAsync<PostgresException>(() => Sql.ExecuteAsync(
            admin, $"DELETE FROM ledger.accounts WHERE id = '{ClearingUsd.Id}'"));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    [Theory]
    [InlineData(0, 1, PostgresErrorCodes.CheckViolation)]
    [InlineData(-5, 1, PostgresErrorCodes.CheckViolation)]
    [InlineData(100_000_000_000, 1, PostgresErrorCodes.CheckViolation)]
    [InlineData(100, 3, PostgresErrorCodes.CheckViolation)]
    public async Task Line_amounts_are_positive_bigint_minor_units_within_the_money_range(long amount, int side, string sqlState)
    {
        var admin = await LedgerSchema.CreateAsync(database);
        var id = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => Sql.ExecuteAsync(admin,
            InsertEntry(id, "TEST:AMOUNT") + InsertLine(id, 1, ClearingUsd, side, amount)));

        Assert.Equal(sqlState, exception.SqlState);
    }

    [Fact]
    public async Task Line_currency_must_match_its_entry_and_account()
    {
        var admin = await LedgerSchema.CreateAsync(database);
        var id = Guid.NewGuid();

        // A USD line against a JPY account violates the (account_id, currency) key.
        var exception = await Assert.ThrowsAsync<PostgresException>(() => Sql.ExecuteAsync(admin,
            InsertEntry(id, "TEST:CURRENCY") + InsertLine(id, 1, SellerPayableJpy, 2, 100, currency: "USD")));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    [Fact]
    public async Task References_are_unique_and_an_entry_can_be_reversed_once_in_its_own_currency()
    {
        var admin = await LedgerSchema.CreateAsync(database);
        var runtime = await LedgerSchema.CreateRuntimeLoginAsync(admin);
        var original = Guid.NewGuid();
        await PostBalancedAsync(runtime, original, "TEST:ORIGINAL");

        var duplicateReference = await Assert.ThrowsAsync<PostgresException>(
            () => PostBalancedAsync(runtime, Guid.NewGuid(), "TEST:ORIGINAL"));
        await PostBalancedAsync(runtime, Guid.NewGuid(), "TEST:REVERSAL-1", reversalOf: original);
        var secondReversal = await Assert.ThrowsAsync<PostgresException>(
            () => PostBalancedAsync(runtime, Guid.NewGuid(), "TEST:REVERSAL-2", reversalOf: original));
        var selfReversalId = Guid.NewGuid();
        var selfReversal = await Assert.ThrowsAsync<PostgresException>(
            () => PostBalancedAsync(runtime, selfReversalId, "TEST:SELF", reversalOf: selfReversalId));
        var unreversed = Guid.NewGuid();
        await PostBalancedAsync(runtime, unreversed, "TEST:UNREVERSED");
        var crossCurrency = await Assert.ThrowsAsync<PostgresException>(() => Sql.ExecuteAsync(runtime,
            InsertEntry(Guid.NewGuid(), "TEST:JPY-REVERSAL", currency: "JPY", reversalOf: unreversed)));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicateReference.SqlState);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, secondReversal.SqlState);
        Assert.Equal(PostgresErrorCodes.CheckViolation, selfReversal.SqlState);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, crossCurrency.SqlState);
    }

    private static Account SeedAccount(string code) =>
        SyntheticSeed.Accounts.Single(account => account.Code.Value == code);

    private static JournalEntry Entry(string reference, params (Account Account, EntrySide Side, long Minor)[] lines) =>
        JournalEntry.Create(
            new JournalEntryId(Guid.NewGuid()),
            JournalReference.Parse(reference),
            "Integration test entry",
            lines.Select(line => new JournalLineDraft(line.Account, line.Side, Money.FromMinorUnits(line.Minor, line.Account.Currency))));

    private static Task PostBalancedAsync(string connectionString, Guid id, string reference, Guid? reversalOf = null) =>
        Sql.ExecuteAsync(connectionString,
            InsertEntry(id, reference, reversalOf: reversalOf)
            + InsertLine(id, 1, ClearingUsd, 1, 100)
            + InsertLine(id, 2, SellerPayableUsd, 2, 100)
            + Finalize(id));

    // Statements sent in one command run in one implicit transaction, so deferred checks run at its end.
    private static string InsertEntry(Guid id, string reference, string currency = "USD", Guid? reversalOf = null) =>
        $"INSERT INTO ledger.journal_entries (id, reference, description, currency, reversal_of, finalized) "
        + $"VALUES ('{id}', '{reference}', 'Test entry', '{currency}', {(reversalOf is null ? "NULL" : $"'{reversalOf}'")}, false);";

    private static string InsertLine(Guid entryId, int lineNumber, Account account, int side, long amount, string? currency = null) =>
        $"INSERT INTO ledger.journal_lines (entry_id, line_number, account_id, side, amount_minor, currency) "
        + $"VALUES ('{entryId}', {lineNumber}, '{account.Id}', {side}, {amount}, '{currency ?? account.Currency.Value}');";

    private static string Finalize(Guid id) => $"SELECT ledger.finalize_journal_entry('{id}');";
}
