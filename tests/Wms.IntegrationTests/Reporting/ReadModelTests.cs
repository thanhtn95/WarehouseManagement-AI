using Npgsql;
using Wms.IntegrationTests.Infrastructure;
using Wms.Modules.Inbound.Contracts;
using Wms.Modules.Inbound.Infrastructure;
using Wms.Modules.Inventory.Contracts;
using Wms.Modules.Inventory.Infrastructure;
using Wms.Modules.Tasks.Contracts;
using Wms.Modules.Tasks.Infrastructure;
using Xunit;

namespace Wms.IntegrationTests.Reporting;

/// <summary>
/// The admin site's read surfaces (§5.5, §5.12).
/// </summary>
public sealed class ReadModelTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private IInventoryReadModel Inventory => new InventoryReadModel(fixture.DataSource);

    private IInboundReadModel Inbound => new InboundReadModel(fixture.DataSource);

    private ITaskReadModel Tasks => new TaskReadModel(fixture.DataSource);

    /// <summary>
    /// Three quantities, never two. <c>available</c> is computed at read time.
    /// </summary>
    [Fact]
    public async Task Balances_ComputeAvailableAndReportNegativesHonestly()
    {
        Seeded data = await SeedAsync();
        await SetBalanceAsync(data, data.BinA, onHand: 88m, allocated: 12m);
        await SetBalanceAsync(data, data.BinB, onHand: -5m, allocated: 0m);

        BalancePage page = await Inventory.GetBalancesAsync(
            Query(data, limit: 50), CancellationToken.None);

        BalanceRow stocked = page.Items.Single(r => r.LocationId == data.BinA);
        Assert.Equal(88m, stocked.OnHand);
        Assert.Equal(12m, stocked.Allocated);
        Assert.Equal(76m, stocked.Available);

        // A negative balance is true information and must not be hidden or
        // clamped — it is precisely what the ledger exists to surface (C1, C3).
        BalanceRow negative = page.Items.Single(r => r.LocationId == data.BinB);
        Assert.Equal(-5m, negative.OnHand);

        // Totals span the whole filtered set, not just the page.
        Assert.Equal(83m, page.Totals.OnHand);
        Assert.Equal(71m, page.Totals.Available);
    }

    /// <summary>
    /// Keyset pagination (§3.4): every row exactly once, no duplicates and no
    /// gaps across page boundaries.
    /// </summary>
    /// <remarks>
    /// The failure this guards is subtle — an unstable or non-unique sort key
    /// lets rows repeat on one page and vanish from another, so a stock report
    /// silently disagrees with itself depending on page size.
    /// </remarks>
    [Fact]
    public async Task Balances_PageThroughWithoutRepeatingOrSkippingRows()
    {
        Seeded data = await SeedAsync();
        Guid[] bins = await SeedBinsAsync(data, 7);

        foreach (Guid bin in bins)
        {
            await SetBalanceAsync(data, bin, onHand: 10m, allocated: 0m);
        }

        List<Guid> seen = [];
        string? cursor = null;

        do
        {
            BalancePage page = await Inventory.GetBalancesAsync(
                Query(data, limit: 2) with { AfterKey = cursor }, CancellationToken.None);

            seen.AddRange(page.Items.Select(r => r.LocationId));
            cursor = page.HasMore ? page.NextAfterKey : null;
        }
        while (cursor is not null);

        Assert.Equal(bins.Length, seen.Count);
        Assert.Equal(bins.Length, seen.Distinct().Count());
        Assert.Equal([.. bins.Order()], [.. seen.Order()]);
    }

    [Fact]
    public async Task Balances_HideZeroPositionsByDefault_ButNotNegativeOnes()
    {
        Seeded data = await SeedAsync();
        await SetBalanceAsync(data, data.BinA, onHand: 0m, allocated: 0m);
        await SetBalanceAsync(data, data.BinB, onHand: -3m, allocated: 0m);

        BalancePage hidden = await Inventory.GetBalancesAsync(
            Query(data, limit: 50), CancellationToken.None);

        Assert.DoesNotContain(hidden.Items, r => r.LocationId == data.BinA);
        Assert.Contains(hidden.Items, r => r.LocationId == data.BinB);

        BalancePage shown = await Inventory.GetBalancesAsync(
            Query(data, limit: 50) with { IncludeZero = true }, CancellationToken.None);

        Assert.Contains(shown.Items, r => r.LocationId == data.BinA);
    }

    /// <summary>
    /// The summary block is independent of the list's filters.
    /// </summary>
    /// <remarks>
    /// A supervisor filtering to one exception type still needs to see the
    /// total open count, or the filter becomes a way to accidentally hide
    /// work — the opposite of what a work surface is for.
    /// </remarks>
    [Fact]
    public async Task Exceptions_SummaryCountsEverythingOpen_EvenWhenTheListIsFiltered()
    {
        Seeded data = await SeedAsync();
        await RaiseExceptionAsync(data, "over_receipt");
        await RaiseExceptionAsync(data, "over_receipt");
        await RaiseExceptionAsync(data, "location_mismatch");

        ExceptionPage filtered = await Inventory.GetExceptionsAsync(
            new ExceptionQuery(data.WarehouseId, "open", "location_mismatch", null, 50),
            CancellationToken.None);

        Assert.Single(filtered.Items);
        Assert.Equal("location_mismatch", filtered.Items[0].ExceptionType);

        Assert.Equal(3, filtered.Summary.Open);
        Assert.Equal(2, filtered.Summary.ByType["over_receipt"]);
        Assert.Equal(1, filtered.Summary.ByType["location_mismatch"]);
        Assert.NotNull(filtered.Summary.OldestAgeHours);
    }

    [Fact]
    public async Task Exceptions_CarryTheContextASupervisorNeedsToAct()
    {
        Seeded data = await SeedAsync();
        await RaiseExceptionAsync(data, "over_receipt", expected: 100m, actual: 106m);

        ExceptionPage page = await Inventory.GetExceptionsAsync(
            new ExceptionQuery(data.WarehouseId, "open", null, null, 50), CancellationToken.None);

        ExceptionRow row = Assert.Single(page.Items);
        Assert.Equal(100m, row.ExpectedQuantity);
        Assert.Equal(106m, row.ActualQuantity);
        Assert.Equal(6m, row.Difference);

        // Enough to walk to the right bin and know what to count.
        Assert.NotNull(row.SkuCode);
        Assert.NotNull(row.LocationCode);
        Assert.NotNull(row.RaisedByDisplayName);
    }

    /// <summary>
    /// The dashboard composes four modules and each answers for its own data.
    /// </summary>
    [Fact]
    public async Task DashboardFigures_ComeFromEachModulesOwnReadModel()
    {
        Seeded data = await SeedAsync();
        await SetBalanceAsync(data, data.BinA, onHand: 40m, allocated: 0m);
        await RaiseExceptionAsync(data, "over_receipt");
        await SeedTasksAsync(data, ready: 3, leased: 1);

        ReceivingSummary receiving =
            await Inbound.GetReceivingSummaryAsync(data.WarehouseId, CancellationToken.None);
        PutawaySummary putaway =
            await Tasks.GetPutawaySummaryAsync(data.WarehouseId, CancellationToken.None);
        ExceptionSummary exceptions =
            (await Inventory.GetExceptionsAsync(
                new ExceptionQuery(data.WarehouseId, "open", null, null, 0),
                CancellationToken.None)).Summary;
        StockSummary stock =
            await Inventory.GetStockSummaryAsync(data.WarehouseId, CancellationToken.None);

        Assert.Equal(3, putaway.TasksReady);
        Assert.Equal(1, putaway.TasksLeased);
        Assert.Equal(1, exceptions.Open);
        Assert.Equal(1, stock.SkuCount);
        Assert.Equal(40m, stock.OnHand);

        // No receipts were opened, so receiving reports zero rather than
        // failing — a dashboard for a quiet warehouse is still a dashboard.
        Assert.Equal(0, receiving.OpenReceipts);
    }

    /// <summary>
    /// The ledger view: ordered by <c>sequence</c>, signed for the reader, and
    /// carrying the device's own clock for forensics.
    /// </summary>
    /// <remarks>
    /// Ordering by <c>recorded_at</c> instead would be wrong in a way that is
    /// invisible on small data: two movements can share a millisecond, so a
    /// time-ordered cursor repeats or skips rows (C4, invariant 5).
    /// </remarks>
    [Fact]
    public async Task Movements_AreOrderedBySequenceAndSignedForTheReader()
    {
        Seeded data = await SeedAsync();
        DateTimeOffset deviceClock = new(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);

        await PostMovementAsync(data, to: data.BinA, quantity: 100m, type: "receipt",
            deviceReportedAt: deviceClock);
        await PostMovementAsync(data, from: data.BinA, quantity: 12m, type: "pick");

        MovementPage page = await Inventory.GetMovementsAsync(
            new MovementQuery(data.WarehouseId, null, null, null, null, null, null, 50),
            CancellationToken.None);

        Assert.Equal(2, page.Items.Count);
        Assert.True(page.Items[0].Sequence < page.Items[1].Sequence);

        // Stock arriving is positive; stock leaving with no destination is
        // negative, so the ledger reads the way an accountant expects.
        Assert.Equal(100m, page.Items[0].QuantityBase);
        Assert.Equal(-12m, page.Items[1].QuantityBase);

        // The handheld's own clock survived the round trip. It is forensic —
        // the answer to "what did the device think the time was" when a clock
        // turns out to be wrong — and is never used for ordering.
        Assert.Equal(deviceClock, page.Items[0].DeviceReportedAt);
        Assert.Null(page.Items[1].DeviceReportedAt);

        Assert.NotEqual(default, page.Items[0].RecordedAt);
        Assert.Equal("Tanaka", page.Items[0].ActorDisplayName);
    }

    [Fact]
    public async Task Movements_PageBySequenceWithoutRepeatingOrSkipping()
    {
        Seeded data = await SeedAsync();
        for (int i = 0; i < 5; i++)
        {
            await PostMovementAsync(data, to: data.BinA, quantity: 1m, type: "receipt");
        }

        List<Guid> seen = [];
        long? cursor = null;

        do
        {
            MovementPage page = await Inventory.GetMovementsAsync(
                new MovementQuery(data.WarehouseId, null, null, null, null, null, cursor, 2),
                CancellationToken.None);

            seen.AddRange(page.Items.Select(m => m.Id));
            cursor = page.HasMore ? page.NextAfterSequence : null;
        }
        while (cursor is not null);

        Assert.Equal(5, seen.Count);
        Assert.Equal(5, seen.Distinct().Count());
    }

    /// <summary>
    /// When the server clock disagrees with the ledger's own order, the ledger
    /// order wins.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the test that actually pins "ordered by <c>sequence</c>, never
    /// by timestamp" (C4, invariant 5), and it took two attempts to write one
    /// that does. Paging N movements posted in separate transactions passes
    /// either way, because <c>recorded_at</c> then rises with the sequence.
    /// Posting them in one transaction — so they share an instant exactly —
    /// also passes, because Postgres happens to break that tie consistently
    /// here. Neither discriminates, so neither is evidence.
    /// </para>
    /// <para>
    /// Making the two orders genuinely <em>disagree</em> does. A clock that
    /// steps backwards, or a partition restored out of order, produces exactly
    /// this: a later movement carrying an earlier timestamp. Order by time and
    /// the ledger reads in the wrong order and the keyset cursor walks a
    /// different sequence than it reports.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Movements_AreOrderedBySequence_EvenWhenTimestampsDisagree()
    {
        Seeded data = await SeedAsync();
        await PostBatchInOneTransactionAsync(data, count: 3);

        // Drag the LAST movement's clock behind the first. Only a test may do
        // this: stock_movement is append-only in production (docs/shortcuts.md
        // tracks that it is not yet enforced by a database grant).
        await ExecuteAsync(
            """
            UPDATE stock_movement SET recorded_at = recorded_at - interval '1 hour'
             WHERE sequence = (SELECT max(sm.sequence) FROM stock_movement sm
                                 JOIN location l ON l.id = sm.to_location_id
                                WHERE l.warehouse_id = @p);
            """,
            data.WarehouseId);

        MovementPage page = await Inventory.GetMovementsAsync(
            new MovementQuery(data.WarehouseId, null, null, null, null, null, null, 50),
            CancellationToken.None);

        Assert.Equal(3, page.Items.Count);

        // Ascending by sequence, regardless of what the clocks say.
        Assert.Equal(
            [.. page.Items.Select(m => m.Sequence).Order()],
            [.. page.Items.Select(m => m.Sequence)]);

        // And the row with the oldest timestamp is genuinely last, which is
        // what makes this test able to fail.
        Assert.Equal(
            page.Items.MinBy(m => m.RecordedAt)!.Sequence,
            page.Items[^1].Sequence);
    }

    private async Task PostBatchInOneTransactionAsync(Seeded data, int count)
    {
        await using NpgsqlConnection connection = await fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();

        StockLedger ledger = new(new BusinessCalendar());
        for (int i = 0; i < count; i++)
        {
            await ledger.PostAsync(
                connection,
                transaction,
                new PostMovementCommand(
                    WarehouseId: data.WarehouseId,
                    OwnerId: data.OwnerId,
                    ItemId: data.ItemId,
                    LotId: Guid.Empty,
                    StockStatus: "available",
                    FromLocationId: null,
                    ToLocationId: data.BinA,
                    QuantityBase: 1m,
                    EnteredQuantity: 1m,
                    EnteredUom: "EACH",
                    MovementType: "receipt",
                    ReasonCode: null,
                    ReferenceType: null,
                    ReferenceId: null,
                    ActorUserId: data.UserId,
                    DeviceId: null,
                    IdempotencyKey: Guid.CreateVersion7().ToString()),
                CancellationToken.None);
        }

        await transaction.CommitAsync();
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

    [Fact]
    public async Task Receipts_ListCarriesLineCountsWithoutAQueryPerRow()
    {
        Seeded data = await SeedAsync();
        Guid receiptId = await SeedReceiptAsync(data, lines: 3, confirmed: 2, discrepant: 1);

        ReceiptPage page = await Inbound.GetReceiptsAsync(
            new ReceiptQuery(data.WarehouseId, null, null, null, null, 50, null),
            CancellationToken.None);

        ReceiptRow row = Assert.Single(page.Items);
        Assert.Equal(receiptId, row.Id);
        Assert.Equal(3, row.LinesTotal);
        Assert.Equal(2, row.LinesConfirmed);
        Assert.Equal(1, row.DiscrepancyCount);
    }

    [Fact]
    public async Task ReceiptDetail_ReturnsItsLines_AndNullForAnUnknownId()
    {
        Seeded data = await SeedAsync();
        Guid receiptId = await SeedReceiptAsync(data, lines: 2, confirmed: 1, discrepant: 0);

        ReceiptDetail? detail = await Inbound.GetReceiptAsync(receiptId, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(2, detail!.Lines.Count);
        Assert.Equal([1, 2], detail.Lines.Select(l => l.LineNo));
        Assert.Equal("Widget", detail.Lines[0].ItemName);

        Assert.Null(await Inbound.GetReceiptAsync(Guid.CreateVersion7(), CancellationToken.None));
    }

    private async Task<Guid> SeedReceiptAsync(Seeded data, int lines, int confirmed, int discrepant)
    {
        Guid receiptId = Guid.CreateVersion7();

        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(
            """
            INSERT INTO receipt (id, warehouse_id, receipt_number, receipt_type, owner_id,
                                 status, created_by, created_at, updated_at)
            VALUES (@receiptId, @warehouseId, right(@receiptId::text, 12), 'against_asn',
                    @ownerId, 'in_progress', @userId, now(), now());

            INSERT INTO receipt_line (id, receipt_id, line_no, item_id, owner_id,
                                      expected_quantity, received_quantity,
                                      discrepancy_type, uom_code)
            SELECT gen_random_uuid(), @receiptId, g, @itemId, @ownerId,
                   10,
                   CASE WHEN g <= @confirmed THEN 10 ELSE NULL END,
                   CASE WHEN g <= @discrepant THEN 'over' ELSE NULL END,
                   'EACH'
              FROM generate_series(1, @lines) AS g;
            """);
        command.Parameters.AddWithValue("receiptId", receiptId);
        command.Parameters.AddWithValue("warehouseId", data.WarehouseId);
        command.Parameters.AddWithValue("ownerId", data.OwnerId);
        command.Parameters.AddWithValue("itemId", data.ItemId);
        command.Parameters.AddWithValue("userId", data.UserId);
        command.Parameters.AddWithValue("lines", lines);
        command.Parameters.AddWithValue("confirmed", confirmed);
        command.Parameters.AddWithValue("discrepant", discrepant);

        await command.ExecuteNonQueryAsync();
        return receiptId;
    }

    private async Task PostMovementAsync(
        Seeded data,
        decimal quantity,
        string type,
        Guid? from = null,
        Guid? to = null,
        DateTimeOffset? deviceReportedAt = null)
    {
        await using NpgsqlConnection connection = await fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();

        await new StockLedger(new BusinessCalendar()).PostAsync(
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
                IdempotencyKey: Guid.CreateVersion7().ToString(),
                DeviceReportedAt: deviceReportedAt),
            CancellationToken.None);

        await transaction.CommitAsync();
    }

    private static BalanceQuery Query(Seeded data, int limit) =>
        new(data.WarehouseId, null, null, null, null, null, false, limit, null);

    private async Task<Guid[]> SeedBinsAsync(Seeded data, int count)
    {
        Guid[] ids = [.. Enumerable.Range(0, count).Select(_ => Guid.CreateVersion7())];

        foreach ((Guid id, int index) in ids.Select((id, i) => (id, i)))
        {
            await using NpgsqlCommand command = fixture.DataSource.CreateCommand(
                """
                INSERT INTO location (id, warehouse_id, zone_id, code, location_type,
                                      pick_sequence, created_at, updated_at)
                VALUES (@id, @warehouseId, @zoneId, right(@id::text, 12), 'bin',
                        @seq, now(), now());
                """);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("warehouseId", data.WarehouseId);
            command.Parameters.AddWithValue("zoneId", data.ZoneId);
            command.Parameters.AddWithValue("seq", 100 + index);
            await command.ExecuteNonQueryAsync();
        }

        return ids;
    }

    private async Task SetBalanceAsync(Seeded data, Guid locationId, decimal onHand, decimal allocated)
    {
        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(
            """
            INSERT INTO stock_balance (owner_id, item_id, location_id, lot_id, stock_status,
                                       on_hand, allocated, version, updated_at)
            VALUES (@ownerId, @itemId, @locationId,
                    '00000000-0000-0000-0000-000000000000', 'available',
                    @onHand, @allocated, 1, now());
            """);
        command.Parameters.AddWithValue("ownerId", data.OwnerId);
        command.Parameters.AddWithValue("itemId", data.ItemId);
        command.Parameters.AddWithValue("locationId", locationId);
        command.Parameters.AddWithValue("onHand", onHand);
        command.Parameters.AddWithValue("allocated", allocated);
        await command.ExecuteNonQueryAsync();
    }

    private async Task RaiseExceptionAsync(
        Seeded data, string type, decimal? expected = null, decimal? actual = null)
    {
        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(
            """
            INSERT INTO inventory_exception (id, exception_type, severity, warehouse_id,
                                             owner_id, item_id, location_id,
                                             expected_quantity, actual_quantity,
                                             raised_at, raised_by_user_id, status)
            VALUES (gen_random_uuid(), @type, 'medium', @warehouseId,
                    @ownerId, @itemId, @locationId,
                    @expected, @actual, now(), @userId, 'open');
            """);
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("warehouseId", data.WarehouseId);
        command.Parameters.AddWithValue("ownerId", data.OwnerId);
        command.Parameters.AddWithValue("itemId", data.ItemId);
        command.Parameters.AddWithValue("locationId", data.BinA);
        command.Parameters.AddWithValue("expected", (object?)expected ?? DBNull.Value);
        command.Parameters.AddWithValue("actual", (object?)actual ?? DBNull.Value);
        command.Parameters.AddWithValue("userId", data.UserId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task SeedTasksAsync(Seeded data, int ready, int leased)
    {
        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(
            """
            INSERT INTO task (id, warehouse_id, zone_id, task_type, status,
                              priority, sort_sequence, created_at)
            SELECT gen_random_uuid(), @warehouseId, @zoneId, 'putaway', 'ready', 100, g, now()
              FROM generate_series(1, @ready) AS g;

            INSERT INTO task (id, warehouse_id, zone_id, task_type, status,
                              priority, sort_sequence, created_at)
            SELECT gen_random_uuid(), @warehouseId, @zoneId, 'putaway', 'leased', 100, g, now()
              FROM generate_series(1, @leased) AS g;
            """);
        command.Parameters.AddWithValue("warehouseId", data.WarehouseId);
        command.Parameters.AddWithValue("zoneId", data.ZoneId);
        command.Parameters.AddWithValue("ready", ready);
        command.Parameters.AddWithValue("leased", leased);
        await command.ExecuteNonQueryAsync();
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
            VALUES (@binA, @warehouseId, @zoneId, right(@binA::text, 12), 'bin', 1, now(), now()),
                   (@binB, @warehouseId, @zoneId, right(@binB::text, 12), 'bin', 2, now(), now());

            INSERT INTO owner (id, code, name, created_at, updated_at)
            VALUES (@ownerId, right(@ownerId::text, 12), 'O', now(), now());

            INSERT INTO item (id, sku_code, name_i18n, base_uom, created_at, updated_at)
            VALUES (@itemId, right(@itemId::text, 12), '{"en":"Widget"}', 'EACH', now(), now());

            INSERT INTO app_user (id, user_type, display_name, status, security_stamp,
                                  valid_from, created_at, updated_at)
            VALUES (@userId, 'operator', 'Tanaka', 'active', gen_random_uuid(),
                    current_date, now(), now());
            """);

        command.Parameters.AddWithValue("warehouseId", data.WarehouseId);
        command.Parameters.AddWithValue("zoneId", data.ZoneId);
        command.Parameters.AddWithValue("binA", data.BinA);
        command.Parameters.AddWithValue("binB", data.BinB);
        command.Parameters.AddWithValue("ownerId", data.OwnerId);
        command.Parameters.AddWithValue("itemId", data.ItemId);
        command.Parameters.AddWithValue("userId", data.UserId);

        await command.ExecuteNonQueryAsync();
        return data;
    }

    private sealed record Seeded(
        Guid WarehouseId,
        Guid ZoneId,
        Guid BinA,
        Guid BinB,
        Guid OwnerId,
        Guid ItemId)
    {
        public Guid UserId { get; } = Guid.CreateVersion7();
    }
}
