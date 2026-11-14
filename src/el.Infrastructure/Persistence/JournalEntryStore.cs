using el.Application.Abstractions;
using el.Domain.Ledger;
using Microsoft.EntityFrameworkCore;

namespace el.Infrastructure.Persistence;

internal sealed class JournalEntryStore : IJournalEntryStore
{
    private readonly ExactLedgerDbContext _dbContext;

    public JournalEntryStore(ExactLedgerDbContext dbContext) => _dbContext = dbContext;

    public async Task AddAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (_dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Journal entries are posted inside a unit of work.");
        }

        var header = new JournalEntryRecord
        {
            Id = entry.Id,
            Reference = entry.Reference,
            Description = entry.Description,
            Currency = entry.Currency,
            ReversalOf = entry.ReversalOf,
            Finalized = false,
        };

        var lines = entry.Lines.Select(line => new JournalLineRecord
        {
            EntryId = entry.Id,
            LineNumber = line.LineNumber,
            AccountId = line.AccountId,
            Side = line.Side,
            AmountMinor = line.Amount.MinorUnits,
            Currency = line.Amount.Currency,
        }).ToArray();

        _dbContext.Add(header);
        _dbContext.AddRange(lines);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _dbContext.Database.ExecuteSqlAsync(
            $"SELECT ledger.finalize_journal_entry({entry.Id.Value})", cancellationToken);

        // History is never updated through EF; stop tracking the written rows.
        _dbContext.Entry(header).State = EntityState.Detached;

        foreach (var line in lines)
        {
            _dbContext.Entry(line).State = EntityState.Detached;
        }
    }
}
