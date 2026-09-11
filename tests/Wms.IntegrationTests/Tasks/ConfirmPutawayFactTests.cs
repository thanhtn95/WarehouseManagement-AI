using Npgsql;
using Wms.IntegrationTests.Infrastructure;
using Wms.Modules.Inventory.Infrastructure;
using Wms.Modules.Platform.Infrastructure;
using Wms.Modules.Tasks.Application;
using Wms.SharedKernel;
using Xunit;

namespace Wms.IntegrationTests.Tasks;

/// <summary>
/// <c>putaway_confirmed</c> — the first fact whose movement touches two stock
/// positions, and the one Phase 1A exit criteria 1 and 3 depend on.
/// </summary>
/// <remarks>
/// §6.3 is explicit that a destination mismatch is a fact, not an error: "if
/// the operator put stock in A-14-02 when directed to A-12-03 — because the
/// directed bin was full — the movement records where it actually went, and a
/// location_mismatch exception is raised. Rejecting it would leave the system
/// believing stock is somewhere it is not, which is strictly worse than an
/// exception."
/// </remarks>
public sealed class ConfirmPutawayFactTests(PostgresFixture fixture)
    : IClassFixture<PostgresFixture>
{
    private readonly ConfirmPutawayFactHandler _handler = new(
        new StockLedger(new BusinessCalendar()), new IdempotencyStore(), new Outbox());

    private readonly StockLedger _ledger = new(new BusinessCalendar());

    [Fact]
    public async Task Putaway_MovesStockOutOfReceivingAndIntoTheBin()
    {
        Seeded data = await SeedAsync(requestedQuantity: 100m);
        await StockInReceivingAsync(data, 100m);

        FactResult result = await HandleAsync(NewFact(data, quantity: 100m));

        Assert.Equal(FactStatus.Accepted, result.Status);
        Assert.Null(result.ExceptionId);

        // Both ends moved. This is the assertion a receipt-only test can
        // never make, and the one that fails if the ledger reverts to
        // deriving a single position.
        Assert.Equal(0m, await OnHandAsync(data, data.ReceivingId));
        Assert.Equal(100m, await OnHandAsync(data, data.DirectedBinId));

        Assert.Equal("confirmed", await ScalarAsync<string>(
            "SELECT status FROM task_line WHERE id = @p;", data.TaskLineId));
        Assert.Equal("completed", await ScalarAsync<string>(
            "SELECT status FROM task WHERE id = @p;", data.TaskId));
    }

    /// <summary>
    /// The directed bin was full, so the operator used another one. §6.3: the
    /// movement records where the stock actually went.
    /// </summary>
    [Fact]
    public async Task PutawayToADifferentBinThanDirected_RecordsWhereItWentAndRaisesLocationMismatch()
    {
        Seeded data = await SeedAsync(requestedQuantity: 40m);
        await StockInReceivingAsync(data, 40m);

        FactResult result = await HandleAsync(
            NewFact(data, quantity: 40m, to: data.AlternateBinId, reason: "PUT_LOCATION_FULL"));

        // Accepted, not refused. The stock is already in the other bin.
        Assert.Equal(FactStatus.Accepted, result.Status);
        Assert.NotNull(result.ExceptionId);

        Assert.Equal(40m, await OnHandAsync(data, data.AlternateBinId));
        Assert.Equal(0m, await OnHandAsync(data, data.DirectedBinId));

        Assert.Equal("location_mismatch", await ScalarAsync<string>(
            "SELECT exception_type FROM inventory_exception WHERE id = @p;",
            result.ExceptionId!.Value));

        // The exception points at the bin the stock is actually in — the
        // whole point is to send someone to the right place.
        Assert.Equal(data.AlternateBinId, await ScalarAsync<Guid>(
            "SELECT location_id FROM inventory_exception WHERE id = @p;",
            result.ExceptionId!.Value));
    }

    /// <summary>
    /// §7.2: a task already completed by another operator raises
    /// <c>reallocated_task</c>. Both operators really did carry stock, so both
    /// movements are real.
    /// </summary>
    [Fact]
    public async Task ASecondOperatorConfirmingTheSameLine_RaisesReallocatedTask()
    {
        Seeded data = await SeedAsync(requestedQuantity: 10m);
        await StockInReceivingAsync(data, 10m);

        FactResult first = await HandleAsync(NewFact(data, quantity: 10m));
        FactResult second = await HandleAsync(NewFact(data, quantity: 10m));

        Assert.Null(first.ExceptionId);
        Assert.NotNull(second.ExceptionId);
        Assert.Equal("reallocated_task", await ScalarAsync<string>(
            "SELECT exception_type FROM inventory_exception WHERE id = @p;",
            second.ExceptionId!.Value));

        // The source goes negative, and that is correct: the system believed
        // 10 were there and 20 were carried away. Suppressing it would hide
        // exactly what the ledger exists to reveal (C1, C3).
        Assert.Equal(-10m, await OnHandAsync(data, data.ReceivingId));
        Assert.Equal(20m, await OnHandAsync(data, data.DirectedBinId));
    }

    /// <summary>
    /// Phase 1A exit criterion 3: confirmed offline, device rebooted, replayed
    /// on reconnection. The movement lands exactly once.
    /// </summary>
    [Fact]
    public async Task ReplayedPutaway_MovesStockExactlyOnce()
    {
        Seeded data = await SeedAsync(requestedQuantity: 25m);
        await StockInReceivingAsync(data, 25m);

        PutawayConfirmedFact fact = NewFact(data, quantity: 25m);
        FactResult first = await HandleAsync(fact);
        FactResult second = await HandleAsync(fact);

        Assert.Equal(FactStatus.Accepted, first.Status);
        Assert.Equal(FactStatus.Duplicate, second.Status);
        Assert.Equal(first.MovementId, second.MovementId);

        Assert.Equal(1, await ScalarAsync<long>(
            "SELECT count(*) FROM stock_movement WHERE reference_id = @p;", data.TaskLineId));
        Assert.Equal(25m, await OnHandAsync(data, data.DirectedBinId));
        Assert.Equal(0m, await OnHandAsync(data, data.ReceivingId));
    }

    /// <summary>
    /// A partial putaway is not a divergence. The residual stays in the source
    /// location and the ledger says so, so nothing is unaccounted for and
    /// there is nothing for a supervisor to resolve.
    /// </summary>
    [Fact]
    public async Task PartialPutaway_LeavesTheResidualInSourceAndRaisesNoException()
    {
        Seeded data = await SeedAsync(requestedQuantity: 100m);
        await StockInReceivingAsync(data, 100m);

        FactResult result = await HandleAsync(NewFact(data, quantity: 60m));

        Assert.Null(result.ExceptionId);
        Assert.Equal(40m, await OnHandAsync(data, data.ReceivingId));
        Assert.Equal(60m, await OnHandAsync(data, data.DirectedBinId));

        Assert.Equal("short", await ScalarAsync<string>(
            "SELECT status FROM task_line WHERE id = @p;", data.TaskLineId));
    }

    [Fact]
    public async Task PutawayToALocationInAnotherWarehouse_IsRejected()
    {
        Seeded data = await SeedAsync(requestedQuantity: 10m);
        await StockInReceivingAsync(data, 10m);

        FactResult result = await HandleAsync(
            NewFact(data, quantity: 10m, to: data.ForeignBinId));

        Assert.Equal(FactStatus.Rejected, result.Status);
        Assert.Equal(FactRejection.LocationInAnotherWarehouse, result.Rejection);
        Assert.Equal(10m, await OnHandAsync(data, data.ReceivingId));
    }

    [Fact]
    public async Task PutawayFromAnUnknownLocation_IsRejectedNotThrown()
    {
        Seeded data = await SeedAsync(requestedQuantity: 10m);

        FactResult result = await HandleAsync(
            NewFact(data, quantity: 10m) with { FromLocationId = Guid.CreateVersion7() });

        Assert.Equal(FactStatus.Rejected, result.Status);
        Assert.Equal(FactRejection.UnknownLocation, result.Rejection);
    }

    private static PutawayConfirmedFact NewFact(
        Seeded data, decimal quantity, Guid? to = null, string? reason = null) =>
        new(
            ClientFactId: Guid.CreateVersion7().ToString(),
            TaskLineId: data.TaskLineId,
            FromLocationId: data.ReceivingId,
            ToLocationId: to ?? data.DirectedBinId,
            Quantity: quantity,
            Uom: "EACH",
            ReasonCode: reason,
            ActorUserId: data.UserId,
            DeviceId: null);

    private async Task<FactResult> HandleAsync(PutawayConfirmedFact fact)
    {
        await using NpgsqlConnection connection = await fixture.DataSource.OpenConnectionAsync();
        return await _handler.HandleAsync(connection, fact);
    }

    /// <summary>
    /// Puts stock in receiving through the ledger rather than by raw insert,
    /// so the starting position is one the application could actually have
    /// produced.
    /// </summary>
    private async Task StockInReceivingAsync(Seeded data, decimal quantity)
    {
        await using NpgsqlConnection connection = await fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();

        await _ledger.PostAsync(
            connection,
            transaction,
            new Modules.Inventory.Contracts.PostMovementCommand(
                WarehouseId: data.WarehouseId,
                OwnerId: data.OwnerId,
                ItemId: data.ItemId,
                LotId: Guid.Empty,
                StockStatus: "available",
                FromLocationId: null,
                ToLocationId: data.ReceivingId,
                QuantityBase: quantity,
                EnteredQuantity: quantity,
                EnteredUom: "EACH",
                MovementType: "receipt",
                ReasonCode: null,
                ReferenceType: null,
                ReferenceId: null,
                ActorUserId: data.UserId,
                DeviceId: null,
                IdempotencyKey: Guid.CreateVersion7().ToString()),
            CancellationToken.None);

        await transaction.CommitAsync();
    }

    private async Task<decimal> OnHandAsync(Seeded data, Guid locationId)
    {
        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(
            """
            SELECT COALESCE(sum(on_hand), 0) FROM stock_balance
             WHERE item_id = @itemId AND location_id = @locationId;
            """);
        command.Parameters.AddWithValue("itemId", data.ItemId);
        command.Parameters.AddWithValue("locationId", locationId);
        return (decimal)(await command.ExecuteScalarAsync())!;
    }

    private async Task<T> ScalarAsync<T>(string sql, object parameter)
    {
        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("p", parameter);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task<Seeded> SeedAsync(decimal requestedQuantity)
    {
        Seeded data = new(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            Guid.CreateVersion7());

        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(
            """
            INSERT INTO warehouse (id, code, name, timezone, created_at, updated_at)
            VALUES (@warehouseId, right(@warehouseId::text, 12), 'T', 'Asia/Tokyo', now(), now()),
                   (@otherWarehouseId, right(@otherWarehouseId::text, 12), 'T2',
                    'Europe/Berlin', now(), now());

            INSERT INTO zone (id, warehouse_id, code, name_i18n, zone_type, created_at, updated_at)
            VALUES (@zoneId, @warehouseId, 'A', '{"en":"A"}', 'bulk', now(), now()),
                   (@otherZoneId, @otherWarehouseId, 'A', '{"en":"A"}', 'bulk', now(), now());

            INSERT INTO location (id, warehouse_id, zone_id, code, location_type,
                                  pick_sequence, created_at, updated_at)
            VALUES (@receivingId, @warehouseId, @zoneId, right(@receivingId::text, 12),
                    'staging', 0, now(), now()),
                   (@directedBinId, @warehouseId, @zoneId, right(@directedBinId::text, 12),
                    'bin', 1, now(), now()),
                   (@alternateBinId, @warehouseId, @zoneId, right(@alternateBinId::text, 12),
                    'bin', 2, now(), now()),
                   (@foreignBinId, @otherWarehouseId, @otherZoneId,
                    right(@foreignBinId::text, 12), 'bin', 1, now(), now());

            INSERT INTO owner (id, code, name, created_at, updated_at)
            VALUES (@ownerId, right(@ownerId::text, 12), 'O', now(), now());

            INSERT INTO item (id, sku_code, name_i18n, base_uom, created_at, updated_at)
            VALUES (@itemId, right(@itemId::text, 12), '{"en":"I"}', 'EACH', now(), now());

            INSERT INTO app_user (id, user_type, display_name, status, security_stamp,
                                  valid_from, created_at, updated_at)
            VALUES (@userId, 'operator', 'Putaway', 'active', gen_random_uuid(),
                    current_date, now(), now());

            INSERT INTO task (id, warehouse_id, zone_id, task_type, status,
                              priority, sort_sequence, created_at)
            VALUES (@taskId, @warehouseId, @zoneId, 'putaway', 'leased', 100, 1, now());

            INSERT INTO task_line (id, task_id, line_no, owner_id, item_id, lot_id,
                                   from_location_id, to_location_id,
                                   requested_quantity, uom_code, status)
            VALUES (@taskLineId, @taskId, 1, @ownerId, @itemId,
                    '00000000-0000-0000-0000-000000000000',
                    @receivingId, @directedBinId, @requestedQuantity, 'EACH', 'pending');
            """);

        command.Parameters.AddWithValue("warehouseId", data.WarehouseId);
        command.Parameters.AddWithValue("otherWarehouseId", Guid.CreateVersion7());
        command.Parameters.AddWithValue("zoneId", data.ZoneId);
        command.Parameters.AddWithValue("otherZoneId", Guid.CreateVersion7());
        command.Parameters.AddWithValue("receivingId", data.ReceivingId);
        command.Parameters.AddWithValue("directedBinId", data.DirectedBinId);
        command.Parameters.AddWithValue("alternateBinId", data.AlternateBinId);
        command.Parameters.AddWithValue("foreignBinId", data.ForeignBinId);
        command.Parameters.AddWithValue("ownerId", data.OwnerId);
        command.Parameters.AddWithValue("itemId", data.ItemId);
        command.Parameters.AddWithValue("userId", data.UserId);
        command.Parameters.AddWithValue("taskId", data.TaskId);
        command.Parameters.AddWithValue("taskLineId", data.TaskLineId);
        command.Parameters.AddWithValue("requestedQuantity", requestedQuantity);

        await command.ExecuteNonQueryAsync();
        return data;
    }

    private sealed record Seeded(
        Guid WarehouseId,
        Guid ZoneId,
        Guid ReceivingId,
        Guid DirectedBinId,
        Guid AlternateBinId,
        Guid ForeignBinId,
        Guid OwnerId,
        Guid ItemId,
        Guid UserId,
        Guid TaskId)
    {
        public Guid TaskLineId { get; } = Guid.CreateVersion7();
    }
}
