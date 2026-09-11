using System.Collections.Concurrent;
using Npgsql;
using Wms.IntegrationTests.Infrastructure;
using Wms.Modules.Tasks.Application;
using Xunit;

namespace Wms.IntegrationTests.Tasks;

/// <summary>
/// Task dispatch through the application (§5.3, §6.8).
/// </summary>
/// <remarks>
/// <c>TaskLeaseConcurrencyTests</c> already races the dispatch <em>query</em>.
/// This races the <em>service</em>, which is what actually ships — a wrapper
/// that opened its own transaction per statement, or dropped
/// <c>SKIP LOCKED</c> on the way through, would leave that query test green
/// while handing two operators the same task.
/// </remarks>
public sealed class LeaseServiceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const int Operators = 8;
    private const int Iterations = 25;

    private readonly LeaseService _leases = new();

    /// <summary>
    /// Phase 1A exit criterion 4, through the code path a device actually
    /// calls.
    /// </summary>
    [Fact]
    public async Task WhenOperatorsRaceForTheSameZone_NoTaskIsLeasedTwice()
    {
        Seeded data = await SeedAsync();
        Guid[] operators = await SeedOperatorsAsync(data, Operators);

        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            await ResetTasksAsync(data, Operators * 3);

            ConcurrentBag<Guid> leased = [];
            using Barrier barrier = new(Operators);

            await Task.WhenAll(operators.Select(async operatorId =>
            {
                // Sequential calls would pass regardless of SKIP LOCKED and
                // prove nothing.
                await Task.Yield();
                barrier.SignalAndWait();

                LeasedWork work = await LeaseAsync(data, operatorId, batchSize: 3);
                foreach (LeasedTask task in work.Tasks)
                {
                    leased.Add(task.Id);
                }
            }));

            Guid[] issued = [.. leased];
            Assert.Equal(issued.Length, issued.Distinct().Count());

            // Assert against the database, not against what the calls
            // returned: the response can be right while the rows are wrong.
            Assert.Equal(issued.Length, await ScalarAsync<long>(
                """
                SELECT count(*) FROM task
                 WHERE warehouse_id = @p AND status = 'leased';
                """,
                data.WarehouseId));

            // Exactly enough work for everyone, so all of it should have gone.
            Assert.Equal(Operators * 3, issued.Length);
        }
    }

    /// <summary>
    /// What <c>SKIP LOCKED</c> actually buys: a poller is never blocked by
    /// another poller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This test exists because the obvious one does not work. Racing N
    /// pollers and asserting "no duplicates" passes <strong>with or
    /// without</strong> <c>SKIP LOCKED</c> — verified by removing it — because
    /// under READ COMMITTED a plain <c>FOR UPDATE</c> blocks, then re-checks
    /// <c>status = 'ready'</c> against a fresh snapshot and drops the row that
    /// was leased while it waited. Duplicate prevention comes from the row
    /// lock plus that predicate re-check, not from <c>SKIP LOCKED</c>.
    /// </para>
    /// <para>
    /// <c>SKIP LOCKED</c> is what stops pollers queueing behind each other. At
    /// the 50-operator scale this system targets, serialised dispatch means
    /// operators standing still waiting for a task list — so the property
    /// worth pinning is latency under contention, which is what this asserts:
    /// with three tasks locked by someone else, a lease must return the next
    /// three <em>immediately</em> rather than wait.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ALeaseIsNotBlockedByTasksAnotherPollerHasLocked()
    {
        Seeded data = await SeedAsync();
        Guid operatorId = (await SeedOperatorsAsync(data, 1))[0];
        await ResetTasksAsync(data, 6);

        // Hold a lock on the first three tasks, exactly as a mid-flight
        // poller would.
        await using NpgsqlConnection holder = await fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction held = await holder.BeginTransactionAsync();

        List<Guid> lockedIds = [];
        await using (NpgsqlCommand hold = new(HoldSql, holder, held))
        {
            hold.Parameters.AddWithValue("warehouseId", data.WarehouseId);
            await using NpgsqlDataReader reader = await hold.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                lockedIds.Add(reader.GetGuid(0));
            }
        }

        Assert.Equal(3, lockedIds.Count);

        // If dispatch blocks instead of skipping, this never returns and the
        // cancellation turns into a failed test rather than a hang.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        LeasedWork work = await LeaseAsync(
            data, operatorId, batchSize: 3, cancellationToken: timeout.Token);

        await held.RollbackAsync();

        Assert.Equal(3, work.Tasks.Count);
        Assert.DoesNotContain(work.Tasks, task => lockedIds.Contains(task.Id));
    }

    private const string HoldSql = """
        SELECT id FROM task
         WHERE status = 'ready' AND warehouse_id = @warehouseId
         ORDER BY priority, sort_sequence
         FOR UPDATE SKIP LOCKED
         LIMIT 3;
        """;

    /// <summary>
    /// "Self-contained by design" (§5.3) — the device must be able to complete
    /// every leased task with no further server contact.
    /// </summary>
    [Fact]
    public async Task ALeasedBatchCarriesEverythingTheDeviceNeedsOffline()
    {
        Seeded data = await SeedAsync();
        Guid operatorId = (await SeedOperatorsAsync(data, 1))[0];
        await ResetTasksAsync(data, 1);

        LeasedWork work = await LeaseAsync(data, operatorId, batchSize: 10);

        LeasedTask task = Assert.Single(work.Tasks);
        LeasedLine line = Assert.Single(task.Lines);

        Assert.NotNull(work.LeaseId);
        Assert.NotNull(work.ExpiresAt);

        // Long enough to survive a shift offline, not a stopwatch on the
        // operator.
        Assert.True(work.ExpiresAt > DateTimeOffset.UtcNow.AddHours(8));

        Assert.Equal(data.SkuCode, line.Item.SkuCode);
        Assert.Equal("Widget", line.Item.Name);
        Assert.Contains(data.Barcode, line.Item.Barcodes);

        // Both ends of the move, with the walk order that makes routing
        // possible at all.
        Assert.NotNull(line.From);
        Assert.NotNull(line.To);
        Assert.Equal(0, line.From!.PickSequence);

        // The reason codes an operator may need while offline travel with the
        // batch — a device that had to fetch PUT_LOCATION_FULL could not
        // report the one situation it most needs to.
        Assert.Contains(work.ReasonCodes, code => code.Code == "PUT_LOCATION_FULL");
        Assert.DoesNotContain(work.ReasonCodes, code => code.Code == "RCV_SUPPLIER_OVER");
    }

    /// <summary>
    /// The item name arrives already in the operator's language, because the
    /// handheld holds no translation table.
    /// </summary>
    [Fact]
    public async Task ItemNamesAreResolvedToTheOperatorsLocale()
    {
        Seeded data = await SeedAsync();
        Guid operatorId = (await SeedOperatorsAsync(data, 1))[0];
        await ExecuteAsync("UPDATE app_user SET locale = 'ja' WHERE id = @p;", operatorId);
        await ResetTasksAsync(data, 1);

        LeasedWork work = await LeaseAsync(data, operatorId, batchSize: 1);

        Assert.Equal("ウィジェット", work.Tasks[0].Lines[0].Item.Name);
        Assert.Contains(work.ReasonCodes, c =>
            c.Code == "PUT_LOCATION_FULL" && c.Label == "指定ロケーション満杯");
    }

    /// <summary>
    /// An idle poller is a normal state, not an error (§5.3).
    /// </summary>
    [Fact]
    public async Task WithNoWorkAvailable_TheLeaseIsEmptyRatherThanAFailure()
    {
        Seeded data = await SeedAsync();
        Guid operatorId = (await SeedOperatorsAsync(data, 1))[0];
        await ResetTasksAsync(data, 0);

        LeasedWork work = await LeaseAsync(data, operatorId, batchSize: 10);

        Assert.Null(work.LeaseId);
        Assert.Empty(work.Tasks);
    }

    [Fact]
    public async Task ReleasingALease_ReturnsUnworkedTasksToTheReadyQueue()
    {
        Seeded data = await SeedAsync();
        Guid operatorId = (await SeedOperatorsAsync(data, 1))[0];
        await ResetTasksAsync(data, 4);

        LeasedWork work = await LeaseAsync(data, operatorId, batchSize: 4);
        Assert.Equal(4, work.Tasks.Count);

        await using (NpgsqlConnection connection = await fixture.DataSource.OpenConnectionAsync())
        {
            await _leases.ReleaseAsync(connection, work.LeaseId!.Value, operatorId, CancellationToken.None);
        }

        Assert.Equal(4, await ScalarAsync<long>(
            "SELECT count(*) FROM task WHERE warehouse_id = @p AND status = 'ready';",
            data.WarehouseId));

        // The lease is gone, not merely ignored: a stale lease id left on the
        // row would confuse any later reclaim.
        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM task WHERE warehouse_id = @p AND lease_id IS NOT NULL;",
            data.WarehouseId));
    }

    /// <summary>
    /// Reclaiming someone else's lease is <c>task.reassign</c>'s job
    /// (<c>/work/tasks/{id}/reclaim</c>, §5.3), not this endpoint's. Without
    /// the <c>lease_user_id</c> match in <c>ReleaseSql</c>, any holder of
    /// <c>task.lease</c> could name another operator's lease id here and
    /// silently pull their in-progress work back to the ready queue.
    /// </summary>
    [Fact]
    public async Task ReleasingALease_DoesNothingForAnotherOperatorsLease()
    {
        Seeded data = await SeedAsync();
        Guid[] operators = await SeedOperatorsAsync(data, 2);
        Guid owner = operators[0];
        Guid bystander = operators[1];
        await ResetTasksAsync(data, 4);

        LeasedWork work = await LeaseAsync(data, owner, batchSize: 4);
        Assert.Equal(4, work.Tasks.Count);

        await using (NpgsqlConnection connection = await fixture.DataSource.OpenConnectionAsync())
        {
            int affected = await _leases.ReleaseAsync(
                connection, work.LeaseId!.Value, bystander, CancellationToken.None);
            Assert.Equal(0, affected);
        }

        // Still leased — untouched by the bystander's attempt.
        Assert.Equal(4, await ScalarAsync<long>(
            "SELECT count(*) FROM task WHERE warehouse_id = @p AND status = 'leased';",
            data.WarehouseId));
    }

    [Fact]
    public async Task AZoneFilter_ExcludesWorkInOtherZones()
    {
        Seeded data = await SeedAsync();
        Guid operatorId = (await SeedOperatorsAsync(data, 1))[0];
        await ResetTasksAsync(data, 3);

        LeasedWork elsewhere = await LeaseAsync(
            data, operatorId, batchSize: 10, zoneIds: [Guid.CreateVersion7()]);

        Assert.Empty(elsewhere.Tasks);

        LeasedWork here = await LeaseAsync(data, operatorId, batchSize: 10, zoneIds: [data.ZoneId]);
        Assert.Equal(3, here.Tasks.Count);
    }

    private async Task<LeasedWork> LeaseAsync(
        Seeded data,
        Guid operatorId,
        int batchSize,
        IReadOnlyList<Guid>? zoneIds = null,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection =
            await fixture.DataSource.OpenConnectionAsync(cancellationToken);
        return await _leases.LeaseAsync(
            connection,
            new LeaseRequest(
                WarehouseId: data.WarehouseId,
                TaskTypes: ["putaway"],
                ZoneIds: zoneIds ?? [],
                BatchSize: batchSize,
                UserId: operatorId,
                DeviceId: null),
            cancellationToken);
    }

    private async Task ResetTasksAsync(Seeded data, int readyTasks)
    {
        await ExecuteAsync(
            """
            DELETE FROM task_line WHERE task_id IN (SELECT id FROM task WHERE warehouse_id = @p);
            DELETE FROM task WHERE warehouse_id = @p;
            """,
            data.WarehouseId);

        for (int i = 0; i < readyTasks; i++)
        {
            Guid taskId = Guid.CreateVersion7();

            await using NpgsqlCommand command = fixture.DataSource.CreateCommand(
                """
                INSERT INTO task (id, warehouse_id, zone_id, task_type, status,
                                  priority, sort_sequence, created_at)
                VALUES (@taskId, @warehouseId, @zoneId, 'putaway', 'ready', 100, @seq, now());

                INSERT INTO task_line (id, task_id, line_no, owner_id, item_id, lot_id,
                                       from_location_id, to_location_id,
                                       requested_quantity, uom_code, status)
                VALUES (gen_random_uuid(), @taskId, 1, @ownerId, @itemId,
                        '00000000-0000-0000-0000-000000000000',
                        @receivingId, @binId, 10, 'EACH', 'pending');
                """);
            command.Parameters.AddWithValue("taskId", taskId);
            command.Parameters.AddWithValue("warehouseId", data.WarehouseId);
            command.Parameters.AddWithValue("zoneId", data.ZoneId);
            command.Parameters.AddWithValue("seq", i);
            command.Parameters.AddWithValue("ownerId", data.OwnerId);
            command.Parameters.AddWithValue("itemId", data.ItemId);
            command.Parameters.AddWithValue("receivingId", data.ReceivingId);
            command.Parameters.AddWithValue("binId", data.BinId);
            await command.ExecuteNonQueryAsync();
        }
    }

    private async Task<Guid[]> SeedOperatorsAsync(Seeded data, int count)
    {
        Guid[] ids = [.. Enumerable.Range(0, count).Select(_ => Guid.CreateVersion7())];

        foreach (Guid id in ids)
        {
            await using NpgsqlCommand command = fixture.DataSource.CreateCommand(
                """
                INSERT INTO app_user (id, user_type, display_name, status, security_stamp,
                                      valid_from, created_at, updated_at)
                VALUES (@id, 'operator', 'Op', 'active', gen_random_uuid(),
                        current_date, now(), now());
                """);
            command.Parameters.AddWithValue("id", id);
            await command.ExecuteNonQueryAsync();
        }

        return ids;
    }

    private async Task ExecuteAsync(string sql, object parameter)
    {
        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("p", parameter);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, object parameter)
    {
        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("p", parameter);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task<Seeded> SeedAsync()
    {
        Seeded data = new(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(
            """
            INSERT INTO warehouse (id, code, name, timezone, created_at, updated_at)
            VALUES (@warehouseId, right(@warehouseId::text, 12), 'T', 'Asia/Tokyo', now(), now());

            INSERT INTO zone (id, warehouse_id, code, name_i18n, zone_type, created_at, updated_at)
            VALUES (@zoneId, @warehouseId, 'A', '{"en":"A"}', 'bulk', now(), now());

            INSERT INTO location (id, warehouse_id, zone_id, code, location_type,
                                  pick_sequence, created_at, updated_at)
            VALUES (@receivingId, @warehouseId, @zoneId, right(@receivingId::text, 12),
                    'staging', 0, now(), now()),
                   (@binId, @warehouseId, @zoneId, right(@binId::text, 12),
                    'bin', 10, now(), now());

            INSERT INTO owner (id, code, name, created_at, updated_at)
            VALUES (@ownerId, right(@ownerId::text, 12), 'O', now(), now());

            INSERT INTO item (id, sku_code, name_i18n, base_uom, created_at, updated_at)
            VALUES (@itemId, @skuCode,
                    '{"en":"Widget","ja":"ウィジェット"}', 'EACH', now(), now());

            INSERT INTO item_uom (id, item_id, uom_code, qty_in_base, is_discrete,
                                  created_at, updated_at)
            VALUES (gen_random_uuid(), @itemId, 'EACH', 1, true, now(), now());

            INSERT INTO item_barcode (id, item_id, item_uom_id, barcode, created_at, updated_at)
            SELECT gen_random_uuid(), @itemId, iu.id, @barcode, now(), now()
              FROM item_uom iu WHERE iu.item_id = @itemId AND iu.uom_code = 'EACH';
            """);

        command.Parameters.AddWithValue("warehouseId", data.WarehouseId);
        command.Parameters.AddWithValue("zoneId", data.ZoneId);
        command.Parameters.AddWithValue("receivingId", data.ReceivingId);
        command.Parameters.AddWithValue("binId", data.BinId);
        command.Parameters.AddWithValue("ownerId", data.OwnerId);
        command.Parameters.AddWithValue("itemId", data.ItemId);
        command.Parameters.AddWithValue("skuCode", data.SkuCode);
        command.Parameters.AddWithValue("barcode", data.Barcode);

        await command.ExecuteNonQueryAsync();
        return data;
    }

    private sealed record Seeded(
        Guid WarehouseId,
        Guid ZoneId,
        Guid ReceivingId,
        Guid BinId,
        Guid OwnerId,
        Guid ItemId)
    {
        // Unique per seed. Tests share a container, so a literal SKU or
        // barcode here collides with every other test in the class — the same
        // trap as asserting on a global row count.
        public string SkuCode { get; } = $"SKU-{Guid.CreateVersion7():N}"[..20];

        public string Barcode { get; } = Guid.CreateVersion7().ToString("N")[..13];
    }
}
