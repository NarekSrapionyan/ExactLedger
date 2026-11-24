namespace el.Infrastructure.Persistence;

// Only these fixed SQL fragments are composed; request values are always parameters.
internal static class ReportQueries
{
    internal const string Common = """
        WITH events AS (
            SELECT e.*, COALESCE(s.posted_at_utc, r.refunded_at_utc, t.occurred_at_utc, bc.confirmed_at_utc) AS posted_at,
                   CASE WHEN s.id IS NOT NULL THEN 'Settlement' WHEN r.settlement_id IS NOT NULL THEN 'Refund'
                        WHEN t.hold_id IS NOT NULL THEN 'EscrowRelease' WHEN pc.instruction_id IS NOT NULL THEN 'Payout'
                        ELSE 'Journal' END AS kind
            FROM ledger.journal_entries e
            LEFT JOIN ledger.settlements s ON s.journal_entry_id = e.id
            LEFT JOIN ledger.settlement_refunds r ON r.journal_entry_id = e.id
            LEFT JOIN ledger.escrow_hold_transitions t ON t.journal_entry_id = e.id AND t.state = 'Released'
            LEFT JOIN ledger.payout_confirmations pc ON pc.journal_entry_id = e.id
            LEFT JOIN ledger.clearing_batch_confirmations bc ON bc.batch_id = pc.batch_id
            WHERE e.finalized AND (@cut IS NULL OR e.sequence <= @cut)
        ), lines AS (
            SELECT e.id, e.sequence, e.reference, e.description, e.reversal_of, e.kind, e.posted_at,
                   l.line_number, l.account_id, a.party_id, a.type AS account_type, l.currency, l.side, l.amount_minor
            FROM events e JOIN ledger.journal_lines l ON l.entry_id = e.id
            JOIN ledger.accounts a ON a.id = l.account_id
            WHERE (@currency IS NULL OR l.currency = @currency) AND (@party IS NULL OR a.party_id = @party)
              AND (@from IS NULL OR e.posted_at >= (@from::date::timestamp AT TIME ZONE 'UTC'))
              AND (@to IS NULL OR e.posted_at < ((@to::date + 1)::timestamp AT TIME ZONE 'UTC'))
              AND (@batch IS NULL OR EXISTS (SELECT 1 FROM ledger.clearing_batch_entries be WHERE be.batch_id = @batch AND be.journal_entry_id = e.id)
                   OR EXISTS (SELECT 1 FROM ledger.payout_confirmations pc WHERE pc.batch_id = @batch AND pc.journal_entry_id = e.id))
        ), instructions AS (
            SELECT i.*, b.business_date, b.cut_at_utc, b.cut_sequence,
                   CASE WHEN pe.id IS NOT NULL THEN i.amount_minor ELSE 0 END AS paid,
                   CASE WHEN pe.id IS NULL THEN i.amount_minor ELSE 0 END AS reserved
            FROM ledger.payout_instructions i JOIN ledger.clearing_batches b ON b.id = i.batch_id
            LEFT JOIN ledger.payout_confirmations pc ON pc.instruction_id = i.id
            LEFT JOIN events pe ON pe.id = pc.journal_entry_id
            WHERE (@currency IS NULL OR i.currency = @currency) AND (@party IS NULL OR i.party_id = @party)
              AND (@cut IS NULL OR b.cut_sequence <= @cut) AND (@batch IS NULL OR b.id = @batch)
              AND (@from IS NULL OR b.business_date >= @from) AND (@to IS NULL OR b.business_date <= @to)
        )
        """;

    internal const string Journal = """
        SELECT jsonb_build_object(
            'journalEntryId', id::text, 'lineNumber', line_number::text, 'sequence', sequence::text,
            'reference', reference, 'description', description, 'postedAtUtc', COALESCE(to_char(posted_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'), ''),
            'reversalOf', COALESCE(reversal_of::text, ''), 'accountId', account_id::text, 'partyId', COALESCE(party_id::text, ''),
            'accountType', account_type::text, 'currency', currency,
            'debit', (CASE WHEN side = 1 THEN amount_minor ELSE 0 END)::text,
            'credit', (CASE WHEN side = 2 THEN amount_minor ELSE 0 END)::text)::text AS "Data"
        FROM lines ORDER BY id, line_number LIMIT 100001
        """;

    internal const string Statement = """
        SELECT data::text AS "Data" FROM (
            SELECT id::text || ':' || lpad(line_number::text, 8, '0') AS row_id, jsonb_build_object(
                'id', id::text || ':' || line_number::text, 'event', kind,
                'postedAtUtc', COALESCE(to_char(posted_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'), ''),
                'journalEntryId', id::text, 'accountId', account_id::text, 'accountType', account_type::text,
                'partyId', party_id::text, 'currency', currency,
                'debit', (CASE WHEN side = 1 THEN amount_minor ELSE 0 END)::text,
                'credit', (CASE WHEN side = 2 THEN amount_minor ELSE 0 END)::text,
                'grossAllocation', (CASE WHEN kind = 'Settlement' AND side = 2 THEN amount_minor ELSE 0 END)::text,
                'deductions', (CASE WHEN kind = 'Refund' AND side = 1 THEN amount_minor ELSE 0 END)::text,
                'heldEscrow', (CASE WHEN account_type = 3 THEN CASE WHEN side = 2 THEN amount_minor ELSE -amount_minor END ELSE 0 END)::text,
                'releasedEscrow', (CASE WHEN kind = 'EscrowRelease' AND account_type = 3 AND side = 1 THEN amount_minor ELSE 0 END)::text,
                'reserved', '0', 'confirmedPayouts', (CASE WHEN kind = 'Payout' AND side = 1 THEN amount_minor ELSE 0 END)::text) AS data
            FROM lines WHERE party_id IS NOT NULL AND account_type IN (2, 3)
            UNION ALL
            SELECT id::text, jsonb_build_object('id', id::text, 'event', 'Reservation',
                'postedAtUtc', to_char(cut_at_utc AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
                'journalEntryId', '', 'accountId', '', 'accountType', '2', 'partyId', party_id::text, 'currency', currency,
                'debit', '0', 'credit', '0', 'grossAllocation', '0', 'deductions', '0', 'heldEscrow', '0',
                'releasedEscrow', '0', 'reserved', reserved::text, 'confirmedPayouts', '0')
            FROM instructions
        ) rows ORDER BY row_id LIMIT 100001
        """;

    internal const string Balances = """
        SELECT jsonb_build_object('accountId', a.id::text, 'partyId', COALESCE(a.party_id::text, ''),
            'accountType', a.type::text, 'currency', a.currency,
            'debit', COALESCE(x.debit, 0)::text, 'credit', COALESCE(x.credit, 0)::text,
            'balance', (CASE WHEN a.type = 1 THEN COALESCE(x.debit, 0) - COALESCE(x.credit, 0)
                            ELSE COALESCE(x.credit, 0) - COALESCE(x.debit, 0) END)::text,
            'reserved', (CASE WHEN a.type = 2 THEN COALESCE(i.reserved, 0) ELSE 0 END)::text,
            'available', (CASE WHEN a.type = 2 THEN COALESCE(x.credit, 0) - COALESCE(x.debit, 0) - COALESCE(i.reserved, 0) ELSE 0 END)::text)::text AS "Data"
        FROM ledger.accounts a
        LEFT JOIN (SELECT account_id, SUM(amount_minor) FILTER (WHERE side = 1) AS debit,
                          SUM(amount_minor) FILTER (WHERE side = 2) AS credit FROM lines GROUP BY account_id) x ON x.account_id = a.id
        LEFT JOIN (SELECT party_id, currency, SUM(reserved) AS reserved FROM instructions GROUP BY party_id, currency) i
            ON i.party_id = a.party_id AND i.currency = a.currency
        WHERE (@currency IS NULL OR a.currency = @currency) AND (@party IS NULL OR a.party_id = @party)
        ORDER BY a.id LIMIT 100001
        """;

    internal const string TrialBalance = """
        SELECT jsonb_build_object('currency', currency,
            'debit', COALESCE(SUM(amount_minor) FILTER (WHERE side = 1), 0)::text,
            'credit', COALESCE(SUM(amount_minor) FILTER (WHERE side = 2), 0)::text,
            'difference', (COALESCE(SUM(amount_minor) FILTER (WHERE side = 1), 0) - COALESCE(SUM(amount_minor) FILTER (WHERE side = 2), 0))::text)::text AS "Data"
        FROM lines GROUP BY currency ORDER BY currency
        """;

    internal const string Instructions = """
        SELECT jsonb_build_object('instructionId', id::text, 'batchId', batch_id::text, 'reference', reference,
            'businessDate', business_date::text, 'partyId', party_id::text, 'currency', currency,
            'amount', amount_minor::text, 'reserved', reserved::text, 'paid', paid::text)::text AS "Data"
        FROM instructions ORDER BY id LIMIT 100001
        """;

    internal const string Batches = """
        SELECT jsonb_build_object('batchId', b.id::text, 'businessDate', b.business_date::text, 'currency', b.currency,
            'state', CASE WHEN c.batch_id IS NOT NULL THEN 'Confirmed' WHEN e.batch_id IS NOT NULL THEN 'Exported' ELSE 'Prepared' END,
            'entryCount', b.entry_count::text, 'instructionCount', b.instruction_count::text,
            'instructions', b.total_minor::text, 'reserved', COALESCE(i.reserved, 0)::text, 'paid', COALESCE(i.paid, 0)::text,
            'payableDebits', COALESCE(p.debits, 0)::text, 'bankCredits', COALESCE(p.credits, 0)::text,
            'isReconciled', (b.total_minor = COALESCE(i.reserved, 0) + COALESCE(i.paid, 0)
                AND COALESCE(i.paid, 0) = COALESCE(p.debits, 0) AND COALESCE(i.paid, 0) = COALESCE(p.credits, 0))::text)::text AS "Data"
        FROM ledger.clearing_batches b
        LEFT JOIN ledger.clearing_batch_exports e ON e.batch_id = b.id
        LEFT JOIN ledger.clearing_batch_confirmations c ON c.batch_id = b.id
        LEFT JOIN (SELECT batch_id, SUM(reserved) AS reserved, SUM(paid) AS paid FROM instructions GROUP BY batch_id) i ON i.batch_id = b.id
        LEFT JOIN (SELECT pc.batch_id,
                    SUM(l.amount_minor) FILTER (WHERE l.side = 1 AND a.type = 2) AS debits,
                    SUM(l.amount_minor) FILTER (WHERE l.side = 2 AND a.type = 1) AS credits
                   FROM ledger.payout_confirmations pc JOIN events pe ON pe.id = pc.journal_entry_id
                   JOIN ledger.journal_lines l ON l.entry_id = pe.id JOIN ledger.accounts a ON a.id = l.account_id
                   GROUP BY pc.batch_id) p ON p.batch_id = b.id
        WHERE (@currency IS NULL OR b.currency = @currency) AND (@batch IS NULL OR b.id = @batch)
          AND (@from IS NULL OR b.business_date >= @from) AND (@to IS NULL OR b.business_date <= @to)
          AND (@cut IS NULL OR b.cut_sequence <= @cut)
        ORDER BY b.id LIMIT 100001
        """;
}
