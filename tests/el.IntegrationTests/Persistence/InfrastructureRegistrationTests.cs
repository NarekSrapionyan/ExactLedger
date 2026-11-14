using el.Application.Abstractions;
using el.Infrastructure;
using el.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace el.IntegrationTests.Persistence;

public sealed class InfrastructureRegistrationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_connection_string_fails_only_when_the_database_is_first_used(string? connectionString)
    {
        using var services = new ServiceCollection().AddInfrastructure(connectionString).BuildServiceProvider();
        using var scope = services.CreateScope();

        var exception = Assert.Throws<InvalidOperationException>(
            () => scope.ServiceProvider.GetRequiredService<ExactLedgerDbContext>());

        Assert.Contains("ConnectionStrings__ExactLedger", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => InfrastructureServiceCollectionExtensions.AddInfrastructure(null!, "Host=localhost"));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddInfrastructure((Func<IServiceProvider, string?>)null!));
        Assert.Throws<ArgumentNullException>(() => ExactLedgerDbContextOptions.Configure(null!, "Host=localhost"));
    }
}

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", PostgreSqlCollection.Category)]
public sealed class ApiPersistenceTests(PostgreSqlFixture database)
{
    [Fact]
    public async Task Api_reads_the_runtime_connection_string_from_configuration()
    {
        var connectionString = await database.CreateDatabaseAsync();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:ExactLedger"] = connectionString })));

        await using var scope = factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var dbContext = scope.ServiceProvider.GetRequiredService<ExactLedgerDbContext>();

        Assert.True(await unitOfWork.ExecuteAsync(dbContext.Database.CanConnectAsync, CancellationToken.None));
    }
}
