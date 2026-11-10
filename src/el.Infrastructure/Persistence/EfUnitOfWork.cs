using el.Application.Abstractions;

namespace el.Infrastructure.Persistence;

/// <summary>
/// One explicit transaction per unit of work on the scoped DbContext. No retrying execution
/// strategy is configured, so a failed unit of work is never silently replayed.
/// </summary>
internal sealed class EfUnitOfWork : IUnitOfWork
{
    private readonly ExactLedgerDbContext _dbContext;

    public EfUnitOfWork(ExactLedgerDbContext dbContext) => _dbContext = dbContext;

    public async Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (_dbContext.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException("A unit of work is already running in this scope; units of work do not nest.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var result = await work(cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return result;
        }
        catch
        {
            // The transaction rolls back when disposed. Drop tracked changes so they cannot be
            // saved later by accident in the same scope.
            _dbContext.ChangeTracker.Clear();
            throw;
        }
    }
}
