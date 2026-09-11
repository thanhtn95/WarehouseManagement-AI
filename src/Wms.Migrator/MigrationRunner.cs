using DbUp;
using DbUp.Builder;
using DbUp.Engine;

namespace Wms.Migrator;

/// <summary>
/// Applies the embedded migration scripts to a database.
/// </summary>
/// <remarks>
/// Exposed as a callable type, not just a Main, so integration tests run the
/// same path production does — journal included. A test fixture that applied
/// the .sql files by itself would be testing a second, parallel
/// implementation of migration that nothing else uses.
/// </remarks>
public static class MigrationRunner
{
    /// <summary>
    /// Runs every migration not yet recorded in the journal, in filename
    /// order, each in its own transaction.
    /// </summary>
    public static DatabaseUpgradeResult Run(string connectionString, bool logToConsole = true)
    {
        UpgradeEngineBuilder builder = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(typeof(MigrationRunner).Assembly)
            // Per-script transactions: a failing migration rolls itself back
            // rather than leaving the schema half-changed. Note that a future
            // CREATE INDEX CONCURRENTLY migration cannot run inside a
            // transaction and will need its own handling — the
            // sql-migrations skill already calls for it to live in its own
            // file, which is the hook for that.
            .WithTransactionPerScript();

        if (logToConsole)
        {
            builder = builder.LogToConsole();
        }

        return builder.Build().PerformUpgrade();
    }
}
