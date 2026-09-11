namespace Wms.Modules.Tasks.Contracts;

/// <summary>
/// Task-queue figures for the admin dashboard (§5.12).
/// </summary>
public interface ITaskReadModel
{
    Task<PutawaySummary> GetPutawaySummaryAsync(
        Guid warehouseId, CancellationToken cancellationToken);
}

/// <param name="TasksLeased">
/// Held by a device right now. A number that keeps climbing while
/// TasksCompletedToday does not is how an abandoned handheld shows up before
/// anyone reports it.
/// </param>
public sealed record PutawaySummary(
    int TasksReady,
    int TasksLeased,
    int TasksCompletedToday);
