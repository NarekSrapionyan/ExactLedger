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
            .WithWebHostBuilder(builder => builder.UseEnvironment(environment));

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
}
