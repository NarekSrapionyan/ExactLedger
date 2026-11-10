using Npgsql;
using Testcontainers.PostgreSql;

namespace el.IntegrationTests.Persistence;

/// <summary>
/// One throwaway PostgreSQL container per test run. It requires Docker and fails after a startup
/// timeout instead of hanging. Each caller gets its own freshly created database, so tests never
/// share state. The container's credentials are generated test values, not real secrets.
/// </summary>
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    /// <summary>Pinned to an exact version so every machine and CI run uses the same server.</summary>
    public const string Image = "postgres:17.6-alpine";

    private const int StartupTimeoutMinutes = 3;

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image).Build();

    public async Task<string> CreateDatabaseAsync()
    {
        var name = "el_test_" + Guid.NewGuid().ToString("N");

        await using (var connection = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
    }

    public async Task InitializeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(StartupTimeoutMinutes));

        try
        {
            await _container.StartAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The PostgreSQL test container did not start within {StartupTimeoutMinutes} minutes. "
                + $"Check that Docker is running and that '{Image}' can be pulled (run: docker pull {Image}).");
        }
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "PostgreSQL";

    /// <summary>Trait value for tests that need Docker; CI skips them on Windows runners.</summary>
    public const string Category = "PostgreSQL";
}
