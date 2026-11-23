using System.Numerics;
using el.Domain.Common;
using el.Domain.Ledger;

namespace el.Domain.Clearing;

public sealed record PartyPayable(PartyId PartyId, BigInteger CreditTotal, BigInteger DebitTotal, BigInteger ActiveReservations)
{
    public BigInteger Balance => CreditTotal - DebitTotal;

    public BigInteger Available => Balance - ActiveReservations;
}

public sealed record PayoutInstructionDraft(string Reference, PartyId PartyId, Money Amount);

public sealed record PayoutPlan(IReadOnlyList<PayoutInstructionDraft> Instructions, IReadOnlyList<ClearingFinding> Findings)
{
    public BigInteger TotalMinorUnits =>
        Instructions.Aggregate(BigInteger.Zero, (total, instruction) => total + instruction.Amount.MinorUnits);
}

public static class PayoutPlanner
{
    public static PayoutPlan Plan(DateOnly businessDate, CurrencyCode currency, IEnumerable<PartyPayable> payables)
    {
        ArgumentNullException.ThrowIfNull(currency);
        ArgumentNullException.ThrowIfNull(payables);

        var snapshot = payables.ToArray();

        if (snapshot.Any(payable => payable is null))
        {
            throw new ArgumentException("Payables must not contain null.", nameof(payables));
        }

        var ordered = snapshot.OrderBy(payable => payable.PartyId.Value.ToString("D"), StringComparer.Ordinal).ToArray();

        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index].PartyId == ordered[index - 1].PartyId)
            {
                throw new ArgumentException("A party must appear once per currency.", nameof(payables));
            }
        }

        var instructions = new List<PayoutInstructionDraft>();
        var findings = new List<ClearingFinding>();

        foreach (var payable in ordered)
        {
            var subject = payable.PartyId.Value.ToString("D");

            if (payable.Balance.Sign < 0)
            {
                findings.Add(new ClearingFinding(ClearingFindingCode.NegativePayable, subject));
            }
            else if (payable.Available.Sign < 0)
            {
                findings.Add(new ClearingFinding(ClearingFindingCode.NegativeAvailability, subject));
            }
            else if (payable.Available > Money.MaxMinorUnits)
            {
                findings.Add(new ClearingFinding(ClearingFindingCode.PayoutAmountOutOfRange, subject));
            }
            else if (payable.Available.Sign > 0)
            {
                instructions.Add(new PayoutInstructionDraft(
                    PayoutReference.Create(businessDate, currency, instructions.Count + 1),
                    payable.PartyId,
                    Money.FromMinorUnits((long)payable.Available, currency)));
            }
        }

        return new PayoutPlan(instructions, findings);
    }
}
