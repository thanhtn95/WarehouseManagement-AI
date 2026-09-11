using Npgsql;
using Xunit;

namespace Wms.IntegrationTests.Infrastructure;

/// <summary>
/// Proves the fixture migrates through the real migrator rather than
/// applying the .sql files itself.
///
/// Without this, "the fixture uses DbUp" is an assertion about code that
/// nothing checks — and a well-meaning change back to direct file
/// application would keep every other test green while silently removing
/// the journal, the ordering DbUp enforces, and any coverage of the
/// migrator itself.
/// </summary>
public sealed class MigrationJournalTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Fixture_MigratesThroughDbUp_LeavingAPopulatedJournal()
    {
        await using NpgsqlCommand journalExists = fixture.DataSource.CreateCommand(
            "SELECT to_regclass('public.schemaversions') IS NOT NULL;");
        bool exists = (bool)(await journalExists.ExecuteScalarAsync())!;

        Assert.True(
            exists,
            "No schemaversions table: the fixture applied migrations without DbUp, so the "
            + "migrator and its journal are untested.");

        await using NpgsqlCommand scripts = fixture.DataSource.CreateCommand(
            "SELECT scriptname FROM schemaversions ORDER BY scriptname;");

        List<string> applied = [];
        await using NpgsqlDataReader reader = await scripts.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            applied.Add(reader.GetString(0));
        }

        // Every migration in the repository should be journalled. Comparing
        // against the file count catches a script that silently failed to
        // embed — an empty resource set would otherwise "migrate" a database
        // to nothing and pass.
        int migrationFileCount = Directory
            .GetFiles(Path.Combine(RepositoryRoot(), "db", "migrations"), "*.sql")
            .Length;

        Assert.Equal(migrationFileCount, applied.Count);
        Assert.All(applied, name => Assert.StartsWith("Migrations.", name, StringComparison.Ordinal));
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "db", "migrations")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate db/migrations walking up from {AppContext.BaseDirectory}.");
    }
}
