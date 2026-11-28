using el.Application.Abstractions;
using el.Application.Audit;
using el.Application.Ledger;
using el.Application.Settlements;
using el.Domain.Clearing;
using el.Domain.Common;
using el.Domain.Ledger;

namespace el.Application.Clearing;

public sealed record StatementResult(byte[] Content, string Sha256);

public sealed record ConfirmationResult(ClearingBatch Batch, bool Created);

public sealed record PayoutPosting(Guid InstructionId, Guid JournalEntryId, int StatementRow);

public interface IClearingConfirmationStore
{
    Task AddAsync(Guid batchId, ConfirmationMetadata metadata, IReadOnlyList<PayoutPosting> postings, CancellationToken ct);
}

/// <summary>
/// Simulated bank confirmation: only a complete exact statement match posts Dr Payable / Cr Simulated Bank Clearing
/// per instruction, consumes the reservations and confirms the batch, all in one transaction.
/// </summary>
public sealed class ClearingConfirmationService(IClearingStore store, IClearingConfirmationStore confirmations,
    ISettlementStore settlements, JournalPostingService posting, IUnitOfWork unitOfWork,
    IMonetaryOperationLock operationLock, IAuditTrail audit, TimeProvider clock)
{
    public async Task<StatementResult> SimulateAsync(Guid batchId, CancellationToken ct)
    {
        var batch = await store.FindBatchAsync(batchId, ct) ?? throw ClearingErrors.NotFound();

        if (batch.Export is null)
        {
            throw ClearingErrors.Conflict("BATCH_NOT_EXPORTED", "Export the batch before simulating its bank statement.");
        }

        var statement = StatementCsv.Simulate(ClearingSupport.Definition(batch.Currency), ClearingSupport.Expected(batch));

        return new StatementResult(statement.Content.ToArray(), statement.Sha256);
    }

    public Task<ConfirmationResult> ConfirmAsync(Guid batchId, byte[] statement, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(statement);

        var sha = ArtifactHash.Sha256Hex(statement);

        return unitOfWork.ExecuteAsync(async token =>
        {
            await operationLock.AcquireAsync(token);

            var batch = await store.FindBatchAsync(batchId, token) ?? throw ClearingErrors.NotFound();

            if (batch.Confirmation is not null)
            {
                return string.Equals(batch.Confirmation.StatementSha256, sha, StringComparison.Ordinal)
                    ? new ConfirmationResult(batch, false)
                    : throw ClearingErrors.Conflict("STATEMENT_CONFLICT", "The batch was already confirmed with a different statement.");
            }

            if (batch.Export is null)
            {
                throw ClearingErrors.Conflict("BATCH_NOT_EXPORTED", "Export the batch before confirming it.");
            }

            var parsed = StatementCsv.Parse(statement);

            if (!parsed.IsValid)
            {
                throw new ClearingException(ClearingFailure.Invalid, "STATEMENT_INVALID", "The statement is not a valid synthetic statement file.",
                    [new ClearingDetail(parsed.Error.ToString(), null, parsed.ErrorLine)]);
            }

            var match = StatementMatcher.Match(ClearingSupport.Definition(batch.Currency), ClearingSupport.Expected(batch), parsed.Rows);

            if (!match.IsMatch)
            {
                throw new ClearingException(ClearingFailure.Conflict, "STATEMENT_MISMATCH",
                    "The statement does not match every exported instruction; nothing was confirmed.",
                    match.Problems.Take(100).Select(problem => new ClearingDetail(problem.Kind.ToString(),
                        problem.Kind == StatementProblemKind.MissingInstruction ? problem.Reference : null, problem.Line)).ToArray());
            }

            var accounts = await settlements.AccountsAsync(batch.Currency, token);
            var clearing = accounts.Single(account => account.Type == AccountType.Clearing);
            var lines = parsed.Rows.ToDictionary(row => row.Reference, row => row.Line, StringComparer.Ordinal);
            var postings = new List<PayoutPosting>();

            foreach (var instruction in batch.Instructions)
            {
                var payable = accounts.Single(account => account.Type == AccountType.Payable
                    && account.PartyId == new PartyId(instruction.PartyId));
                var amount = Money.FromMinorUnits(instruction.AmountMinor, clearing.Currency);
                var entry = JournalEntry.Create(new JournalEntryId(Guid.NewGuid()),
                    JournalReference.Parse("PAYOUT:" + instruction.Reference), "Simulated payout confirmation",
                    [new JournalLineDraft(payable, EntrySide.Debit, amount), new JournalLineDraft(clearing, EntrySide.Credit, amount)]);

                await posting.PostWithinTransactionAsync(entry, token);
                postings.Add(new PayoutPosting(instruction.Id, entry.Id.Value, lines[instruction.Reference]));
            }

            await confirmations.AddAsync(batch.Id,
                new ConfirmationMetadata(sha, batch.Instructions.Count, batch.TotalMinorUnits, Now()), postings, token);
            await audit.RecordAsync("clearing.batch.confirmed", batch.Id.ToString(), token);

            return new ConfirmationResult((await store.FindBatchAsync(batch.Id, token))!, true);
        }, ct);
    }

    private DateTimeOffset Now()
    {
        var now = clock.GetUtcNow();

        return new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
    }
}
