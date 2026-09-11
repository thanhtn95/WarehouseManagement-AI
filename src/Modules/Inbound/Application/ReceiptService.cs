using System.Data;
using Npgsql;
using Wms.Modules.Inventory.Contracts;

namespace Wms.Modules.Inbound.Application;

/// <summary>
/// The receipt lifecycle commands (§5.4): open a receipt, start it, and
/// complete it — the last of which is what turns received stock into directed
/// putaway work (§6.2 step 10).
/// </summary>
/// <remarks>
/// These are <strong>commands</strong>, not facts. Nothing physical has
/// happened when a supervisor opens or completes a receipt, so refusing one
/// asks nobody to undo anything and a <c>409</c> is a legitimate answer —
/// which is exactly why the quantity confirmations that <em>do</em> record
/// physical events live on <c>/sync/facts</c> instead.
/// </remarks>
public sealed class ReceiptService(IBusinessCalendar businessCalendar)
{
    /// <summary>
    /// Allocates the next document number for the warehouse's business day.
    /// </summary>
    /// <remarks>
    /// <c>UPDATE … RETURNING</c> against <c>document_sequence</c> is
    /// concurrency-safe without an explicit lock (§3.1): the row lock the
    /// update takes is held for the rest of the transaction, so two
    /// simultaneous receipts cannot receive the same number. Keyed by
    /// business date, so numbering restarts on the warehouse's own next day
    /// rather than at a UTC midnight in the middle of a night shift.
    /// </remarks>
    private const string NextNumberSql = """
        INSERT INTO document_sequence (warehouse_id, document_type, business_date, last_number)
        VALUES (@warehouseId, 'receipt', @businessDate, 1)
        ON CONFLICT (warehouse_id, document_type, business_date)
        DO UPDATE SET last_number = document_sequence.last_number + 1
        RETURNING last_number;
        """;

    private const string CreateReceiptSql = """
        INSERT INTO receipt (id, warehouse_id, receipt_number, receipt_type,
                             supplier_reference, owner_id, status, expected_at,
                             created_by, created_at, updated_at)
        VALUES (@id, @warehouseId, @receiptNumber, @receiptType,
                @supplierReference, @ownerId, 'draft', @expectedAt,
                @createdBy, now(), now());
        """;

    private const string AddLineSql = """
        INSERT INTO receipt_line (id, receipt_id, line_no, item_id, owner_id,
                                  expected_quantity, uom_code)
        VALUES (@id, @receiptId, @lineNo, @itemId, @ownerId, @expectedQuantity, @uom);
        """;

    private const string LoadReceiptSql = """
        SELECT r.status, r.warehouse_id, r.owner_id, w.code
          FROM receipt r
          JOIN warehouse w ON w.id = r.warehouse_id
         WHERE r.id = @receiptId
           FOR UPDATE OF r;
        """;

    private const string WarehouseIdSql = "SELECT warehouse_id FROM receipt WHERE id = @receiptId;";

    private const string SetStatusSql = """
        UPDATE receipt
           SET status       = @status,
               started_at   = COALESCE(started_at, CASE WHEN @status = 'in_progress'
                                                        THEN now() END),
               completed_at = CASE WHEN @status = 'received' THEN now() ELSE completed_at END,
               version      = version + 1,
               updated_at   = now()
         WHERE id = @receiptId;
        """;

    /// <summary>
    /// Lines still awaiting a count when completion was attempted.
    /// </summary>
    /// <remarks>
    /// A line with no <c>received_quantity</c> has not been counted at all —
    /// distinct from one counted as zero, which is a real and meaningful
    /// result ("the pallet was empty") and must not block completion.
    /// </remarks>
    private const string UnconfirmedLinesSql = """
        SELECT line_no FROM receipt_line
         WHERE receipt_id = @receiptId AND received_quantity IS NULL
         ORDER BY line_no;
        """;

    /// <summary>
    /// What actually arrived, and where it is standing right now.
    /// </summary>
    /// <remarks>
    /// Read from the <strong>ledger</strong>, not from
    /// <c>receipt_line.received_quantity</c>. The ledger is the only record of
    /// *where* the stock was put, and grouping by that location handles a line
    /// confirmed in two parts to two different staging bays without special
    /// cases. §5.4 is explicit that putaway is generated for actual received
    /// quantities, never expected ones.
    /// </remarks>
    private const string ReceivedStockSql = """
        SELECT rl.id, rl.owner_id, rl.item_id, rl.uom_code,
               sm.to_location_id, l.zone_id, sum(sm.quantity_base) AS quantity
          FROM receipt_line rl
          JOIN stock_movement sm ON sm.reference_id = rl.id
                                AND sm.reference_type = 'receipt_line'
          JOIN location l ON l.id = sm.to_location_id
         WHERE rl.receipt_id = @receiptId
         GROUP BY rl.id, rl.owner_id, rl.item_id, rl.uom_code, sm.to_location_id, l.zone_id
        HAVING sum(sm.quantity_base) > 0
         ORDER BY rl.id;
        """;

    /// <summary>
    /// The Phase 1A putaway strategy: first empty bin by pick sequence.
    /// </summary>
    /// <remarks>
    /// "Empty" means no stock at all, which is why this is explicitly a
    /// proof-of-concept strategy — it ignores capacity, item affinity,
    /// velocity and mixed-item rules, and will happily send a single unit to a
    /// pallet location. The proposal names it as the one deliberately crude
    /// part of Phase 1A putaway; configurable strategies are Phase 3.
    /// <c>@excluded</c> keeps two lines of the same receipt from being
    /// directed to the same bin.
    /// </remarks>
    private const string FirstEmptyBinSql = """
        SELECT l.id
          FROM location l
         WHERE l.warehouse_id = @warehouseId
           AND l.location_type = 'bin'
           AND l.status = 'active'
           AND NOT (l.id = ANY(@excluded))
           AND NOT EXISTS (SELECT 1 FROM stock_balance sb
                            WHERE sb.location_id = l.id AND sb.on_hand <> 0)
         ORDER BY l.pick_sequence
         LIMIT 1;
        """;

    private const string CreateTaskSql = """
        INSERT INTO task (id, warehouse_id, zone_id, task_type, status,
                          priority, sort_sequence, reference_type, reference_id, created_at)
        VALUES (@id, @warehouseId, @zoneId, 'putaway', 'ready',
                100, @sortSequence, 'receipt', @receiptId, now());
        """;

    private const string CreateTaskLineSql = """
        INSERT INTO task_line (id, task_id, line_no, owner_id, item_id, lot_id,
                               from_location_id, to_location_id,
                               requested_quantity, uom_code, status)
        VALUES (@id, @taskId, 1, @ownerId, @itemId,
                '00000000-0000-0000-0000-000000000000',
                @fromLocationId, @toLocationId, @quantity, @uom, 'pending');
        """;

    public async Task<CreatedReceipt> CreateAsync(
        NpgsqlConnection connection,
        CreateReceiptCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(command);

        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        DateOnly businessDate = await businessCalendar.ResolveAsync(
            connection, transaction, command.WarehouseId, cancellationToken);

        string warehouseCode = await WarehouseCodeAsync(
            connection, transaction, command.WarehouseId, cancellationToken);

        int sequence = await NextNumberAsync(
            connection, transaction, command.WarehouseId, businessDate, cancellationToken);

        string receiptNumber =
            $"RCV-{warehouseCode}-{businessDate:yyMMdd}-{sequence:D4}";

        Guid receiptId = Guid.CreateVersion7();
        await using (NpgsqlCommand insert = new(CreateReceiptSql, connection, transaction))
        {
            insert.Parameters.AddWithValue("id", receiptId);
            insert.Parameters.AddWithValue("warehouseId", command.WarehouseId);
            insert.Parameters.AddWithValue("receiptNumber", receiptNumber);
            insert.Parameters.AddWithValue("receiptType", command.ReceiptType);
            insert.Parameters.AddWithValue(
                "supplierReference", (object?)command.SupplierReference ?? DBNull.Value);
            insert.Parameters.AddWithValue("ownerId", command.OwnerId);
            insert.Parameters.AddWithValue(
                "expectedAt", (object?)command.ExpectedAt ?? DBNull.Value);
            insert.Parameters.AddWithValue("createdBy", command.ActorUserId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        List<Guid> lineIds = [];
        int lineNo = 0;
        foreach (CreateReceiptLine line in command.Lines)
        {
            Guid lineId = Guid.CreateVersion7();
            lineIds.Add(lineId);

            await using NpgsqlCommand insertLine = new(AddLineSql, connection, transaction);
            insertLine.Parameters.AddWithValue("id", lineId);
            insertLine.Parameters.AddWithValue("receiptId", receiptId);
            insertLine.Parameters.AddWithValue("lineNo", ++lineNo);
            insertLine.Parameters.AddWithValue("itemId", line.ItemId);
            insertLine.Parameters.AddWithValue("ownerId", command.OwnerId);
            insertLine.Parameters.AddWithValue(
                "expectedQuantity", (object?)line.ExpectedQuantity ?? DBNull.Value);
            insertLine.Parameters.AddWithValue("uom", line.Uom);
            await insertLine.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new CreatedReceipt(receiptId, receiptNumber, "draft", lineIds);
    }

    /// <summary>
    /// The receipt's warehouse, unlocked and outside any transaction.
    /// </summary>
    /// <remarks>
    /// For scope-checking <c>start</c>/<c>complete</c> before the caller's
    /// real connection is opened — the endpoint layer needs this before it
    /// knows whether to authorize the command at all, so it cannot reuse the
    /// locked read <see cref="StartAsync"/>/<see cref="CompleteAsync"/> take
    /// internally. A receipt's warehouse never changes after creation, so an
    /// unlocked read here is not a race: it is only ever stale in the sense
    /// that the receipt could stop existing between this call and the next,
    /// which those methods already handle as <c>NotFound</c>. Keeping the
    /// query here, rather than as raw SQL in <c>Wms.Api</c>, is what keeps
    /// the receipt table's shape known to exactly one module.
    /// </remarks>
    public static async Task<Guid?> WarehouseIdAsync(
        NpgsqlDataSource dataSource, Guid receiptId, CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = dataSource.CreateCommand(WarehouseIdSql);
        command.Parameters.AddWithValue("receiptId", receiptId);

        return await command.ExecuteScalarAsync(cancellationToken) as Guid?;
    }

    /// <summary>
    /// Moves a receipt from <c>draft</c> to <c>in_progress</c>.
    /// </summary>
    public async Task<ReceiptTransition> StartAsync(
        NpgsqlConnection connection,
        Guid receiptId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        ReceiptContext? receipt = await LoadAsync(
            connection, transaction, receiptId, cancellationToken);

        if (receipt is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ReceiptTransition.NotFound();
        }

        if (receipt.Status != "draft")
        {
            await transaction.RollbackAsync(cancellationToken);
            return ReceiptTransition.InvalidTransition(receipt.Status, "in_progress");
        }

        await SetStatusAsync(connection, transaction, receiptId, "in_progress", cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return ReceiptTransition.Succeeded("in_progress");
    }

    /// <summary>
    /// Closes the receipt and generates directed putaway work (§6.2 step 10).
    /// </summary>
    /// <remarks>
    /// One transaction, so a completion that fails partway leaves neither a
    /// closed receipt with no putaway work nor putaway work for a receipt
    /// still open. This mutates Receipt and creates Task aggregates, which is
    /// permitted here for the same reason task generation always is: the
    /// tasks are new rows nobody else can be holding, so there is no
    /// cross-aggregate lock to order.
    /// </remarks>
    public async Task<ReceiptCompletion> CompleteAsync(
        NpgsqlConnection connection,
        Guid receiptId,
        bool generatePutaway,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        ReceiptContext? receipt = await LoadAsync(
            connection, transaction, receiptId, cancellationToken);

        if (receipt is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ReceiptCompletion.NotFound();
        }

        if (receipt.Status != "in_progress")
        {
            await transaction.RollbackAsync(cancellationToken);
            return ReceiptCompletion.InvalidTransition(receipt.Status);
        }

        int[] unconfirmed = await UnconfirmedLineNumbersAsync(
            connection, transaction, receiptId, cancellationToken);

        if (unconfirmed.Length > 0)
        {
            // Refusable, and safe to refuse: nothing physical happens when a
            // supervisor presses complete, so the answer "you have not
            // counted lines 4 and 9 yet" is actionable.
            await transaction.RollbackAsync(cancellationToken);
            return ReceiptCompletion.LinesUnconfirmed(unconfirmed);
        }

        int tasksCreated = 0;
        if (generatePutaway)
        {
            tasksCreated = await GeneratePutawayAsync(
                connection, transaction, receipt, receiptId, cancellationToken);
        }

        await SetStatusAsync(connection, transaction, receiptId, "received", cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return ReceiptCompletion.Succeeded(tasksCreated);
    }

    private async Task<int> GeneratePutawayAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ReceiptContext receipt,
        Guid receiptId,
        CancellationToken cancellationToken)
    {
        List<ReceivedStock> received = [];
        await using (NpgsqlCommand command = new(ReceivedStockSql, connection, transaction))
        {
            command.Parameters.AddWithValue("receiptId", receiptId);

            await using NpgsqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                received.Add(new ReceivedStock(
                    OwnerId: reader.GetGuid(1),
                    ItemId: reader.GetGuid(2),
                    Uom: reader.GetString(3),
                    FromLocationId: reader.GetGuid(4),
                    ZoneId: reader.GetGuid(5),
                    Quantity: reader.GetDecimal(6)));
            }
        }

        List<Guid> assigned = [];
        int sortSequence = 0;

        foreach (ReceivedStock stock in received)
        {
            Guid? destination = await FirstEmptyBinAsync(
                connection, transaction, receipt.WarehouseId, assigned, cancellationToken);

            if (destination is Guid bin)
            {
                assigned.Add(bin);
            }

            // A null destination is an UNDIRECTED putaway, not a failure. With
            // no empty bin available the operator is told to put the stock
            // somewhere sensible and report where — which is strictly better
            // than refusing to close a receipt for goods that are already on
            // the dock. The putaway handler treats a null directed location as
            // "no mismatch is possible", so no spurious exception follows.
            Guid taskId = Guid.CreateVersion7();
            await using (NpgsqlCommand task = new(CreateTaskSql, connection, transaction))
            {
                task.Parameters.AddWithValue("id", taskId);
                task.Parameters.AddWithValue("warehouseId", receipt.WarehouseId);
                task.Parameters.AddWithValue("zoneId", stock.ZoneId);
                task.Parameters.AddWithValue("sortSequence", ++sortSequence);
                task.Parameters.AddWithValue("receiptId", receiptId);
                await task.ExecuteNonQueryAsync(cancellationToken);
            }

            await using NpgsqlCommand taskLine = new(CreateTaskLineSql, connection, transaction);
            taskLine.Parameters.AddWithValue("id", Guid.CreateVersion7());
            taskLine.Parameters.AddWithValue("taskId", taskId);
            taskLine.Parameters.AddWithValue("ownerId", stock.OwnerId);
            taskLine.Parameters.AddWithValue("itemId", stock.ItemId);
            taskLine.Parameters.AddWithValue("fromLocationId", stock.FromLocationId);
            taskLine.Parameters.AddWithValue("toLocationId", (object?)destination ?? DBNull.Value);
            taskLine.Parameters.AddWithValue("quantity", stock.Quantity);
            taskLine.Parameters.AddWithValue("uom", stock.Uom);
            await taskLine.ExecuteNonQueryAsync(cancellationToken);
        }

        return received.Count;
    }

    private static async Task<Guid?> FirstEmptyBinAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid warehouseId,
        List<Guid> excluded,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(FirstEmptyBinSql, connection, transaction);
        command.Parameters.AddWithValue("warehouseId", warehouseId);
        command.Parameters.AddWithValue("excluded", excluded.ToArray());

        return await command.ExecuteScalarAsync(cancellationToken) as Guid?;
    }

    private static async Task<int[]> UnconfirmedLineNumbersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid receiptId,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(UnconfirmedLinesSql, connection, transaction);
        command.Parameters.AddWithValue("receiptId", receiptId);

        List<int> lineNumbers = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lineNumbers.Add(reader.GetInt32(0));
        }

        return [.. lineNumbers];
    }

    private static async Task<ReceiptContext?> LoadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid receiptId,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(LoadReceiptSql, connection, transaction);
        command.Parameters.AddWithValue("receiptId", receiptId);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ReceiptContext(
                Status: reader.GetString(0),
                WarehouseId: reader.GetGuid(1),
                OwnerId: reader.GetGuid(2),
                WarehouseCode: reader.GetString(3))
            : null;
    }

    private static async Task SetStatusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid receiptId,
        string status,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(SetStatusSql, connection, transaction);
        command.Parameters.AddWithValue("receiptId", receiptId);
        command.Parameters.AddWithValue("status", status);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string> WarehouseCodeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid warehouseId,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command =
            new("SELECT code FROM warehouse WHERE id = @id;", connection, transaction);
        command.Parameters.AddWithValue("id", warehouseId);

        return await command.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new InvalidOperationException($"Unknown warehouse {warehouseId}.");
    }

    private static async Task<int> NextNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid warehouseId,
        DateOnly businessDate,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(NextNumberSql, connection, transaction);
        command.Parameters.AddWithValue("warehouseId", warehouseId);
        command.Parameters.Add(new NpgsqlParameter("businessDate", NpgsqlTypes.NpgsqlDbType.Date)
        {
            Value = businessDate,
        });

        return (int)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private sealed record ReceiptContext(
        string Status, Guid WarehouseId, Guid OwnerId, string WarehouseCode);

    private sealed record ReceivedStock(
        Guid OwnerId, Guid ItemId, string Uom, Guid FromLocationId, Guid ZoneId, decimal Quantity);
}
