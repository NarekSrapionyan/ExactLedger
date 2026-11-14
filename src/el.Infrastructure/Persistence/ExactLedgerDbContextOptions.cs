using Microsoft.EntityFrameworkCore;

namespace el.Infrastructure.Persistence;

/// <summary>Shared PostgreSQL options for the runtime, tests and design-time tooling.</summary>
public static class ExactLedgerDbContextOptions
{
    /// <exception cref="InvalidOperationException">The connection string is missing.</exception>
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder builder, string? connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "The database connection string is not configured. Set ConnectionStrings:ExactLedger in user secrets "
                + "or the ConnectionStrings__ExactLedger environment variable.");
        }

        return builder.UseNpgsql(
            connectionString,
            npgsql => npgsql.MigrationsHistoryTable(ExactLedgerDbContext.MigrationsHistoryTable, ExactLedgerDbContext.Schema));
    }
}
