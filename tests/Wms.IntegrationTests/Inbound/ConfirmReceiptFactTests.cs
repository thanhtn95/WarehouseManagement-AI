using Npgsql;
using Wms.IntegrationTests.Infrastructure;
using Wms.Modules.Inbound.Application;
using Wms.Modules.Inventory.Infrastructure;
using Wms.Modules.Platform.Infrastructure;
using Wms.SharedKernel;
using Xunit;

namespace Wms.IntegrationTests.Inbound;

/// <summary>
/// Phase 1A exit criterion 5, and the reason /sync/facts exists at all:
///
///   "An over-receipt — expected 100, counted 106 — records 106 and raises
///    an exception record. It does not return an error to the device."
///
/// The proposal calls this the criterion that matters most, because it is
/// the only test proving the command/fact split was actually implemented,
/// and the one most likely to have been built as a rejection by mistake —
/// rejecting invalid input is what instinct says to do.
/// </summary>
public sealed class ConfirmReceiptFactTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private readonly ConfirmReceiptFactHandler _handler = new(
        new StockLedger(new BusinessCalendar()), new IdempotencyStore(), new Outbox());

    [Fact]
    public async Task OverReceipt_RecordsWhatWasCounted_AndRaisesAnExceptionInsteadOfFailing()
    {
        Fixture data = await SeedAsync(expectedQuantity: 100m);

        FactResult accepted = await HandleAsync(
            new ReceiptConfirmedFact(
                ClientFactId: Guid.CreateVersion7().ToString(),
                ReceiptLineId: data.ReceiptLineId,
                Quantity: 106m,
                Uom: "EACH",
                ToLocationId: data.LocationId,
                ReasonCode: "RCV_SUPPLIER_OVER",
                ActorUserId: data.UserId,
                DeviceId: null));

        // It did not throw, and it did not reject. That is most of the point.
        Assert.Equal(FactStatus.Accepted, accepted.Status);
        Assert.NotNull(accepted.ExceptionId);

        // The ledger records what was physically counted — 106, not 100.
        Assert.Equal(106m, await ScalarAsync<decimal>(
            "SELECT quantity_base FROM stock_movement WHERE reference_id = @p;", data.ReceiptLineId));

        Assert.Equal(106m, await ScalarAsync<decimal>(
            "SELECT on_hand FROM stock_balance WHERE location_id = @p;", data.LocationId));

        // The discrepancy became a supervisor's problem, not the receiver's.
        Assert.Equal("over_receipt", await ScalarAsync<string>(
            "SELECT exception_type FROM inventory_exception WHERE id = @p;", accepted.ExceptionId!.Value));

        Assert.Equal("over", await ScalarAsync<string>(
            "SELECT discrepancy_type FROM receipt_line WHERE id = @p;", data.ReceiptLineId));

        // And the cross-module effect went through the outbox, in the same
        // transaction as its cause. Scoped to THIS receipt: tests share a
        // container, so a global count here would depend on execution order.
        Assert.Equal(1, await ScalarAsync<long>(
            """
            SELECT count(*) FROM outbox_message
             WHERE aggregate_id = @p AND message_type = 'ReceiptLineConfirmed';
            """,
            data.ReceiptId));
    }

    [Fact]
    public async Task ExactReceipt_RaisesNoException()
    {
        Fixture data = await SeedAsync(expectedQuantity: 50m);

        FactResult accepted = await HandleAsync(NewFact(data, quantity: 50m));

        Assert.Null(accepted.ExceptionId);
        Assert.Equal("none", await ScalarAsync<string>(
            "SELECT discrepancy_type FROM receipt_line WHERE id = @p;", data.ReceiptLineId));
    }

    [Fact]
    public async Task ReplayedFact_ProducesExactlyOneMovement()
    {
        Fixture data = await SeedAsync(expectedQuantity: 10m);
        ReceiptConfirmedFact fact = NewFact(data, quantity: 10m);

        FactResult first = await HandleAsync(fact);
        FactResult second = await HandleAsync(fact);

        Assert.Equal(FactStatus.Accepted, first.Status);
        Assert.Equal(FactStatus.Duplicate, second.Status);
        Assert.Equal(first.MovementId, second.MovementId);
        Assert.Equal(first.Sequence, second.Sequence);

        // The handheld retried over bad wifi. The stock moved once.
        Assert.Equal(1, await ScalarAsync<long>(
            "SELECT count(*) FROM stock_movement WHERE reference_id = @p;", data.ReceiptLineId));

        Assert.Equal(10m, await ScalarAsync<decimal>(
            "SELECT on_hand FROM stock_balance WHERE location_id = @p;", data.LocationId));
    }

    [Fact]
    public async Task SameClientFactIdWithADifferentPayload_IsRejectedAsAClientBug()
    {
        Fixture data = await SeedAsync(expectedQuantity: 10m);
        string key = Guid.CreateVersion7().ToString();

        await HandleAsync(NewFact(data, quantity: 10m, key: key));

        // Same id, different quantity: answering with either version would
        // silently pick one at random. It is reported per fact rather than
        // thrown, because throwing would fail the other nineteen facts in the
        // batch and reach the handheld as a 5xx.
        FactResult rejected = await HandleAsync(NewFact(data, quantity: 4m, key: key));

        Assert.Equal(FactStatus.Rejected, rejected.Status);
        Assert.Equal(FactRejection.IdempotencyKeyReused, rejected.Rejection);
        Assert.Null(rejected.MovementId);

        Assert.Equal(1, await ScalarAsync<long>(
            "SELECT count(*) FROM stock_movement WHERE reference_id = @p;", data.ReceiptLineId));
    }

    /// <summary>
    /// A device holding a fact against a line the server has never heard of —
    /// deleted, or never synced to this deployment.
    /// </summary>
    [Fact]
    public async Task UnknownReceiptLine_IsRejectedPerFact_NotThrown()
    {
        Fixture data = await SeedAsync(expectedQuantity: 10m);
        ReceiptConfirmedFact orphan = NewFact(data, quantity: 10m) with
        {
            ReceiptLineId = Guid.CreateVersion7(),
        };

        FactResult rejected = await HandleAsync(orphan);

        Assert.Equal(FactStatus.Rejected, rejected.Status);
        Assert.Equal(FactRejection.UnknownReceiptLine, rejected.Rejection);

        // Nothing was written, and the rollback released the idempotency
        // claim rather than leaving the id permanently poisoned.
        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM stock_movement WHERE reference_id = @p;", orphan.ReceiptLineId));
        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM idempotency_record WHERE key = @p;", orphan.ClientFactId));
    }

    /// <summary>
    /// Two receivers working one pallet line, or one device draining a queue
    /// that holds two partial counts. Distinct keys, so neither is a replay —
    /// and invariant 4 forbids refusing the second.
    /// </summary>
    [Fact]
    public async Task TwoConfirmationsOfOneLine_AccumulateInsteadOfOverwriting()
    {
        Fixture data = await SeedAsync(expectedQuantity: 100m);

        FactResult first = await HandleAsync(NewFact(data, quantity: 40m));
        FactResult second = await HandleAsync(NewFact(data, quantity: 60m));

        // Each posted its own movement — the stock physically arrived twice.
        Assert.NotEqual(first.MovementId, second.MovementId);
        Assert.Equal(2, await ScalarAsync<long>(
            "SELECT count(*) FROM stock_movement WHERE reference_id = @p;", data.ReceiptLineId));

        // The line agrees with the ledger. Assigning rather than accumulating
        // would leave it reading 60 against a ledger holding 100.
        Assert.Equal(100m, await ScalarAsync<decimal>(
            "SELECT received_quantity FROM receipt_line WHERE id = @p;", data.ReceiptLineId));

        // And 40-then-60 against an expected 100 is a complete line, not two
        // short ones: the discrepancy follows the total, so the second
        // confirmation clears the first's under-receipt rather than adding to
        // it.
        Assert.NotNull(first.ExceptionId);
        Assert.Null(second.ExceptionId);
        Assert.Equal(FactStatus.Accepted, second.Status);
        Assert.Equal("none", await ScalarAsync<string>(
            "SELECT discrepancy_type FROM receipt_line WHERE id = @p;", data.ReceiptLineId));
    }

    private static ReceiptConfirmedFact NewFact(Fixture data, decimal quantity, string? key = null) =>
        new(
            ClientFactId: key ?? Guid.CreateVersion7().ToString(),
            ReceiptLineId: data.ReceiptLineId,
            Quantity: quantity,
            Uom: "EACH",
            ToLocationId: data.LocationId,
            ReasonCode: null,
            ActorUserId: data.UserId,
            DeviceId: null);

    private async Task<NpgsqlConnection> OpenAsync() =>
        await fixture.DataSource.OpenConnectionAsync();

    /// <summary>
    /// Owns the connection the handler borrows.
    /// </summary>
    /// <remarks>
    /// The handler deliberately does not dispose a connection it did not
    /// open, so every caller must. Passing <c>await OpenAsync()</c> straight
    /// into the call leaks it back to nothing — invisible here against a pool
    /// of 100, and the same shape in the /sync/facts endpoint would exhaust
    /// the reserved wms_api pool within seconds under 50 operators, which is
    /// exactly the stall invariant 10 reserves that pool to prevent.
    /// </remarks>
    private async Task<FactResult> HandleAsync(ReceiptConfirmedFact fact)
    {
        await using NpgsqlConnection connection = await OpenAsync();
        return await _handler.HandleAsync(connection, fact);
    }

    /// <summary>
    /// A complete, isolated receipt: every test gets its own warehouse,
    /// location and line, so nothing shares rows with anything else.
    /// </summary>
    private async Task<Fixture> SeedAsync(decimal? expectedQuantity)
    {
        Fixture data = new(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            Guid.CreateVersion7(), Guid.CreateVersion7());

        await using NpgsqlConnection connection = await OpenAsync();
        await using NpgsqlTransaction seed = await connection.BeginTransactionAsync();
        await using NpgsqlCommand command = new(
            // Codes take the TAIL of the uuid, not the head: UUIDv7 is
            // time-ordered, so ids minted milliseconds apart share their
            // leading characters and left() collides across tests.
            """
            INSERT INTO warehouse (id, code, name, timezone, created_at, updated_at)
            VALUES (@warehouseId, right(@warehouseId::text, 12), 'Test', 'Asia/Tokyo', now(), now());

            INSERT INTO zone (id, warehouse_id, code, name_i18n, zone_type, created_at, updated_at)
            VALUES (@zoneId, @warehouseId, 'A', '{"en":"A"}', 'receiving', now(), now());

            INSERT INTO location (id, warehouse_id, zone_id, code, location_type,
                                  pick_sequence, created_at, updated_at)
            VALUES (@locationId, @warehouseId, @zoneId, right(@locationId::text, 12),
                    'bin', 1, now(), now());

            INSERT INTO owner (id, code, name, created_at, updated_at)
            VALUES (@ownerId, right(@ownerId::text, 12), 'Owner', now(), now());

            INSERT INTO item (id, sku_code, name_i18n, base_uom, created_at, updated_at)
            VALUES (@itemId, right(@itemId::text, 12), '{"en":"Item"}', 'EACH', now(), now());

            INSERT INTO app_user (id, user_type, display_name, status, security_stamp,
                                  valid_from, created_at, updated_at)
            VALUES (@userId, 'operator', 'Operator', 'active', gen_random_uuid(),
                    current_date, now(), now());

            INSERT INTO receipt (id, warehouse_id, receipt_number, receipt_type, owner_id,
                                 status, created_by, created_at, updated_at)
            VALUES (@receiptId, @warehouseId, right(@receiptId::text, 12), 'against_asn',
                    @ownerId, 'in_progress', @userId, now(), now());

            INSERT INTO receipt_line (id, receipt_id, line_no, item_id, owner_id,
                                      expected_quantity, uom_code)
            VALUES (@receiptLineId, @receiptId, 1, @itemId, @ownerId,
                    @expectedQuantity, 'EACH');
            """,
            connection);

        command.Parameters.AddWithValue("warehouseId", data.WarehouseId);
        command.Parameters.AddWithValue("zoneId", data.ZoneId);
        command.Parameters.AddWithValue("locationId", data.LocationId);
        command.Parameters.AddWithValue("ownerId", data.OwnerId);
        command.Parameters.AddWithValue("itemId", data.ItemId);
        command.Parameters.AddWithValue("userId", data.UserId);
        command.Parameters.AddWithValue("receiptId", data.ReceiptId);
        command.Parameters.AddWithValue("receiptLineId", data.ReceiptLineId);
        command.Parameters.AddWithValue("expectedQuantity", (object?)expectedQuantity ?? DBNull.Value);

        await command.ExecuteNonQueryAsync();
        await seed.CommitAsync();
        return data;
    }

    private async Task<T> ScalarAsync<T>(string sql, object? parameter = null)
    {
        await using NpgsqlConnection connection = await OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        if (parameter is not null)
        {
            command.Parameters.AddWithValue("p", parameter);
        }

        return (T)(await command.ExecuteScalarAsync())!;
    }

    private sealed record Fixture(
        Guid WarehouseId,
        Guid ZoneId,
        Guid LocationId,
        Guid OwnerId,
        Guid ItemId,
        Guid UserId,
        Guid ReceiptId,
        Guid ReceiptLineId);
}
