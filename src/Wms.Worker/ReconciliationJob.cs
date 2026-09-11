using Npgsql;
using Wms.Modules.Inventory.Application;

namespace Wms.Worker;

/// <summary>
/// Runs balance reconciliation for every warehouse on a schedule (H1, §1.3).
/// </summary>
/// <remarks>
/// Lives in the Worker, not the API: it runs on the <c>wms_worker</c> role
/// with a 300-second statement timeout, and nothing that could take that long
/// may share a connection pool with scan confirmations (C7, invariant 10).
/// </remarks>
public sealed class ReconciliationJob(
    NpgsqlDataSource dataSource,
    ReconciliationService reconciliation,
    ILogger<ReconciliationJob> logger) : BackgroundService
{
    /// <summary>
    /// H1 specifies every 15 minutes: frequent enough that a divergence is
    /// caught within one shift, infrequent enough to be invisible in load.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Interval);

        do
        {
            try
            {
                await RunAllWarehousesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed run must not kill the loop. The next one starts
                // from the same watermark, so nothing is skipped by failing —
                // which is the property that makes swallowing this safe.
                logger.LogError(ex, "Reconciliation run failed; will retry on the next tick.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunAllWarehousesAsync(CancellationToken cancellationToken)
    {
        List<Guid> warehouseIds = [];
        await using (NpgsqlCommand list = dataSource.CreateCommand(
            "SELECT id FROM warehouse ORDER BY code;"))
        {
            await using NpgsqlDataReader reader =
                await list.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                warehouseIds.Add(reader.GetGuid(0));
            }
        }

        foreach (Guid warehouseId in warehouseIds)
        {
            await using NpgsqlConnection connection =
                await dataSource.OpenConnectionAsync(cancellationToken);

            ReconciliationResult result = await reconciliation.RunAsync(
                connection, warehouseId, cancellationToken);

            if (result.RunId is null)
            {
                continue;
            }

            if (result.Variances.Count > 0)
            {
                // Logged at Error because it means the ledger and the balances
                // disagree, which should never happen and is the one signal
                // this whole job exists to raise. §8 alerts on it.
                logger.LogError(
                    "Reconciliation found {VarianceCount} variance(s) in warehouse "
                    + "{WarehouseId}, run {RunId}, sequences {From}..{To}.",
                    result.Variances.Count, warehouseId, result.RunId,
                    result.FromSequence, result.ToSequence);
            }
            else
            {
                logger.LogInformation(
                    "Reconciliation checked {RowsChecked} position(s) in warehouse "
                    + "{WarehouseId} with no variance.",
                    result.RowsChecked, warehouseId);
            }
        }
    }
}
