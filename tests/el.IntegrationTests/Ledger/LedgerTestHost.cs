using el.Application.Abstractions;
using el.Application.Ledger;
using el.Infrastructure;
using el.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace el.IntegrationTests.Ledger;

/// <summary>The real infrastructure registrations over one migrated, isolated database.</summary>
internal sealed class LedgerTestHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private LedgerTestHost(ServiceProvider provider, string connectionString)
    {
        _provider = provider;
        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    public static async Task<LedgerTestHost> CreateAsync(string connectionString, MonetaryOperationLockOptions? lockOptions = null)
    {
        var services = new ServiceCollection();

        if (lockOptions is not null)
        {
            services.AddSingleton(lockOptions);
        }

        services.AddInfrastructure(connectionString);

        var host = new LedgerTestHost(services.BuildServiceProvider(), connectionString);

        await using var scope = host.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ExactLedgerDbContext>().Database.MigrateAsync(); // no-op if already migrated

        return host;
    }

    public AsyncServiceScope CreateScope() => _provider.CreateAsyncScope();

    /// <summary>One independent session: its own scope, DbContext, connection and unit of work.</summary>
    public async Task RunInUnitOfWorkAsync(
        Func<IServiceProvider, CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        await using var scope = CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await unitOfWork.ExecuteAsync(
            async ct =>
            {
                await work(scope.ServiceProvider, ct);

                return true;
            },
            cancellationToken);
    }

    public async Task<T> ScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);

        return (T)(await command.ExecuteScalarAsync())!;
    }

    public async Task ExecuteAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    public Task<long> CountAsync(string table, string? where = null) =>
        ScalarAsync<long>(FormattableString.Invariant($"SELECT COUNT(*) FROM ledger.{table} {(where is null ? string.Empty : "WHERE " + where)}"));

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
    }
}
