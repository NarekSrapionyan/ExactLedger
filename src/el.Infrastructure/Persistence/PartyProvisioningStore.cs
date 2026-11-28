using el.Application.Knowledge;
using el.Domain.Common;
using el.Domain.Ledger;
using Microsoft.EntityFrameworkCore;

namespace el.Infrastructure.Persistence;

internal sealed class RegisteredPartyRow
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Status { get; set; }
    public bool PayoutEligible { get; set; }
}

internal sealed class PartyProvisioningStore(ExactLedgerDbContext db) : IPartyProvisioningStore
{
    public async Task<RegisteredParty?> FindAsync(Guid id, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<RegisteredPartyRow>($"""
            SELECT p.id AS "Id", p.name AS "Name", p.status AS "Status",
                   EXISTS(SELECT 1 FROM ledger.payout_parties pp WHERE pp.party_id = p.id) AS "PayoutEligible"
            FROM ledger.parties p WHERE p.id = {id}
            """).ToListAsync(ct);
        return rows.Count == 0 ? null : Map(rows[0]);
    }

    public async Task<IReadOnlyList<RegisteredParty>> ListAsync(int offset, int limit, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<RegisteredPartyRow>($"""
            SELECT p.id AS "Id", p.name AS "Name", p.status AS "Status",
                   EXISTS(SELECT 1 FROM ledger.payout_parties pp WHERE pp.party_id = p.id) AS "PayoutEligible"
            FROM ledger.parties p ORDER BY p.name, p.id LIMIT {limit} OFFSET {offset}
            """).ToListAsync(ct);
        return rows.Select(Map).ToArray();
    }

    public async Task<IReadOnlyList<ProvisionedAccount>> AccountsAsync(Guid? partyId, string? currency, int offset, int limit, CancellationToken ct)
    {
        var query = db.Set<Account>().AsNoTracking();
        if (partyId is { } id) { var party = new PartyId(id); query = query.Where(a => a.PartyId == party); }
        if (currency is not null) { var code = CurrencyCode.Parse(currency); query = query.Where(a => a.Currency == code); }
        return (await query.OrderBy(a => a.Code).ThenBy(a => a.Id).Skip(offset).Take(limit).ToListAsync(ct))
            .Select(a => new ProvisionedAccount(a.Id.Value, a.Code.Value, a.Type, a.Currency.Value, a.PartyId?.Value, a.Status)).ToArray();
    }

    public async Task AddAsync(Party party, bool payoutEligible, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Registration requires a transaction.");
        db.Add(party);
        foreach (var currency in CurrencyReferenceData.Catalog.Definitions.Select(d => d.Code).Distinct())
        foreach (var type in new[] { AccountType.Payable, AccountType.Escrow })
        {
            var prefix = type == AccountType.Payable ? "PAYABLE" : "ESCROW";
            var code = AccountCode.Parse(prefix + "." + party.Id.Value.ToString("N").ToUpperInvariant() + "." + currency.Value);
            db.Add(new Account(new AccountId(Guid.NewGuid()), code, type, currency, party.Id));
        }
        await db.SaveChangesAsync(ct);
        if (payoutEligible)
            await db.Database.ExecuteSqlAsync($"INSERT INTO ledger.payout_parties (party_id) VALUES ({party.Id.Value})", ct);
    }

    private static RegisteredParty Map(RegisteredPartyRow row) => new(row.Id, row.Name, (PartyStatus)row.Status, row.PayoutEligible);
}
