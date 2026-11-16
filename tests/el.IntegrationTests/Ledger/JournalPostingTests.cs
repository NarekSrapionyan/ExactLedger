
using el.Application.Ledger;
using el.Domain.Ledger;
using el.Infrastructure.Persistence;
using el.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace el.IntegrationTests.Ledger;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", PostgreSqlCollection.Category)]
public sealed class JournalPostingTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);
    private readonly PostgreSqlFixture fixture;

    public JournalPostingTests(PostgreSqlFixture fixture) => this.fixture = fixture;

    private sealed class InjectedFailureException : Exception;

    private async Task<LedgerTestHost> CreateHostAsync() =>
        await LedgerTestHost.CreateAsync(await fixture.CreateDatabaseAsync());

    /// <summary>
    /// Standalone posting: PostAsync owns the unit of work.
    /// Each call gets an independent scope and DbContext.
    /// </summary>
    private static async Task PostAsync(
        LedgerTestHost host,
        JournalEntry entry,
        CancellationToken cancellationToken = default)
    {
        await using var scope = host.CreateScope();

        var posting = scope.ServiceProvider
            .GetRequiredService<JournalPostingService>();

        await posting.PostAsync(entry, cancellationToken);
    }

    /// <summary>
    /// Checks a PostgreSQL error through the exception chain.
    /// </summary>
    private static bool HasPostgresError(
        Exception exception,
        params string[] sqlStates)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres &&
                sqlStates.Contains(postgres.SqlState))
            {
                return true;
            }
        }

        return false;
    }

    private static Task<long> EntryCountAsync(
        LedgerTestHost host,
        string referencePrefix) =>
        host.ScalarAsync<long>(
            "SELECT COUNT(*) FROM ledger.journal_entries WHERE reference LIKE @prefix",
            new NpgsqlParameter("prefix", referencePrefix + "%"));

    [Fact]
    public async Task A_posted_entry_is_persisted_finalized_with_all_its_lines()
    {
        await using var host = await CreateHostAsync();

        var data = new LedgerTestData(host);

        var entry = data.BuildEntry(
            "BE008.POST.OK",
            await data.LoadAccountAsync(LedgerTestData.ClearingUsd),
            await data.LoadAccountAsync(LedgerTestData.RevenueUsd),
            25000);

        // PostAsync owns its own transaction.
        await PostAsync(host, entry);

        Assert.Equal(
            1,
            await host.CountAsync(
                "journal_entries",
                "reference = 'BE008.POST.OK' AND finalized"));

        Assert.Equal(
            2,
            await host.CountAsync(
                "journal_lines",
                "entry_id IN (SELECT id FROM ledger.journal_entries WHERE reference = 'BE008.POST.OK')"));
    }

    [Fact]
    public async Task PostAsync_rejects_an_existing_unit_of_work()
    {
        await using var host = await CreateHostAsync();

        var data = new LedgerTestData(host);

        var entry = data.BuildEntry(
            "BE008.NESTED",
            await data.LoadAccountAsync(LedgerTestData.ClearingUsd),
            await data.LoadAccountAsync(LedgerTestData.RevenueUsd),
            100);

        // This deliberately verifies that PostAsync cannot nest another unit of work.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.RunInUnitOfWorkAsync(
                (sp, ct) =>
                    sp.GetRequiredService<JournalPostingService>()
                        .PostAsync(entry, ct)));

        Assert.Contains("do not nest", exception.Message);

        Assert.Equal(0, await EntryCountAsync(host, "BE008.NESTED"));
    }

    [Fact]
    public async Task A_failure_after_the_entry_was_written_but_before_commit_leaves_nothing()
    {
        await using var host = await CreateHostAsync();

        var data = new LedgerTestData(host);

        var entry = data.BuildEntry(
            "BE008.FAIL.BEFORE",
            await data.LoadAccountAsync(LedgerTestData.ClearingUsd),
            await data.LoadAccountAsync(LedgerTestData.RevenueUsd),
            25000);

        await Assert.ThrowsAsync<InjectedFailureException>(
            () => host.RunInUnitOfWorkAsync(async (sp, ct) =>
            {
                // The caller already owns the transaction.
                await sp.GetRequiredService<JournalPostingService>()
                    .PostWithinTransactionAsync(entry, ct);

                // Simulate a failure after posting but before transaction commit.
                throw new InjectedFailureException();
            }));

        Assert.Equal(0, await EntryCountAsync(host, "BE008.FAIL.BEFORE"));
        Assert.Equal(0, await host.CountAsync("journal_lines"));
    }

    [Fact]
    public async Task A_failure_at_commit_rolls_back_the_valid_entry_posted_in_the_same_transaction()
    {
        await using var host = await CreateHostAsync();

        var data = new LedgerTestData(host);

        var entry = data.BuildEntry(
            "BE008.COMMIT.VALID",
            await data.LoadAccountAsync(LedgerTestData.ClearingUsd),
            await data.LoadAccountAsync(LedgerTestData.RevenueUsd),
            25000);

        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => host.RunInUnitOfWorkAsync(async (sp, ct) =>
            {
                await sp.GetRequiredService<JournalPostingService>()
                    .PostWithinTransactionAsync(entry, ct);

                // Insert an invalid unfinalized entry.
                // The deferred database validation must reject the transaction at COMMIT.
                await sp.GetRequiredService<ExactLedgerDbContext>()
                    .Database.ExecuteSqlAsync(
                        $"INSERT INTO ledger.journal_entries (id, reference, description, currency, finalized) VALUES ({Guid.NewGuid()}, {"BE008.COMMIT.BAD"}, {"never finalized"}, {"USD"}, false)",
                        ct);
            }));

        Assert.True(
            HasPostgresError(exception, "EL102", "EL103"),
            exception.ToString());

        // Both the valid entry and the invalid entry must be absent.
        Assert.Equal(0, await EntryCountAsync(host, "BE008.COMMIT."));
    }

    [Fact]
    public async Task A_canceled_post_leaves_nothing()
    {
        await using var host = await CreateHostAsync();

        var data = new LedgerTestData(host);

        var entry = data.BuildEntry(
            "BE008.CANCEL",
            await data.LoadAccountAsync(LedgerTestData.ClearingUsd),
            await data.LoadAccountAsync(LedgerTestData.RevenueUsd),
            25000);

        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => host.RunInUnitOfWorkAsync(async (sp, ct) =>
            {
                await sp.GetRequiredService<JournalPostingService>()
                    .PostWithinTransactionAsync(entry, ct);

                await cancellation.CancelAsync();

                ct.ThrowIfCancellationRequested();
            }, cancellation.Token));

        Assert.Equal(0, await EntryCountAsync(host, "BE008.CANCEL"));
    }

    [Fact]
    public async Task A_duplicate_reference_is_rejected_and_only_one_entry_exists()
    {
        await using var host = await CreateHostAsync();

        var data = new LedgerTestData(host);

        var clearing = await data.LoadAccountAsync(LedgerTestData.ClearingUsd);
        var revenue = await data.LoadAccountAsync(LedgerTestData.RevenueUsd);

        await PostAsync(
            host,
            data.BuildEntry("BE008.DUP", clearing, revenue, 100));

        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => PostAsync(
                host,
                data.BuildEntry("BE008.DUP", clearing, revenue, 100)));

        Assert.True(
            HasPostgresError(exception, PostgresErrorCodes.UniqueViolation),
            exception.ToString());

        Assert.Equal(1, await EntryCountAsync(host, "BE008.DUP"));
    }

    [Fact]
    public async Task An_account_closed_after_the_entry_was_built_is_rejected_under_the_lock()
    {
        await using var host = await CreateHostAsync();

        var data = new LedgerTestData(host);

        var clearing = await data.LoadAccountAsync(LedgerTestData.ClearingUsd);
        var revenue = await data.LoadAccountAsync(LedgerTestData.RevenueUsd);

        // The domain entry was built while both accounts were active.
        var entry = data.BuildEntry(
            "BE008.CLOSED",
            clearing,
            revenue,
            100);

        await data.CloseAccountAsync(LedgerTestData.RevenueUsd);

        // Posting must re-check the persisted account status.
        var exception = await Assert.ThrowsAsync<AccountNotPostableException>(
            () => PostAsync(host, entry));

        Assert.Equal(AccountPostingProblem.NotActive, exception.Problem);

        Assert.Equal(0, await EntryCountAsync(host, "BE008.CLOSED"));
    }

    [Fact]
    public async Task An_unknown_account_is_rejected()
    {
        await using var host = await CreateHostAsync();

        var unknown = new AccountId(Guid.NewGuid());

        var exception = await Assert.ThrowsAsync<AccountNotPostableException>(
            () => host.RunInUnitOfWorkAsync(
                (sp, ct) =>
                    sp.GetRequiredService<IJournalAccountVerifier>()
                        .EnsurePostableAsync([unknown], ct)));

        Assert.Equal(AccountPostingProblem.NotFound, exception.Problem);
        Assert.Equal(unknown, exception.AccountId);
    }

    [Fact]
    public async Task Closing_an_account_waits_for_an_in_flight_post_to_finish()
    {
        await using var host = await CreateHostAsync();

        var data = new LedgerTestData(host);

        var entry = data.BuildEntry(
            "BE008.CLOSE.WAIT",
            await data.LoadAccountAsync(LedgerTestData.ClearingUsd),
            await data.LoadAccountAsync(LedgerTestData.RevenueUsd),
            100);

        var posted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // This transaction acquires the monetary lock and account row locks.
        var poster = Task.Run(() =>
            host.RunInUnitOfWorkAsync(async (sp, ct) =>
            {
                await sp.GetRequiredService<JournalPostingService>()
                    .PostWithinTransactionAsync(entry, ct);

                posted.SetResult();

                // Keep the transaction open while the other session attempts closure.
                await release.Task;
            }));

        try
        {
            // Either posting reaches the held state or its task fails.
            await (await Task.WhenAny(posted.Task, poster).WaitAsync(Limit));

            // A concurrent account closure must wait for the FOR SHARE row lock.
            await using var connection =
                new NpgsqlConnection(host.ConnectionString);

            await connection.OpenAsync();

            await using var transaction =
                await connection.BeginTransactionAsync();

            await using var command = new NpgsqlCommand(
                """
                SET LOCAL lock_timeout = '300ms';
                UPDATE ledger.accounts
                SET status = @closed
                WHERE code = @code
                """,
                connection,
                transaction);

            command.Parameters.AddWithValue(
                "closed",
                (int)AccountStatus.Closed);

            command.Parameters.AddWithValue(
                "code",
                LedgerTestData.RevenueUsd);

            var exception = await Assert.ThrowsAsync<PostgresException>(
                () => command.ExecuteNonQueryAsync());

            Assert.Equal(
                PostgresErrorCodes.LockNotAvailable,
                exception.SqlState);
        }
        finally
        {
            // Never leave the posting transaction waiting if an assertion fails.
            release.TrySetResult();

            await poster.WaitAsync(Limit);
        }

        // Once posting commits, closure can proceed.
        await data.CloseAccountAsync(LedgerTestData.RevenueUsd);

        Assert.Equal(1, await EntryCountAsync(host, "BE008.CLOSE.WAIT"));
    }

    [Fact]
    public async Task Parallel_posts_to_the_same_accounts_lose_no_updates_and_conserve_debits_and_credits()
    {
        await using var host = await CreateHostAsync();

        var data = new LedgerTestData(host);

        var clearing = await data.LoadAccountAsync(LedgerTestData.ClearingUsd);
        var revenue = await data.LoadAccountAsync(LedgerTestData.RevenueUsd);

        const int Count = 16;

        // Build entries before starting parallel operations.
        // Each PostAsync invocation creates its own scope and DbContext.
        var entries = Enumerable.Range(1, Count)
            .Select(i =>
                data.BuildEntry(
                    FormattableString.Invariant($"BE008.PAR.{i:D3}"),
                    clearing,
                    revenue,
                    i * 100L))
            .ToArray();

        await Task.WhenAll(
            entries.Select(entry => PostAsync(host, entry)))
            .WaitAsync(Limit);

        var expected = 100L * Count * (Count + 1) / 2; // 13,600

        await using var scope = host.CreateScope();

        var queries = scope.ServiceProvider
            .GetRequiredService<ILedgerBalanceQueries>();

        var clearingBalance = await queries.GetAccountBalanceAsync(
            clearing.Id,
            CancellationToken.None);

        var revenueBalance = await queries.GetAccountBalanceAsync(
            revenue.Id,
            CancellationToken.None);

        var trial = Assert.Single(
            await queries.GetTrialBalanceAsync(
                null,
                CancellationToken.None));

        Assert.Equal(Count, await EntryCountAsync(host, "BE008.PAR."));

        Assert.Equal(expected, (long)clearingBalance!.DebitTotal);
        Assert.Equal(expected, (long)revenueBalance!.CreditTotal);

        Assert.True(trial.IsBalanced);
        Assert.Equal(expected, (long)trial.DebitTotal);
    }
}
