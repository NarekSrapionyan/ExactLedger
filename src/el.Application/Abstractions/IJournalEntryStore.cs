using el.Domain.Ledger;

namespace el.Application.Abstractions;

/// <summary>Persists validated journal entries as immutable, finalized ledger history.</summary>
public interface IJournalEntryStore
{
    /// <summary>
    /// Writes the entry and its lines and finalizes it in the current unit of work. The database
    /// checks the entry again when the unit of work commits.
    /// </summary>
    /// <exception cref="InvalidOperationException">No unit of work is running.</exception>
    Task AddAsync(JournalEntry entry, CancellationToken cancellationToken);
}
