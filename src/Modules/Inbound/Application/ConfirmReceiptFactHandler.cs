using System.Data;
using System.Text.Json;
using Npgsql;
using Wms.Modules.Inventory.Contracts;
using Wms.Modules.Platform.Contracts;
using Wms.SharedKernel;

namespace Wms.Modules.Inbound.Application;

/// <summary>
/// The seven-step fact-processing contract (§4.3), for
/// <c>receipt_confirmed</c>.
/// </summary>
/// <remarks>
/// The order is the contract, and step 2 having no guard clause is the
/// load-bearing detail: the goods have physically moved, so a
/// <c>WHERE on_hand &gt;= qty</c> would make the statement fail and force
/// the device to handle a rejection it cannot act on.
///
/// This transaction mutates two aggregates — StockPosition and Receipt —
/// which is one of exactly two permitted exceptions to the
/// one-aggregate-per-transaction rule (C6, §4.3). It qualifies because it is
/// short, touches a single row per aggregate, and takes its locks in a fixed
/// order.
/// </remarks>
public sealed class ConfirmReceiptFactHandler(
    IStockLedger stockLedger,
    IIdempotencyStore idempotencyStore,
    IOutbox outbox)
{
    private const string LoadLineSql = """
        SELECT rl.item_id, rl.owner_id, rl.expected_quantity, r.warehouse_id, r.id
          FROM receipt_line rl
          JOIN receipt r ON r.id = rl.receipt_id
         WHERE rl.id = @receiptLineId
           FOR UPDATE OF rl;
        """;

    /// <summary>
    /// Accumulates, never assigns.
    /// </summary>
    /// <remarks>
    /// One line can legitimately be confirmed more than once — two receivers
    /// working a single pallet line, or one device draining a queue holding
    /// two partial counts — and each confirmation carries its own
    /// idempotency key, so neither is a replay. Invariant 4 forbids refusing
    /// the second one, which leaves accumulation as the only behaviour that
    /// keeps <c>received_quantity</c> agreeing with the ledger. Assigning
    /// would let the second fact overwrite the first: the ledger would say
    /// 60 and the line would say 20.
    ///
    /// <c>discrepancy_type</c> is therefore derived from the running total
    /// rather than from the incoming fact, or two half-counts of a complete
    /// line would each raise a spurious under-receipt exception.
    /// </remarks>
    private const string ConfirmLineSql = """
        UPDATE receipt_line
           SET received_quantity = COALESCE(received_quantity, 0) + @quantity,
               discrepancy_type  = CASE
                   WHEN expected_quantity IS NULL THEN 'none'
                   WHEN COALESCE(received_quantity, 0) + @quantity > expected_quantity THEN 'over'
                   WHEN COALESCE(received_quantity, 0) + @quantity < expected_quantity THEN 'under'
                   ELSE 'none'
               END,
               status            = 'confirmed'
         WHERE id = @receiptLineId
        RETURNING received_quantity, discrepancy_type;
        """;

    /// <summary>
    /// Validates the destination the device named, inside the same
    /// transaction.
    /// </summary>
    /// <remarks>
    /// Two failures hide here, and neither is caught by any constraint in a
    /// useful way. A destination that does not exist reaches the
    /// <c>stock_balance.location_id</c> foreign key, and the resulting
    /// database error escapes the per-fact boundary — failing the whole batch
    /// and reaching a handheld as a <c>5xx</c> it cannot act on. A
    /// destination that exists in <em>another warehouse</em> succeeds
    /// cleanly, which is worse: the stock lands physically in site B while
    /// <c>business_date</c> is derived from site A's day boundary and the
    /// over-receipt exception is raised into site A's queue, so B's
    /// supervisor never sees a discrepancy sitting in B's rack. A mis-scanned
    /// label carrying a valid id from another site is enough to cause it.
    /// </remarks>
    private const string LoadDestinationWarehouseSql = """
        SELECT warehouse_id FROM location WHERE id = @locationId;
        """;

    private const string RaiseExceptionSql = """
        INSERT INTO inventory_exception (id, exception_type, severity, warehouse_id,
                                         owner_id, item_id, location_id,
                                         expected_quantity, actual_quantity,
                                         stock_movement_id, raised_at,
                                         raised_by_user_id, device_id, status)
        VALUES (@id, @exceptionType, @severity, @warehouseId,
                @ownerId, @itemId, @locationId,
                @expectedQuantity, @actualQuantity,
                @stockMovementId, now(),
                @raisedByUserId, @deviceId, 'open');
        """;

    public async Task<FactResult> HandleAsync(
        NpgsqlConnection connection,
        ReceiptConfirmedFact fact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(fact);

        // READ COMMITTED is pinned, not inherited. The idempotency claim in
        // step 1 depends on per-statement snapshots: the loser of a race
        // blocks on the winner's speculative insert, then reads the winner's
        // committed response through a fresh snapshot. Under REPEATABLE READ
        // that second read is pinned to the transaction's own snapshot, sees
        // nothing, and returns Conflict — turning a handheld's legitimate
        // retry into a 409 that quarantines an already-accepted fact. A
        // server- or role-level default is not something this handler should
        // be silently at the mercy of.
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
                // Rolled back, so the key stays claimed by whoever claimed it
                // first and this attempt leaves no trace.
                await transaction.RollbackAsync(cancellationToken);
                return FactResult.Rejected(FactRejection.IdempotencyKeyReused);
        }

        ReceiptLineContext? line = await LoadLineAsync(
            connection, transaction, fact.ReceiptLineId, cancellationToken);

        if (line is null)
        {
            // Rolling back releases the idempotency claim too, so a later
            // fact that legitimately reuses this id after the line appears is
            // not permanently blocked by a rejection.
            await transaction.RollbackAsync(cancellationToken);
            return FactResult.Rejected(FactRejection.UnknownReceiptLine);
        }

        Guid? destinationWarehouse = await LoadDestinationWarehouseAsync(
            connection, transaction, fact.ToLocationId, cancellationToken);

        if (destinationWarehouse is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return FactResult.Rejected(FactRejection.UnknownLocation);
        }

        if (destinationWarehouse != line.WarehouseId)
        {
            await transaction.RollbackAsync(cancellationToken);
            return FactResult.Rejected(FactRejection.LocationInAnotherWarehouse);
        }

        // 2 + 3. Mutate the balance, then append the ledger row.
        StockMovementResult movement = await stockLedger.PostAsync(
            connection,
            transaction,
            new PostMovementCommand(
                WarehouseId: line.WarehouseId,
                OwnerId: line.OwnerId,
                ItemId: line.ItemId,
                LotId: SentinelLotId,
                StockStatus: "available",
                FromLocationId: null,
                ToLocationId: fact.ToLocationId,
                QuantityBase: fact.Quantity,
                EnteredQuantity: fact.Quantity,
                EnteredUom: fact.Uom,
                MovementType: "receipt",
                ReasonCode: fact.ReasonCode,
                ReferenceType: "receipt_line",
                ReferenceId: fact.ReceiptLineId,
                ActorUserId: fact.ActorUserId,
                DeviceId: fact.DeviceId,
                IdempotencyKey: fact.ClientFactId,
                DeviceReportedAt: fact.OccurredAtDevice),
            cancellationToken);

        // 4. Advance the work item this fact confirms against. The database
        //    computes the discrepancy, because it is a property of the line's
        //    accumulated total and not of this one fact.
        ConfirmedLine confirmed = await ConfirmLineAsync(
            connection, transaction, fact, cancellationToken);

        // 5. Divergence check. Reality disagreeing with expectation is an
        //    exception record, never a refusal.
        Guid? exceptionId = null;
        if (confirmed.DiscrepancyType is "over" or "under")
        {
            exceptionId = await RaiseExceptionAsync(
                connection, transaction, fact, line, movement.MovementId,
                confirmed, cancellationToken);
        }

        // 6. Outbox, in the same transaction as its cause.
        await outbox.EnqueueAsync(
            connection, transaction, "Receipt", line.ReceiptId, "ReceiptLineConfirmed",
            JsonSerializer.Serialize(new
            {
                receiptLineId = fact.ReceiptLineId,
                quantity = fact.Quantity,
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

    /// <summary>
    /// The "no lot" sentinel: a position that is not lot-tracked points at a
    /// real row rather than NULL, because lot_id participates in the
    /// stock_balance primary key.
    /// </summary>
    private static readonly Guid SentinelLotId = Guid.Empty;

    /// <summary>
    /// Returns what the original call returned, marked as a duplicate.
    /// </summary>
    /// <remarks>
    /// Still throws, and deliberately: a claimed key with no stored response
    /// means a previous run committed its claim without committing its work,
    /// which this handler's transaction makes impossible. It is a bug in the
    /// server, not bad input from a device, and silently inventing a result
    /// would hide it.
    /// </remarks>
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

    /// <summary>
    /// Returns null when the line does not exist, rather than throwing.
    /// </summary>
    /// <remarks>
    /// A fact naming a line the server has never heard of is bad input from a
    /// device, not a server fault. Throwing here would surface as a 5xx and
    /// take the other nineteen facts in the batch down with it.
    /// </remarks>
    private static async Task<ReceiptLineContext?> LoadLineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid receiptLineId,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(LoadLineSql, connection, transaction);
        command.Parameters.AddWithValue("receiptLineId", receiptLineId);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ReceiptLineContext(
            ItemId: reader.GetGuid(0),
            OwnerId: reader.GetGuid(1),
            ExpectedQuantity: reader.IsDBNull(2) ? null : reader.GetDecimal(2),
            WarehouseId: reader.GetGuid(3),
            ReceiptId: reader.GetGuid(4));
    }

    private static async Task<Guid?> LoadDestinationWarehouseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid locationId,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command =
            new(LoadDestinationWarehouseSql, connection, transaction);
        command.Parameters.AddWithValue("locationId", locationId);

        return await command.ExecuteScalarAsync(cancellationToken) as Guid?;
    }

    private static async Task<ConfirmedLine> ConfirmLineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ReceiptConfirmedFact fact,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(ConfirmLineSql, connection, transaction);
        command.Parameters.AddWithValue("receiptLineId", fact.ReceiptLineId);
        command.Parameters.AddWithValue("quantity", fact.Quantity);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ConfirmedLine(reader.GetDecimal(0), reader.GetString(1))
            : throw new InvalidOperationException(
                $"Receipt line {fact.ReceiptLineId} vanished mid-transaction.");
    }

    private static async Task<Guid> RaiseExceptionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ReceiptConfirmedFact fact,
        ReceiptLineContext line,
        Guid movementId,
        ConfirmedLine confirmed,
        CancellationToken cancellationToken)
    {
        Guid exceptionId = Guid.CreateVersion7();

        await using NpgsqlCommand command = new(RaiseExceptionSql, connection, transaction);
        command.Parameters.AddWithValue("id", exceptionId);
        command.Parameters.AddWithValue("exceptionType", $"{confirmed.DiscrepancyType}_receipt");
        command.Parameters.AddWithValue("severity", "medium");
        command.Parameters.AddWithValue("warehouseId", line.WarehouseId);
        command.Parameters.AddWithValue("ownerId", line.OwnerId);
        command.Parameters.AddWithValue("itemId", line.ItemId);
        command.Parameters.AddWithValue("locationId", fact.ToLocationId);
        command.Parameters.AddWithValue("expectedQuantity", (object?)line.ExpectedQuantity ?? DBNull.Value);
        // The line's accumulated total, not this one fact's quantity — the
        // supervisor resolving this needs to compare like with like against
        // expected_quantity, which is also a line-level figure.
        command.Parameters.AddWithValue("actualQuantity", confirmed.ReceivedQuantity);
        command.Parameters.AddWithValue("stockMovementId", movementId);
        command.Parameters.AddWithValue("raisedByUserId", fact.ActorUserId);
        command.Parameters.AddWithValue("deviceId", (object?)fact.DeviceId ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
        return exceptionId;
    }

    /// <summary>
    /// Guards against a key being reused with a different payload. Hashing
    /// the fields rather than the object graph keeps the hash stable across
    /// serializer changes.
    /// </summary>
    /// <remarks>
    /// Every field must be reduced to one canonical text form first.
    /// <c>decimal</c> preserves trailing zeros and <c>System.Text.Json</c>
    /// binds the scale written in the JSON, so <c>10</c> and <c>10.0</c> are
    /// numerically equal and would otherwise hash differently — a queued fact
    /// whose quantity round-trips through IndexedDB with a different scale
    /// would come back as a 409 and quarantine itself (§6.4 case B). Rounding
    /// to the storage scale of <c>numeric(18,4)</c> is what makes the hash a
    /// property of the value rather than of its formatting.
    /// </remarks>
    private static string Hash(ReceiptConfirmedFact fact) => FactHash.Of(
        fact.ReceiptLineId.ToString(),
        FactHash.Quantity(fact.Quantity),
        fact.Uom,
        fact.ToLocationId.ToString(),
        fact.ReasonCode,
        fact.ActorUserId.ToString());

    /// <summary>
    /// The line's state after this confirmation was folded into it.
    /// </summary>
    private sealed record ConfirmedLine(decimal ReceivedQuantity, string DiscrepancyType);

    private sealed record ReceiptLineContext(
        Guid ItemId,
        Guid OwnerId,
        decimal? ExpectedQuantity,
        Guid WarehouseId,
        Guid ReceiptId);
}
