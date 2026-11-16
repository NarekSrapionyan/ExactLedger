using el.Application.Security;
using el.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace el.Infrastructure.Security;

internal sealed class IdentityCredentials
{
    private readonly PasswordHasher<IdentityRecord> _hasher = new();
    private readonly IdentityRecord _dummy = new();
    private readonly string _dummyHash;

    public IdentityCredentials() => _dummyHash = _hasher.HashPassword(_dummy, Guid.NewGuid().ToString("N"));
    public string Hash(IdentityRecord identity, string password) => _hasher.HashPassword(identity, password);
    public bool Verify(IdentityRecord? identity, string password)
    {
        var result = _hasher.VerifyHashedPassword(identity ?? _dummy, identity?.PasswordHash ?? _dummyHash, password);
        return identity is not null && result != PasswordVerificationResult.Failed;
    }
}

internal sealed class DatabaseIdentityAuthenticator(ExactLedgerDbContext database, IdentityCredentials credentials) : IIdentityAuthenticator
{
    public async Task<AuthenticatedIdentity?> AuthenticateAsync(string username, string password, CancellationToken cancellationToken)
    {
        var normalized = username.Trim().ToLowerInvariant();
        var identity = await database.Set<IdentityRecord>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.Username == normalized, cancellationToken);
        return credentials.Verify(identity, password) && identity is { Enabled: true } ? identity.ToIdentity() : null;
    }
}
