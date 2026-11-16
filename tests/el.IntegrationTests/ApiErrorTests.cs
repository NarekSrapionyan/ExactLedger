using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using el.Api.Errors;
using el.Application.Abstractions;
using el.Application.Ledger;
using el.Domain.Ledger;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace el.IntegrationTests;

public sealed class ApiErrorTests
{
    [Theory]
    [InlineData(400, "invalid_request")]
    [InlineData(401, "unauthenticated")]
    [InlineData(403, "forbidden")]
    [InlineData(404, "not_found")]
    [InlineData(409, "conflict")]
    public async Task Empty_error_responses_have_the_shared_contract_and_a_server_correlation_id(int status, string code)
    {
        using var server = CreateServer(context =>
        {
            context.Response.StatusCode = status;
            return Task.CompletedTask;
        });
        using var client = server.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "untrusted-client-value");
        using var response = await client.GetAsync("/");
        await AssertProblemAsync(response, status, code);
    }

    [Theory]
    [InlineData("lock", 409, "ledger_busy")]
    [InlineData("account", 409, "account_not_postable")]
    [InlineData("journal", 400, "LEDGER_UNBALANCED")]
    [InlineData("json", 400, "invalid_request")]
    public async Task Known_failures_have_stable_public_codes(string kind, int status, string code)
    {
        var failure = kind switch
        {
            "lock" => (Exception)new LedgerLockTimeoutException(TimeSpan.FromSeconds(10), new Exception("private database details")),
            "account" => new AccountNotPostableException(new AccountId(Guid.NewGuid()), AccountPostingProblem.NotActive),
            "journal" => new JournalValidationException(JournalError.Unbalanced),
            _ => new JsonException("private request body"),
        };
        using var server = CreateServer(_ => throw failure);
        using var client = server.GetTestClient();
        using var response = await client.GetAsync("/");
        await AssertProblemAsync(response, status, code);
    }

    [Fact]
    public async Task Unexpected_exception_details_and_credentials_are_absent_from_response_and_logs()
    {
        const string secret = "password=private-password;Bearer private-token";
        var logs = new CapturedLogs();
        using var server = CreateServer(_ => throw new InvalidOperationException(secret), logs);
        using var client = server.GetTestClient();
        using var response = await client.GetAsync("/?password=" + Uri.EscapeDataString(secret));
        await AssertProblemAsync(response, 500, "internal_error");
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        Assert.DoesNotContain(logs.Messages, message => message.Contains(secret, StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, message => message.Contains(Uri.EscapeDataString(secret), StringComparison.Ordinal));
        var correlationId = response.Headers.GetValues("X-Correlation-ID").Single();
        Assert.Contains(logs.Messages, message => message.Contains(correlationId, StringComparison.Ordinal));
    }

    private static IHost CreateServer(RequestDelegate endpoint, CapturedLogs? logs = null) =>
        new HostBuilder().ConfigureWebHost(builder => builder.UseTestServer()
            .ConfigureServices(services => services.AddApiErrorHandling())
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                if (logs is not null) logging.AddProvider(logs);
            })
            .Configure(app =>
            {
                app.UseApiErrorHandling();
                app.Run(endpoint);
            })).Start();

    private static async Task AssertProblemAsync(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(status, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("message").GetString()));
        var correlationId = body.RootElement.GetProperty("correlationId").GetString();
        Assert.True(Guid.TryParseExact(correlationId, "N", out _));
        Assert.Equal(correlationId, response.Headers.GetValues("X-Correlation-ID").Single());
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturedLogger(Messages);
        public void Dispose() { }

        private sealed class CapturedLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception) + exception);
        }
    }
}
