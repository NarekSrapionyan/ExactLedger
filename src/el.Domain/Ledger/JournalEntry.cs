using System.Collections.ObjectModel;
using System.Numerics;
using el.Domain.Common;

namespace el.Domain.Ledger;

/// <summary>
/// A balanced, immutable double-entry journal entry in one currency. It can only be created
/// through <see cref="Create"/> or <see cref="FromGeneratedComponents"/>, which enforce:
/// at least two lines, positive amounts, one currency for all lines and their accounts,
/// active accounts, and debits exactly equal to credits. Atomic posting is BE-008.
/// </summary>
public sealed class JournalEntry : Entity<JournalEntryId>
{
    public const int MaxDescriptionLength = 200;

    private JournalEntry(
        JournalEntryId id,
        string description,
        CurrencyCode currency,
        ReadOnlyCollection<JournalLine> lines,
        BigInteger totalMinorUnits)
        : base(id)
    {
        Description = description;
        Currency = currency;
        Lines = lines;
        TotalMinorUnits = totalMinorUnits;
    }

    public string Description { get; }

    public CurrencyCode Currency { get; }

    /// <summary>Lines in submitted order, numbered from 1.</summary>
    public IReadOnlyList<JournalLine> Lines { get; }

    /// <summary>Total of each side (debits equal credits). Kept wide because it may exceed one Money value.</summary>
    public BigInteger TotalMinorUnits { get; }

    /// <summary>Creates an entry from explicitly submitted lines. Zero and negative lines are rejected.</summary>
    /// <exception cref="JournalValidationException">A ledger invariant is broken.</exception>
    public static JournalEntry Create(JournalEntryId id, string description, IEnumerable<JournalLineDraft> lines) =>
        Build(id, description, Snapshot(lines, nameof(lines)));

    /// <summary>
    /// Creates an entry from calculated components such as a settlement waterfall. Zero-valued
    /// components are omitted before validation; negative components are still rejected.
    /// </summary>
    /// <exception cref="JournalValidationException">A ledger invariant is broken.</exception>
    public static JournalEntry FromGeneratedComponents(
        JournalEntryId id,
        string description,
        IEnumerable<JournalLineDraft> components) =>
        Build(id, description, Snapshot(components, nameof(components)).Where(line => !line.Amount.IsZero).ToArray());

    private static JournalLineDraft[] Snapshot(IEnumerable<JournalLineDraft> lines, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(lines, parameterName);

        var snapshot = lines.ToArray();

        if (snapshot.Any(line => line is null))
        {
            throw new ArgumentException("Journal lines must not contain null.", parameterName);
        }

        return snapshot;
    }

    private static JournalEntry Build(JournalEntryId id, string description, JournalLineDraft[] drafts)
    {
        if (id == default)
        {
            throw new ArgumentException("A journal entry ID must not be empty.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(description.Length, MaxDescriptionLength, nameof(description));

        if (drafts.Length < 2)
        {
            throw new JournalValidationException(JournalError.TooFewLines);
        }

        var currency = drafts[0].Amount.Currency;
        var debits = BigInteger.Zero;
        var credits = BigInteger.Zero;
        var lines = new JournalLine[drafts.Length];

        for (var index = 0; index < drafts.Length; index++)
        {
            var draft = drafts[index];
            var lineNumber = index + 1;

            if (!draft.Amount.IsPositive)
            {
                throw new JournalValidationException(JournalError.NonPositiveAmount, lineNumber);
            }

            if (draft.Amount.Currency != currency)
            {
                throw new JournalValidationException(JournalError.MixedCurrencies, lineNumber);
            }

            if (draft.Account.Currency != currency)
            {
                throw new JournalValidationException(JournalError.AccountCurrencyMismatch, lineNumber);
            }

            if (!draft.Account.IsActive)
            {
                throw new JournalValidationException(JournalError.InactiveAccount, lineNumber);
            }

            if (draft.Side == EntrySide.Debit)
            {
                debits += draft.Amount.MinorUnits;
            }
            else
            {
                credits += draft.Amount.MinorUnits;
            }

            lines[index] = new JournalLine(lineNumber, draft.Account.Id, draft.Side, draft.Amount);
        }

        if (debits != credits)
        {
            throw new JournalValidationException(JournalError.Unbalanced);
        }

        return new JournalEntry(id, description, currency, Array.AsReadOnly(lines), debits);
    }
}
