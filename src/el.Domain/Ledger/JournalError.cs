namespace el.Domain.Ledger;

/// <summary>Why a journal entry was rejected.</summary>
public enum JournalError
{
    /// <summary>Fewer than two lines remain.</summary>
    TooFewLines = 1,

    /// <summary>A line amount is zero or negative.</summary>
    NonPositiveAmount,

    /// <summary>A line amount is in a different currency from the first line.</summary>
    MixedCurrencies,

    /// <summary>A line amount's currency differs from its account's currency.</summary>
    AccountCurrencyMismatch,

    /// <summary>A line targets an account that is not active.</summary>
    InactiveAccount,

    /// <summary>Total debits do not exactly equal total credits.</summary>
    Unbalanced,
}
