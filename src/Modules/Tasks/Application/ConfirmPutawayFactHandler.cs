using System.Data;
using System.Text.Json;
using Npgsql;
using Wms.Modules.Inventory.Contracts;
using Wms.Modules.Platform.Contracts;
using Wms.SharedKernel;

namespace Wms.Modules.Tasks.Application;

/// <summary>
/// The seven-step fact-processing contract (§4.3), for
/// <c>putaway_confirmed</c>.
/// </summary>
/// <remarks>
/// This is the first fact type whose movement touches <strong>two</strong>
/// stock positions — source and destination — which is why the ledger applies
/// a set of position deltas in one statement ordered by the conflict-target
/// tuple. Two operators moving stock A→B and B→A at the same moment would
/// otherwise deadlock.
///
/// Like receipt confirmation, it mutates StockPosition together with the
/// single work item it confirms against (Task), which C6 permits as a
/// structural exception for fact confirmation (§4.3).
/// </remarks>
public sealed class ConfirmPutawayFactHandler(
    IStockLedger stockLedger,
    IIdempotencyStore idempotencyStore,
    IOutbox outbox)
{
    private const string LoadLineSql = """
        SELECT tl.owner_id, tl.item_id, tl.lot_id, tl.requested_quantity,
               tl.to_location_id, tl.status, t.warehouse_id, t.id
          FROM task_line tl
          JOIN task t ON t.id = tl.task_id
         WHERE tl.id = @taskLineId
           FOR UPDATE OF tl;
        """;

    /// <summary>
    /// Both locations must exist and belong to the task's own warehouse.
    /// </summary>
    /// <remarks>
    /// Checked in one round trip rather than two. An unknown location would
    /// otherwise reach the <c>stock_balance.location_id</c> foreign key and
    /// fail the entire batch; a location in another warehouse would succeed
    /// and quietly move stock between sites.
    /// </remarks>
    private const string LoadLocationWarehousesSql = """
        SELECT id, warehouse_id FROM location WHERE id = ANY(@locationIds);
        """;

    private const string ConfirmLineSql = """
        UPDATE task_line
           SET confirmed_quantity = @quantity,
               status             = CASE WHEN @quantity < requested_quantity
                                         THEN 'short' ELSE 'confirmed' END
         WHERE id = @taskLineId;
        """;

    /// <summary>
    /// Completes the parent task only once no line is still pending.
    /// </summary>
    /// <remarks>
    /// A short line counts as finished: the operator moved what was there and
    /// the residual legitimately remains in the source location, where the
    /// ledger already shows it. Leaving the task open would send them back to
    /// a bin they have already emptied.
    /// </remarks>
    private const string CompleteTaskSql = """
        UPDATE task
           SET status       = 'completed',
               completed_at = now(),
               version      = version + 1
         WHERE id = @taskId
           AND NOT EXISTS (SELECT 1 FROM task_line
                            WHERE task_id = @taskId AND status = 'pending');
        """;

    private const string RaiseExceptionSql = """
        INSERT INTO inventory_exception (id, exception_type, severity, warehouse_id,
                                         owner_id, item_id, location_id, task_id,
                                         expected_quantity, actual_quantity,
                                         stock_movement_id, raised_at,
                                         raised_by_user_id, device_id, status)
        VALUES (@id, @exceptionType, @severity, @warehouseId,
                @ownerId, @itemId, @locationId, @taskId,
                @expectedQuantity, @actualQuantity,
                @stockMovementId, now(),
                @raisedByUserId, @deviceId, 'open');
        """;

    public async Task<FactResult> HandleAsync(
        NpgsqlConnection connection,
        PutawayConfirmedFact fact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(fact);

        // READ COMMITTED is pinned, not inherited: the idempotency claim
        // depends on per-statement snapshots (see IIdempotencyStore).
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken);

        // 1. Idempotency guard, as the first statement.
        IdempotencyClaim claim = await idempotencyStore.TryClaimAsync(
            connection, transaction, fact.ClientFactId, fact.ActorUserId,
            Hash(fact), cancellationToken);

        switch (claim.Outcome)
        {
            case IdempotencyOutcome.Replay:
                await transaction.CommitAsync(cancellationToken);
                return Replayed(claim.StoredResponseBody, fact.ClientFactId);

            case IdempotencyOutcome.Conflict:
                await transaction.RollbackAsync(cancellationToken);
                return FactResult.Rejected(FactRejection.IdempotencyKeyReused);
        }

        TaskLineContext? line = await LoadLineAsync(
            connection, transaction, fact.TaskLineId, cancellationToken);

        if (line is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return FactResult.Rejected(FactRejection.UnknownTaskLine);
        }

        FactRejection? locationProblem = await ValidateLocationsAsync(
            connection, transaction, fact, line.WarehouseId, cancellationToken);

        if (locationProblem is FactRejection rejection)
        {
            await transaction.RollbackAsync(cancellationToken);
            return FactResult.Rejected(rejection);
        }

        // 2 + 3. Both positions move, then the ledger row is appended.
        StockMovementResult movement = await stockLedger.PostAsync(
            connection,
            transaction,
            new PostMovementCommand(
                WarehouseId: line.WarehouseId,
                OwnerId: line.OwnerId,
                ItemId: line.ItemId,
                LotId: line.LotId,
                StockStatus: "available",
                FromLocationId: fact.FromLocationId,
                ToLocationId: fact.ToLocationId,
                QuantityBase: fact.Quantity,
                EnteredQuantity: fact.Quantity,
                EnteredUom: fact.Uom,
                MovementType: "putaway",
                ReasonCode: fact.ReasonCode,
                ReferenceType: "task_line",
                ReferenceId: fact.TaskLineId,
                ActorUserId: fact.ActorUserId,
                DeviceId: fact.DeviceId,
                IdempotencyKey: fact.ClientFactId,
                DeviceReportedAt: fact.OccurredAtDevice),
            cancellationToken);

        // 4. Advance the work item, then close the task if nothing is left.
        await ConfirmLineAsync(connection, transaction, fact, cancellationToken);
        await CompleteTaskAsync(connection, transaction, line.TaskId, cancellationToken);

        // 5. Divergence check. Two kinds are possible here, and neither is a
        //    refusal: the stock is already in the bin either way.
        Guid? exceptionId = null;

        if (line.Status is "confirmed" or "short")
        {
            // Someone else already confirmed this line. Both operators really
            // did move stock, so both movements are real — the second simply
            // drives the source negative, which is true information (C3).
            exceptionId = await RaiseExceptionAsync(
                connection, transaction, fact, line, movement.MovementId,
                "reallocated_task", "high", cancellationToken);
        }
        else if (line.DirectedToLocationId is Guid directed
            && directed != fact.ToLocationId)
        {
            exceptionId = await RaiseExceptionAsync(
                connection, transaction, fact, line, movement.MovementId,
                "location_mismatch", "medium", cancellationToken);
        }

        // 6. Outbox, in the same transaction as its cause.
        await outbox.EnqueueAsync(
            connection, transaction, "Task", line.TaskId, "PutawayConfirmed",
            JsonSerializer.Serialize(new
            {
                taskLineId = fact.TaskLineId,
                quantity = fact.Quantity,
                toLocationId = fact.ToLocationId,
                movementId = movement.MovementId,
                exceptionId,
            }),
            cancellationToken);

        // 7. Record the response so a replay returns exactly this.
        FactResult accepted = FactResult.Accepted(
            movement.MovementId, movement.Sequence, exceptionId);
        await idempotencyStore.CompleteAsync(
            connection, transaction, fact.ClientFactId, fact.ActorUserId,
            202, JsonSerializer.Serialize(accepted), cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return accepted;
    }

    private static FactResult Replayed(string? storedBody, string clientFactId)
    {
        FactResult? stored = storedBody is null
            ? null
            : JsonSerializer.Deserialize<FactResult>(storedBody);

        return stored is null
            ? throw new InvalidOperationException(
                $"clientFactId '{clientFactId}' was claimed but stored no response.")
            : stored with { Status = FactStatus.Duplicate };
    }

    private static async Task<TaskLineContext?> LoadLineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid taskLineId,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(LoadLineSql, connection, transaction);
        command.Parameters.AddWithValue("taskLineId", taskLineId);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new TaskLineContext(
            OwnerId: reader.GetGuid(0),
            ItemId: reader.GetGuid(1),
            LotId: reader.GetGuid(2),
            RequestedQuantity: reader.GetDecimal(3),
            DirectedToLocationId: reader.IsDBNull(4) ? null : reader.GetGuid(4),
            Status: reader.GetString(5),
            WarehouseId: reader.GetGuid(6),
            TaskId: reader.GetGuid(7));
    }

    private static async Task<FactRejection?> ValidateLocationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PutawayConfirmedFact fact,
        Guid warehouseId,
        CancellationToken cancellationToken)
    {
        Guid[] wanted = fact.FromLocationId == fact.ToLocationId
            ? [fact.FromLocationId]
            : [fact.FromLocationId, fact.ToLocationId];

        await using NpgsqlCommand command =
            new(LoadLocationWarehousesSql, connection, transaction);
        command.Parameters.AddWithValue("locationIds", wanted);

        Dictionary<Guid, Guid> warehouseByLocation = [];
        await using (NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                warehouseByLocation[reader.GetGuid(0)] = reader.GetGuid(1);
            }
        }

        foreach (Guid locationId in wanted)
        {
            if (!warehouseByLocation.TryGetValue(locationId, out Guid found))
            {
                return FactRejection.UnknownLocation;
            }

            if (found != warehouseId)
            {
                return FactRejection.LocationInAnotherWarehouse;
            }
        }

        return null;
    }

    private static async Task ConfirmLineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PutawayConfirmedFact fact,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(ConfirmLineSql, connection, transaction);
        command.Parameters.AddWithValue("taskLineId", fact.TaskLineId);
        command.Parameters.AddWithValue("quantity", fact.Quantity);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CompleteTaskAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(CompleteTaskSql, connection, transaction);
        command.Parameters.AddWithValue("taskId", taskId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Guid> RaiseExceptionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PutawayConfirmedFact fact,
        TaskLineContext line,
        Guid movementId,
        string exceptionType,
        string severity,
        CancellationToken cancellationToken)
    {
        Guid exceptionId = Guid.CreateVersion7();

        await using NpgsqlCommand command = new(RaiseExceptionSql, connection, transaction);
        command.Parameters.AddWithValue("id", exceptionId);
        command.Parameters.AddWithValue("exceptionType", exceptionType);
        command.Parameters.AddWithValue("severity", severity);
        command.Parameters.AddWithValue("warehouseId", line.WarehouseId);
        command.Parameters.AddWithValue("ownerId", line.OwnerId);
        command.Parameters.AddWithValue("itemId", line.ItemId);

        // The location recorded is where the stock ACTUALLY went — the whole
        // point of the exception is to send someone to the right bin.
        command.Parameters.AddWithValue("locationId", fact.ToLocationId);
        command.Parameters.AddWithValue("taskId", line.TaskId);
        command.Parameters.AddWithValue("expectedQuantity", line.RequestedQuantity);
        command.Parameters.AddWithValue("actualQuantity", fact.Quantity);
        command.Parameters.AddWithValue("stockMovementId", movementId);
        command.Parameters.AddWithValue("raisedByUserId", fact.ActorUserId);
        command.Parameters.AddWithValue("deviceId", (object?)fact.DeviceId ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
        return exceptionId;
    }

    private static string Hash(PutawayConfirmedFact fact) => FactHash.Of(
        fact.TaskLineId.ToString(),
        fact.FromLocationId.ToString(),
        fact.ToLocationId.ToString(),
        FactHash.Quantity(fact.Quantity),
        fact.Uom,
        fact.ReasonCode,
        fact.ActorUserId.ToString());

    private sealed record TaskLineContext(
        Guid OwnerId,
        Guid ItemId,
        Guid LotId,
        decimal RequestedQuantity,
        Guid? DirectedToLocationId,
        string Status,
        Guid WarehouseId,
        Guid TaskId);
}
