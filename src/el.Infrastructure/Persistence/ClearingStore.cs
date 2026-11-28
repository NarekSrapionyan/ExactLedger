using System.Globalization;
using System.Numerics;
using el.Application.Clearing;
using el.Application.Settlements;
using el.Domain.Clearing;
using el.Domain.Common;
using el.Domain.Ledger;
using Microsoft.EntityFrameworkCore;

namespace el.Infrastructure.Persistence;

internal sealed class ClearingStore(ExactLedgerDbContext db) : IClearingStore
{
    public async Task<ClearingBatch?> FindBatchAsync(Guid id, CancellationToken ct)
    {
        var row = (await db.Database.SqlQuery<BatchRow>($"""
            SELECT b.id AS "Id", b.business_date AS "BusinessDate", b.currency AS "Currency", b.cut_at_utc AS "CutAtUtc",
                   b.cut_sequence AS "CutSequence", b.entry_count AS "EntryCount", b.total_minor::text AS "TotalMinor"
            FROM ledger.clearing_batches b WHERE b.id = {id}
            """).ToListAsync(ct)).SingleOrDefault();

        return row is null ? null : await MapAsync(row, ct);
    }

    public async Task<ClearingBatch?> FindBatchAsync(DateOnly businessDate, string currency, CancellationToken ct)
    {
        var row = (await db.Database.SqlQuery<BatchRow>($"""
            SELECT b.id AS "Id", b.business_date AS "BusinessDate", b.currency AS "Currency", b.cut_at_utc AS "CutAtUtc",
                   b.cut_sequence AS "CutSequence", b.entry_count AS "EntryCount", b.total_minor::text AS "TotalMinor"
            FROM ledger.clearing_batches b WHERE b.business_date = {businessDate} AND b.currency = {currency}
            """).ToListAsync(ct)).SingleOrDefault();

        return row is null ? null : await MapAsync(row, ct);
    }

    public async Task<IReadOnlyList<ClearingBatch>> ListBatchesAsync(int offset, int limit, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<BatchRow>($"""
            SELECT b.id AS "Id", b.business_date AS "BusinessDate", b.currency AS "Currency", b.cut_at_utc AS "CutAtUtc",
                   b.cut_sequence AS "CutSequence", b.entry_count AS "EntryCount", b.total_minor::text AS "TotalMinor"
            FROM ledger.clearing_batches b
            ORDER BY b.business_date DESC, b.currency
            LIMIT {limit} OFFSET {offset}
            """).ToListAsync(ct);
        var result = new List<ClearingBatch>();

        foreach (var row in rows.OrderByDescending(item => item.BusinessDate).ThenBy(item => item.Currency, StringComparer.Ordinal))
        {
            result.Add(await MapAsync(row, ct));
        }

        return result;
    }

    public Task<bool> HasUnconfirmedBatchAsync(string currency, CancellationToken ct) =>
        db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (SELECT 1 FROM ledger.clearing_batches b WHERE b.currency = {currency}
                             AND NOT EXISTS (SELECT 1 FROM ledger.clearing_batch_confirmations c WHERE c.batch_id = b.id)) AS "Value"
            """).SingleAsync(ct);

    public Task<long> LatestJournalSequenceAsync(CancellationToken ct) =>
        db.Database.SqlQuery<long>($"""
            SELECT COALESCE(MAX(e.sequence), 0) AS "Value" FROM ledger.journal_entries e WHERE e.finalized
            """).SingleAsync(ct);

    public async Task<IReadOnlyList<CandidateEntry>> UnbatchedEntriesAsync(string currency, long watermark, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<CandidateRow>($"""
            SELECT e.id AS "Id", e.sequence AS "Sequence", k.kind AS "Kind"
            FROM ledger.journal_entries e
            JOIN (SELECT journal_entry_id AS entry_id, 'Settlement'::text AS kind FROM ledger.settlements
                  UNION ALL SELECT journal_entry_id, 'Refund' FROM ledger.settlement_refunds
                  UNION ALL SELECT journal_entry_id, 'EscrowRelease' FROM ledger.escrow_hold_transitions WHERE state = 'Released') k
              ON k.entry_id = e.id
            WHERE e.finalized AND e.currency = {currency} AND e.sequence <= {watermark}
              AND NOT EXISTS (SELECT 1 FROM ledger.clearing_batch_entries be WHERE be.journal_entry_id = e.id)
            """).ToListAsync(ct);

        return rows.OrderBy(row => row.Sequence).ThenBy(row => row.Id)
            .Select(row => new CandidateEntry(row.Id, row.Kind, row.Sequence)).ToArray();
    }

    public async Task<IReadOnlyList<PartyPayable>> PayableBalancesAsync(string currency, long watermark, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<PayableRow>($"""
            SELECT a.party_id AS "PartyId",
                   COALESCE(SUM(l.amount_minor) FILTER (WHERE l.side = 2), 0)::text AS "CreditTotal",
                   COALESCE(SUM(l.amount_minor) FILTER (WHERE l.side = 1), 0)::text AS "DebitTotal",
                   COALESCE((SELECT SUM(i.amount_minor) FROM ledger.payout_instructions i
                              WHERE i.party_id = a.party_id AND i.currency = a.currency
                                AND NOT EXISTS (SELECT 1 FROM ledger.payout_confirmations c WHERE c.instruction_id = i.id)), 0)::text AS "Reserved"
            FROM ledger.accounts a
            JOIN ledger.payout_parties pp ON pp.party_id = a.party_id
            JOIN ledger.parties p ON p.id = a.party_id
            LEFT JOIN (SELECT jl.account_id, jl.side, jl.amount_minor
                         FROM ledger.journal_lines jl JOIN ledger.journal_entries je ON je.id = jl.entry_id
                        WHERE je.finalized AND je.sequence <= {watermark}) l ON l.account_id = a.id
            WHERE a.type = 2 AND a.currency = {currency} AND a.status = 1 AND p.status = 1
            GROUP BY a.id, a.party_id, a.currency
            """).ToListAsync(ct);

        return rows.Select(row => new PartyPayable(new PartyId(row.PartyId),
            ExactAggregateQueries.ParseExactInteger(row.CreditTotal), ExactAggregateQueries.ParseExactInteger(row.DebitTotal),
            ExactAggregateQueries.ParseExactInteger(row.Reserved))).ToArray();
    }

    public async Task<IReadOnlyList<SelectedEntryFacts>> EntryFactsAsync(IReadOnlyCollection<Guid> entryIds, CancellationToken ct)
    {
        var ids = entryIds.ToArray();
        var rows = await db.Database.SqlQuery<EntryFactRow>($"""
            SELECT e.id AS "Id", e.currency AS "Currency", e.finalized AS "Finalized", COUNT(l.line_number)::int AS "LineCount",
                   COALESCE(SUM(l.amount_minor) FILTER (WHERE l.side = 1), 0)::text AS "DebitTotal",
                   COALESCE(SUM(l.amount_minor) FILTER (WHERE l.side = 2), 0)::text AS "CreditTotal"
            FROM ledger.journal_entries e LEFT JOIN ledger.journal_lines l ON l.entry_id = e.id
            WHERE e.id = ANY({ids})
            GROUP BY e.id, e.currency, e.finalized
            """).ToListAsync(ct);

        return rows.Select(row => new SelectedEntryFacts(new JournalEntryId(row.Id), CurrencyCode.Parse(row.Currency), row.Finalized,
            row.LineCount, ExactAggregateQueries.ParseExactInteger(row.DebitTotal), ExactAggregateQueries.ParseExactInteger(row.CreditTotal))).ToArray();
    }

    public async Task AddBatchAsync(NewClearingBatch batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Clearing writes require a transaction.");
        }

        var total = batch.TotalMinorUnits.ToString(CultureInfo.InvariantCulture);
        var entryCount = batch.Entries.Count;
        var instructionCount = batch.Instructions.Count;

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO ledger.clearing_batches (id, business_date, currency, cut_at_utc, cut_sequence, entry_count, instruction_count, total_minor)
            VALUES ({batch.Id}, {batch.BusinessDate}, {batch.Currency}, {batch.CutAtUtc}, {batch.CutSequence}, {entryCount}, {instructionCount}, CAST({total} AS numeric))
            """, ct);

        foreach (var entry in batch.Entries)
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO ledger.clearing_batch_entries (journal_entry_id, batch_id, kind) VALUES ({entry.Id}, {batch.Id}, {entry.Kind})
                """, ct);
        }

        foreach (var instruction in batch.Instructions)
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO ledger.payout_instructions (id, batch_id, party_id, reference, currency, amount_minor)
                VALUES ({instruction.Id}, {batch.Id}, {instruction.PartyId}, {instruction.Reference}, {batch.Currency}, {instruction.AmountMinor})
                """, ct);
        }
    }

    public Task<BigInteger> InstructionTotalAsync(Guid batchId, CancellationToken ct) =>
        db.Database.QueryExactIntegerAsync($"SELECT sum(i.amount_minor) FROM ledger.payout_instructions i WHERE i.batch_id = {batchId}", ct);

    public Task<BigInteger> ActiveReservationTotalAsync(Guid batchId, CancellationToken ct) =>
        db.Database.QueryExactIntegerAsync($"""
            SELECT sum(i.amount_minor) FROM ledger.payout_instructions i
             WHERE i.batch_id = {batchId}
               AND NOT EXISTS (SELECT 1 FROM ledger.payout_confirmations c WHERE c.instruction_id = i.id)
            """, ct);

    private async Task<ClearingBatch> MapAsync(BatchRow row, CancellationToken ct)
    {
        var instructions = (await db.Database.SqlQuery<InstructionRow>($"""
            SELECT i.id AS "Id", i.reference AS "Reference", i.party_id AS "PartyId", i.amount_minor AS "AmountMinor"
            FROM ledger.payout_instructions i WHERE i.batch_id = {row.Id}
            """).ToListAsync(ct)).OrderBy(item => item.Reference, StringComparer.Ordinal)
            .Select(item => new PayoutInstruction(item.Id, item.Reference, item.PartyId, item.AmountMinor)).ToArray();
        var export = (await db.Database.SqlQuery<ExportMetaRow>($"""
            SELECT e.content_sha256 AS "Sha256", e.record_count AS "RecordCount", e.total_minor::text AS "TotalMinor", e.generated_at_utc AS "GeneratedAtUtc"
            FROM ledger.clearing_batch_exports e WHERE e.batch_id = {row.Id}
            """).ToListAsync(ct)).SingleOrDefault();
        var confirmation = (await db.Database.SqlQuery<ConfirmationRow>($"""
            SELECT c.statement_sha256 AS "StatementSha256", c.instruction_count AS "InstructionCount", c.total_minor::text AS "TotalMinor",
                   c.confirmed_at_utc AS "ConfirmedAtUtc"
            FROM ledger.clearing_batch_confirmations c WHERE c.batch_id = {row.Id}
            """).ToListAsync(ct)).SingleOrDefault();

        return new ClearingBatch(row.Id, row.BusinessDate, row.Currency, row.CutAtUtc, row.CutSequence, row.EntryCount,
            ExactAggregateQueries.ParseExactInteger(row.TotalMinor), instructions,
            export is null ? null : new ExportMetadata(export.Sha256, export.RecordCount,
                ExactAggregateQueries.ParseExactInteger(export.TotalMinor), export.GeneratedAtUtc),
            confirmation is null ? null : new ConfirmationMetadata(confirmation.StatementSha256, confirmation.InstructionCount,
                ExactAggregateQueries.ParseExactInteger(confirmation.TotalMinor), confirmation.ConfirmedAtUtc));
    }
}

/// <summary>A settlement is assigned once its journal entry is in a clearing batch. Called by refunds under the monetary lock.</summary>
internal sealed class ClearingBatchMembership(ExactLedgerDbContext db) : ISettlementBatchMembership
{
    public Task<bool> IsAssignedAsync(Guid settlementId, CancellationToken ct) =>
        db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (SELECT 1 FROM ledger.settlements s
                             JOIN ledger.clearing_batch_entries be ON be.journal_entry_id = s.journal_entry_id
                            WHERE s.id = {settlementId}) AS "Value"
            """).SingleAsync(ct);
}
