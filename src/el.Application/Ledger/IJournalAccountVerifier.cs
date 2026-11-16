using el.Domain.Ledger;

namespace el.Application.Ledger;

public enum AccountPostingProblem
{
    NotFound = 1,
    NotActive = 2,
}

/// <summary>An account referenced by a journal entry does not exist or is not active.</summary>
public sealed class AccountNotPostableException : Exception
{
    public AccountNotPostableException(AccountId accountId, AccountPostingProblem problem)
        : base(problem == AccountPostingProblem.NotFound
            ? "A journal line references an account that does not exist."
            : "A journal line references an account that is not active.")
    {
        AccountId = accountId;
        Problem = problem;
    }

    public AccountId AccountId { get; }

    public AccountPostingProblem Problem { get; }
}

/// <summary>
/// Re-checks, inside the posting transaction and under the monetary lock, that every account is
/// still active. The entry was validated against an earlier snapshot, so this is the authoritative check.
/// </summary>
public interface IJournalAccountVerifier
{
    /// <exception cref="AccountNotPostableException">An account is missing or not active.</exception>
    /// <exception cref="Abstractions.LedgerLockTimeoutException">An account row stayed locked past the wait limit.</exception>
    Task EnsurePostableAsync(IReadOnlyCollection<AccountId> accountIds, CancellationToken cancellationToken);
}
