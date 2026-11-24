using el.Application.Abstractions;
using el.Application.Ledger;
using el.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using el.Application.Audit;
using el.Application.Security;
using el.Infrastructure.Security;
using el.Application.Knowledge;
using el.Application.Settlements;
using el.Application.Clearing;
using el.Application.Reporting;

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
        services.AddScoped<ISettlementStore, SettlementStore>();
        services.AddScoped<ISettlementQueries, SettlementQueries>();
        services.AddScoped<IPartyProvisioningStore, PartyProvisioningStore>();
        services.AddScoped<PartyProvisioningService>();
        services.AddSingleton<ISettlementCodec, SettlementCodec>();
        services.AddScoped<SettlementService>();
        services.AddScoped<IEscrowStore, EscrowStore>();
        services.AddScoped<EscrowService>();
        services.AddScoped<IRefundStore, RefundStore>();
        services.AddScoped<ISettlementBatchMembership, ClearingBatchMembership>();
        services.AddScoped<RefundService>();
        services.AddScoped<IClearingStore, ClearingStore>();
        services.AddScoped<ClearingService>();
        services.AddScoped<IClearingExportStore, ClearingExportStore>();
        services.AddScoped<ClearingExportService>();
        services.AddScoped<IClearingConfirmationStore, ClearingConfirmationStore>();
        services.AddScoped<ClearingConfirmationService>();
        services.AddScoped<IReportStore, ReportStore>();
        services.AddScoped<ReportService>();

        return services;
    }

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string? runtimeConnectionString) =>
        services.AddInfrastructure(_ => runtimeConnectionString);
}
