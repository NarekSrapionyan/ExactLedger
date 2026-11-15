using el.Application.Abstractions;
using el.Application.Ledger;
using el.Domain.Ledger;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace el.Infrastructure.Persistence;

internal sealed class AccountStatusRow
{
    public Guid Id { get; set; }

    public int Status { get; set; }
}

internal sealed class JournalAccountVerifier : IJournalAccountVerifier
{
    private readonly ExactLedgerDbContext _dbContext;
    private readonly TimeSpan _waitTimeout;

    public JournalAccountVerifier(ExactLedgerDbContext dbContext, MonetaryOperationLockOptions? options = null)
    {
        _dbContext = dbContext;
        _waitTimeout = (options ?? new MonetaryOperationLockOptions()).WaitTimeout;
    }

    public async Task EnsurePostableAsync(IReadOnlyCollection<AccountId> accountIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accountIds);

        if (accountIds.Count == 0)
        {
            return;
        }

        var ids = accountIds.Select(id => id.Value).ToArray();

        List<AccountStatusRow> rows;

        try
        {
            // FOR SHARE: a concurrent UPDATE of accounts.status waits until this transaction ends,
            // so an account cannot be closed between this check and the commit. The runtime role has
            // UPDATE (status) on accounts, which is the privilege FOR SHARE requires.
            rows = await _dbContext.Database.SqlQuery<AccountStatusRow>($"""
                SELECT a.id AS "Id", a.status AS "Status"
                FROM ledger.accounts a
                WHERE a.id = ANY({ids})
                FOR SHARE OF a
                """).ToListAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            throw new LedgerLockTimeoutException(_waitTimeout, exception);
        }

        var statuses = rows.ToDictionary(row => row.Id, row => row.Status);

        foreach (var accountId in accountIds)
        {
            if (!statuses.TryGetValue(accountId.Value, out var status))
            {
                throw new AccountNotPostableException(accountId, AccountPostingProblem.NotFound);
            }

            if (status != (int)AccountStatus.Active)
            {
                throw new AccountNotPostableException(accountId, AccountPostingProblem.NotActive);
            }
        }
    }
}
