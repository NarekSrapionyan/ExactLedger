using el.Application.Abstractions;
using el.Infrastructure;
using el.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace el.IntegrationTests.Persistence;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", PostgreSqlCollection.Category)]
public sealed class UnitOfWorkTests(PostgreSqlFixture database)
{
    [Fact]
    public async Task Commits_the_work_and_returns_its_result()
    {
        var connectionString = await CreateDatabaseWithScratchTableAsync();
        await using var services = Build(connectionString);
        await using var scope = services.CreateAsyncScope();
        var (unitOfWork, dbContext) = Resolve(scope);

        var result = await unitOfWork.ExecuteAsync(async cancellationToken =>
        {
            await dbContext.Database.ExecuteSqlRawAsync("INSERT INTO scratch (id) VALUES (1)", cancellationToken);
            return 42;
        }, CancellationToken.None);

        Assert.Equal(42, result);
        Assert.Equal(1L, await Sql.ScalarAsync<long>(connectionString, "SELECT count(*) FROM scratch"));
        Assert.Null(dbContext.Database.CurrentTransaction);
    }

    [Fact]
    public async Task Rolls_back_everything_when_the_work_fails()
    {
        var connectionString = await CreateDatabaseWithScratchTableAsync();
        await using var services = Build(connectionString);
        await using var scope = services.CreateAsyncScope();
        var (unitOfWork, dbContext) = Resolve(scope);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.ExecuteAsync<int>(async cancellationToken =>
        {
            await dbContext.Database.ExecuteSqlRawAsync("INSERT INTO scratch (id) VALUES (1)", cancellationToken);
            throw new InvalidOperationException("Simulated failure before commit.");
        }, CancellationToken.None));

        Assert.Equal("Simulated failure before commit.", exception.Message);
        Assert.Equal(0L, await Sql.ScalarAsync<long>(connectionString, "SELECT count(*) FROM scratch"));
        Assert.Null(dbContext.Database.CurrentTransaction);

        // The scope stays usable after a failed unit of work.
        await unitOfWork.ExecuteAsync(
            cancellationToken => dbContext.Database.ExecuteSqlRawAsync("INSERT INTO scratch (id) VALUES (2)", cancellationToken),
            CancellationToken.None);
        Assert.Equal(2, await Sql.ScalarAsync<int>(connectionString, "SELECT id FROM scratch"));
    }

    [Fact]
    public async Task Units_of_work_do_not_nest()
    {
        var connectionString = await CreateDatabaseWithScratchTableAsync();
        await using var services = Build(connectionString);
        await using var scope = services.CreateAsyncScope();
        var (unitOfWork, dbContext) = Resolve(scope);

        await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.ExecuteAsync(async cancellationToken =>
        {
            await dbContext.Database.ExecuteSqlRawAsync("INSERT INTO scratch (id) VALUES (1)", cancellationToken);
            return await unitOfWork.ExecuteAsync(_ => Task.FromResult(0), cancellationToken);
        }, CancellationToken.None));

        Assert.Equal(0L, await Sql.ScalarAsync<long>(connectionString, "SELECT count(*) FROM scratch"));
    }

    [Fact]
    public async Task Each_fixture_database_is_isolated()
    {
        var first = await CreateDatabaseWithScratchTableAsync();
        var second = await database.CreateDatabaseAsync();

        await Sql.ExecuteAsync(first, "INSERT INTO scratch (id) VALUES (1)");

        Assert.NotEqual(first, second);
        Assert.False(await Sql.ScalarAsync<bool>(
            second, "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'scratch')"));
    }

    [Fact]
    public async Task Uses_the_ledger_schema_and_its_own_migrations_history_table()
    {
        await using var services = Build(await database.CreateDatabaseAsync());
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ExactLedgerDbContext>();

        Assert.True(await dbContext.Database.CanConnectAsync());
        Assert.Equal(ExactLedgerDbContext.Schema, dbContext.Model.GetDefaultSchema());
        Assert.Empty(await dbContext.Database.GetAppliedMigrationsAsync());
    }

    private static ServiceProvider Build(string connectionString) =>
        new ServiceCollection().AddInfrastructure(connectionString).BuildServiceProvider(validateScopes: true);

    private static (IUnitOfWork UnitOfWork, ExactLedgerDbContext DbContext) Resolve(AsyncServiceScope scope) =>
        (scope.ServiceProvider.GetRequiredService<IUnitOfWork>(), scope.ServiceProvider.GetRequiredService<ExactLedgerDbContext>());

    private async Task<string> CreateDatabaseWithScratchTableAsync()
    {
        var connectionString = await database.CreateDatabaseAsync();
        await Sql.ExecuteAsync(connectionString, "CREATE TABLE scratch (id integer PRIMARY KEY)");
        return connectionString;
    }
}
