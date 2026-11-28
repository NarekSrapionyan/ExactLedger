using el.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace el.IntegrationTests.Persistence;

/// <summary>
/// Creates an isolated database by applying all migrations to an empty database, as the
/// container's administrative user (standing in for the schema-owning migration role).
/// </summary>
internal static class LedgerSchema
{
    internal static async Task<string> CreateAsync(PostgreSqlFixture database)
    {
        var connectionString = await database.CreateDatabaseAsync();

        await using var context = NewContext(connectionString);
        await context.Database.MigrateAsync();

        return connectionString;
    }

    /// <summary>A login user that only has the exactledger_runtime privileges.</summary>
    internal static async Task<string> CreateRuntimeLoginAsync(string adminConnectionString)
    {
        var user = "el_runtime_" + Guid.NewGuid().ToString("N")[..12];
        var password = Guid.NewGuid().ToString("N");

        await Sql.ExecuteAsync(
            adminConnectionString,
            $"CREATE ROLE {user} LOGIN PASSWORD '{password}' IN ROLE {LedgerDatabase.RuntimeRole}");

        return new NpgsqlConnectionStringBuilder(adminConnectionString) { Username = user, Password = password }.ConnectionString;
    }

    internal static ExactLedgerDbContext NewContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ExactLedgerDbContext>();
        ExactLedgerDbContextOptions.Configure(options, connectionString);

        return new ExactLedgerDbContext(options.Options);
    }
}
