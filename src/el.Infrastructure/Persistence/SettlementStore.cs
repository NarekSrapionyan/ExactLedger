using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using el.Application.Settlements;
using el.Application.Abstractions;
using el.Domain.Common;
using el.Domain.Ledger;
using el.Domain.Settlements;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace el.Infrastructure.Persistence;

internal sealed class SettlementCodec : ISettlementCodec
{
    internal static readonly JsonSerializerOptions Options = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
    public string RequestHash(SettlementInput input) => Hash(SettlementCalculator.Normalize(input));
    public string ResultHash(SettlementCalculation calculation) => Hash(calculation);
    private static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Options))));
}

internal sealed class SettlementStore(ExactLedgerDbContext db, MonetaryOperationLockOptions? options = null) : ISettlementStore
{
    public async Task<StoredSettlementCommand?> FindCommandAsync(string clientId, string key, CancellationToken ct)
    {
        var command = await db.Set<SettlementCommandRecord>().AsNoTracking().SingleOrDefaultAsync(row =>
            row.ClientId == clientId && row.Operation == "settlement.create" && row.Key == key, ct);
        if (command is null) return null;
        return new(command.RequestHash, (await GetAsync(command.SettlementId, ct))
            ?? throw new InvalidOperationException("A retained command has no settlement."));
    }
    public Task<bool> TradeExistsAsync(string clientId, string tradeId, CancellationToken ct) =>
        db.Set<SettlementRecord>().AnyAsync(row => row.ClientId == clientId && row.TradeId == tradeId, ct);
    public async Task<IReadOnlyList<Account>> AccountsAsync(string currency, CancellationToken ct)
    {
        var code = CurrencyCode.Parse(currency);
        return await db.Set<Account>().AsNoTracking().Where(account => account.Currency == code).ToListAsync(ct);
    }
    public async Task EnsurePartiesActiveAsync(IReadOnlyCollection<Guid> partyIds, CancellationToken ct)
    {
        var ids = partyIds.Distinct().ToArray();
        List<AccountStatusRow> rows;
        try
        {
            // Keep provisioning changes from suspending a party between validation and commit.
            rows = await db.Database.SqlQuery<AccountStatusRow>($"""
                SELECT id AS "Id", status AS "Status" FROM ledger.parties WHERE id = ANY({ids}) ORDER BY id FOR SHARE
                """).ToListAsync(ct);
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.LockNotAvailable)
        { throw new LedgerLockTimeoutException((options ?? new MonetaryOperationLockOptions()).WaitTimeout, error); }
        if (rows.Count != ids.Length || rows.Any(row => row.Status != (int)PartyStatus.Active))
            throw new SettlementValidationException("INVALID_PARTY", "All participants must be known and active.");
    }
    public Task AddAsync(SettledTrade s, string key, string requestHash, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Settlement writes require a transaction.");
        db.Add(new SettlementRecord { Id = s.Id, ClientId = s.Input.ClientId, TradeId = s.Input.TradeId, Currency = s.Input.Currency,
            GrossMinor = s.Input.GrossMinor, JournalEntryId = s.JournalEntryId, OccurredAt = s.Input.OccurredAt, PostedAtUtc = s.PostedAtUtc,
            InputJson = JsonSerializer.Serialize(s.Input, SettlementCodec.Options), SnapshotJson = JsonSerializer.Serialize(s.Snapshot, SettlementCodec.Options),
            ResultJson = JsonSerializer.Serialize(s.Calculation, SettlementCodec.Options), ResultHash = s.ResultHash });
        for (var ordinal = 0; ordinal < s.Calculation.Allocations.Count; ordinal++)
        {
            var row = s.Calculation.Allocations[ordinal];
            db.Add(new SettlementAllocationRecord { SettlementId = s.Id, Ordinal = ordinal, LineId = row.LineId, PartyId = row.PartyId,
                AccountId = row.AccountId, Currency = s.Input.Currency, Component = row.Component, AmountMinor = row.AmountMinor });
            if (row.Component == "Escrow" && row.AmountMinor > 0)
                db.Add(new EscrowHoldRecord { Id = Guid.NewGuid(), SettlementId = s.Id, AllocationOrdinal = ordinal, PartyId = row.PartyId!.Value,
                    EscrowAccountId = row.AccountId, PayableAccountId = s.Snapshot.Accounts.Single(account => account.PartyId == row.PartyId && account.Type == AccountType.Payable).Id,
                    Currency = s.Input.Currency, OriginalHeldMinor = row.AmountMinor, DueAtUtc = s.Input.OccurredAt.AddDays(s.Snapshot.Profile.HoldDays) });
        }
        db.Add(new SettlementCommandRecord { ClientId = s.Input.ClientId, Key = key, RequestHash = requestHash, SettlementId = s.Id });
        return Task.CompletedTask;
    }
    public async Task<SettledTrade?> GetAsync(Guid id, CancellationToken ct)
    {
        var row = await db.Set<SettlementRecord>().AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct);
        return row is null ? null : new(row.Id, row.JournalEntryId, row.PostedAtUtc,
            JsonSerializer.Deserialize<SettlementInput>(row.InputJson, SettlementCodec.Options)!,
            JsonSerializer.Deserialize<SettlementSnapshot>(row.SnapshotJson, SettlementCodec.Options)!,
            JsonSerializer.Deserialize<SettlementCalculation>(row.ResultJson, SettlementCodec.Options)!, row.ResultHash);
    }
}
