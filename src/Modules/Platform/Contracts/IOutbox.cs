using Npgsql;

namespace Wms.Modules.Platform.Contracts;

/// <summary>
/// Step 6 of the fact-processing contract (§4.3): the only sanctioned
/// channel for a cross-aggregate effect.
/// </summary>
/// <remarks>
/// Enqueued inside the caller's transaction, so a message can never be lost
/// while its cause was committed, nor delivered for something that rolled
/// back. Delivery is at-least-once, so every consumer must be idempotent.
/// </remarks>
public interface IOutbox
{
    Task EnqueueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string aggregateType,
        Guid aggregateId,
        string messageType,
        string payloadJson,
        CancellationToken cancellationToken);
}
