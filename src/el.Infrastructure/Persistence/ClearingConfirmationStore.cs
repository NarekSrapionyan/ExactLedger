using System.Globalization;
using el.Application.Clearing;
using Microsoft.EntityFrameworkCore;

namespace el.Infrastructure.Persistence;

internal sealed class ClearingConfirmationStore(ExactLedgerDbContext db) : IClearingConfirmationStore
{
    public async Task AddAsync(Guid batchId, ConfirmationMetadata metadata, IReadOnlyList<PayoutPosting> postings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(postings);

        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Clearing writes require a transaction.");
        }

        var total = metadata.TotalMinorUnits.ToString(CultureInfo.InvariantCulture);

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO ledger.clearing_batch_confirmations (batch_id, statement_sha256, instruction_count, total_minor, confirmed_at_utc)
            VALUES ({batchId}, {metadata.StatementSha256}, {metadata.InstructionCount}, CAST({total} AS numeric), {metadata.ConfirmedAtUtc})
            """, ct);

        foreach (var posting in postings)
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO ledger.payout_confirmations (instruction_id, batch_id, journal_entry_id, statement_row)
                VALUES ({posting.InstructionId}, {batchId}, {posting.JournalEntryId}, {posting.StatementRow})
                """, ct);
        }
    }
}
