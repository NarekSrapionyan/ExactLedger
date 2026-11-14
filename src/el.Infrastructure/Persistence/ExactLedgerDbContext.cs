using Microsoft.EntityFrameworkCore;

namespace el.Infrastructure.Persistence;

/// <summary>
/// The ExactLedger database. All tables live in the <see cref="Schema"/> schema and are mapped by
/// IEntityTypeConfiguration classes in Persistence/Configurations.
/// </summary>
public sealed class ExactLedgerDbContext : DbContext
{
    public const string Schema = "ledger";

    public const string MigrationsHistoryTable = "__ef_migrations_history";

    public ExactLedgerDbContext(DbContextOptions<ExactLedgerDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ExactLedgerDbContext).Assembly);
    }
}
