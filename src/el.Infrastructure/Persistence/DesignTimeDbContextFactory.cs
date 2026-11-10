using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace el.Infrastructure.Persistence;

/// <summary>
/// Used only by the dotnet-ef tool. Applying migrations uses the schema-owning migration role
/// from the ConnectionStrings__ExactLedgerMigrations environment variable. Adding a migration
/// does not connect, so a placeholder is used when the variable is not set.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ExactLedgerDbContext>
{
    public ExactLedgerDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "ConnectionStrings__" + InfrastructureServiceCollectionExtensions.MigrationConnectionStringName);

        var options = new DbContextOptionsBuilder<ExactLedgerDbContext>();
        ExactLedgerDbContextOptions.Configure(
            options,
            string.IsNullOrWhiteSpace(connectionString) ? "Host=localhost;Database=exactledger_design_time" : connectionString);

        return new ExactLedgerDbContext(options.Options);
    }
}
