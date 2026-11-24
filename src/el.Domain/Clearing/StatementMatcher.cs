using el.Domain.Common;

namespace el.Domain.Clearing;

public enum StatementProblemKind
{
    MissingInstruction = 1,
    UnknownReference = 2,
    DuplicateReference = 3,
    CurrencyMismatch = 4,
    AmountMismatch = 5,
    NotSynthetic = 6,
}

public sealed record StatementProblem(StatementProblemKind Kind, string Reference, int Line);

public sealed record StatementMatchResult(IReadOnlyList<StatementProblem> Problems)
{
    public bool IsMatch => Problems.Count == 0;
}

public static class StatementMatcher
{
    public static StatementMatchResult Match(
        CurrencyDefinition currency, IEnumerable<ExportedInstruction> expected, IEnumerable<StatementRow> rows)
    {
        ArgumentNullException.ThrowIfNull(currency);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(rows);

        var expectedAmounts = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var instruction in expected)
        {
            ArgumentNullException.ThrowIfNull(instruction);

            if (!expectedAmounts.TryAdd(instruction.Reference, MoneyText.Format(instruction.Amount, currency)))
            {
                throw new ArgumentException("Instruction references must be unique.", nameof(expected));
            }
        }

        var code = currency.Code.ToString();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var problems = new List<StatementProblem>();

        foreach (var row in rows)
        {
            ArgumentNullException.ThrowIfNull(row);

            if (!string.Equals(row.Source, StatementCsv.SyntheticSource, StringComparison.Ordinal))
            {
                problems.Add(new StatementProblem(StatementProblemKind.NotSynthetic, row.Reference, row.Line));
            }

            if (!seen.Add(row.Reference))
            {
                problems.Add(new StatementProblem(StatementProblemKind.DuplicateReference, row.Reference, row.Line));

                continue;
            }

            if (!expectedAmounts.TryGetValue(row.Reference, out var amount))
            {
                problems.Add(new StatementProblem(StatementProblemKind.UnknownReference, row.Reference, row.Line));

                continue;
            }

            if (!string.Equals(row.Currency, code, StringComparison.Ordinal))
            {
                problems.Add(new StatementProblem(StatementProblemKind.CurrencyMismatch, row.Reference, row.Line));
            }

            if (!string.Equals(row.Amount, amount, StringComparison.Ordinal))
            {
                problems.Add(new StatementProblem(StatementProblemKind.AmountMismatch, row.Reference, row.Line));
            }
        }

        foreach (var reference in expectedAmounts.Keys.Order(StringComparer.Ordinal))
        {
            if (!seen.Contains(reference))
            {
                problems.Add(new StatementProblem(StatementProblemKind.MissingInstruction, reference, 0));
            }
        }

        return new StatementMatchResult(problems);
    }
}
