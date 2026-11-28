
using System.Security.Cryptography;
using el.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace el.IntegrationTests;

internal static class ApiTestConfiguration
{
    internal static readonly string SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

    internal static IWebHostBuilder WithTestAuthentication(this IWebHostBuilder builder) =>
        builder
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{JwtSettings.SectionName}:Issuer"] = "exactledger-tests",
                [$"{JwtSettings.SectionName}:Audience"] = "exactledger-test-clients",
                [$"{JwtSettings.SectionName}:SigningKey"] = SigningKey,
                ["Clearing:Schedule:Enabled"] = "false",
                ["Escrow:Schedule:Enabled"] = "false",
            }));
}
