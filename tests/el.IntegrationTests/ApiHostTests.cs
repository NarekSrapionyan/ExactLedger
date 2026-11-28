using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace el.IntegrationTests;

public sealed class ApiHostTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Liveness_returns_healthy_without_a_database(string environment)
    {
        await using var factory = CreateFactory(environment);
        using var client = CreateClient(factory);

        using var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Development_exposes_the_OpenApi_document()
    {
        await using var factory = CreateFactory("Development");
        using var client = CreateClient(factory);

        using var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ExactLedger API", document.RootElement.GetProperty("info").GetProperty("title").GetString());
        Assert.Equal("v1", document.RootElement.GetProperty("info").GetProperty("version").GetString());
        var paths = document.RootElement.GetProperty("paths");
        foreach (var (path, method) in new[]
        {
            ("/v1/auth/login", "post"), ("/v1/auth/me", "get"),
            ("/v1/ledger/accounts/{accountId}/balance", "get"),
            ("/v1/ledger/trial-balance", "get"), ("/v1/audit-events", "get"),
        })
        {
            var operation = paths.GetProperty(path).GetProperty(method);
            Assert.True(operation.TryGetProperty("summary", out var summary) && !string.IsNullOrWhiteSpace(summary.GetString()), path);
        }
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.Equal("Credentials for a seeded identity.", schemas.GetProperty("LoginRequest").GetProperty("description").GetString());
        Assert.Equal("Token lifetime in seconds.", schemas.GetProperty("AccessTokenResponse").GetProperty("properties")
            .GetProperty("expiresIn").GetProperty("description").GetString());
        var money = schemas.GetProperty("Money").GetProperty("properties");
        Assert.Equal(new[] { "amount", "currency" }, money.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal("string", money.GetProperty("amount").GetProperty("type").GetString());
        Assert.Equal("string", money.GetProperty("currency").GetProperty("type").GetString());
        foreach (var path in new[] { "/v1/regions", "/v1/deduction-types", "/v1/rules", "/v1/fee-profiles" })
            Assert.True(paths.GetProperty(path).GetProperty("get").TryGetProperty("summary", out _));
        var submit = paths.GetProperty("/v1/settlements").GetProperty("post");
        Assert.Contains(submit.GetProperty("parameters").EnumerateArray(), parameter =>
            parameter.GetProperty("name").GetString() == "Idempotency-Key" && parameter.GetProperty("required").GetBoolean());
        var variants = paths.GetProperty("/v1/settlements/{id}").GetProperty("get").GetProperty("responses")
            .GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("oneOf");
        Assert.Equal(2, variants.GetArrayLength());
        var refund = paths.GetProperty("/v1/settlements/{id}/refund").GetProperty("post");
        Assert.Contains(refund.GetProperty("parameters").EnumerateArray(), parameter =>
            parameter.GetProperty("name").GetString() == "Idempotency-Key" && parameter.GetProperty("required").GetBoolean());
        var statementBody = paths.GetProperty("/v1/clearing-batches/{id}/confirm-simulated").GetProperty("post")
            .GetProperty("requestBody");
        Assert.True(statementBody.GetProperty("required").GetBoolean());
        Assert.Equal("string", statementBody.GetProperty("content").GetProperty("text/csv")
            .GetProperty("schema").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Production_hides_OpenApi_and_returns_ProblemDetails_for_unknown_routes()
    {
        await using var factory = CreateFactory("Production");
        using var client = CreateClient(factory);

        using var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(404, problem.RootElement.GetProperty("status").GetInt32());
    }

    private static WebApplicationFactory<Program> CreateFactory(string environment) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment(environment).WithTestAuthentication());

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
}
