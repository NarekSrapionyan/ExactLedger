using el.Application.Abstractions;
using el.Application.Audit;
using el.Application.Knowledge;
using el.Application.Ledger;
using el.Domain.Common;
using el.Domain.Ledger;
using el.Domain.Settlements;

namespace el.Application.Settlements;

public sealed record SettledTrade(Guid Id, Guid JournalEntryId, DateTimeOffset PostedAtUtc,
    SettlementInput Input, SettlementSnapshot Snapshot, SettlementCalculation Calculation, string ResultHash);
public sealed record StoredSettlementCommand(string RequestHash, SettledTrade Settlement);
public sealed class SettlementConflictException(string code) : Exception("The settlement command conflicts with existing state.")
{ public string Code { get; } = code; }

public interface ISettlementCodec
{
    string RequestHash(SettlementInput input);
    string ResultHash(SettlementCalculation calculation);
}
public interface ISettlementStore
{
    Task<StoredSettlementCommand?> FindCommandAsync(string clientId, string key, CancellationToken ct);
    Task<bool> TradeExistsAsync(string clientId, string tradeId, CancellationToken ct);
    Task<IReadOnlyList<Account>> AccountsAsync(string currency, CancellationToken ct);
    Task EnsurePartiesActiveAsync(IReadOnlyCollection<Guid> partyIds, CancellationToken ct);
    Task AddAsync(SettledTrade settlement, string key, string requestHash, CancellationToken ct);
    Task<SettledTrade?> GetAsync(Guid id, CancellationToken ct);
}

public sealed class SettlementService(ISettlementStore store, ISettlementCodec codec, KnowledgeService knowledge,
    IUnitOfWork unitOfWork, IMonetaryOperationLock operationLock, JournalPostingService posting,
    IAuditTrail audit, TimeProvider clock)
{
    public async Task<SettledTrade> SubmitAsync(SettlementInput supplied, string key, CancellationToken ct)
    {
        var input = SettlementCalculator.Normalize(supplied);
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || !key.All(character => character is >= '!' and <= '~'))
            throw new SettlementValidationException("INVALID_IDEMPOTENCY_KEY", "Supply a visible ASCII Idempotency-Key of 1 to 128 characters.");
        var requestHash = codec.RequestHash(input);
        return await unitOfWork.ExecuteAsync(async token =>
        {
            await operationLock.AcquireAsync(token);
            var original = await store.FindCommandAsync(input.ClientId, key, token);
            if (original is not null)
            {
                if (original.RequestHash != requestHash) throw new SettlementConflictException("IDEMPOTENCY_CONFLICT");
                return original.Settlement;
            }
            if (await store.TradeExistsAsync(input.ClientId, input.TradeId, token)) throw new SettlementConflictException("TRADE_ALREADY_SETTLED");
            var selection = await knowledge.SelectWithinTransactionAsync(input.RegionCode, input.ProfileId, input.ClientId,
                input.CorporateCode, input.Currency, input.OccurredAt, token);
            var partyIds = input.Beneficiaries.Select(line => line.PartyId).Distinct().ToArray();
            await store.EnsurePartiesActiveAsync(partyIds, token);
            var configuredIds = selection.Rules.Select(rule => rule.BeneficiaryAccountId)
                .Concat([selection.Profile.CommissionAccountId, selection.Profile.ProcessorAccountId]).ToArray();
            var accounts = (await store.AccountsAsync(input.Currency, token)).Where(account =>
                account.Type == AccountType.Clearing || configuredIds.Contains(account.Id.Value) ||
                (account.PartyId is { } party && partyIds.Contains(party.Value))).ToArray();
            var snapshot = new SettlementSnapshot(selection.Profile, selection.Rules,
                Array.AsReadOnly(accounts.OrderBy(account => account.Id.Value).Select(account =>
                    new SettlementAccount(account.Id.Value, account.PartyId?.Value, account.Type, account.Currency.Value)).ToArray()),
                selection.RegionalOutcome, selection.CurrencyReferenceVersion,
                CurrencyReferenceData.Catalog.Resolve(CurrencyCode.Parse(input.Currency), selection.CurrencyReferenceVersion).Exponent,
                selection.RoundingPolicyVersion, SettlementCalculator.PolicyVersion);
            var calculation = SettlementCalculator.Calculate(input, snapshot);
            var clearingAccounts = accounts.Where(account => account.Type == AccountType.Clearing).ToArray();
            if (clearingAccounts.Length != 1) throw new SettlementValidationException("INVALID_ACCOUNT_MAPPING", "Exactly one clearing account is required.");
            var clearing = clearingAccounts[0];
            var used = calculation.Allocations.Select(row => row.AccountId).Append(clearing.Id.Value).Distinct().ToArray();
            if (accounts.Any(account => used.Contains(account.Id.Value) && !account.IsActive))
                throw new SettlementValidationException("INVALID_ACCOUNT_MAPPING", "A mapped account is inactive.");
            await store.EnsurePartiesActiveAsync(accounts.Where(account => used.Contains(account.Id.Value) && account.PartyId is not null)
                .Select(account => account.PartyId!.Value.Value).Distinct().ToArray(), token);
            var currency = CurrencyCode.Parse(input.Currency);
            var id = Guid.NewGuid(); var journalId = new JournalEntryId(Guid.NewGuid());
            var lines = new List<JournalLineDraft> { new(clearing, EntrySide.Debit, Money.FromMinorUnits(input.GrossMinor, currency)) };
            lines.AddRange(calculation.Allocations.Select(row => new JournalLineDraft(accounts.Single(account => account.Id.Value == row.AccountId),
                EntrySide.Credit, Money.FromMinorUnits(row.AmountMinor, currency))));
            var entry = JournalEntry.FromGeneratedComponents(journalId, JournalReference.Parse("SETTLEMENT:" + id.ToString("N").ToUpperInvariant()), "Captured trade settlement", lines);
            await posting.PostWithinTransactionAsync(entry, token);
            // PostgreSQL timestamps retain microseconds; normalize before the first HTTP result too.
            var now = clock.GetUtcNow();
            var postedAt = new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
            var settlement = new SettledTrade(id, journalId.Value, postedAt, input, snapshot, calculation, codec.ResultHash(calculation));
            await store.AddAsync(settlement, key, requestHash, token);
            await audit.RecordAsync("settlement.created", id.ToString(), token);
            return settlement;
        }, ct);
    }

    public bool ReplayMatches(SettledTrade settlement) => codec.ResultHash(SettlementCalculator.Calculate(settlement.Input, settlement.Snapshot)) == settlement.ResultHash;
}
