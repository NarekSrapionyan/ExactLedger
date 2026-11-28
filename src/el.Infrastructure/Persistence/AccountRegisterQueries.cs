using el.Application.Ledger;
using el.Domain.Ledger;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace el.Infrastructure.Persistence;

internal sealed class AccountRegisterRow
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public int Type { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string Opening { get; set; } = string.Empty;
    public string Debit { get; set; } = string.Empty;
    public string Credit { get; set; } = string.Empty;
}

internal sealed class AccountRegisterQueries(ExactLedgerDbContext db) : IAccountRegisterQueries
{
    private const string Sql = """
        WITH posted AS (
            SELECT e.id, COALESCE(s.posted_at_utc, r.refunded_at_utc, t.occurred_at_utc, c.confirmed_at_utc) AS at_utc
            FROM ledger.journal_entries e
            LEFT JOIN ledger.settlements s ON s.journal_entry_id = e.id
            LEFT JOIN ledger.settlement_refunds r ON r.journal_entry_id = e.id
            LEFT JOIN ledger.escrow_hold_transitions t ON t.journal_entry_id = e.id AND t.state = 'Released'
            LEFT JOIN ledger.payout_confirmations pc ON pc.journal_entry_id = e.id
            LEFT JOIN ledger.clearing_batch_confirmations c ON c.batch_id = pc.batch_id
            WHERE e.finalized
        ), lines AS (
            SELECT l.account_id, l.amount_minor, l.side, p.at_utc
            FROM ledger.journal_lines l JOIN posted p ON p.id = l.entry_id
        )
        SELECT a.id AS "Id", a.code AS "Code", a.type AS "Type", a.currency AS "Currency",
            COALESCE(SUM(CASE WHEN l.side = 1 THEN l.amount_minor::numeric ELSE -l.amount_minor::numeric END)
                FILTER (WHERE l.at_utc < @from), 0)::text AS "Opening",
            COALESCE(SUM(l.amount_minor) FILTER (WHERE l.side = 1 AND (@from IS NULL OR l.at_utc >= @from)
                AND (@until IS NULL OR l.at_utc < @until)), 0)::text AS "Debit",
            COALESCE(SUM(l.amount_minor) FILTER (WHERE l.side = 2 AND (@from IS NULL OR l.at_utc >= @from)
                AND (@until IS NULL OR l.at_utc < @until)), 0)::text AS "Credit"
        FROM ledger.accounts a LEFT JOIN lines l ON l.account_id = a.id
        WHERE (@currency IS NULL OR a.currency = @currency)
        GROUP BY a.id, a.code, a.type, a.currency
        ORDER BY a.code, a.id LIMIT @limit OFFSET @offset
        """;

    public async Task<IReadOnlyList<AccountRegisterBalance>> ReadAsync(string? currency, DateOnly? from, DateOnly? to,
        int offset, int limit, CancellationToken ct)
    {
        DateTimeOffset? start = from is { } first ? new(first.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null;
        DateTimeOffset? end = to is { } last ? last == DateOnly.MaxValue ? DateTimeOffset.MaxValue
            : new(last.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null;
        var rows = await db.Database.SqlQueryRaw<AccountRegisterRow>(Sql,
            new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = (object?)start ?? DBNull.Value },
            new NpgsqlParameter("until", NpgsqlDbType.TimestampTz) { Value = (object?)end ?? DBNull.Value },
            new NpgsqlParameter("currency", NpgsqlDbType.Text) { Value = (object?)currency ?? DBNull.Value },
            new NpgsqlParameter("offset", offset), new NpgsqlParameter("limit", limit)).ToListAsync(ct);
        return rows.Select(row => new AccountRegisterBalance(row.Id, row.Code, (AccountType)row.Type, row.Currency,
            ExactAggregateQueries.ParseExactInteger(row.Opening), ExactAggregateQueries.ParseExactInteger(row.Debit),
            ExactAggregateQueries.ParseExactInteger(row.Credit))).ToArray();
    }
}
