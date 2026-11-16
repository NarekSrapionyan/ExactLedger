using el.Api.Errors;
using el.Application.Security;

namespace el.Api.Authentication;

/// <summary>Credentials for a seeded identity.</summary>
/// <param name="Username">Seeded username.</param>
/// <param name="Password">Identity password.</param>
public sealed record LoginRequest(string? Username, string? Password);
/// <summary>A bearer token valid for 30 minutes.</summary>
/// <param name="AccessToken">Signed JWT.</param>
/// <param name="TokenType">Bearer.</param>
/// <param name="ExpiresIn">Token lifetime in seconds.</param>
/// <param name="ExpiresAtUtc">Expiry time in UTC.</param>
public sealed record AccessTokenResponse(string AccessToken, string TokenType, int ExpiresIn, DateTimeOffset ExpiresAtUtc);

public static class AuthenticationEndpoints
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/v1/auth/login", LoginAsync).WithName("Login").AllowAnonymous().RequireRateLimiting("login")
            .Produces<AccessTokenResponse>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(429)
            .WithTags("Authentication");
        endpoints.MapGet("/v1/auth/me", Me)
            .RequireAuthorization().Produces<AuthenticatedIdentity>().ProducesProblem(401).WithTags("Authentication");
        return endpoints;
    }

    /// <summary>Sign in and receive a 30-minute bearer token.</summary>
    public static async Task<IResult> LoginAsync(LoginRequest request, HttpContext context,
        IIdentityAuthenticator identities, IAccessTokenIssuer tokens, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Username) || request.Username.Length > 64)
            errors["username"] = ["A username of at most 64 characters is required."];
        if (string.IsNullOrEmpty(request.Password) || request.Password.Length > 256)
            errors["password"] = ["A password of at most 256 characters is required."];
        if (errors.Count != 0) return ApiErrorResponses.Validation(context, errors);

        var identity = await identities.AuthenticateAsync(request.Username!, request.Password!, cancellationToken);
        if (identity is null)
        {
            return Results.Problem(ApiErrorResponses.Create(context, 401, "invalid_credentials"));
        }
        var token = tokens.Issue(identity);
        return Results.Ok(new AccessTokenResponse(token.Value, "Bearer", 1800, token.ExpiresAtUtc));
    }

    /// <summary>Read your identity, roles and ownership bindings.</summary>
    public static IResult Me(HttpContext context) => Results.Ok(RequestIdentity.Get(context));
}
