using el.Api.Errors;
using el.Application.Security;

namespace el.Api.Authentication;

public sealed record LoginRequest(string? Username, string? Password);
public sealed record AccessTokenResponse(string AccessToken, string TokenType, int ExpiresIn, DateTimeOffset ExpiresAtUtc);

public static class AuthenticationEndpoints
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/v1/auth/login", LoginAsync).WithName("Login").AllowAnonymous().RequireRateLimiting("login")
            .Produces<AccessTokenResponse>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(429)
            .WithTags("Authentication").WithSummary("Authenticate a provisioned educational identity.");
        endpoints.MapGet("/v1/auth/me", (HttpContext context) => Results.Ok(RequestIdentity.Get(context)))
            .RequireAuthorization().WithTags("Authentication")
            .WithSummary("Return the authenticated identity and its server-assigned bindings.");
        return endpoints;
    }

    private static async Task<IResult> LoginAsync(LoginRequest request, HttpContext context,
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
}
