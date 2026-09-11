using Npgsql;
using Wms.Modules.Inbound.Contracts;

namespace Wms.Modules.Inbound.Infrastructure;

/// <inheritdoc />
public sealed class InboundReadModel(NpgsqlDataSource dataSource) : IInboundReadModel
{
    /// <summary>
    /// All three receiving figures in one round trip.
    /// </summary>
    /// <remarks>
    /// "Today" is the warehouse's business day, derived from its own timezone
    /// and day boundary exactly as the ledger derives business_date. A
    /// dashboard that counted by UTC date would split a night shift in two and
    /// disagree with every report beside it.
    /// </remarks>
    private const string Sql = """
        WITH day AS (
            SELECT (((now() AT TIME ZONE w.timezone) - w.day_boundary_time))::date AS business_date,
                   w.timezone, w.day_boundary_time
              FROM warehouse w WHERE w.id = @warehouseId
        )
        SELECT
            (SELECT count(*)::int FROM receipt
              WHERE warehouse_id = @warehouseId
                AND status IN ('draft', 'in_progress')),
            (SELECT count(*)::int FROM receipt r, day d
              WHERE r.warehouse_id = @warehouseId
                AND r.status IN ('received', 'putaway', 'closed')
                AND (((r.completed_at AT TIME ZONE d.timezone) - d.day_boundary_time))::date
                    = d.business_date),
            (SELECT count(*)::int FROM receipt_line rl
               JOIN receipt r ON r.id = rl.receipt_id
              WHERE r.warehouse_id = @warehouseId
                AND rl.discrepancy_type IS NOT NULL
                AND rl.discrepancy_type <> 'none'
                AND r.status <> 'cancelled');
        """;

    /// <summary>
    /// The receipt list, with per-receipt line counts computed in the same
    /// query.
    /// </summary>
    /// <remarks>
    /// Lateral aggregation rather than N+1 round trips: a list of fifty
    /// receipts that fetched its own line counts per row would issue fifty-one
    /// queries for one screen, which is invisible on a demo database and the
    /// first thing to fall over on a real one.
    ///
    /// Keyset by id (§3.4). Receipt ids are UUIDv7 and therefore time-ordered,
    /// so ordering by id is also chronological — a rare case where the natural
    /// key and the natural sort agree.
    /// </remarks>
    private const string ReceiptsSql = """
        SELECT r.id, r.receipt_number, r.status, r.supplier_reference,
               counts.lines_total, counts.lines_confirmed, counts.discrepancy_count,
               r.expected_at, r.started_at
          FROM receipt r
          CROSS JOIN LATERAL (
              SELECT count(*)::int AS lines_total,
                     count(*) FILTER (WHERE rl.received_quantity IS NOT NULL)::int
                         AS lines_confirmed,
                     count(*) FILTER (WHERE rl.discrepancy_type IS NOT NULL
                                        AND rl.discrepancy_type <> 'none')::int
                         AS discrepancy_count
                FROM receipt_line rl WHERE rl.receipt_id = r.id
          ) counts
         WHERE (@warehouseId::uuid IS NULL OR r.warehouse_id = @warehouseId)
           AND (@status::text      IS NULL OR r.status = @status)
           AND (@supplierReference::text IS NULL
                OR r.supplier_reference = @supplierReference)
           AND (@businessDateFrom::date IS NULL OR r.created_at >= @businessDateFrom)
           AND (@businessDateTo::date   IS NULL
                OR r.created_at < (@businessDateTo::date + 1))
           AND (@afterId::uuid IS NULL OR r.id > @afterId)
         ORDER BY r.id
         LIMIT @limit;
        """;

    private const string ReceiptSql = """
        SELECT r.id, r.receipt_number, r.status, r.receipt_type,
               r.owner_id, o.code, r.supplier_reference, r.version
          FROM receipt r
          JOIN owner o ON o.id = r.owner_id
         WHERE r.id = @receiptId;
        """;

    private const string ReceiptLinesSql = """
        SELECT rl.id, rl.line_no, rl.item_id, i.sku_code,
               COALESCE(i.name_i18n->>'en', i.sku_code),
               rl.expected_quantity, rl.received_quantity, rl.uom_code,
               rl.discrepancy_type, rl.status
          FROM receipt_line rl
          JOIN item i ON i.id = rl.item_id
         WHERE rl.receipt_id = @receiptId
         ORDER BY rl.line_no;
        """;

    public async Task<ReceiptPage> GetReceiptsAsync(
        ReceiptQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        List<ReceiptRow> rows = [];
        await using (NpgsqlCommand command = dataSource.CreateCommand(ReceiptsSql))
        {
            command.Parameters.AddWithValue(
                "warehouseId", (object?)query.WarehouseId ?? DBNull.Value);
            command.Parameters.AddWithValue("status", (object?)query.Status ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "supplierReference", (object?)query.SupplierReference ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "businessDateFrom", (object?)query.BusinessDateFrom ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "businessDateTo", (object?)query.BusinessDateTo ?? DBNull.Value);
            command.Parameters.AddWithValue("afterId", (object?)query.AfterId ?? DBNull.Value);
            command.Parameters.AddWithValue("limit", query.Limit + 1);

            await using NpgsqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new ReceiptRow(
                    Id: reader.GetGuid(0),
                    ReceiptNumber: reader.GetString(1),
                    Status: reader.GetString(2),
                    SupplierReference: reader.IsDBNull(3) ? null : reader.GetString(3),
                    LinesTotal: reader.GetInt32(4),
                    LinesConfirmed: reader.GetInt32(5),
                    DiscrepancyCount: reader.GetInt32(6),
                    ExpectedAt: reader.IsDBNull(7)
                        ? null
                        : reader.GetFieldValue<DateTimeOffset>(7),
                    StartedAt: reader.IsDBNull(8)
                        ? null
                        : reader.GetFieldValue<DateTimeOffset>(8)));
            }
        }

        bool hasMore = rows.Count > query.Limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return new ReceiptPage(rows, rows.Count > 0 ? rows[^1].Id : null, hasMore);
    }

    public async Task<ReceiptDetail?> GetReceiptAsync(
        Guid receiptId, CancellationToken cancellationToken)
    {
        ReceiptDetail? detail = null;

        await using (NpgsqlCommand command = dataSource.CreateCommand(ReceiptSql))
        {
            command.Parameters.AddWithValue("receiptId", receiptId);

            await using NpgsqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                detail = new ReceiptDetail(
                    Id: reader.GetGuid(0),
                    ReceiptNumber: reader.GetString(1),
                    Status: reader.GetString(2),
                    ReceiptType: reader.GetString(3),
                    OwnerId: reader.GetGuid(4),
                    OwnerCode: reader.GetString(5),
                    SupplierReference: reader.IsDBNull(6) ? null : reader.GetString(6),
                    Version: reader.GetInt64(7),
                    Lines: []);
            }
        }

        if (detail is null)
        {
            return null;
        }

        List<ReceiptLineRow> lines = [];
        await using (NpgsqlCommand command = dataSource.CreateCommand(ReceiptLinesSql))
        {
            command.Parameters.AddWithValue("receiptId", receiptId);

            await using NpgsqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                lines.Add(new ReceiptLineRow(
                    Id: reader.GetGuid(0),
                    LineNo: reader.GetInt32(1),
                    ItemId: reader.GetGuid(2),
                    SkuCode: reader.GetString(3),
                    ItemName: reader.GetString(4),
                    ExpectedQuantity: reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                    ReceivedQuantity: reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                    Uom: reader.GetString(7),
                    DiscrepancyType: reader.IsDBNull(8) ? null : reader.GetString(8),
                    Status: reader.IsDBNull(9) ? null : reader.GetString(9)));
            }
        }

        return detail with { Lines = lines };
    }

    public async Task<ReceivingSummary> GetReceivingSummaryAsync(
        Guid warehouseId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("warehouseId", warehouseId);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ReceivingSummary(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2))
            : new ReceivingSummary(0, 0, 0);
    }
}
