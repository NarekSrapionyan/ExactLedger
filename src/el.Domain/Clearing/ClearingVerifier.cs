using System.Numerics;
using el.Domain.Common;
using el.Domain.Ledger;

namespace el.Domain.Clearing;

public sealed record CurrencyTotals(CurrencyCode Currency, BigInteger DebitTotal, BigInteger CreditTotal);

public sealed record SelectedEntryFacts(
    JournalEntryId EntryId, CurrencyCode Currency, bool Finalized, int LineCount, BigInteger DebitTotal, BigInteger CreditTotal);

public sealed record ClearingVerificationInput(
    CurrencyCode Currency,
    IReadOnlyList<CurrencyTotals> TrialBalance,
    IReadOnlyList<SelectedEntryFacts> Entries,
    PayoutPlan Plan,
    BigInteger PersistedInstructionTotal,
    BigInteger PersistedReservationTotal);

public static class ClearingVerifier
{
    public static ClearingVerificationReport Verify(ClearingVerificationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var findings = new List<ClearingFinding>();

        foreach (var totals in input.TrialBalance.OrderBy(item => item.Currency.ToString(), StringComparer.Ordinal))
        {
            if (totals.DebitTotal != totals.CreditTotal)
            {
                findings.Add(new ClearingFinding(ClearingFindingCode.TrialBalanceUnbalanced, totals.Currency.ToString()));
            }
        }

        findings.AddRange(input.Plan.Findings);

        var planned = input.Plan.TotalMinorUnits;

        if (input.PersistedInstructionTotal != planned || input.PersistedReservationTotal != planned)
        {
            findings.Add(new ClearingFinding(ClearingFindingCode.ReservationTotalMismatch, input.Currency.ToString()));
        }

        foreach (var entry in input.Entries.OrderBy(item => item.EntryId.ToString(), StringComparer.Ordinal))
        {
            var subject = entry.EntryId.ToString();

            if (!entry.Finalized)
            {
                findings.Add(new ClearingFinding(ClearingFindingCode.EntryNotFinalized, subject));
            }

            if (entry.LineCount < 2)
            {
                findings.Add(new ClearingFinding(ClearingFindingCode.EntryTooFewLines, subject));
            }

            if (entry.DebitTotal != entry.CreditTotal)
            {
                findings.Add(new ClearingFinding(ClearingFindingCode.EntryUnbalanced, subject));
            }

            if (entry.Currency != input.Currency)
            {
                findings.Add(new ClearingFinding(ClearingFindingCode.EntryCurrencyMismatch, subject));
            }
        }

        return new ClearingVerificationReport(findings);
    }
}
