using System.Data;
using Npgsql;

namespace Wms.Modules.Inventory.Application;

/// <summary>
/// Recomputes <c>stock_balance</c> from the ledger and reports any
/// disagreement (H1, §2.3). This is the system's own correctness monitor, and
/// the main reason the ledger design is worth its cost.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Incremental, not a full scan.</strong> Recomputing every balance
/// from a ledger of hundreds of millions of rows is a multi-hour job, which
/// means it gets disabled within a month of go-live — and a correctness
/// monitor nobody runs is worse than none, because its existence is what
/// stops anyone building a different check. Each run recomputes only the
/// position tuples that had movement since the last watermark.
/// </para>
/// <para>
/// <strong>It never corrects anything.</strong> A variance is written and
/// alerted on, never fixed silently: the difference is the symptom, and
/// quietly assigning the ledger's answer to the balance destroys the only
/// evidence of whatever caused it.
/// </para>
/// </remarks>
public sealed class ReconciliationService
{
    /// <summary>
    /// How far behind real time a run stops reading.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ADR 0002: a reader must never take its watermark from
    /// <c>max(sequence)</c>. A sequence is assigned at INSERT but the row
    /// becomes visible at COMMIT, so with concurrent writers a watermark taken
    /// from <c>max()</c> permanently skips every movement whose transaction was
    /// still open at that instant — and this consumer reporting zero variance
    /// over a set of rows it silently omitted is precisely the failure that
    /// makes a clean result worthless.
    /// </para>
    /// <para>
    /// Of the two remedies the ADR allows, this takes the second: bound the
    /// scan by a lag rather than by snapshot xmin. It rests on one stated
    /// assumption — <em>no transaction writing <c>stock_movement</c> stays
    /// open longer than this lag</em> — which the API's five-second statement
    /// timeout (C7) makes true with two orders of magnitude to spare.
    /// <c>recorded_at</c> is transaction-start time, so a movement is only
    /// ever considered once its transaction cannot still be in flight. The
    /// xmin approach is exact but compares an <c>xid</c> to an <c>xid8</c>
    /// through a text cast and has a wraparound edge; these files are read
    /// during incidents, and a comprehensible bound that is provably safe
    /// under a documented assumption beats a clever one that is not obviously
    /// either.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan VisibilityLag = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The highest sequence safe to treat as final.
    /// </summary>
    /// <remarks>
    /// Null when no movement is old enough yet, which is a normal state on a
    /// quiet warehouse and means "nothing to do", not an error.
    /// </remarks>
    private const string WatermarkSql = """
        SELECT max(sm.sequence)
          FROM stock_movement sm
          JOIN location l ON l.id = COALESCE(sm.to_location_id, sm.from_location_id)
         WHERE l.warehouse_id = @warehouseId
           AND sm.recorded_at <= now() - @lag::interval;
        """;

    private const string LastWatermarkSql = """
        SELECT COALESCE(max(to_sequence), 0)
          FROM balance_reconciliation_run
         WHERE warehouse_id = @warehouseId AND status = 'completed';
        """;

    private const string StartRunSql = """
        INSERT INTO balance_reconciliation_run
            (id, warehouse_id, started_at, from_sequence, to_sequence, status)
        VALUES (@id, @warehouseId, now(), @fromSequence, @toSequence, 'running');
        """;

    /// <summary>
    /// Recomputes each touched position from the whole ledger and compares.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The inner <c>touched</c> set is the incremental part — only tuples with
    /// movement in this window. The recompute against those tuples is
    /// deliberately <strong>not</strong> windowed: comparing a delta to a
    /// running total would find a variance on every row. Each movement
    /// contributes twice where it has both ends, negative at the source and
    /// positive at the destination, which is the same arithmetic
    /// <c>StockLedger</c> applies forward.
    /// </para>
    /// <para>
    /// <c>FULL OUTER JOIN</c>, not inner: a balance row with no ledger history
    /// and a ledger position with no balance row are both real corruption, and
    /// an inner join would report neither.
    /// </para>
    /// </remarks>
    private const string CompareSql = """
        WITH movement_leg AS (
            SELECT owner_id, item_id, lot_id, stock_status,
                   to_location_id AS location_id, quantity_base AS quantity, sequence
              FROM stock_movement
             WHERE to_location_id IS NOT NULL
            UNION ALL
            SELECT owner_id, item_id, lot_id, stock_status,
                   from_location_id, -quantity_base, sequence
              FROM stock_movement
             WHERE from_location_id IS NOT NULL
        ),
        touched AS (
            SELECT DISTINCT ml.owner_id, ml.item_id, ml.lot_id,
                            ml.stock_status, ml.location_id
              FROM movement_leg ml
              JOIN location l ON l.id = ml.location_id
             WHERE l.warehouse_id = @warehouseId
               AND ml.sequence > @fromSequence
               AND ml.sequence <= @toSequence
        ),
        ledger AS (
            SELECT t.owner_id, t.item_id, t.lot_id, t.stock_status, t.location_id,
                   COALESCE(sum(ml.quantity), 0) AS quantity
              FROM touched t
              LEFT JOIN movement_leg ml
                     ON ml.owner_id = t.owner_id
                    AND ml.item_id = t.item_id
                    AND ml.lot_id = t.lot_id
                    AND ml.stock_status = t.stock_status
                    AND ml.location_id = t.location_id
                    AND ml.sequence <= @toSequence
             GROUP BY t.owner_id, t.item_id, t.lot_id, t.stock_status, t.location_id
        )
        SELECT COALESCE(g.owner_id, b.owner_id)         AS owner_id,
               COALESCE(g.item_id, b.item_id)           AS item_id,
               COALESCE(g.location_id, b.location_id)   AS location_id,
               COALESCE(g.lot_id, b.lot_id)             AS lot_id,
               COALESCE(g.stock_status, b.stock_status) AS stock_status,
               COALESCE(g.quantity, 0)                  AS ledger_quantity,
               COALESCE(b.on_hand, 0)                   AS balance_quantity
          FROM ledger g
          FULL OUTER JOIN stock_balance b
            ON b.owner_id = g.owner_id
           AND b.item_id = g.item_id
           AND b.lot_id = g.lot_id
           AND b.stock_status = g.stock_status
           AND b.location_id = g.location_id
         WHERE g.owner_id IS NOT NULL;
        """;

    private const string RecordVarianceSql = """
        INSERT INTO balance_variance
            (id, run_id, owner_id, item_id, location_id, lot_id, stock_status,
             ledger_quantity, balance_quantity, difference)
        VALUES (@id, @runId, @ownerId, @itemId, @locationId, @lotId, @stockStatus,
                @ledgerQuantity, @balanceQuantity, @difference);
        """;

    private const string CompleteRunSql = """
        UPDATE balance_reconciliation_run
           SET completed_at = now(), rows_checked = @rowsChecked,
               variance_count = @varianceCount, status = @status
         WHERE id = @runId;
        """;

    public async Task<ReconciliationResult> RunAsync(
        NpgsqlConnection connection,
        Guid warehouseId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        long fromSequence = await ScalarAsync<long>(
            connection, transaction, LastWatermarkSql, warehouseId, cancellationToken);

        long? toSequence = await WatermarkAsync(
            connection, transaction, warehouseId, cancellationToken);

        if (toSequence is null || toSequence <= fromSequence)
        {
            // Nothing has settled since the last run. A quiet warehouse is not
            // a failure, and recording an empty run would bloat the history
            // that operations actually reads.
            await transaction.CommitAsync(cancellationToken);
            return new ReconciliationResult(null, fromSequence, fromSequence, 0, []);
        }

        Guid runId = Guid.CreateVersion7();
        await using (NpgsqlCommand start = new(StartRunSql, connection, transaction))
        {
            start.Parameters.AddWithValue("id", runId);
            start.Parameters.AddWithValue("warehouseId", warehouseId);
            start.Parameters.AddWithValue("fromSequence", fromSequence);
            start.Parameters.AddWithValue("toSequence", toSequence.Value);
            await start.ExecuteNonQueryAsync(cancellationToken);
        }

        List<BalanceVariance> variances = [];
        int rowsChecked = 0;

        await using (NpgsqlCommand compare = new(CompareSql, connection, transaction))
        {
            compare.Parameters.AddWithValue("warehouseId", warehouseId);
            compare.Parameters.AddWithValue("fromSequence", fromSequence);
            compare.Parameters.AddWithValue("toSequence", toSequence.Value);

            await using NpgsqlDataReader reader =
                await compare.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rowsChecked++;

                decimal ledgerQuantity = reader.GetDecimal(5);
                decimal balanceQuantity = reader.GetDecimal(6);
                if (ledgerQuantity == balanceQuantity)
                {
                    continue;
                }

                variances.Add(new BalanceVariance(
                    OwnerId: reader.GetGuid(0),
                    ItemId: reader.GetGuid(1),
                    LocationId: reader.GetGuid(2),
                    LotId: reader.GetGuid(3),
                    StockStatus: reader.GetString(4),
                    LedgerQuantity: ledgerQuantity,
                    BalanceQuantity: balanceQuantity));
            }
        }

        foreach (BalanceVariance variance in variances)
        {
            await RecordVarianceAsync(connection, transaction, runId, variance, cancellationToken);
        }

        await using (NpgsqlCommand complete = new(CompleteRunSql, connection, transaction))
        {
            complete.Parameters.AddWithValue("runId", runId);
            complete.Parameters.AddWithValue("rowsChecked", rowsChecked);
            complete.Parameters.AddWithValue("varianceCount", variances.Count);
            complete.Parameters.AddWithValue("status", "completed");
            await complete.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return new ReconciliationResult(
            runId, fromSequence, toSequence.Value, rowsChecked, variances);
    }

    private static async Task<long?> WatermarkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid warehouseId,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(WatermarkSql, connection, transaction);
        command.Parameters.AddWithValue("warehouseId", warehouseId);
        command.Parameters.AddWithValue("lag", VisibilityLag);

        return await command.ExecuteScalarAsync(cancellationToken) as long?;
    }

    private static async Task RecordVarianceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid runId,
        BalanceVariance variance,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(RecordVarianceSql, connection, transaction);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("runId", runId);
        command.Parameters.AddWithValue("ownerId", variance.OwnerId);
        command.Parameters.AddWithValue("itemId", variance.ItemId);
        command.Parameters.AddWithValue("locationId", variance.LocationId);
        command.Parameters.AddWithValue("lotId", variance.LotId);
        command.Parameters.AddWithValue("stockStatus", variance.StockStatus);
        command.Parameters.AddWithValue("ledgerQuantity", variance.LedgerQuantity);
        command.Parameters.AddWithValue("balanceQuantity", variance.BalanceQuantity);
        command.Parameters.AddWithValue("difference", variance.Difference);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        Guid warehouseId,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(sql, connection, transaction);
        command.Parameters.AddWithValue("warehouseId", warehouseId);
        return (T)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}

/// <param name="RunId">
/// Null when there was nothing settled to check, which is a normal outcome on
/// a quiet warehouse rather than a failed run.
/// </param>
public sealed record ReconciliationResult(
    Guid? RunId,
    long FromSequence,
    long ToSequence,
    int RowsChecked,
    IReadOnlyList<BalanceVariance> Variances);

/// <param name="Difference">
/// Ledger minus balance. Positive means the balance is understated.
/// </param>
public sealed record BalanceVariance(
    Guid OwnerId,
    Guid ItemId,
    Guid LocationId,
    Guid LotId,
    string StockStatus,
    decimal LedgerQuantity,
    decimal BalanceQuantity)
{
    public decimal Difference => LedgerQuantity - BalanceQuantity;
}
