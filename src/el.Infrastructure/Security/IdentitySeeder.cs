using System.Security.Cryptography;
using System.Text;
using el.Application.Audit;
using el.Application.Security;
using el.Domain.Ledger;
using el.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace el.Infrastructure.Security;

internal sealed class IdentitySeeder(ExactLedgerDbContext database, IdentityCredentials credentials,
    IAuditTrail audit, AuditContext context) : IIdentitySeeder
{
    public async Task SeedAsync(string password, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        if (password.Length is < 16 or > 256) throw new ArgumentException("Choose a local seed password of 16 to 256 characters.", nameof(password));
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        context.ActorId = "identity-seeder";

        foreach (var seed in Seeds())
        {
            var existing = await database.Set<IdentityRecord>().SingleOrDefaultAsync(row => row.Username == seed.Username, cancellationToken);
            if (existing is not null && existing.Id != seed.Id)
                throw new InvalidOperationException("A seed username belongs to another identity.");
            if (existing is not null && credentials.Verify(existing, password)
                && existing.Roles.SequenceEqual(seed.Roles) && existing.ClientId == seed.ClientId && existing.PartyId == seed.PartyId)
                continue;

            if (existing is null) { existing = seed; database.Add(existing); }
            existing.PasswordHash = credentials.Hash(existing, password);
            existing.Roles = seed.Roles;
            existing.ClientId = seed.ClientId;
            existing.PartyId = seed.PartyId;
            await audit.RecordAsync("identity.seeded", existing.Id.ToString("D"), cancellationToken);
        }
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static IEnumerable<IdentityRecord> Seeds()
    {
        yield return Seed("integrator", [IdentityRoles.Integrator], "demo-client-1");
        yield return Seed("integrator-other", [IdentityRoles.Integrator], "demo-client-2");
        yield return Seed("finance", [IdentityRoles.FinanceOperator]);
        yield return Seed("admin", [IdentityRoles.ConfigurationAdministrator]);
        yield return Seed("auditor", [IdentityRoles.Auditor]);
        yield return Seed("scheduler", [IdentityRoles.Scheduler]);
        yield return Seed("payee-seller-1", [IdentityRoles.Payee], partyId: SyntheticSeed.SellerOne.Id);
        yield return Seed("payee-seller-2", [IdentityRoles.Payee], partyId: SyntheticSeed.SellerTwo.Id);
        yield return Seed("demo-admin", [IdentityRoles.ConfigurationAdministrator, IdentityRoles.FinanceOperator]);
    }

    private static IdentityRecord Seed(string username, string[] roles, string? clientId = null, PartyId? partyId = null) =>
        new()
        {
            Id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("exactledger-identity:" + username)).AsSpan(0, 16)),
            Username = username, Roles = roles, ClientId = clientId, PartyId = partyId,
        };
}
