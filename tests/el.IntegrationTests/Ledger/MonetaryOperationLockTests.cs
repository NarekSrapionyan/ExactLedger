using System.Data;
using el.Application.Abstractions;
using el.Infrastructure.Persistence;
using el.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace el.IntegrationTests.Ledger;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", PostgreSqlCollection.Category)]
public sealed class MonetaryOperationLockTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private readonly PostgreSqlFixture fixture;

    public MonetaryOperationLockTests(PostgreSqlFixture fixture)
    {
        this.fixture = fixture;
    }

    private async Task<LedgerTestHost> CreateHostAsync(
        MonetaryOperationLockOptions? options = null) =>
        await LedgerTestHost.CreateAsync(
            await fixture.CreateDatabaseAsync(),
            options);

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// One independent session:
    /// transaction -> acquire lock -> execute body -> commit or rollback.
    /// </summary>
    private static async Task HoldAsync(
        LedgerTestHost host,
        Func<Task> whileHeld,
        CancellationToken cancellationToken = default,
        bool commit = true)
    {
        await using var scope = host.CreateScope();

        var context = scope.ServiceProvider
            .GetRequiredService<ExactLedgerDbContext>();

        await using var transaction =
            await context.Database.BeginTransactionAsync(cancellationToken);

        var monetaryLock = scope.ServiceProvider
            .GetRequiredService<IMonetaryOperationLock>();

        await monetaryLock.AcquireAsync(cancellationToken);

        await whileHeld();

        if (commit)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        else
        {
            await transaction.RollbackAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Waits for a signal or surfaces the worker's exception if it fails first.
    /// </summary>
    private static async Task SignalOrFailAsync(
        Task signal,
        Task worker)
    {
        var completed = await Task.WhenAny(signal, worker).WaitAsync(Limit);

        await completed;
    }

    /// <summary>
    /// Starts a transaction that holds the monetary lock until Release is signaled.
    /// </summary>
    private static (Task Holder, TaskCompletionSource Release) StartHolder(
        LedgerTestHost host,
        out Task held)
    {
        var heldSignal = NewSignal();
        var release = NewSignal();

        var holder = Task.Run(
            () => HoldAsync(
                host,
                async () =>
                {
                    heldSignal.SetResult();
                    await release.Task;
                }));

        held = heldSignal.Task;

        return (holder, release);
    }

    [Fact]
    public async Task Acquire_outside_a_transaction_is_rejected()
    {
        await using var host = await CreateHostAsync();

        await using var scope = host.CreateScope();

        var monetaryLock = scope.ServiceProvider
            .GetRequiredService<IMonetaryOperationLock>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => monetaryLock.AcquireAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Acquire_in_a_repeatable_read_transaction_is_rejected()
    {
        await using var host = await CreateHostAsync();

        await using var scope = host.CreateScope();

        var context = scope.ServiceProvider
            .GetRequiredService<ExactLedgerDbContext>();

        await using var transaction =
            await context.Database.BeginTransactionAsync(
                IsolationLevel.RepeatableRead);

        var monetaryLock = scope.ServiceProvider
            .GetRequiredService<IMonetaryOperationLock>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => monetaryLock.AcquireAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_lock_can_be_taken_twice_in_one_transaction()
    {
        await using var host = await CreateHostAsync();

        await using var scope = host.CreateScope();

        var context = scope.ServiceProvider
            .GetRequiredService<ExactLedgerDbContext>();

        var monetaryLock = scope.ServiceProvider
            .GetRequiredService<IMonetaryOperationLock>();

        await using var transaction =
            await context.Database.BeginTransactionAsync();

        await monetaryLock.AcquireAsync(CancellationToken.None);

        // Transaction-scoped PostgreSQL advisory locks are re-entrant
        // within the same transaction.
        await monetaryLock.AcquireAsync(CancellationToken.None);

        await transaction.CommitAsync();
    }

    [Fact]
    public async Task A_second_session_waits_until_the_holder_commits()
    {
        await using var host = await CreateHostAsync();

        var (holder, release) = StartHolder(host, out var held);

        await SignalOrFailAsync(held, holder);

        var acquired = NewSignal();

        var waiter = Task.Run(
            () => HoldAsync(
                host,
                () =>
                {
                    acquired.SetResult();
                    return Task.CompletedTask;
                }));

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(750));

            Assert.False(acquired.Task.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        await Task.WhenAll(holder, waiter).WaitAsync(Limit);

        Assert.True(acquired.Task.IsCompleted);
    }

    [Fact]
    public async Task A_rollback_releases_the_lock()
    {
        await using var host = await CreateHostAsync();

        // The first transaction explicitly rolls back.
        await HoldAsync(
            host,
            () => Task.CompletedTask,
            commit: false).WaitAsync(Limit);

        // A second transaction must be able to acquire the same lock.
        await HoldAsync(
            host,
            () => Task.CompletedTask).WaitAsync(Limit);
    }

    [Fact]
    public async Task A_waiter_times_out_with_a_typed_exception_and_the_holder_is_unaffected()
    {
        var wait = TimeSpan.FromMilliseconds(300);

        await using var host = await CreateHostAsync(
            new MonetaryOperationLockOptions
            {
                WaitTimeout = wait
            });

        var (holder, release) = StartHolder(host, out var held);

        await SignalOrFailAsync(held, holder);

        try
        {
            var exception = await Assert.ThrowsAsync<LedgerLockTimeoutException>(
                () => HoldAsync(
                    host,
                    () => Task.CompletedTask));

            Assert.Equal(wait, exception.Waited);
        }
        finally
        {
            release.TrySetResult();
        }

        await holder.WaitAsync(Limit);

        // The failed waiter must not leave the lock in a broken state.
        await HoldAsync(
            host,
            () => Task.CompletedTask).WaitAsync(Limit);
    }

    [Fact]
    public async Task A_canceled_waiter_throws_and_does_not_leak_the_lock()
    {
        await using var host = await CreateHostAsync();

        var (holder, release) = StartHolder(host, out var held);

        await SignalOrFailAsync(held, holder);

        try
        {
            using var cancellation =
                new CancellationTokenSource(
                    TimeSpan.FromMilliseconds(300));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => HoldAsync(
                    host,
                    () => Task.CompletedTask,
                    cancellation.Token));
        }
        finally
        {
            release.TrySetResult();
        }

        await holder.WaitAsync(Limit);

        // Cancellation must not leave an advisory lock behind.
        await HoldAsync(
            host,
            () => Task.CompletedTask).WaitAsync(Limit);
    }

    [Fact]
    public async Task Sessions_never_overlap_under_parallel_load()
    {
        await using var host = await CreateHostAsync();

        var gate = new object();

        var inside = 0;
        var maxInside = 0;

        var workers = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(
                () => HoldAsync(
                    host,
                    async () =>
                    {
                        lock (gate)
                        {
                            inside++;
                            maxInside = Math.Max(maxInside, inside);
                        }

                        await Task.Delay(30);

                        lock (gate)
                        {
                            inside--;
                        }
                    })))
            .ToArray();

        await Task.WhenAll(workers).WaitAsync(Limit);

        Assert.Equal(1, maxInside);
    }
}
