namespace el.Application.Abstractions;

/// <summary>
/// The single exclusive lock that every money-changing workflow (journal posting, settlement,
/// escrow release, refund, daily cut, payout confirmation) takes in the same database
/// transaction as its changes. It is transaction-scoped: the database releases it when that
/// transaction commits or rolls back, so there is no release method. Acquire it first, before
/// reading any state the workflow depends on. Taking it again in the same transaction is allowed.
/// </summary>
public interface IMonetaryOperationLock
{
    /// <exception cref="InvalidOperationException">No transaction is active, or it is not READ COMMITTED.</exception>
    /// <exception cref="LedgerLockTimeoutException">The lock was not available within the wait limit.</exception>
    /// <exception cref="OperationCanceledException">The wait was canceled.</exception>
    Task AcquireAsync(CancellationToken cancellationToken);
}
