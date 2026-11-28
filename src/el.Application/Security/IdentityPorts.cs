namespace el.Application.Security;

public sealed record IssuedAccessToken(string Value, DateTimeOffset ExpiresAtUtc);

public interface IAccessTokenIssuer
{
    IssuedAccessToken Issue(AuthenticatedIdentity identity);
}

public interface IIdentityAuthenticator
{
    Task<AuthenticatedIdentity?> AuthenticateAsync(string username, string password, CancellationToken cancellationToken);
}

/// <summary>Trusted deployment/CLI operation, never an HTTP account-registration endpoint.</summary>
public interface IIdentitySeeder
{
    Task SeedAsync(string password, CancellationToken cancellationToken);
}
