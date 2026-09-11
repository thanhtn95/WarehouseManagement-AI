using Npgsql;

namespace Wms.Modules.Inventory.Contracts;

/// <summary>
/// The only way another module may change stock quantities.
/// </summary>
/// <remarks>
/// Deliberately takes the caller's connection and transaction. Fact
/// confirmation is one of exactly two transactions permitted to mutate more
/// than one aggregate (C6), and that only holds if the ledger write is
/// genuinely inside the caller's transaction — an implementation that
/// opened its own connection would silently turn one atomic operation into
/// two that can half-fail.
/// </remarks>
public interface IStockLedger
{
    Task<StockMovementResult> PostAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostMovementCommand command,
        CancellationToken cancellationToken);
}

/// <summary>
/// A single physical movement of stock. <paramref name="QuantityBase"/> is
/// signed: positive brings stock into a position, negative removes it.
/// </summary>
public sealed record PostMovementCommand(
    Guid WarehouseId,
    Guid OwnerId,
    Guid ItemId,
    Guid LotId,
    string StockStatus,
    Guid? FromLocationId,
    Guid? ToLocationId,
    decimal QuantityBase,
    decimal EnteredQuantity,
    string EnteredUom,
    string MovementType,
    string? ReasonCode,
    string? ReferenceType,
    Guid? ReferenceId,
    Guid ActorUserId,
    Guid? DeviceId,
    string? IdempotencyKey,
    DateTimeOffset? DeviceReportedAt = null);

/// <summary>
/// What the ledger recorded, and the balance that resulted.
/// </summary>
/// <remarks>
/// <paramref name="OnHandAfter"/> may be negative. That is true information —
/// the system's belief was wrong — and the caller's job is to raise an
/// exception record, not to reject the movement.
/// </remarks>
public sealed record StockMovementResult(Guid MovementId, long Sequence, decimal OnHandAfter);
