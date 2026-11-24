namespace el.Infrastructure.Persistence;

/// <summary>Names and SQLSTATE codes defined by the ledger safety migrations.</summary>
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

    /// <summary>An escrow hold transition with an unknown state.</summary>
    public const string UnsupportedEscrowTransition = "EL401";

    /// <summary>An escrow release does not match its hold or its Dr Escrow / Cr Payable journal entry.</summary>
    public const string InvalidEscrowRelease = "EL402";

    /// <summary>A refund is not an exact, linked reversal of its settlement with all holds marked Refunded.</summary>
    public const string InvalidRefund = "EL403";

    /// <summary>A row was appended to a clearing batch after the transaction that created it.</summary>
    public const string ClearingAppendAfterCommit = "EL501";

    /// <summary>A clearing batch disagrees with its entries, instructions, the ledger or the one-unconfirmed-batch rule.</summary>
    public const string InvalidClearingBatch = "EL502";

    /// <summary>A payout export does not match its batch.</summary>
    public const string InvalidClearingExport = "EL503";

    /// <summary>A confirmation is not backed by an export and exact Dr Payable / Cr Clearing postings.</summary>
    public const string InvalidClearingConfirmation = "EL504";
}
