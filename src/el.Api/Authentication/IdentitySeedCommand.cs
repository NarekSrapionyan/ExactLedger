using el.Application.Security;
using el.Infrastructure;

namespace el.Api.Authentication;

internal static class IdentitySeedCommand
{
    internal static async Task RunAsync(IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString(InfrastructureServiceCollectionExtensions.MigrationConnectionStringName);
        if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("The trusted migration connection is required to seed identities.");
        var password = configuration["Authentication:SeedPassword"];
        if (string.IsNullOrEmpty(password)) throw new InvalidOperationException("Authentication:SeedPassword is required.");
        await using var provider = new ServiceCollection().AddInfrastructure(connection).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IIdentitySeeder>().SeedAsync(password, CancellationToken.None);
    }
}
