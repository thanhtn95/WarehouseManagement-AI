using Npgsql;
using Wms.IntegrationTests.Infrastructure;
using Wms.Modules.Inbound.Application;
using Wms.Modules.Inventory.Contracts;
using Wms.Modules.Inventory.Infrastructure;
using Wms.Modules.Platform.Infrastructure;
using Wms.SharedKernel;
using Xunit;

namespace Wms.IntegrationTests.Receiving;

/// <summary>
/// The receipt lifecycle, and the step that turns received stock into
/// directed work: <c>POST /receipts/{id}/complete</c> (§5.4, §6.2 step 10).
/// </summary>
/// <remarks>
/// §5.4 is emphatic that "putaway tasks are generated for <strong>actual</strong>
/// received quantities, never expected" — which is the whole reason the
/// over-receipt path exists. A system that generated putaway for the expected
/// 100 after counting 106 would strand six units in receiving with nothing
/// telling anyone they are there.
/// </remarks>
public sealed class ReceiptLifecycleTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private readonly ReceiptService _receipts = new(new BusinessCalendar());

    private readonly ConfirmReceiptFactHandler _confirm = new(
        new StockLedger(new BusinessCalendar()), new IdempotencyStore(), new Outbox());

    [Fact]
    public async Task ReceiptNumber_IsSequentialPerWarehouseBusinessDay()
    {
        Seeded data = await SeedAsync();

        CreatedReceipt first = await CreateAsync(data);
        CreatedReceipt second = await CreateAsync(data);

        // RCV-<warehouse>-<yyMMdd>-<0001>
        Assert.StartsWith($"RCV-{data.WarehouseCode}-", first.ReceiptNumber, StringComparison.Ordinal);
        Assert.EndsWith("-0001", first.ReceiptNumber, StringComparison.Ordinal);
        Assert.EndsWith("-0002", second.ReceiptNumber, StringComparison.Ordinal);
        Assert.Equal("draft", first.Status);
    }

    /// <summary>
    /// Phase 1A exit criterion 1, end to end through the application: receive
    /// stock, close the receipt, and get directed putaway work for what
    /// actually arrived.
    /// </summary>
    [Fact]
    public async Task CompletingAnOverReceipt_GeneratesPutawayForTheActualQuantity()
    {
        Seeded data = await SeedAsync();
        CreatedReceipt receipt = await CreateAsync(data, expectedQuantity: 100m);

        await StartAsync(receipt.Id);
        await ConfirmAsync(data, receipt.LineIds[0], quantity: 106m);

        ReceiptCompletion completion = await CompleteAsync(receipt.Id);

        Assert.Equal(ReceiptOutcome.Succeeded, completion.Outcome);
        Assert.Equal(1, completion.PutawayTasksCreated);

        // 106, not the expected 100. Generating for the expectation would
        // strand six units in receiving with nothing pointing at them.
        Assert.Equal(106m, await ScalarAsync<decimal>(
            """
            SELECT tl.requested_quantity
              FROM task_line tl JOIN task t ON t.id = tl.task_id
             WHERE t.reference_id = @p;
            """,
            receipt.Id));

        // The task moves stock OUT of where it actually is — the receiving
        // bay the device named — not out of a location assumed by config.
        Assert.Equal(data.ReceivingId, await ScalarAsync<Guid>(
            """
            SELECT tl.from_location_id
              FROM task_line tl JOIN task t ON t.id = tl.task_id
             WHERE t.reference_id = @p;
            """,
            receipt.Id));

        // First empty bin by pick sequence — the Phase 1A strategy.
        Assert.Equal(data.FirstBinId, await ScalarAsync<Guid>(
            """
            SELECT tl.to_location_id
              FROM task_line tl JOIN task t ON t.id = tl.task_id
             WHERE t.reference_id = @p;
            """,
            receipt.Id));

        Assert.Equal("received", await ScalarAsync<string>(
            "SELECT status FROM receipt WHERE id = @p;", receipt.Id));
        Assert.Equal("ready", await ScalarAsync<string>(
            "SELECT status FROM task WHERE reference_id = @p;", receipt.Id));
    }

    /// <summary>
    /// A command may be refused — and this one should be, because nothing
    /// physical has happened and the supervisor can act on the answer.
    /// </summary>
    [Fact]
    public async Task CompletingWithUncountedLines_IsRefusedAndNamesThem()
    {
        Seeded data = await SeedAsync();
        CreatedReceipt receipt = await CreateAsync(
            data, expectedQuantity: 10m, extraLines: 2);

        await StartAsync(receipt.Id);
        await ConfirmAsync(data, receipt.LineIds[0], quantity: 10m);

        ReceiptCompletion completion = await CompleteAsync(receipt.Id);

        Assert.Equal(ReceiptOutcome.LinesUnconfirmed, completion.Outcome);
        Assert.Equal([2, 3], completion.UnconfirmedLineNos);

        // Refused means refused: no tasks, and the receipt stays open.
        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM task WHERE reference_id = @p;", receipt.Id));
        Assert.Equal("in_progress", await ScalarAsync<string>(
            "SELECT status FROM receipt WHERE id = @p;", receipt.Id));
    }

    /// <summary>
    /// A line counted as zero has been counted. "The pallet was empty" is a
    /// result, not an omission, and must not block completion.
    /// </summary>
    [Fact]
    public async Task ALineCountedAsZero_DoesNotBlockCompletion()
    {
        Seeded data = await SeedAsync();
        CreatedReceipt receipt = await CreateAsync(data, expectedQuantity: 10m);

        await StartAsync(receipt.Id);
        await ConfirmAsync(data, receipt.LineIds[0], quantity: 0m);

        ReceiptCompletion completion = await CompleteAsync(receipt.Id);

        Assert.Equal(ReceiptOutcome.Succeeded, completion.Outcome);

        // Nothing arrived, so there is nothing to put away.
        Assert.Equal(0, completion.PutawayTasksCreated);
    }

    [Fact]
    public async Task StartingAReceiptTwice_IsRefusedAsAnInvalidTransition()
    {
        Seeded data = await SeedAsync();
        CreatedReceipt receipt = await CreateAsync(data);

        Assert.Equal(ReceiptOutcome.Succeeded, (await StartAsync(receipt.Id)).Outcome);

        ReceiptTransition second = await StartAsync(receipt.Id);
        Assert.Equal(ReceiptOutcome.InvalidTransition, second.Outcome);
        Assert.Equal("in_progress", second.FromStatus);
    }

    [Fact]
    public async Task CompletingADraftReceipt_IsRefused()
    {
        Seeded data = await SeedAsync();
        CreatedReceipt receipt = await CreateAsync(data);

        ReceiptCompletion completion = await CompleteAsync(receipt.Id);

        Assert.Equal(ReceiptOutcome.InvalidTransition, completion.Outcome);
        Assert.Equal("draft", completion.FromStatus);
    }

    private async Task<CreatedReceipt> CreateAsync(
        Seeded data, decimal? expectedQuantity = null, int extraLines = 0)
    {
        List<CreateReceiptLine> lines = [new(data.ItemId, expectedQuantity, "EACH")];
        for (int i = 0; i < extraLines; i++)
        {
            lines.Add(new CreateReceiptLine(data.ItemId, expectedQuantity, "EACH"));
        }

        await using NpgsqlConnection connection = await fixture.DataSource.OpenConnectionAsync();
        return await _receipts.CreateAsync(
            connection,
            new CreateReceiptCommand(
                WarehouseId: data.WarehouseId,
                ReceiptType: "against_asn",
                OwnerId: data.OwnerId,
                SupplierReference: "PO-TEST",
                ExpectedAt: null,
                ActorUserId: data.UserId,
                Lines: lines),
            CancellationToken.None);
    }

    private async Task<ReceiptTransition> StartAsync(Guid receiptId)
    {
        await using NpgsqlConnection connection = await fixture.DataSource.OpenConnectionAsync();
        return await _receipts.StartAsync(connection, receiptId, CancellationToken.None);
    }

    private async Task<ReceiptCompletion> CompleteAsync(Guid receiptId)
    {
        await using NpgsqlConnection connection = await fixture.DataSource.OpenConnectionAsync();
        return await _receipts.CompleteAsync(
            connection, receiptId, generatePutaway: true, CancellationToken.None);
    }

    private async Task ConfirmAsync(Seeded data, Guid receiptLineId, decimal quantity)
    {
        await using NpgsqlConnection connection = await fixture.DataSource.OpenConnectionAsync();
        FactResult result = await _confirm.HandleAsync(
            connection,
            new ReceiptConfirmedFact(
                ClientFactId: Guid.CreateVersion7().ToString(),
                ReceiptLineId: receiptLineId,
                Quantity: quantity,
                Uom: "EACH",
                ToLocationId: data.ReceivingId,
                ReasonCode: null,
                ActorUserId: data.UserId,
                DeviceId: null),
            CancellationToken.None);

        // A test whose setup silently failed would assert against an empty
        // database and pass for the wrong reason.
        Assert.Equal(FactStatus.Accepted, result.Status);
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
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            Guid.CreateVersion7());

        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(
            """
            INSERT INTO warehouse (id, code, name, timezone, created_at, updated_at)
            VALUES (@warehouseId, @warehouseCode, 'T', 'Asia/Tokyo', now(), now());

            INSERT INTO zone (id, warehouse_id, code, name_i18n, zone_type, created_at, updated_at)
            VALUES (@zoneId, @warehouseId, 'A', '{"en":"A"}', 'receiving', now(), now());

            -- Pick sequence 0 is the receiving bay and is staging, not a bin,
            -- so the strategy cannot direct putaway back into it.
            INSERT INTO location (id, warehouse_id, zone_id, code, location_type,
                                  pick_sequence, created_at, updated_at)
            VALUES (@receivingId, @warehouseId, @zoneId, right(@receivingId::text, 12),
                    'staging', 0, now(), now()),
                   (@firstBinId, @warehouseId, @zoneId, right(@firstBinId::text, 12),
                    'bin', 10, now(), now()),
                   (@secondBinId, @warehouseId, @zoneId, right(@secondBinId::text, 12),
                    'bin', 20, now(), now());

            INSERT INTO owner (id, code, name, created_at, updated_at)
            VALUES (@ownerId, right(@ownerId::text, 12), 'O', now(), now());

            INSERT INTO item (id, sku_code, name_i18n, base_uom, created_at, updated_at)
            VALUES (@itemId, right(@itemId::text, 12), '{"en":"I"}', 'EACH', now(), now());

            INSERT INTO app_user (id, user_type, display_name, status, security_stamp,
                                  valid_from, created_at, updated_at)
            VALUES (@userId, 'staff', 'Supervisor', 'active', gen_random_uuid(),
                    current_date, now(), now());
            """);

        command.Parameters.AddWithValue("warehouseId", data.WarehouseId);
        command.Parameters.AddWithValue("warehouseCode", data.WarehouseCode);
        command.Parameters.AddWithValue("zoneId", data.ZoneId);
        command.Parameters.AddWithValue("receivingId", data.ReceivingId);
        command.Parameters.AddWithValue("firstBinId", data.FirstBinId);
        command.Parameters.AddWithValue("secondBinId", data.SecondBinId);
        command.Parameters.AddWithValue("ownerId", data.OwnerId);
        command.Parameters.AddWithValue("itemId", data.ItemId);
        command.Parameters.AddWithValue("userId", data.UserId);

        await command.ExecuteNonQueryAsync();
        return data;
    }

    private sealed record Seeded(
        Guid WarehouseId,
        Guid ZoneId,
        Guid ReceivingId,
        Guid FirstBinId,
        Guid SecondBinId,
        Guid OwnerId,
        Guid ItemId)
    {
        public Guid UserId { get; } = Guid.CreateVersion7();

        /// <summary>
        /// Short enough to sit inside a document number without dominating it.
        /// </summary>
        public string WarehouseCode { get; } =
            Guid.CreateVersion7().ToString("N")[^6..].ToUpperInvariant();
    }
}
