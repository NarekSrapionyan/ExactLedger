using el.Domain.Common;
using el.Domain.Ledger;

namespace el.Infrastructure.Persistence;

/// <summary>
/// Persistence row of a journal entry. Rows are written once and then protected by database
/// privileges and triggers; <see cref="Finalized"/> changes only through ledger.finalize_journal_entry.
/// </summary>
internal sealed class JournalEntryRecord
{
    public JournalEntryId Id { get; set; }

    public JournalReference Reference { get; set; } = null!;

    public string Description { get; set; } = null!;

    public CurrencyCode Currency { get; set; } = null!;

    public JournalEntryId? ReversalOf { get; set; }

    public bool Finalized { get; set; }
}

/// <summary>Persistence row of a journal line: a positive BIGINT amount in minor units.</summary>
internal sealed class JournalLineRecord
{
    public JournalEntryId EntryId { get; set; }

    public int LineNumber { get; set; }

    public AccountId AccountId { get; set; }

    public EntrySide Side { get; set; }

    public long AmountMinor { get; set; }

    public CurrencyCode Currency { get; set; } = null!;
}
