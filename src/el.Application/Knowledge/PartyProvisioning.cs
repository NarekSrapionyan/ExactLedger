using el.Application.Abstractions;
using el.Application.Audit;
using el.Domain.Ledger;

namespace el.Application.Knowledge;

public sealed record RegisteredParty(Guid Id, string Name, PartyStatus Status, bool PayoutEligible);
public sealed record ProvisionedAccount(Guid Id, string Code, AccountType Type, string Currency, Guid? PartyId, AccountStatus Status);
public sealed record PartyRegistration(RegisteredParty Party, bool Created);

public interface IPartyProvisioningStore
{
    Task<RegisteredParty?> FindAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<RegisteredParty>> ListAsync(int offset, int limit, CancellationToken ct);
    Task<IReadOnlyList<ProvisionedAccount>> AccountsAsync(Guid? partyId, string? currency, int offset, int limit, CancellationToken ct);
    Task AddAsync(Party party, bool payoutEligible, CancellationToken ct);
}

public sealed class PartyProvisioningService(IPartyProvisioningStore store, IUnitOfWork unitOfWork,
    IMonetaryOperationLock operationLock, IAuditTrail audit)
{
    public Task<PartyRegistration> RegisterAsync(Guid id, string? name, bool payoutEligible, CancellationToken ct)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(name) || name.Length > Party.MaxNameLength || name.Any(char.IsControl))
            throw new KnowledgeException(KnowledgeFailure.Invalid, "INVALID_PARTY", "Supply a non-empty ID and a name of 1 to 200 characters.");
        var party = new Party(new PartyId(id), name.Trim());
        return unitOfWork.ExecuteAsync(async token =>
        {
            await operationLock.AcquireAsync(token);
            var existing = await store.FindAsync(id, token);
            if (existing is not null)
            {
                if (existing.Name != party.Name || existing.PayoutEligible != payoutEligible)
                    throw new KnowledgeException(KnowledgeFailure.Conflict, "PARTY_CONFLICT", "This ID is already registered with different content.");
                return new PartyRegistration(existing, false);
            }
            await store.AddAsync(party, payoutEligible, token);
            await audit.RecordAsync("party.registered", id.ToString("D"), token);
            return new PartyRegistration(new(id, party.Name, party.Status, payoutEligible), true);
        }, ct);
    }
}
