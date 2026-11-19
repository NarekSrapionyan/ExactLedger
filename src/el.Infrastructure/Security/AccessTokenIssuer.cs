using System.Security.Claims;
using el.Application.Security;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace el.Infrastructure.Security;

public sealed class AccessTokenIssuer(IOptions<JwtSettings> options, TimeProvider clock) : IAccessTokenIssuer
{
    public IssuedAccessToken Issue(AuthenticatedIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Username);
        if (identity.Roles.Count == 0 || identity.Roles.Any(role => !IdentityRoles.All.Contains(role, StringComparer.Ordinal)))
            throw new ArgumentException("At least one recognized identity role is required.", nameof(identity));

        var settings = options.Value;
        var now = DateTimeOffset.FromUnixTimeSeconds(clock.GetUtcNow().ToUnixTimeSeconds());
        var expires = now.AddMinutes(30);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, identity.Id),
            new(JwtRegisteredClaimNames.Name, identity.Username),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        claims.AddRange(identity.Roles.Distinct(StringComparer.Ordinal).Select(role => new Claim("role", role)));
        if (identity.ClientId is not null) claims.Add(new("client_id", identity.ClientId));
        if (identity.PartyId is not null) claims.Add(new("party_id", identity.PartyId.Value.ToString("D")));

        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Convert.FromBase64String(settings.SigningKey)), SecurityAlgorithms.HmacSha256),
        });
        return new IssuedAccessToken(token, expires);
    }
}
