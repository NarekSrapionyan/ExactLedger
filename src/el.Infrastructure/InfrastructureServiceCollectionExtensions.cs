using el.Application.Abstractions;
using el.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace el.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>Connection string name for the least-privileged runtime role.</summary>
    public const string RuntimeConnectionStringName = "ExactLedger";

    /// <summary>Connection string name for the schema-owning migration role (design time only).</summary>
    public const string MigrationConnectionStringName = "ExactLedgerMigrations";

    /// <summary>
    /// Registers persistence. The connection string is resolved when a DbContext is first created,
    /// so hosts that never use the database, such as liveness checks, start without one.
    /// </summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        Func<IServiceProvider, string?> runtimeConnectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(runtimeConnectionString);

        services.AddDbContext<ExactLedgerDbContext>(
            (provider, options) => ExactLedgerDbContextOptions.Configure(options, runtimeConnectionString(provider)));
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();

        return services;
    }

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string? runtimeConnectionString) =>
        services.AddInfrastructure(_ => runtimeConnectionString);
}
