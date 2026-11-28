using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using el.Application.Security;
using el.Infrastructure.Security;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace el.Api.Authentication;

public static class TokenAuthenticationExtensions
{
    public static IServiceCollection AddTokenAuthentication(this IServiceCollection services)
    {
        services.AddOptions<JwtSettings>().BindConfiguration(JwtSettings.SectionName)
            .Validate(settings => !string.IsNullOrWhiteSpace(settings.Issuer), "A JWT issuer is required.")
            .Validate(settings => !string.IsNullOrWhiteSpace(settings.Audience), "A JWT audience is required.")
            .Validate(settings => settings.HasValidSigningKey(), "The JWT signing key must be base64 encoding at least 32 random bytes.")
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IAccessTokenIssuer, AccessTokenIssuer>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtSettings>>((bearer, configured) =>
            {
                var settings = configured.Value;
                bearer.MapInboundClaims = false;
                bearer.IncludeErrorDetails = false;
                bearer.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        var principal = context.Principal!;
                        var roles = principal.FindAll("role").Select(claim => claim.Value).ToArray();
                        var subject = principal.FindAll("sub").ToArray();
                        var clientIds = principal.FindAll("client_id").ToArray();
                        var partyIds = principal.FindAll("party_id").ToArray();
                        if (subject.Length != 1 || string.IsNullOrWhiteSpace(subject[0].Value) || subject[0].Value.Length > 100
                            || roles.Length == 0 || roles.Any(role => !IdentityRoles.All.Contains(role, StringComparer.Ordinal))
                            || clientIds.Length > 1 || partyIds.Length > 1
                            || (roles.Contains(IdentityRoles.Integrator) && (clientIds.Length != 1 || string.IsNullOrWhiteSpace(clientIds[0].Value)))
                            || (roles.Contains(IdentityRoles.Payee) && (partyIds.Length != 1 || !Guid.TryParse(partyIds[0].Value, out var id) || id == Guid.Empty)))
                            context.Fail("Invalid identity bindings.");
                        return Task.CompletedTask;
                    },
                };
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = settings.Issuer,
                    ValidateAudience = true,
                    ValidAudience = settings.Audience,
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(settings.SigningKey)),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    RequireExpirationTime = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero,
                    NameClaimType = "name",
                    RoleClaimType = "role",
                };
            });
        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser().RequireAssertion(_ => false).Build();
            foreach (var permission in Enum.GetValues<Permission>())
                options.AddPolicy(permission.ToString(), policy => policy.RequireAuthenticatedUser()
                    .RequireRole(PermissionMatrix.RolesFor(permission)));
        });
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "local",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        return services;
    }
}
