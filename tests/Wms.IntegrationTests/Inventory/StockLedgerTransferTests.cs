using System.Collections.Concurrent;
using Npgsql;
using Wms.IntegrationTests.Infrastructure;
using Wms.Modules.Inventory.Contracts;
using Wms.Modules.Inventory.Infrastructure;
using Xunit;

namespace Wms.IntegrationTests.Inventory;

/// <summary>
/// A movement with both a source and a destination — the shape putaway
/// (§6.3) and every later transfer use, and the one no receipt test can
/// reach, because a receipt only ever has a destination.
/// </summary>
/// <remarks>
/// The first test exists because the ledger previously derived a single
/// position from <c>ToLocationId ?? FromLocationId</c>. That silently
/// incremented the destination and never decremented the source: stock
/// duplicated, ledger and balances permanently diverged, and nothing but
/// reconciliation would ever have noticed — long after the cause was
/// forgotten.
/// </remarks>
public sealed class StockLedgerTransferTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const int MoversPerDirection = 6;
    private const int Iterations = 30;

    private readonly StockLedger _ledger = new(new BusinessCalendar());

    [Fact]
    public async Task Transfer_DecrementsTheSourceAndIncrementsTheDestination()
    {
        Fixture data = await SeedAsync();
        await PostAsync(data, from: null, to: data.LocationA, quantity: 100m, type: "receipt");

        StockMovementResult moved = await PostAsync(
            data, from: data.LocationA, to: data.LocationB, quantity: 30m, type: "putaway");

        Assert.Equal(70m, await OnHandAsync(data, data.LocationA));
        Assert.Equal(30m, await OnHandAsync(data, data.LocationB));

        // The caller is told about the position the stock came to rest in.
        Assert.Equal(30m, moved.OnHandAfter);

        // One ledger row records the whole movement, carrying both ends.
        Assert.Equal(1, await ScalarAsync<long>(
            """
            SELECT count(*) FROM stock_movement
             WHERE id = @p AND from_location_id IS NOT NULL AND to_location_id IS NOT NULL;
            """,
            moved.MovementId));

        // Nothing was created or destroyed: the ledger's net equals the sum
        // of the balances it produced.
        Assert.Equal(100m, await ScalarAsync<decimal>(
            "SELECT sum(on_hand) FROM stock_balance WHERE item_id = @p;", data.ItemId));
    }

    /// <summary>
    /// A source is allowed to go negative. An offline putaway out of a bin
    /// the server believes is empty is true information, not an error to
    /// suppress (C1, C3).
    /// </summary>
    [Fact]
    public async Task Transfer_OutOfAnEmptyBin_DrivesTheSourceNegativeInsteadOfFailing()
    {
        Fixture data = await SeedAsync();

        await PostAsync(data, from: data.LocationA, to: data.LocationB, quantity: 5m, type: "putaway");

        Assert.Equal(-5m, await OnHandAsync(data, data.LocationA));
        Assert.Equal(5m, await OnHandAsync(data, data.LocationB));
    }

    /// <summary>
    /// Mandatory concurrency test 3: no deadlock.
    /// </summary>
    /// <remarks>
    /// Operators moving stock A→B and B→A at the same moment each lock two
    /// <c>stock_balance</c> rows. Acquired in the order the caller happens to
    /// name them, that is a textbook lock-order inversion and one side dies
    /// with SQLSTATE 40P01 mid-shift. The ledger's upsert orders its feeding
    /// subplan by the conflict-target tuple so every writer takes the rows in
    /// the same order; this test is what keeps that ORDER BY from being
    /// tidied away as decoration.
    /// </remarks>
    [Fact]
    public async Task OpposingTransfersBetweenTwoBins_NeverDeadlock()
    {
        Fixture data = await SeedAsync();
        await PostAsync(data, from: null, to: data.LocationA, quantity: 10_000m, type: "receipt");
        await PostAsync(data, from: null, to: data.LocationB, quantity: 10_000m, type: "receipt");

        ConcurrentBag<Exception> failures = [];

        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            using Barrier barrier = new(MoversPerDirection * 2);

            await Task.WhenAll(Enumerable.Range(0, MoversPerDirection * 2).Select(async i =>
            {
                bool forward = i % 2 == 0;

                // Every mover blocks here, so the opposing directions collide
                // rather than politely queueing.
                await Task.Yield();
                barrier.SignalAndWait();

                try
                {
                    await PostAsync(
                        data,
                        from: forward ? data.LocationA : data.LocationB,
                        to: forward ? data.LocationB : data.LocationA,
                        quantity: 1m,
                        type: "move");
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }));
        }

        // Asserted first and separately from the catch-all below, so a
        // lock-order regression reports itself as a deadlock rather than as
        // an anonymous "one exception occurred".
        Assert.DoesNotContain(failures, f =>
            f is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected });
        Assert.Empty(failures);

        // Equal traffic in both directions, so the bins end where they began.
        Assert.Equal(10_000m, await OnHandAsync(data, data.LocationA));
        Assert.Equal(10_000m, await OnHandAsync(data, data.LocationB));
    }

    private async Task<StockMovementResult> PostAsync(
        Fixture data, Guid? from, Guid? to, decimal quantity, string type)
    {
        await using NpgsqlConnection connection = await fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();

        StockMovementResult result = await _ledger.PostAsync(
            connection,
            transaction,
            new PostMovementCommand(
                WarehouseId: data.WarehouseId,
                OwnerId: data.OwnerId,
                ItemId: data.ItemId,
                LotId: Guid.Empty,
                StockStatus: "available",
                FromLocationId: from,
                ToLocationId: to,
                QuantityBase: quantity,
                EnteredQuantity: quantity,
                EnteredUom: "EACH",
                MovementType: type,
                ReasonCode: null,
                ReferenceType: null,
                ReferenceId: null,
                ActorUserId: data.UserId,
                DeviceId: null,
                IdempotencyKey: Guid.CreateVersion7().ToString()),
            CancellationToken.None);

        await transaction.CommitAsync();
        return result;
    }

    private async Task<decimal> OnHandAsync(Fixture data, Guid locationId)
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

    private async Task<Fixture> SeedAsync()
    {
        Fixture data = new(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            Guid.CreateVersion7());

        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(
            """
            INSERT INTO warehouse (id, code, name, timezone, created_at, updated_at)
            VALUES (@warehouseId, right(@warehouseId::text, 12), 'Test', 'Asia/Tokyo', now(), now());

            INSERT INTO zone (id, warehouse_id, code, name_i18n, zone_type, created_at, updated_at)
            VALUES (@zoneId, @warehouseId, 'A', '{"en":"A"}', 'bulk', now(), now());

            INSERT INTO location (id, warehouse_id, zone_id, code, location_type,
                                  pick_sequence, created_at, updated_at)
            VALUES (@locationA, @warehouseId, @zoneId, right(@locationA::text, 12),
                    'bin', 1, now(), now()),
                   (@locationB, @warehouseId, @zoneId, right(@locationB::text, 12),
                    'bin', 2, now(), now());

            INSERT INTO owner (id, code, name, created_at, updated_at)
            VALUES (@ownerId, right(@ownerId::text, 12), 'Owner', now(), now());

            INSERT INTO item (id, sku_code, name_i18n, base_uom, created_at, updated_at)
            VALUES (@itemId, right(@itemId::text, 12), '{"en":"Item"}', 'EACH', now(), now());

            INSERT INTO app_user (id, user_type, display_name, status, security_stamp,
                                  valid_from, created_at, updated_at)
            VALUES (@userId, 'operator', 'Operator', 'active', gen_random_uuid(),
                    current_date, now(), now());
            """);

        command.Parameters.AddWithValue("warehouseId", data.WarehouseId);
        command.Parameters.AddWithValue("zoneId", data.ZoneId);
        command.Parameters.AddWithValue("locationA", data.LocationA);
        command.Parameters.AddWithValue("locationB", data.LocationB);
        command.Parameters.AddWithValue("ownerId", data.OwnerId);
        command.Parameters.AddWithValue("itemId", data.ItemId);
        command.Parameters.AddWithValue("userId", data.UserId);

        await command.ExecuteNonQueryAsync();
        return data;
    }

    private sealed record Fixture(
        Guid WarehouseId,
        Guid ZoneId,
        Guid LocationA,
        Guid LocationB,
        Guid OwnerId,
        Guid ItemId,
        Guid UserId);
}
