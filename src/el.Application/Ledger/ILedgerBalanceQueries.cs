using System.Numerics;
using el.Domain.Common;
using el.Domain.Ledger;

namespace el.Application.Ledger;

/// <summary>
/// Exact totals of the finalized lines of one account. Totals are BigInteger minor units and are
/// never narrowed into Money: an account total can exceed Money's per-value limit.
/// </summary>
public sealed record AccountBalance(AccountId AccountId, CurrencyCode Currency, BigInteger DebitTotal, BigInteger CreditTotal, AccountType Type, PartyId? PartyId = null)
{
    public EntrySide NormalSide => AccountTypes.NormalSide(Type);

    /// <summary>Debits minus credits (debit-positive).</summary>
    public BigInteger NetDebit => DebitTotal - CreditTotal;

    /// <summary>The balance as a positive number when it sits on the account's normal side.</summary>
    public BigInteger OnNormalSide(EntrySide normalSide) => normalSide switch
    {
        EntrySide.Debit => DebitTotal - CreditTotal,
        EntrySide.Credit => CreditTotal - DebitTotal,
        _ => throw new ArgumentOutOfRangeException(nameof(normalSide), normalSide, "The entry side is not defined."),
    };
}

/// <summary>Exact debit and credit totals of all finalized lines in one currency.</summary>
public sealed record CurrencyTrialBalance(CurrencyCode Currency, BigInteger DebitTotal, BigInteger CreditTotal)
{
    public BigInteger Difference => DebitTotal - CreditTotal;

    public bool IsBalanced => Difference.IsZero;
}

/// <summary>Read queries derived from immutable, finalized journal lines. They take no lock.</summary>
public interface ILedgerBalanceQueries
{
    /// <summary>Returns zero totals for an account without postings, and null for an unknown account.</summary>
    Task<AccountBalance?> GetAccountBalanceAsync(AccountId accountId, CancellationToken cancellationToken);

    /// <summary>
    /// One row per currency with finalized lines, ordered by currency code, never netted across
    /// currencies. A currency filter limits the result to that currency.
    /// </summary>
    Task<IReadOnlyList<CurrencyTrialBalance>> GetTrialBalanceAsync(CurrencyCode? currency, CancellationToken cancellationToken);
}
