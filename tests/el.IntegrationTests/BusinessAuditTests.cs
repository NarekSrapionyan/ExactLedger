using el.Application.Audit;
using el.Application.Abstractions;
using el.Application.Ledger;
using el.Application.Security;
using el.Domain.Common;
using el.Domain.Ledger;
using el.Infrastructure;
using el.Infrastructure.Persistence;
using el.IntegrationTests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Text.RegularExpressions;

namespace el.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", PostgreSqlCollection.Category)]
public sealed class BusinessAuditTests(PostgreSqlFixture database)
{
    private sealed class InjectedFailure : Exception;

    [Fact]
    public async Task Migrated_identity_role_constraint_matches_the_application_roles_exactly()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var definition = await host.ScalarAsync<string>("""
            SELECT pg_get_constraintdef(oid) FROM pg_constraint
            WHERE conrelid = 'ledger.identities'::regclass AND conname = 'ck_identities_roles'
            """);
        var storedRoles = Regex.Matches(definition, "'([^']+)'")
            .Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(IdentityRoles.All.Order(StringComparer.Ordinal).ToArray(), storedRoles);
    }

    [Fact]
    public async Task Posting_and_actor_audit_commit_or_rollback_together_as_the_runtime_role()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        await using var services = new ServiceCollection().AddInfrastructure(host.RuntimeConnection).BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var actor = scope.ServiceProvider.GetRequiredService<AuditContext>();
        actor.ActorId = "synthetic-actor"; actor.CorrelationId = "correlation-009";
        var posting = scope.ServiceProvider.GetRequiredService<JournalPostingService>();
        var committed = Entry("AUDIT:COMMIT");
        await posting.PostAsync(committed, CancellationToken.None);
        var recorded = Assert.Single(await scope.ServiceProvider.GetRequiredService<IAuditQueries>()
            .ReadAsync(0, 100, CancellationToken.None), row => row.Operation == "journal.posted");
        Assert.Equal("synthetic-actor", recorded.ActorId);
        Assert.Equal("correlation-009", recorded.CorrelationId);
        Assert.Equal(committed.Id.ToString(), recorded.TargetId);
        Assert.Equal(TimeSpan.Zero, recorded.OccurredAtUtc.Offset);

        var rolledBack = Entry("AUDIT:ROLLBACK");
        await Assert.ThrowsAsync<InjectedFailure>(() => scope.ServiceProvider.GetRequiredService<IUnitOfWork>()
            .ExecuteAsync<bool>(async ct =>
            {
                await posting.PostWithinTransactionAsync(rolledBack, ct);
                throw new InjectedFailure();
            }, CancellationToken.None));
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.journal_entries"));
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.audit_events WHERE operation = 'journal.posted'"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IAuditTrail>()
            .RecordAsync("outside-transaction", "target", CancellationToken.None));
    }

    [Fact]
    public async Task Seed_is_idempotent_preserves_disabled_accounts_and_does_not_reaudit_unchanged_users()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        await host.ExecuteAsync("UPDATE ledger.identities SET enabled = false WHERE username = 'finance'");
        await using var services = new ServiceCollection().AddInfrastructure(host.AdminConnection).BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IIdentitySeeder>().SeedAsync(IdentityApiHost.Password, CancellationToken.None);
        Assert.Equal(9L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.identities"));
        Assert.Equal(9L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.audit_events"));
        Assert.False(await host.ScalarAsync<bool>("SELECT enabled FROM ledger.identities WHERE username = 'finance'"));
    }

    [Fact]
    public async Task Runtime_cannot_assign_roles_or_rewrite_audit_and_owner_cannot_rewrite_audit_either()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        foreach (var sql in new[] { "UPDATE ledger.identities SET roles = ARRAY['FinanceOperator']", "DELETE FROM ledger.audit_events" })
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(host.RuntimeConnection, sql));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
        }
        foreach (var sql in new[] { "UPDATE ledger.audit_events SET actor_id = 'changed'", "DELETE FROM ledger.audit_events", "TRUNCATE ledger.audit_events" })
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() => host.ExecuteAsync(sql));
            Assert.Equal(LedgerDatabase.HistoryImmutable, exception.SqlState);
        }
    }

    private static JournalEntry Entry(string reference)
    {
        var debit = SyntheticSeed.Accounts.Single(account => account.Code.Value == "CLEARING.BANK.USD");
        var credit = SyntheticSeed.Accounts.Single(account => account.Code.Value == "REVENUE.COMMISSION.USD");
        var amount = Money.FromMinorUnits(100, CurrencyReferenceData.Usd);
        return JournalEntry.Create(new JournalEntryId(Guid.NewGuid()), JournalReference.Parse(reference),
            "Synthetic audited posting", [new(debit, EntrySide.Debit, amount), new(credit, EntrySide.Credit, amount)]);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
