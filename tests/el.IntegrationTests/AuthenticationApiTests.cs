using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using el.Api.Authentication;
using el.Application.Security;
using el.Application.Ledger;
using el.Domain.Common;
using el.Domain.Ledger;
using el.Infrastructure;
using el.Infrastructure.Persistence;
using el.IntegrationTests.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace el.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", PostgreSqlCollection.Category)]
public sealed class AuthenticationApiTests(PostgreSqlFixture database)
{
    [Fact]
    public async Task Login_uses_hashed_seeded_credentials_and_ignores_requested_role_or_owner()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var hash = await host.ScalarAsync<string>("SELECT password_hash FROM ledger.identities WHERE username = 'integrator'");
        Assert.NotEqual(IdentityApiHost.Password, hash);
        using var response = await host.Client.PostAsJsonAsync("/v1/auth/login", new
        {
            username = "integrator", password = IdentityApiHost.Password, roles = new[] { "FinanceOperator" },
            clientId = "someone-else", partyId = Guid.NewGuid(),
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var result = (await response.Content.ReadFromJsonAsync<AccessTokenResponse>())!;
        var token = new JwtSecurityTokenHandler().ReadJwtToken(result.AccessToken);
        Assert.Equal("Integrator", Assert.Single(token.Claims, claim => claim.Type == "role").Value);
        Assert.Equal("demo-client-1", token.Claims.Single(claim => claim.Type == "client_id").Value);
        Assert.DoesNotContain(token.Claims, claim => claim.Type == "party_id");
        Assert.Equal(1800, result.ExpiresIn);
        Assert.Equal(TimeSpan.FromMinutes(30), token.ValidTo - token.ValidFrom);
    }

    [Fact]
    public async Task Unknown_wrong_and_disabled_credentials_share_the_same_public_error()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        foreach (var (username, password) in new[] { ("not-a-user", IdentityApiHost.Password), ("finance", "incorrect") })
        {
            using var response = await host.Client.PostAsJsonAsync("/v1/auth/login", new { username, password });
            await AssertErrorAsync(response, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        }
        await host.ExecuteAsync("UPDATE ledger.identities SET enabled = false WHERE username = 'finance'");
        using var disabled = await host.Client.PostAsJsonAsync("/v1/auth/login", new { username = "finance", password = IdentityApiHost.Password });
        await AssertErrorAsync(disabled, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
    }

    [Fact]
    public async Task Login_validation_includes_field_errors_without_echoing_credentials()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        using var response = await host.Client.PostAsJsonAsync("/v1/auth/login", new { username = "", password = "" });
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "INVALID_REQUEST");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("errors").TryGetProperty("username", out _));
        Assert.True(body.RootElement.GetProperty("errors").TryGetProperty("password", out _));
    }

    [Theory]
    [InlineData("/v1/auth/login")]
    [InlineData("/v1/auth/login/")]
    [InlineData("/V1/AUTH/LOGIN")]
    public async Task Login_body_limit_cannot_be_bypassed_by_route_spelling(string path)
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var body = JsonSerializer.Serialize(new { username = "finance", password = new string('x', 5000) });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        content.Headers.ContentLength = Encoding.UTF8.GetByteCount(body);
        using var response = await host.Client.PostAsync(path, content);
        await AssertErrorAsync(response, HttpStatusCode.RequestEntityTooLarge, "REQUEST_REJECTED");
    }

    [Fact]
    public async Task Login_rate_limiting_has_the_same_structured_error_contract()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        HttpResponseMessage? last = null;
        try
        {
            for (var i = 0; i < 21; i++)
            {
                last?.Dispose();
                last = await host.Client.PostAsJsonAsync("/v1/auth/login", new { username = "", password = "" });
            }
            await AssertErrorAsync(last!, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        }
        finally { last?.Dispose(); }
    }

    [Fact]
    public async Task Real_ledger_and_audit_endpoints_enforce_the_distinct_PRP_role_grants()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        foreach (var username in new[] { "integrator", "finance", "admin", "payee-seller-1", "auditor", "scheduler", "demo-admin" })
        {
            using var ledger = await host.GetAsAsync(username, "/v1/ledger/trial-balance");
            using var audit = await host.GetAsAsync(username, "/v1/audit-events");
            Assert.Equal(username is "finance" or "auditor" or "demo-admin" ? HttpStatusCode.OK : HttpStatusCode.Forbidden, ledger.StatusCode);
            Assert.Equal(username is "finance" or "admin" or "auditor" or "demo-admin" ? HttpStatusCode.OK : HttpStatusCode.Forbidden, audit.StatusCode);
        }
        using var anonymous = await host.Client.GetAsync("/v1/ledger/trial-balance");
        await AssertErrorAsync(anonymous, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    public async Task Payee_ownership_is_checked_against_the_stored_account_not_caller_parameters()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var owned = SyntheticSeed.Accounts.Single(account => account.Code.Value == "PAYABLE.SELLER-1.USD").Id.Value;
        var foreign = SyntheticSeed.Accounts.Single(account => account.Code.Value == "PAYABLE.SELLER-2.USD").Id.Value;
        using var ownBalance = await host.GetAsAsync("payee-seller-1", $"/v1/ledger/accounts/{owned}/balance");
        Assert.Equal(HttpStatusCode.OK, ownBalance.StatusCode);
        using var body = JsonDocument.Parse(await ownBalance.Content.ReadAsStringAsync());
        Assert.Equal("0.00", body.RootElement.GetProperty("creditTotal").GetProperty("amount").GetString());
        using var denied = await host.GetAsAsync("payee-seller-1", $"/v1/ledger/accounts/{foreign}/balance?partyId={SyntheticSeed.SellerOne.Id.Value}");
        await AssertErrorAsync(denied, HttpStatusCode.NotFound, "NOT_FOUND");
        using var finance = await host.GetAsAsync("finance", $"/v1/ledger/accounts/{foreign}/balance");
        Assert.Equal(HttpStatusCode.OK, finance.StatusCode);
    }

    [Fact]
    public async Task Balance_uses_the_stored_account_normal_side_without_client_arithmetic()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var clearing = SyntheticSeed.Accounts.Single(account => account.Code.Value == "CLEARING.BANK.USD");
        var payable = SyntheticSeed.Accounts.Single(account => account.Code.Value == "PAYABLE.SELLER-1.USD");
        await using var provider = new ServiceCollection().AddInfrastructure(host.RuntimeConnection).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var posting = scope.ServiceProvider.GetRequiredService<JournalPostingService>();
        foreach (var (debit, credit, amount, expected) in new[]
        {
            (clearing, payable, 18445L, "184.45"),
            (payable, clearing, 20000L, "-15.55"),
        })
        {
            var money = Money.FromMinorUnits(amount, CurrencyReferenceData.Usd);
            await posting.PostAsync(JournalEntry.Create(new JournalEntryId(Guid.NewGuid()),
                JournalReference.Parse("BE009:BAL:" + amount), "Synthetic balance test",
                [new(debit, EntrySide.Debit, money), new(credit, EntrySide.Credit, money)]), CancellationToken.None);
            using var response = await host.GetAsAsync("payee-seller-1", $"/v1/ledger/accounts/{payable.Id.Value}/balance");
            response.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("Credit", body.RootElement.GetProperty("normalSide").GetString());
            Assert.Equal(expected, body.RootElement.GetProperty("balance").GetProperty("amount").GetString());
        }
        using var debitResponse = await host.GetAsAsync("finance", $"/v1/ledger/accounts/{clearing.Id.Value}/balance");
        using var debitBody = JsonDocument.Parse(await debitResponse.Content.ReadAsStringAsync());
        Assert.Equal("Debit", debitBody.RootElement.GetProperty("normalSide").GetString());
        Assert.Equal("-15.55", debitBody.RootElement.GetProperty("balance").GetProperty("amount").GetString());
    }

    [Fact]
    public async Task OpenApi_declares_bearer_authentication_and_an_anonymous_login_contract()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        using var response = await host.Client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("bearer", root.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer").GetProperty("scheme").GetString());
        Assert.Equal(0, root.GetProperty("paths").GetProperty("/v1/auth/login").GetProperty("post").GetProperty("security").GetArrayLength());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("security")[0].GetProperty("Bearer").ValueKind);
        var errorSchema = root.GetProperty("components").GetProperty("schemas").GetProperty("ProblemDetails");
        var schemas = root.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.TryGetProperty("MeResponse", out _));
        Assert.True(schemas.TryGetProperty("AuditEventResponse", out _));
        Assert.False(schemas.TryGetProperty("AuthenticatedIdentity", out _));
        Assert.False(schemas.TryGetProperty("AuditEvent", out _));
        foreach (var field in new[] { "code", "message", "correlationId" })
        {
            Assert.Equal("string", errorSchema.GetProperty("properties").GetProperty(field).GetProperty("type").GetString());
            Assert.Contains(errorSchema.GetProperty("required").EnumerateArray(), item => item.GetString() == field);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Production_rejects_plain_HTTP_without_redirect_even_when_HTTPS_port_is_configured(bool login)
    {
        await using var host = await IdentityApiHost.CreateAsync(database, "Production");
        using var request = new HttpRequestMessage(login ? HttpMethod.Post : HttpMethod.Get,
            login ? "http://localhost/v1/auth/login" : "http://localhost/v1/ledger/trial-balance");
        if (login) request.Content = JsonContent.Create(new { username = "finance", password = IdentityApiHost.Password });
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await host.TokenAsync("finance"));
        using var response = await host.Client.SendAsync(request);
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "HTTPS_REQUIRED");
        Assert.Null(response.Headers.Location);
        using var health = await host.Client.GetAsync("http://localhost/health/live");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("correlationId").GetString()));
    }
}

internal sealed class IdentityApiHost : IAsyncDisposable
{
    internal const string Password = "synthetic_test_password_for_BE009";
    private readonly Dictionary<string, string> _tokens = new(StringComparer.Ordinal);
    internal WebApplicationFactory<Program> Factory { get; }
    internal HttpClient Client { get; }
    internal string AdminConnection { get; }
    internal string RuntimeConnection { get; }

    private IdentityApiHost(WebApplicationFactory<Program> factory, string admin, string runtime)
    {
        Factory = factory; AdminConnection = admin; RuntimeConnection = runtime;
        Client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
    }

    internal static async Task<IdentityApiHost> CreateAsync(PostgreSqlFixture database, string environment = "Development")
    {
        var admin = await LedgerSchema.CreateAsync(database);
        await using (var provider = new ServiceCollection().AddInfrastructure(admin).BuildServiceProvider())
        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IIdentitySeeder>().SeedAsync(Password, CancellationToken.None);
        var runtime = await LedgerSchema.CreateRuntimeLoginAsync(admin);
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment(environment)
            .WithTestAuthentication().ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:ExactLedger"] = runtime, ["https_port"] = "443" })));
        return new IdentityApiHost(factory, admin, runtime);
    }

    internal async Task<string> TokenAsync(string username)
    {
        if (_tokens.TryGetValue(username, out var existing)) return existing;
        using var response = await Client.PostAsJsonAsync("/v1/auth/login", new { username, password = Password });
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<AccessTokenResponse>())!.AccessToken;
        _tokens[username] = token;
        return token;
    }

    internal async Task<HttpResponseMessage> GetAsAsync(string username, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(username));
        return await Client.SendAsync(request);
    }

    internal async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    internal async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(AdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
        // Each test owns a separate database/login pool. Like LedgerTestHost,
        // release idle connections before the next test reaches PostgreSQL's limit.
        NpgsqlConnection.ClearAllPools();
    }
}
