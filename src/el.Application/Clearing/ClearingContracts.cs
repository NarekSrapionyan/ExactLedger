using System.Numerics;
using el.Domain.Clearing;
using el.Domain.Common;

namespace el.Application.Clearing;

public enum ClearingBatchState { Prepared = 1, Exported = 2, Confirmed = 3 }

public enum ClearingFailure { Invalid, Conflict, NotFound }

public sealed record ClearingDetail(string Code, string? Subject, int Line);

public sealed class ClearingException(ClearingFailure failure, string code, string message,
    IReadOnlyList<ClearingDetail>? details = null) : Exception(message)
{
    public ClearingFailure Failure { get; } = failure;

    public string Code { get; } = code;

    public IReadOnlyList<ClearingDetail>? Details { get; } = details;
}

internal static class ClearingErrors
{
    internal static ClearingException NotFound() => new(ClearingFailure.NotFound, "NOT_FOUND", "The clearing batch was not found.");

    internal static ClearingException Conflict(string code, string message) => new(ClearingFailure.Conflict, code, message);

    internal static ClearingException Invalid(string code, string message) => new(ClearingFailure.Invalid, code, message);
}

internal static class ClearingSupport
{
    internal static CurrencyDefinition Definition(string currency) =>
        CurrencyReferenceData.Catalog.Resolve(CurrencyCode.Parse(currency), CurrencyReferenceData.InitialVersion);

    internal static IReadOnlyList<ExportedInstruction> Expected(ClearingBatch batch)
    {
        var currency = CurrencyCode.Parse(batch.Currency);

        return batch.Instructions
            .Select(item => new ExportedInstruction(item.Reference, Money.FromMinorUnits(item.AmountMinor, currency))).ToArray();
    }
}

public static class ClearingOutcome
{
    public const string Prepared = "Prepared";
    public const string Existing = "Existing";
    public const string NothingToPrepare = "NothingToPrepare";
}

public sealed record CandidateEntry(Guid Id, string Kind, long Sequence);

public sealed record PayoutInstruction(Guid Id, string Reference, Guid PartyId, long AmountMinor);

public sealed record ExportMetadata(string Sha256, int RecordCount, BigInteger TotalMinorUnits, DateTimeOffset GeneratedAtUtc);

public sealed record ConfirmationMetadata(string StatementSha256, int InstructionCount, BigInteger TotalMinorUnits, DateTimeOffset ConfirmedAtUtc);

/// <summary>State is derived from rows: Exported once an export row exists, Confirmed once a confirmation row exists.</summary>
public sealed record ClearingBatch(Guid Id, DateOnly BusinessDate, string Currency, DateTimeOffset CutAtUtc, long CutSequence,
    int EntryCount, BigInteger TotalMinorUnits, IReadOnlyList<PayoutInstruction> Instructions,
    ExportMetadata? Export, ConfirmationMetadata? Confirmation)
{
    public ClearingBatchState State => Confirmation is not null ? ClearingBatchState.Confirmed
        : Export is not null ? ClearingBatchState.Exported : ClearingBatchState.Prepared;
}

public sealed record NewClearingBatch(Guid Id, DateOnly BusinessDate, string Currency, DateTimeOffset CutAtUtc, long CutSequence,
    IReadOnlyList<CandidateEntry> Entries, IReadOnlyList<PayoutInstruction> Instructions, BigInteger TotalMinorUnits);

public sealed record ClearingPreparation(string Outcome, ClearingBatch? Batch);

public interface IClearingStore
{
    Task<ClearingBatch?> FindBatchAsync(Guid id, CancellationToken ct);

    Task<ClearingBatch?> FindBatchAsync(DateOnly businessDate, string currency, CancellationToken ct);

    Task<IReadOnlyList<ClearingBatch>> ListBatchesAsync(int offset, int limit, CancellationToken ct);

    Task<bool> HasUnconfirmedBatchAsync(string currency, CancellationToken ct);

    /// <summary>Highest finalized journal sequence; call under the monetary lock.</summary>
    Task<long> LatestJournalSequenceAsync(CancellationToken ct);

    Task<IReadOnlyList<CandidateEntry>> UnbatchedEntriesAsync(string currency, long watermark, CancellationToken ct);

    Task<IReadOnlyList<PartyPayable>> PayableBalancesAsync(string currency, long watermark, CancellationToken ct);

    Task<IReadOnlyList<SelectedEntryFacts>> EntryFactsAsync(IReadOnlyCollection<Guid> entryIds, CancellationToken ct);

    Task AddBatchAsync(NewClearingBatch batch, CancellationToken ct);

    Task<BigInteger> InstructionTotalAsync(Guid batchId, CancellationToken ct);

    Task<BigInteger> ActiveReservationTotalAsync(Guid batchId, CancellationToken ct);
}
