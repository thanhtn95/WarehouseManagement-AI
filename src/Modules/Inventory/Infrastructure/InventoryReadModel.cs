using Npgsql;
using Wms.Modules.Inventory.Contracts;

namespace Wms.Modules.Inventory.Infrastructure;

/// <inheritdoc />
public sealed class InventoryReadModel(NpgsqlDataSource dataSource) : IInventoryReadModel
{
    /// <summary>
    /// Keyset pagination over the balance grain (§3.4).
    /// </summary>
    /// <remarks>
    /// The cursor is the primary-key tuple rendered as text and compared as a
    /// row constructor, which is what lets Postgres resume from the index
    /// rather than counting past rows it has already returned. Ordering is by
    /// that same tuple so the cursor is total — ordering by anything
    /// non-unique would let rows repeat or vanish between pages.
    ///
    /// <c>allocated</c> is subtracted but never stored as <c>available</c>:
    /// three quantities, never two.
    /// </remarks>
    private const string BalancesSql = """
        SELECT o.id, o.code,
               i.id, i.sku_code, COALESCE(i.name_i18n->>'en', i.sku_code),
               l.id, l.code, z.code,
               sb.lot_id, sb.stock_status,
               sb.on_hand, sb.allocated,
               sb.on_hand - sb.allocated AS available,
               i.base_uom, sb.version, sb.updated_at,
               (sb.owner_id::text || '|' || sb.item_id::text || '|'
                || sb.location_id::text || '|' || sb.lot_id::text || '|'
                || sb.stock_status) AS keyset
          FROM stock_balance sb
          JOIN owner o    ON o.id = sb.owner_id
          JOIN item i     ON i.id = sb.item_id
          JOIN location l ON l.id = sb.location_id
          JOIN zone z     ON z.id = l.zone_id
         WHERE (@warehouseId::uuid IS NULL OR l.warehouse_id = @warehouseId)
           AND (@itemId::uuid      IS NULL OR sb.item_id = @itemId)
           AND (@locationId::uuid  IS NULL OR sb.location_id = @locationId)
           AND (@zoneId::uuid      IS NULL OR l.zone_id = @zoneId)
           AND (@ownerId::uuid     IS NULL OR sb.owner_id = @ownerId)
           AND (@stockStatus::text IS NULL OR sb.stock_status = @stockStatus)
           AND (@includeZero OR sb.on_hand <> 0)
           AND (@afterKey::text IS NULL
                OR (sb.owner_id::text || '|' || sb.item_id::text || '|'
                    || sb.location_id::text || '|' || sb.lot_id::text || '|'
                    || sb.stock_status) > @afterKey)
         ORDER BY keyset
         LIMIT @limit;
        """;

    /// <summary>
    /// Totals across the whole filtered set, not just the page.
    /// </summary>
    /// <remarks>
    /// A footer that summed only the rows on screen would silently disagree
    /// with itself as the reader paged, and someone would eventually reconcile
    /// against it.
    /// </remarks>
    private const string BalanceTotalsSql = """
        SELECT COALESCE(sum(sb.on_hand), 0),
               COALESCE(sum(sb.allocated), 0),
               COALESCE(sum(sb.on_hand - sb.allocated), 0)
          FROM stock_balance sb
          JOIN location l ON l.id = sb.location_id
         WHERE (@warehouseId::uuid IS NULL OR l.warehouse_id = @warehouseId)
           AND (@itemId::uuid      IS NULL OR sb.item_id = @itemId)
           AND (@locationId::uuid  IS NULL OR sb.location_id = @locationId)
           AND (@zoneId::uuid      IS NULL OR l.zone_id = @zoneId)
           AND (@ownerId::uuid     IS NULL OR sb.owner_id = @ownerId)
           AND (@stockStatus::text IS NULL OR sb.stock_status = @stockStatus)
           AND (@includeZero OR sb.on_hand <> 0);
        """;

    private const string ExceptionsSql = """
        SELECT ie.id, ie.exception_type, ie.severity, ie.raised_at,
               EXTRACT(EPOCH FROM (now() - ie.raised_at)) / 3600.0 AS age_hours,
               i.sku_code, COALESCE(i.name_i18n->>'en', i.sku_code),
               l.code,
               ie.expected_quantity, ie.actual_quantity,
               ie.actual_quantity - ie.expected_quantity AS difference,
               ie.stock_movement_id, ie.task_id,
               u.display_name, d.label, ie.status
          FROM inventory_exception ie
          LEFT JOIN item i      ON i.id = ie.item_id
          LEFT JOIN location l  ON l.id = ie.location_id
          LEFT JOIN app_user u  ON u.id = ie.raised_by_user_id
          LEFT JOIN device d    ON d.id = ie.device_id
         WHERE (@warehouseId::uuid  IS NULL OR ie.warehouse_id = @warehouseId)
           AND (@status::text        IS NULL OR ie.status = @status)
           AND (@exceptionType::text IS NULL OR ie.exception_type = @exceptionType)
           AND (@minAgeHours::float8 IS NULL
                OR ie.raised_at <= now() - make_interval(hours => @minAgeHours::int))
         ORDER BY ie.raised_at
         LIMIT @limit;
        """;

    /// <summary>
    /// The summary block, computed over <em>open</em> exceptions only.
    /// </summary>
    /// <remarks>
    /// Deliberately independent of the list's filters: a supervisor filtering
    /// to <c>short_pick</c> still needs to see that fourteen exceptions are
    /// open overall, or the filter becomes a way to accidentally hide work.
    /// </remarks>
    private const string ExceptionSummarySql = """
        SELECT exception_type, count(*)::int,
               max(EXTRACT(EPOCH FROM (now() - raised_at)) / 3600.0)
          FROM inventory_exception
         WHERE status = 'open'
           AND (@warehouseId::uuid IS NULL OR warehouse_id = @warehouseId)
         GROUP BY exception_type;
        """;

    private const string StockSummarySql = """
        SELECT count(DISTINCT sb.item_id)::int, COALESCE(sum(sb.on_hand), 0)
          FROM stock_balance sb
          JOIN location l ON l.id = sb.location_id
         WHERE l.warehouse_id = @warehouseId AND sb.on_hand <> 0;
        """;

    private const string LatestReconciliationSql = """
        SELECT completed_at, variance_count
          FROM balance_reconciliation_run
         WHERE warehouse_id = @warehouseId AND status = 'completed'
         ORDER BY started_at DESC
         LIMIT 1;
        """;

    /// <summary>
    /// The ledger view, ordered by <c>sequence</c> and never by time.
    /// </summary>
    /// <remarks>
    /// Two movements can share a millisecond and a device clock can be wrong,
    /// so a time-ordered cursor would skip or repeat rows (C4). <c>sequence</c>
    /// is server-assigned and monotonic, which is exactly what a cursor needs.
    ///
    /// The partition key is <c>recorded_at</c>, so a query filtered only by
    /// sequence still scans every partition. <c>business_date_from</c> is the
    /// filter that lets Postgres prune them, which is why it is offered
    /// alongside the cursor rather than instead of it.
    /// </remarks>
    private const string MovementsSql = """
        SELECT sm.id, sm.sequence, sm.recorded_at, sm.device_reported_at, sm.business_date,
               sm.movement_type, sm.item_id, i.sku_code,
               fl.code, tl.code,
               CASE WHEN sm.to_location_id IS NULL
                    THEN -sm.quantity_base ELSE sm.quantity_base END,
               sm.entered_quantity, sm.entered_uom, sm.reason_code,
               sm.reference_type, sm.reference_id,
               sm.actor_user_id, u.display_name, d.label, sm.authorized_by_user_id
          FROM stock_movement sm
          JOIN item i          ON i.id = sm.item_id
          LEFT JOIN location fl ON fl.id = sm.from_location_id
          LEFT JOIN location tl ON tl.id = sm.to_location_id
          LEFT JOIN app_user u  ON u.id = sm.actor_user_id
          LEFT JOIN device d    ON d.id = sm.device_id
         WHERE (@warehouseId::uuid IS NULL
                OR COALESCE(tl.warehouse_id, fl.warehouse_id) = @warehouseId)
           AND (@itemId::uuid       IS NULL OR sm.item_id = @itemId)
           AND (@locationId::uuid   IS NULL
                OR sm.from_location_id = @locationId OR sm.to_location_id = @locationId)
           AND (@movementType::text IS NULL OR sm.movement_type = @movementType)
           AND (@referenceId::uuid  IS NULL OR sm.reference_id = @referenceId)
           AND (@businessDateFrom::date IS NULL OR sm.business_date >= @businessDateFrom)
           AND (@afterSequence::bigint IS NULL OR sm.sequence > @afterSequence)
         ORDER BY sm.sequence
         LIMIT @limit;
        """;

    private const string ReconciliationRunsSql = """
        SELECT id, warehouse_id, started_at, completed_at,
               from_sequence, to_sequence, rows_checked, variance_count, status
          FROM balance_reconciliation_run
         WHERE (@warehouseId::uuid IS NULL OR warehouse_id = @warehouseId)
         ORDER BY started_at DESC
         LIMIT @limit;
        """;

    public async Task<MovementPage> GetMovementsAsync(
        MovementQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        List<MovementRow> rows = [];
        await using (NpgsqlCommand command = dataSource.CreateCommand(MovementsSql))
        {
            command.Parameters.AddWithValue(
                "warehouseId", (object?)query.WarehouseId ?? DBNull.Value);
            command.Parameters.AddWithValue("itemId", (object?)query.ItemId ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "locationId", (object?)query.LocationId ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "movementType", (object?)query.MovementType ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "referenceId", (object?)query.ReferenceId ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "businessDateFrom", (object?)query.BusinessDateFrom ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "afterSequence", (object?)query.AfterSequence ?? DBNull.Value);
            command.Parameters.AddWithValue("limit", query.Limit + 1);

            await using NpgsqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new MovementRow(
                    Id: reader.GetGuid(0),
                    Sequence: reader.GetInt64(1),
                    RecordedAt: reader.GetFieldValue<DateTimeOffset>(2),
                    DeviceReportedAt: reader.IsDBNull(3)
                        ? null
                        : reader.GetFieldValue<DateTimeOffset>(3),
                    BusinessDate: reader.GetFieldValue<DateOnly>(4),
                    MovementType: reader.GetString(5),
                    ItemId: reader.GetGuid(6),
                    SkuCode: reader.GetString(7),
                    FromLocationCode: reader.IsDBNull(8) ? null : reader.GetString(8),
                    ToLocationCode: reader.IsDBNull(9) ? null : reader.GetString(9),
                    QuantityBase: reader.GetDecimal(10),
                    EnteredQuantity: reader.GetDecimal(11),
                    EnteredUom: reader.GetString(12),
                    ReasonCode: reader.IsDBNull(13) ? null : reader.GetString(13),
                    ReferenceType: reader.IsDBNull(14) ? null : reader.GetString(14),
                    ReferenceId: reader.IsDBNull(15) ? null : reader.GetGuid(15),
                    ActorUserId: reader.GetGuid(16),
                    ActorDisplayName: reader.IsDBNull(17) ? null : reader.GetString(17),
                    DeviceLabel: reader.IsDBNull(18) ? null : reader.GetString(18),
                    AuthorizedByUserId: reader.IsDBNull(19) ? null : reader.GetGuid(19)));
            }
        }

        bool hasMore = rows.Count > query.Limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return new MovementPage(rows, rows.Count > 0 ? rows[^1].Sequence : null, hasMore);
    }

    public async Task<IReadOnlyList<ReconciliationRun>> GetReconciliationRunsAsync(
        Guid? warehouseId, int limit, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = dataSource.CreateCommand(ReconciliationRunsSql);
        command.Parameters.AddWithValue("warehouseId", (object?)warehouseId ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", limit);

        List<ReconciliationRun> runs = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            runs.Add(new ReconciliationRun(
                Id: reader.GetGuid(0),
                WarehouseId: reader.GetGuid(1),
                StartedAt: reader.GetFieldValue<DateTimeOffset>(2),
                CompletedAt: reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                FromSequence: reader.GetInt64(4),
                ToSequence: reader.GetInt64(5),
                RowsChecked: reader.GetInt32(6),
                VarianceCount: reader.GetInt32(7),
                Status: reader.GetString(8)));
        }

        return runs;
    }

    public async Task<BalancePage> GetBalancesAsync(
        BalanceQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        List<BalanceRow> rows = [];
        string? lastKey = null;

        // One more than asked for, so "is there another page" is answered by
        // fact rather than by guessing from a full page.
        await using (NpgsqlCommand command = dataSource.CreateCommand(BalancesSql))
        {
            AddFilters(command, query);
            command.Parameters.AddWithValue("limit", query.Limit + 1);
            command.Parameters.AddWithValue(
                "afterKey", (object?)query.AfterKey ?? DBNull.Value);

            await using NpgsqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new BalanceRow(
                    OwnerId: reader.GetGuid(0),
                    OwnerCode: reader.GetString(1),
                    ItemId: reader.GetGuid(2),
                    SkuCode: reader.GetString(3),
                    ItemName: reader.GetString(4),
                    LocationId: reader.GetGuid(5),
                    LocationCode: reader.GetString(6),
                    ZoneCode: reader.GetString(7),
                    LotId: reader.GetGuid(8),
                    StockStatus: reader.GetString(9),
                    OnHand: reader.GetDecimal(10),
                    Allocated: reader.GetDecimal(11),
                    Available: reader.GetDecimal(12),
                    Uom: reader.GetString(13),
                    Version: reader.GetInt64(14),
                    UpdatedAt: reader.GetFieldValue<DateTimeOffset>(15)));

                lastKey = reader.GetString(16);
            }
        }

        bool hasMore = rows.Count > query.Limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
            lastKey = BuildKey(rows[^1]);
        }

        BalanceTotals totals = await GetTotalsAsync(query, cancellationToken);

        return new BalancePage(rows, totals, hasMore ? lastKey : null, hasMore);
    }

    public async Task<ExceptionPage> GetExceptionsAsync(
        ExceptionQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        List<ExceptionRow> rows = [];
        await using (NpgsqlCommand command = dataSource.CreateCommand(ExceptionsSql))
        {
            command.Parameters.AddWithValue(
                "warehouseId", (object?)query.WarehouseId ?? DBNull.Value);
            command.Parameters.AddWithValue("status", (object?)query.Status ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "exceptionType", (object?)query.ExceptionType ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "minAgeHours", (object?)query.MinAgeHours ?? DBNull.Value);
            command.Parameters.AddWithValue("limit", query.Limit);

            await using NpgsqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new ExceptionRow(
                    Id: reader.GetGuid(0),
                    ExceptionType: reader.GetString(1),
                    Severity: reader.GetString(2),
                    RaisedAt: reader.GetFieldValue<DateTimeOffset>(3),
                    AgeHours: Math.Round(reader.GetDouble(4), 1),
                    SkuCode: reader.IsDBNull(5) ? null : reader.GetString(5),
                    ItemName: reader.IsDBNull(6) ? null : reader.GetString(6),
                    LocationCode: reader.IsDBNull(7) ? null : reader.GetString(7),
                    ExpectedQuantity: reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                    ActualQuantity: reader.IsDBNull(9) ? null : reader.GetDecimal(9),
                    Difference: reader.IsDBNull(10) ? null : reader.GetDecimal(10),
                    StockMovementId: reader.IsDBNull(11) ? null : reader.GetGuid(11),
                    TaskId: reader.IsDBNull(12) ? null : reader.GetGuid(12),
                    RaisedByDisplayName: reader.IsDBNull(13) ? null : reader.GetString(13),
                    DeviceLabel: reader.IsDBNull(14) ? null : reader.GetString(14),
                    Status: reader.GetString(15)));
            }
        }

        return new ExceptionPage(
            rows, await GetExceptionSummaryAsync(query.WarehouseId, cancellationToken));
    }

    public async Task<ExceptionSummary> GetExceptionSummaryAsync(
        Guid? warehouseId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = dataSource.CreateCommand(ExceptionSummarySql);
        command.Parameters.AddWithValue("warehouseId", (object?)warehouseId ?? DBNull.Value);

        Dictionary<string, int> byType = [];
        int open = 0;
        double? oldest = null;

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            int count = reader.GetInt32(1);
            byType[reader.GetString(0)] = count;
            open += count;

            double age = reader.GetDouble(2);
            oldest = oldest is null ? age : Math.Max(oldest.Value, age);
        }

        return new ExceptionSummary(open, oldest is null ? null : Math.Round(oldest.Value, 1), byType);
    }

    public async Task<StockSummary> GetStockSummaryAsync(
        Guid warehouseId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = dataSource.CreateCommand(StockSummarySql);
        command.Parameters.AddWithValue("warehouseId", warehouseId);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new StockSummary(reader.GetInt32(0), reader.GetDecimal(1))
            : new StockSummary(0, 0);
    }

    public async Task<ReconciliationSummary?> GetLatestReconciliationAsync(
        Guid warehouseId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = dataSource.CreateCommand(LatestReconciliationSql);
        command.Parameters.AddWithValue("warehouseId", warehouseId);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ReconciliationSummary(
                reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0),
                reader.GetInt32(1))
            : null;
    }

    private async Task<BalanceTotals> GetTotalsAsync(
        BalanceQuery query, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = dataSource.CreateCommand(BalanceTotalsSql);
        AddFilters(command, query);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new BalanceTotals(reader.GetDecimal(0), reader.GetDecimal(1), reader.GetDecimal(2))
            : new BalanceTotals(0, 0, 0);
    }

    private static void AddFilters(NpgsqlCommand command, BalanceQuery query)
    {
        command.Parameters.AddWithValue("warehouseId", (object?)query.WarehouseId ?? DBNull.Value);
        command.Parameters.AddWithValue("itemId", (object?)query.ItemId ?? DBNull.Value);
        command.Parameters.AddWithValue("locationId", (object?)query.LocationId ?? DBNull.Value);
        command.Parameters.AddWithValue("zoneId", (object?)query.ZoneId ?? DBNull.Value);
        command.Parameters.AddWithValue("ownerId", (object?)query.OwnerId ?? DBNull.Value);
        command.Parameters.AddWithValue("stockStatus", (object?)query.StockStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("includeZero", query.IncludeZero);
    }

    private static string BuildKey(BalanceRow row) =>
        $"{row.OwnerId}|{row.ItemId}|{row.LocationId}|{row.LotId}|{row.StockStatus}";
}
