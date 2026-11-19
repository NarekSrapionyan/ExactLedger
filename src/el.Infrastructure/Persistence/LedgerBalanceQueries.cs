using el.Application.Ledger;
using el.Domain.Common;
using el.Domain.Ledger;
using Microsoft.EntityFrameworkCore;

namespace el.Infrastructure.Persistence;

internal sealed class BalanceRow
{
    public int AccountType { get; set; }
    public Guid? PartyId { get; set; }
    public string Currency { get; set; } = string.Empty;

    public string DebitTotal { get; set; } = string.Empty;

    public string CreditTotal { get; set; } = string.Empty;
}

/// <summary>
/// SUM over BIGINT amounts is NUMERIC in PostgreSQL, so it cannot overflow. It is cast to text and
/// parsed into BigInteger. Only lines of finalized entries count (the flag is redundant for committed
/// data and defensive otherwise). Each query is one statement, hence one consistent snapshot.
/// </summary>
internal sealed class LedgerBalanceQueries : ILedgerBalanceQueries
{
    private readonly ExactLedgerDbContext _dbContext;

    public LedgerBalanceQueries(ExactLedgerDbContext dbContext) => _dbContext = dbContext;

    public async Task<AccountBalance?> GetAccountBalanceAsync(AccountId accountId, CancellationToken cancellationToken)
    {
        var id = accountId.Value;
        var debit = (int)EntrySide.Debit;
        var credit = (int)EntrySide.Credit;

        var rows = await _dbContext.Database.SqlQuery<BalanceRow>($"""
            SELECT a.currency AS "Currency", a.party_id AS "PartyId", a.type AS "AccountType",
                   COALESCE(SUM(l.amount_minor) FILTER (WHERE l.side = {debit}), 0)::text AS "DebitTotal",
                   COALESCE(SUM(l.amount_minor) FILTER (WHERE l.side = {credit}), 0)::text AS "CreditTotal"
            FROM ledger.accounts a
            LEFT JOIN (
                SELECT jl.account_id, jl.side, jl.amount_minor
                FROM ledger.journal_lines jl
                JOIN ledger.journal_entries je ON je.id = jl.entry_id
                WHERE je.finalized
            ) l ON l.account_id = a.id
            WHERE a.id = {id}
            GROUP BY a.id, a.currency
            """).ToListAsync(cancellationToken);

        return rows.Count == 0
            ? null
            : new AccountBalance(
                accountId,
                CurrencyCode.Parse(rows[0].Currency),
                ExactAggregateQueries.ParseExactInteger(rows[0].DebitTotal),
                ExactAggregateQueries.ParseExactInteger(rows[0].CreditTotal),
                (AccountType)rows[0].AccountType,
                rows[0].PartyId is { } partyId ? new el.Domain.Ledger.PartyId(partyId) : null);
    }

    public async Task<IReadOnlyList<CurrencyTrialBalance>> GetTrialBalanceAsync(
        CurrencyCode? currency, CancellationToken cancellationToken)
    {
        var debit = (int)EntrySide.Debit;
        var credit = (int)EntrySide.Credit;
        var allCurrencies = currency is null;
        var code = currency?.ToString() ?? string.Empty;

        var rows = await _dbContext.Database.SqlQuery<BalanceRow>($"""
            SELECT jl.currency AS "Currency", NULL::uuid AS "PartyId", 0 AS "AccountType",
                   COALESCE(SUM(jl.amount_minor) FILTER (WHERE jl.side = {debit}), 0)::text AS "DebitTotal",
                   COALESCE(SUM(jl.amount_minor) FILTER (WHERE jl.side = {credit}), 0)::text AS "CreditTotal"
            FROM ledger.journal_lines jl
            JOIN ledger.journal_entries je ON je.id = jl.entry_id
            WHERE je.finalized AND ({allCurrencies} OR jl.currency = {code})
            GROUP BY jl.currency
            """).ToListAsync(cancellationToken);

        return rows
            .Select(row => new CurrencyTrialBalance(
                CurrencyCode.Parse(row.Currency),
                ExactAggregateQueries.ParseExactInteger(row.DebitTotal),
                ExactAggregateQueries.ParseExactInteger(row.CreditTotal)))
            .OrderBy(balance => balance.Currency.ToString(), StringComparer.Ordinal)
            .ToList();
    }
}
