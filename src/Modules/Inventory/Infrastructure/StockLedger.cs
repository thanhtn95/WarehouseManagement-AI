using Npgsql;
using NpgsqlTypes;
using Wms.Modules.Inventory.Contracts;

namespace Wms.Modules.Inventory.Infrastructure;

/// <inheritdoc />
public sealed class StockLedger(IBusinessCalendar businessCalendar) : IStockLedger
{

    /// <summary>
    /// One atomic upsert covering <em>every</em> position the movement
    /// touches — never a SELECT followed by an application-computed UPDATE.
    /// Postgres takes a row lock for the duration of this single statement,
    /// and that is what actually serialises concurrent writers (C6).
    /// </summary>
    /// <remarks>
    /// There is deliberately no guard clause: the goods have already
    /// physically moved, so a <c>WHERE on_hand &gt;= qty</c> would make the
    /// statement fail and force the client to handle a rejection it cannot
    /// act on. A transfer's source is allowed to go negative — a negative
    /// balance is true information (C1, C3).
    ///
    /// The <c>ORDER BY</c> is load-bearing, not cosmetic. A transfer locks
    /// two <c>stock_balance</c> rows, and two operators moving stock A→B and
    /// B→A at the same moment would otherwise take them in opposite orders
    /// and deadlock (SQLSTATE 40P01) mid-shift. Ordering the feeding subplan
    /// by the conflict-target tuple gives every writer in the system one
    /// acquisition order.
    /// </remarks>
    private const string UpsertBalancesSql = """
        INSERT INTO stock_balance (owner_id, item_id, location_id, lot_id, stock_status,
                                   on_hand, allocated, version, updated_at)
        SELECT @ownerId, @itemId, d.location_id, @lotId, @stockStatus,
               d.quantity, 0, 0, now()
          FROM unnest(@locationIds, @quantities) AS d(location_id, quantity)
         ORDER BY d.location_id
        ON CONFLICT (owner_id, item_id, location_id, lot_id, stock_status)
        DO UPDATE SET on_hand    = stock_balance.on_hand + EXCLUDED.on_hand,
                      version    = stock_balance.version + 1,
                      updated_at = now()
        RETURNING location_id, on_hand;
        """;

    private const string InsertMovementSql = """
        INSERT INTO stock_movement (id, recorded_at, device_reported_at, business_date,
                                    owner_id, item_id, lot_id, stock_status,
                                    from_location_id, to_location_id,
                                    quantity_base, entered_quantity, entered_uom,
                                    movement_type, reason_code,
                                    reference_type, reference_id,
                                    actor_user_id, device_id, idempotency_key)
        VALUES (@id, now(), @deviceReportedAt, @businessDate,
                @ownerId, @itemId, @lotId, @stockStatus,
                @fromLocationId, @toLocationId,
                @quantityBase, @enteredQuantity, @enteredUom,
                @movementType, @reasonCode,
                @referenceType, @referenceId,
                @actorUserId, @deviceId, @idempotencyKey)
        RETURNING sequence;
        """;

    public async Task<StockMovementResult> PostAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostMovementCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(command);

        DateOnly businessDate = await businessCalendar.ResolveAsync(
            connection, transaction, command.WarehouseId, cancellationToken);

        // Every position the movement touches, not just one. A receipt has
        // only a destination and an adjustment-out only a source, but a
        // putaway or a transfer has both (§6.3) — decrementing the source is
        // not optional, and omitting it duplicates stock in a way only
        // reconciliation would ever notice.
        Dictionary<Guid, decimal> deltas = new();
        if (command.ToLocationId is Guid destination)
        {
            deltas[destination] = command.QuantityBase;
        }

        if (command.FromLocationId is Guid source)
        {
            // Summed rather than assigned, so a move whose source and
            // destination are the same location nets to a single zero-delta
            // row. Two rows sharing a conflict target would make Postgres
            // refuse the whole statement.
            deltas[source] = deltas.GetValueOrDefault(source) - command.QuantityBase;
        }

        if (deltas.Count == 0)
        {
            throw new ArgumentException(
                "A movement needs a source or a destination location.", nameof(command));
        }

        Guid positionLocationId = command.ToLocationId ?? command.FromLocationId!.Value;
        IReadOnlyDictionary<Guid, decimal> onHandByLocation = await UpsertBalancesAsync(
            connection, transaction, command, deltas, cancellationToken);

        // The caller asked about the position the stock came to rest in.
        decimal onHandAfter = onHandByLocation[positionLocationId];

        Guid movementId = Guid.CreateVersion7();
        long sequence = await InsertMovementAsync(
            connection, transaction, command, movementId, businessDate, cancellationToken);

        return new StockMovementResult(movementId, sequence, onHandAfter);
    }

    private static async Task<IReadOnlyDictionary<Guid, decimal>> UpsertBalancesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostMovementCommand movement,
        Dictionary<Guid, decimal> deltas,
        CancellationToken cancellationToken)
    {
        Guid[] locationIds = [.. deltas.Keys];
        decimal[] quantities = [.. locationIds.Select(id => deltas[id])];

        await using NpgsqlCommand command = new(UpsertBalancesSql, connection, transaction);
        command.Parameters.AddWithValue("ownerId", movement.OwnerId);
        command.Parameters.AddWithValue("itemId", movement.ItemId);
        command.Parameters.AddWithValue("lotId", movement.LotId);
        command.Parameters.AddWithValue("stockStatus", movement.StockStatus);
        command.Parameters.AddWithValue("locationIds", locationIds);
        command.Parameters.AddWithValue("quantities", quantities);

        Dictionary<Guid, decimal> onHandByLocation = [];
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                onHandByLocation[reader.GetGuid(0)] = reader.GetDecimal(1);
            }
        }

        // RETURNING yields one row per affected position. Anything less means
        // a delta was silently dropped, which is the failure this method was
        // rewritten to make impossible — fail loudly rather than commit half
        // a transfer.
        return onHandByLocation.Count == deltas.Count
            ? onHandByLocation
            : throw new InvalidOperationException(
                $"Expected {deltas.Count} balance rows, got {onHandByLocation.Count}.");
    }

    private static async Task<long> InsertMovementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostMovementCommand movement,
        Guid movementId,
        DateOnly businessDate,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(InsertMovementSql, connection, transaction);
        command.Parameters.AddWithValue("id", movementId);

        // Forensic only. `recorded_at` is the server clock and is what
        // ordering and reporting use (C4, invariant 5) — this answers "what
        // did the handheld think the time was", which is the question asked
        // when a device's clock turns out to be wrong.
        command.Parameters.AddWithValue(
            "deviceReportedAt", (object?)movement.DeviceReportedAt ?? DBNull.Value);
        command.Parameters.Add(new NpgsqlParameter("businessDate", NpgsqlDbType.Date)
        {
            Value = businessDate,
        });
        command.Parameters.AddWithValue("ownerId", movement.OwnerId);
        command.Parameters.AddWithValue("itemId", movement.ItemId);
        command.Parameters.AddWithValue("lotId", movement.LotId);
        command.Parameters.AddWithValue("stockStatus", movement.StockStatus);
        command.Parameters.AddWithValue("fromLocationId", (object?)movement.FromLocationId ?? DBNull.Value);
        command.Parameters.AddWithValue("toLocationId", (object?)movement.ToLocationId ?? DBNull.Value);
        command.Parameters.AddWithValue("quantityBase", movement.QuantityBase);
        command.Parameters.AddWithValue("enteredQuantity", movement.EnteredQuantity);
        command.Parameters.AddWithValue("enteredUom", movement.EnteredUom);
        command.Parameters.AddWithValue("movementType", movement.MovementType);
        command.Parameters.AddWithValue("reasonCode", (object?)movement.ReasonCode ?? DBNull.Value);
        command.Parameters.AddWithValue("referenceType", (object?)movement.ReferenceType ?? DBNull.Value);
        command.Parameters.AddWithValue("referenceId", (object?)movement.ReferenceId ?? DBNull.Value);
        command.Parameters.AddWithValue("actorUserId", movement.ActorUserId);
        command.Parameters.AddWithValue("deviceId", (object?)movement.DeviceId ?? DBNull.Value);
        command.Parameters.AddWithValue("idempotencyKey", (object?)movement.IdempotencyKey ?? DBNull.Value);

        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
