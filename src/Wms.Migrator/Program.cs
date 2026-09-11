using DbUp.Engine;
using Wms.Migrator;

// Connection string from the first argument, or WMS_DB_CONNECTION.
// Nothing is read from a config file: this runs as a one-shot container
// whose only job is to migrate and exit, and a config file is one more
// thing that can be stale in the image.
string? connectionString = args.Length > 0
    ? args[0]
    : Environment.GetEnvironmentVariable("WMS_DB_CONNECTION");

if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine(
        "No connection string. Pass it as the first argument or set WMS_DB_CONNECTION.");
    return 2;
}

DatabaseUpgradeResult result = MigrationRunner.Run(connectionString);

if (!result.Successful)
{
    // Exit non-zero so the deploy job fails here, with the API containers
    // untouched (§11). A migrator that reports success on failure is worse
    // than no migrator.
    Console.Error.WriteLine(result.Error);
    return 1;
}

Console.WriteLine($"Migration complete. Scripts applied this run: {result.Scripts.Count()}.");
return 0;
