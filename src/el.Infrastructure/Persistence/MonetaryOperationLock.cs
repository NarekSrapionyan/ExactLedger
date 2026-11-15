using el.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace el.Infrastructure.Persistence;

/// <summary>Waiting and holding limits of the monetary-operation lock. Use Timeout.InfiniteTimeSpan to disable one.</summary>
public sealed class MonetaryOperationLockOptions
{
    /// <summary>Longest wait for the lock (and for account row locks) inside the transaction.</summary>
    public TimeSpan WaitTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Longest the transaction may sit idle between statements. A hung worker is disconnected by the
    /// server, which rolls back and releases the lock.
    /// </summary>
    public TimeSpan MaxIdleInTransaction { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// PostgreSQL transaction-scoped advisory lock with one fixed ledger-wide key. It is released by
/// COMMIT, ROLLBACK or connection loss. Advisory locks are per database, so each database has its own.
/// </summary>
internal sealed class MonetaryOperationLock : IMonetaryOperationLock
{
    /// <summary>"ELMONEY1" as a 64-bit key. Reserved for this lock; use no other advisory-lock keys in this database.</summary>
    internal const long LockKey = 0x454C4D4F4E455931L;

    private const string IsolationSql = "SELECT current_setting('transaction_isolation') AS \"Value\"";

    private readonly ExactLedgerDbContext _dbContext;
    private readonly TimeSpan _waitTimeout;
    private readonly string _waitSetting;
    private readonly string _idleSetting;

    public MonetaryOperationLock(ExactLedgerDbContext dbContext, MonetaryOperationLockOptions? options = null)
    {
        options ??= new MonetaryOperationLockOptions();

        _dbContext = dbContext;
        _waitTimeout = options.WaitTimeout;
        _waitSetting = ToSetting(options.WaitTimeout, nameof(options.WaitTimeout));
        _idleSetting = ToSetting(options.MaxIdleInTransaction, nameof(options.MaxIdleInTransaction));
    }

    public async Task AcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "The monetary-operation lock must be acquired inside a unit-of-work transaction.");
        }

        try
        {
            // Under REPEATABLE READ or SERIALIZABLE the snapshot would be taken before the lock wait.
            var isolation = await _dbContext.Database.SqlQueryRaw<string>(IsolationSql).SingleAsync(cancellationToken);

            if (!string.Equals(isolation, "read committed", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The monetary-operation lock requires a READ COMMITTED transaction.");
            }

            // is_local = true behaves like SET LOCAL: it ends with this transaction.
            await _dbContext.Database.ExecuteSqlAsync(
                $"SELECT set_config('lock_timeout', {_waitSetting}, true), set_config('idle_in_transaction_session_timeout', {_idleSetting}, true)",
                cancellationToken);

            await _dbContext.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({LockKey})", cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            throw new LedgerLockTimeoutException(_waitTimeout, exception);
        }
        catch (PostgresException exception) when (
            exception.SqlState == PostgresErrorCodes.QueryCanceled && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static string ToSetting(TimeSpan value, string name)
    {
        if (value == Timeout.InfiniteTimeSpan)
        {
            return "0";
        }

        var milliseconds = value.Ticks / TimeSpan.TicksPerMillisecond;

        if (milliseconds < 1 || milliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(name, "The limit must be between 1 millisecond and about 24 days, or infinite.");
        }

        return FormattableString.Invariant($"{milliseconds}ms");
    }
}
