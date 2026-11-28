using el.Application.Abstractions;
using el.Domain.Ledger;
using el.Application.Audit;

namespace el.Application.Ledger;

/// <summary>
/// Posts an already validated journal entry: all lines or none, under the shared monetary lock.
/// Order inside the transaction: lock, re-check account status, write and finalize the entry.
/// </summary>
public sealed class JournalPostingService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IMonetaryOperationLock _monetaryLock;
    private readonly IJournalAccountVerifier _accountVerifier;
    private readonly IJournalEntryStore _journalEntries;
    private readonly IAuditTrail _audit;

    public JournalPostingService(
        IUnitOfWork unitOfWork,
        IMonetaryOperationLock monetaryLock,
        IJournalAccountVerifier accountVerifier,
        IJournalEntryStore journalEntries,
        IAuditTrail audit)
    {
        _unitOfWork = unitOfWork;
        _monetaryLock = monetaryLock;
        _accountVerifier = accountVerifier;
        _journalEntries = journalEntries;
        _audit = audit;
    }

    /// <summary>
    /// Posts one entry in its own unit of work. Units of work do not nest, so call
    /// <see cref="PostWithinTransactionAsync"/> instead when one is already running.
    /// A duplicate reference is rejected by the database and rolls everything back.
    /// </summary>
    /// <exception cref="InvalidOperationException">A unit of work is already running in this scope.</exception>
    public async Task PostAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await _unitOfWork.ExecuteAsync(
            async ct =>
            {
                await PostWithinTransactionAsync(entry, ct);

                return entry.Id;
            },
            cancellationToken);
    }

    /// <summary>
    /// For workflows that already run inside a unit of work (settlement, release, refund, confirmation).
    /// </summary>
    public async Task PostWithinTransactionAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await _monetaryLock.AcquireAsync(cancellationToken);

        var accountIds = entry.Lines.Select(line => line.AccountId).Distinct().ToArray();
        await _accountVerifier.EnsurePostableAsync(accountIds, cancellationToken);

        await _journalEntries.AddAsync(entry, cancellationToken);
        await _audit.RecordAsync("journal.posted", entry.Id.ToString(), cancellationToken);
    }
}
