using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using el.Api.Authentication;
using el.Api.Errors;
using el.Application.Security;
using el.Infrastructure.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace el.IntegrationTests;

public sealed class TokenAuthenticationTests
{
    private static readonly string Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
    private const string Issuer = "exactledger-test";
    private const string Audience = "exactledger-test-client";
    private static readonly AuthenticatedIdentity User = new("user-1", "synthetic-integrator", "Integrator", "client-1");

    [Fact]
    public void Issued_tokens_expire_in_thirty_minutes_and_carry_server_owned_identity()
    {
        var now = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
        var partyId = Guid.NewGuid();
        var issuer = new AccessTokenIssuer(Options.Create(Settings()), new FixedClock(now));
        var issued = issuer.Issue(User with { PartyId = partyId });
        var token = new JwtSecurityTokenHandler().ReadJwtToken(issued.Value);

        Assert.Equal(now.AddMinutes(30), issued.ExpiresAtUtc);
        Assert.Equal(now.UtcDateTime, token.ValidFrom);
        Assert.Equal(now.AddMinutes(30).UtcDateTime, token.ValidTo);
        Assert.Equal(Issuer, token.Issuer);
        Assert.Equal(Audience, Assert.Single(token.Audiences));
        Assert.Equal(User.Id, token.Subject);
        Assert.Equal(User.Roles.Single(), token.Claims.Single(claim => claim.Type == "role").Value);
        Assert.Equal(User.ClientId, token.Claims.Single(claim => claim.Type == "client_id").Value);
        Assert.Equal(partyId.ToString("D"), token.Claims.Single(claim => claim.Type == "party_id").Value);
        Assert.NotEqual(issued.Value, issuer.Issue(User).Value); // distinct token IDs
    }

    [Fact]
    public async Task A_missing_token_is_rejected()
    {
        using var host = CreateHost();
        using var client = host.GetTestClient();
        using var response = await client.GetAsync("/authenticated");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("UNAUTHENTICATED", problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_valid_token_authenticates_but_cannot_grant_itself_another_role()
    {
        using var host = CreateHost();
        using var client = host.GetTestClient();
        var issued = host.Services.GetRequiredService<IAccessTokenIssuer>().Issue(User);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued.Value);
        using var allowed = await client.GetAsync("/authenticated");
        using var forbidden = await client.GetAsync("/finance-test");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var problem = JsonDocument.Parse(await forbidden.Content.ReadAsStringAsync());
        Assert.Equal("FORBIDDEN", problem.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("key")]
    [InlineData("algorithm")]
    [InlineData("unsigned")]
    [InlineData("no-expiry")]
    [InlineData("not-yet-valid")]
    [InlineData("tampered-role")]
    public async Task Invalid_tokens_are_rejected_without_exposing_validation_details(string problem)
    {
        using var host = CreateHost();
        using var client = host.GetTestClient();
        var now = DateTime.UtcNow;
        var credentials = problem == "unsigned" ? null : new SigningCredentials(
            new SymmetricSecurityKey(problem == "key" ? RandomNumberGenerator.GetBytes(64) : Convert.FromBase64String(Key)),
            problem == "algorithm" ? SecurityAlgorithms.HmacSha384 : SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken(
            problem == "issuer" ? "another-issuer" : Issuer,
            problem == "audience" ? "another-audience" : Audience,
            [new Claim("sub", User.Id), new Claim("name", User.Username), new Claim("role", User.Roles.Single()), new Claim("client_id", User.ClientId!)],
            problem == "not-yet-valid" ? now.AddMinutes(5) : now.AddHours(-2),
            problem == "no-expiry" ? null : problem == "expired" ? now.AddHours(-1) : now.AddMinutes(30),
            credentials);
        var token = new JwtSecurityTokenHandler().WriteToken(jwt);
        if (problem == "tampered-role")
        {
            var parts = token.Split('.');
            parts[1] = Base64UrlEncoder.Encode(Base64UrlEncoder.Decode(parts[1]).Replace("Integrator", "FinanceOperator", StringComparison.Ordinal));
            token = string.Join('.', parts);
        }
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.GetAsync("/authenticated");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, header => header.Scheme == "Bearer" && header.Parameter is null);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("UNAUTHENTICATED", body.RootElement.GetProperty("code").GetString());
        Assert.True(body.RootElement.TryGetProperty("correlationId", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-base64-key")]
    [InlineData("c2hvcnQ=")]
    public void Missing_or_weak_signing_configuration_fails_closed(string key) =>
        Assert.Throws<OptionsValidationException>(() => CreateHost(key));

    private static JwtSettings Settings() => new() { Issuer = Issuer, Audience = Audience, SigningKey = Key };

    private static IHost CreateHost(string? key = null)
    {
        var host = new HostBuilder().ConfigureWebHost(builder => builder.UseTestServer()
            .ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{JwtSettings.SectionName}:Issuer"] = Issuer,
                [$"{JwtSettings.SectionName}:Audience"] = Audience,
                [$"{JwtSettings.SectionName}:SigningKey"] = key ?? Key,
            }))
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddApiErrorHandling();
                services.AddTokenAuthentication();
            })
            .Configure(app =>
            {
                app.UseApiErrorHandling();
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapGet("/authenticated", () => "Authenticated").RequireAuthorization();
                    // A test-only role gate, not the PRP permission matrix.
                    endpoints.MapGet("/finance-test", () => "Authorized").RequireAuthorization(policy => policy.RequireRole("FinanceOperator"));
                });
            })).Build();
        try { host.Start(); return host; }
        catch { host.Dispose(); throw; }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
