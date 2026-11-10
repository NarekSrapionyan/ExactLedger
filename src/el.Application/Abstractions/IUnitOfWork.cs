namespace el.Application.Abstractions;

/// <summary>
/// Runs a use case's changes in one database transaction. Changes are saved and committed only
/// when the work completes; any exception rolls everything back. Units of work do not nest.
/// </summary>
public interface IUnitOfWork
{
    /// <exception cref="InvalidOperationException">A unit of work is already running in this scope.</exception>
    Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> work, CancellationToken cancellationToken);
}
