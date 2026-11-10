namespace el.Domain.Ledger;

/// <summary>Thrown when journal lines break a ledger invariant. Line numbers are 1-based.</summary>
public sealed class JournalValidationException : Exception
{
    public JournalValidationException(JournalError error, int? lineNumber = null)
        : base(Describe(error, lineNumber))
    {
        Error = error;
        LineNumber = lineNumber;
    }

    public JournalError Error { get; }

    public int? LineNumber { get; }

    private static string Describe(JournalError error, int? lineNumber) => error switch
    {
        JournalError.TooFewLines => "A journal entry needs at least two lines with a positive amount.",
        JournalError.NonPositiveAmount => FormattableString.Invariant($"Journal line {lineNumber} must have a positive amount."),
        JournalError.MixedCurrencies => FormattableString.Invariant($"Journal line {lineNumber} is in a different currency from the entry."),
        JournalError.AccountCurrencyMismatch => FormattableString.Invariant($"Journal line {lineNumber} is in a different currency from its account."),
        JournalError.InactiveAccount => FormattableString.Invariant($"Journal line {lineNumber} targets an account that is not active."),
        JournalError.Unbalanced => "Total debits must exactly equal total credits.",
        _ => "The journal entry is invalid.",
    };
}
