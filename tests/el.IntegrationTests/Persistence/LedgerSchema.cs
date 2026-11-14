using el.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace el.IntegrationTests.Persistence;

/// <summary>Creates an isolated database with the ledger schema and seed data from the EF model.</summary>
internal static class LedgerSchema
{
    internal static async Task<string> CreateAsync(PostgreSqlFixture database)
    {
        var connectionString = await database.CreateDatabaseAsync();

        // Replaced by applying the migrations in BE-007.3.
        await using var context = NewContext(connectionString);
        await context.Database.EnsureCreatedAsync();

        return connectionString;
    }

    internal static ExactLedgerDbContext NewContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ExactLedgerDbContext>();
        ExactLedgerDbContextOptions.Configure(options, connectionString);

        return new ExactLedgerDbContext(options.Options);
    }
}
