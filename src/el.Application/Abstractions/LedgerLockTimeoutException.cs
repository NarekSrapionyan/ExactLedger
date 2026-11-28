namespace el.Application.Abstractions;

/// <summary>
/// A ledger lock (the monetary-operation lock or an account row) was not available within the
/// wait limit. Nothing was changed; the whole operation can be retried.
/// </summary>
public sealed class LedgerLockTimeoutException : Exception
{
    public LedgerLockTimeoutException(TimeSpan waited, Exception innerException)
        : base("Timed out waiting for a ledger lock. The operation was not applied and can be retried.", innerException)
    {
        Waited = waited;
    }

    /// <summary>The configured wait limit that was exceeded.</summary>
    public TimeSpan Waited { get; }
}
