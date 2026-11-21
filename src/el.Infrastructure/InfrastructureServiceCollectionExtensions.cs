using el.Application.Abstractions;
using el.Application.Ledger;
using el.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using el.Application.Audit;
using el.Application.Security;
using el.Infrastructure.Security;
using el.Application.Knowledge;

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
        services.AddScoped<IJournalEntryStore, JournalEntryStore>();

        services.AddScoped<IMonetaryOperationLock, MonetaryOperationLock>();
        services.AddScoped<IJournalAccountVerifier, JournalAccountVerifier>();
        services.AddScoped<ILedgerBalanceQueries, LedgerBalanceQueries>();
        services.AddScoped<JournalPostingService>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped(_ => new AuditContext("system", Guid.NewGuid().ToString("N")));
        services.AddScoped<EfAuditTrail>();
        services.AddScoped<IAuditTrail>(provider => provider.GetRequiredService<EfAuditTrail>());
        services.AddScoped<IAuditQueries>(provider => provider.GetRequiredService<EfAuditTrail>());
        services.AddSingleton<IdentityCredentials>();
        services.AddScoped<IIdentityAuthenticator, DatabaseIdentityAuthenticator>();
        services.AddScoped<IIdentitySeeder, IdentitySeeder>();
        services.AddScoped<IKnowledgeStore, KnowledgeStore>();
        services.AddScoped<KnowledgeService>();

        return services;
    }

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string? runtimeConnectionString) =>
        services.AddInfrastructure(_ => runtimeConnectionString);
}
