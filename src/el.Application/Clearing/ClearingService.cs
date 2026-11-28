using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using el.Application.Abstractions;
using el.Application.Audit;
using el.Application.Ledger;
using el.Domain.Clearing;
using el.Domain.Common;

namespace el.Application.Clearing;

/// <summary>
/// The daily cut under the shared monetary lock: captures cutAt and the journal watermark, packages every unbatched
/// settlement, refund and escrow-release entry once, reserves payee payables, verifies and commits Prepared.
/// It posts nothing and debits no payable.
/// </summary>
public sealed class ClearingService(IClearingStore store, ILedgerBalanceQueries balances, IUnitOfWork unitOfWork,
    IMonetaryOperationLock operationLock, IAuditTrail audit, TimeProvider clock)
{
    public Task<ClearingPreparation> PrepareAsync(DateOnly businessDate, string? currency, CancellationToken ct)
    {
        if (!CurrencyCode.TryParse(currency, out var code)
            || !CurrencyReferenceData.Catalog.TryResolve(code, CurrencyReferenceData.InitialVersion, out _))
        {
            throw ClearingErrors.Invalid("UNSUPPORTED_CURRENCY", "Use USD, JPY or KWD.");
        }

        return unitOfWork.ExecuteAsync(async token =>
        {
            await operationLock.AcquireAsync(token);

            var now = Now();

            if (businessDate == DateOnly.MinValue || businessDate > DateOnly.FromDateTime(now.UtcDateTime))
            {
                throw ClearingErrors.Invalid("INVALID_BUSINESS_DATE", "The business date must be a past or current UTC date.");
            }

            var existing = await store.FindBatchAsync(businessDate, code.Value, token);

            if (existing is not null)
            {
                return new ClearingPreparation(ClearingOutcome.Existing, existing);
            }

            if (await store.HasUnconfirmedBatchAsync(code.Value, token))
            {
                throw ClearingErrors.Conflict("UNCONFIRMED_BATCH_EXISTS", "Confirm the unconfirmed batch of this currency before cutting another.");
            }

            var watermark = await store.LatestJournalSequenceAsync(token);
            var entries = await store.UnbatchedEntriesAsync(code.Value, watermark, token);
            var plan = PayoutPlanner.Plan(businessDate, code, await store.PayableBalancesAsync(code.Value, watermark, token));
            var trial = (await balances.GetTrialBalanceAsync(null, token))
                .Select(row => new CurrencyTotals(row.Currency, row.DebitTotal, row.CreditTotal)).ToArray();

            if (entries.Count == 0 && plan.Instructions.Count == 0)
            {
                if (plan.Findings.Count == 0)
                {
                    return new ClearingPreparation(ClearingOutcome.NothingToPrepare, null);
                }

                throw Failed(ClearingVerifier.Verify(new ClearingVerificationInput(
                    code, trial, [], plan, plan.TotalMinorUnits, plan.TotalMinorUnits)));
            }

            var batch = new NewClearingBatch(BatchId(businessDate, code), businessDate, code.Value, now, watermark, entries,
                plan.Instructions.Select(item => new PayoutInstruction(InstructionId(item.Reference), item.Reference,
                    item.PartyId.Value, item.Amount.MinorUnits)).ToArray(),
                plan.TotalMinorUnits);
            await store.AddBatchAsync(batch, token);

            var facts = await store.EntryFactsAsync(entries.Select(entry => entry.Id).ToArray(), token);
            var report = ClearingVerifier.Verify(new ClearingVerificationInput(code, trial, facts, plan,
                await store.InstructionTotalAsync(batch.Id, token), await store.ActiveReservationTotalAsync(batch.Id, token)));

            if (!report.IsValid)
            {
                // Throwing rolls back the batch, its entries and instructions: nothing Prepared, nothing reserved.
                throw Failed(report);
            }

            await audit.RecordAsync("clearing.batch.prepared", batch.Id.ToString(), token);

            return new ClearingPreparation(ClearingOutcome.Prepared, await store.FindBatchAsync(batch.Id, token));
        }, ct);
    }

    public async Task<ClearingBatch> GetAsync(Guid id, CancellationToken ct) =>
        await store.FindBatchAsync(id, ct) ?? throw ClearingErrors.NotFound();

    public Task<IReadOnlyList<ClearingBatch>> ListAsync(int offset, int limit, CancellationToken ct) =>
        store.ListBatchesAsync(offset, limit, ct);

    internal static Guid BatchId(DateOnly businessDate, CurrencyCode currency) => Derive(
        "exactledger-clearing-batch:" + businessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ":" + currency.Value);

    internal static Guid InstructionId(string reference) => Derive("exactledger-payout:" + reference);

    private static ClearingException Failed(ClearingVerificationReport report) => new(ClearingFailure.Conflict,
        "CLEARING_VERIFICATION_FAILED", "The cut failed verification; nothing was committed.",
        report.Findings.Select(finding => new ClearingDetail(finding.Code.ToString(), finding.Subject, 0)).ToArray());

    private static Guid Derive(string name) => new(SHA256.HashData(Encoding.UTF8.GetBytes(name)).AsSpan(0, 16));

    private DateTimeOffset Now()
    {
        var now = clock.GetUtcNow();

        return new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
    }
}
