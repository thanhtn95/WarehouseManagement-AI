using DbUp.Engine;
using Npgsql;
using Testcontainers.PostgreSql;
using Wms.Migrator;
using Xunit;

namespace Wms.IntegrationTests.Infrastructure;

/// <summary>
/// A real PostgreSQL instance with the project's migrations applied.
/// One container per test class, per the testcontainers-concurrency skill —
/// SKIP LOCKED, advisory locks and partition routing do not exist in SQLite
/// or the EF in-memory provider, and those are the mechanisms carrying this
/// system's correctness burden.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("wms")
        .Build();

    private NpgsqlDataSource? _dataSource;

    public NpgsqlDataSource DataSource =>
        _dataSource ?? throw new InvalidOperationException("Fixture not initialised.");

    /// <summary>
    /// The container's connection string, for tests that need to hand it to
    /// a host rather than open connections themselves.
    /// </summary>
    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ApplyMigrations();
        _dataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    /// <summary>
    /// Migrates the container through the real migrator.
    /// </summary>
    /// <remarks>
    /// This deliberately calls <see cref="MigrationRunner"/> rather than
    /// reading the .sql files itself. A fixture that applied them directly
    /// would be a second, parallel implementation of migration that nothing
    /// else uses — and it would pass even if the migrator, the embedded
    /// resources, or the journal were broken.
    /// </remarks>
    private void ApplyMigrations()
    {
        DatabaseUpgradeResult result = MigrationRunner.Run(
            _container.GetConnectionString(),
            logToConsole: false);

        if (!result.Successful)
        {
            throw new InvalidOperationException(
                $"Migrations failed, so no test in this class can be trusted: {result.Error}");
        }
    }
}
