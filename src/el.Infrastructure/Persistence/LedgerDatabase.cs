namespace el.Infrastructure.Persistence;

/// <summary>Names and SQLSTATE codes defined by the ledger safety migration.</summary>
public static class LedgerDatabase
{
    /// <summary>NOLOGIN group role with the runtime privileges; runtime login users are members of it.</summary>
    public const string RuntimeRole = "exactledger_runtime";

    /// <summary>A line was added to an entry that is already finalized.</summary>
    public const string LineAddedAfterFinalization = "EL101";

    /// <summary>A completed entry has fewer than two lines or its debits do not equal its credits.</summary>
    public const string UnbalancedEntry = "EL102";

    /// <summary>An entry was not finalized before its transaction committed.</summary>
    public const string EntryNotFinalized = "EL103";

    /// <summary>Posted journal history was updated, deleted or truncated.</summary>
    public const string HistoryImmutable = "EL104";

    /// <summary>Finalization was requested for a missing or already finalized entry.</summary>
    public const string InvalidFinalization = "EL105";
}
