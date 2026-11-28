namespace el.Domain.Clearing;

public enum ClearingFindingCode
{
    TrialBalanceUnbalanced = 1,
    NegativePayable = 2,
    NegativeAvailability = 3,
    PayoutAmountOutOfRange = 4,
    ReservationTotalMismatch = 5,
    EntryNotFinalized = 6,
    EntryTooFewLines = 7,
    EntryUnbalanced = 8,
    EntryCurrencyMismatch = 9,
}

public sealed record ClearingFinding(ClearingFindingCode Code, string Subject);

public sealed record ClearingVerificationReport(IReadOnlyList<ClearingFinding> Findings)
{
    public bool IsValid => Findings.Count == 0;
}
