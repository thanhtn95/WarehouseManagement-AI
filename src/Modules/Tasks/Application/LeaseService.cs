using System.Data;
using Npgsql;

namespace Wms.Modules.Tasks.Application;

/// <summary>
/// Task dispatch (§5.3, §6.8): hands a device a batch of work and a lease
/// over it.
/// </summary>
/// <remarks>
/// <para>
/// The response is <strong>self-contained by design</strong>. Item names in
/// the operator's locale, barcodes, and the applicable reason codes all
/// travel with the batch, because the device must be able to complete every
/// leased task with no further server contact — that is what makes the
/// offline path work rather than merely tolerate brief outages.
/// </para>
/// <para>
/// No work available is <c>200</c> with an empty list, never <c>404</c>. An
/// idle poller is a normal state, not an error, and a handheld that treated
/// it as one would show an operator a failure every few seconds.
/// </para>
/// </remarks>
public sealed class LeaseService
{
    /// <summary>
    /// Long enough to survive a full shift offline (§2.4).
    /// </summary>
    /// <remarks>
    /// A lease exists to let a supervisor reclaim work from a device that has
    /// gone under a forklift, not to police how fast people work. Expiring it
    /// in minutes would reclaim tasks from operators who are simply out of
    /// coverage, and their queued confirmations would then arrive against
    /// tasks someone else had already been given.
    /// </remarks>
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromHours(10);

    /// <summary>
    /// The dispatch query from §6.8.
    /// </summary>
    /// <remarks>
    /// Two different mechanisms are at work here and it is worth not
    /// conflating them. <strong>Disjointness</strong> — no task issued twice —
    /// comes from the row lock plus the <c>status = 'ready'</c> predicate:
    /// under READ COMMITTED a blocked <c>FOR UPDATE</c> re-checks that
    /// predicate against a fresh snapshot and drops a row that was leased
    /// while it waited. <strong><c>SKIP LOCKED</c></strong> is what stops the
    /// pollers queueing behind each other at all, which is a throughput
    /// property, not a correctness one — and at fifty operators it is the
    /// difference between dispatch that scales and operators standing still
    /// waiting for a task list.
    ///
    /// Removing <c>SKIP LOCKED</c> therefore does <em>not</em> fail a
    /// "no duplicates" race test; it fails
    /// <c>ALeaseIsNotBlockedByTasksAnotherPollerHasLocked</c>, which is the
    /// test written to pin the property that actually depends on it.
    ///
    /// An empty <c>@zoneIds</c> means "any zone in this warehouse" rather than
    /// "no zones": a device that has not been pinned to a zone should still
    /// receive work.
    /// </remarks>
    private const string LeaseSql = """
        UPDATE task
           SET status             = 'leased',
               lease_user_id      = @userId,
               lease_device_id    = @deviceId,
               lease_id           = @leaseId,
               lease_expires_at   = @expiresAt,
               lease_heartbeat_at = now(),
               version            = version + 1
         WHERE id IN (
             SELECT id
               FROM task
              WHERE status = 'ready'
                AND warehouse_id = @warehouseId
                AND task_type = ANY(@taskTypes)
                AND (cardinality(@zoneIds) = 0 OR zone_id = ANY(@zoneIds))
              ORDER BY priority, sort_sequence
              FOR UPDATE SKIP LOCKED
              LIMIT @batchSize
         )
        RETURNING id, task_type, priority;
        """;

    /// <summary>
    /// Everything the device needs to work the batch without asking again.
    /// </summary>
    /// <remarks>
    /// The item name is resolved to the operator's locale here rather than on
    /// the device, so the handheld never has to hold a translation table, and
    /// falls back to English rather than to nothing — a blank line on a
    /// handheld is worse than a name in the wrong language.
    /// </remarks>
    private const string LinesSql = """
        SELECT tl.task_id, tl.id, tl.line_no,
               tl.from_location_id, fl.code, fl.pick_sequence,
               tl.to_location_id, dl.code,
               tl.item_id, i.sku_code,
               COALESCE(i.name_i18n->>@locale, i.name_i18n->>'en') AS name,
               COALESCE(
                   (SELECT array_agg(b.barcode ORDER BY b.barcode)
                      FROM item_barcode b WHERE b.item_id = tl.item_id),
                   '{}') AS barcodes,
               tl.requested_quantity, tl.uom_code,
               COALESCE(iu.is_discrete, true) AS is_discrete
          FROM task_line tl
          JOIN item i ON i.id = tl.item_id
          LEFT JOIN location fl ON fl.id = tl.from_location_id
          LEFT JOIN location dl ON dl.id = tl.to_location_id
          LEFT JOIN item_uom iu ON iu.item_id = tl.item_id AND iu.uom_code = tl.uom_code
         WHERE tl.task_id = ANY(@taskIds)
         ORDER BY tl.task_id, tl.line_no;
        """;

    /// <summary>
    /// The reason codes valid for the work being handed out.
    /// </summary>
    /// <remarks>
    /// Sent with the batch so an operator who hits a full bin can pick
    /// <c>PUT_LOCATION_FULL</c> while offline. A device that had to fetch
    /// these would be unable to report the one situation it most needs to.
    /// </remarks>
    private const string ReasonCodesSql = """
        SELECT code,
               COALESCE(label_i18n->>@locale, label_i18n->>'en') AS label,
               requires_note, requires_photo, requires_approval
          FROM reason_code
         WHERE is_active
           AND applies_to && @movementTypes
         ORDER BY sort_order, code;
        """;

    /// <summary>
    /// Scoped to the caller's own <c>lease_user_id</c>, not <c>lease_id</c>
    /// alone.
    /// </summary>
    /// <remarks>
    /// Without this, any operator holding <c>task.lease</c> anywhere could
    /// release a lease held by someone else, in any warehouse, just by naming
    /// its id. Reclaiming another operator's (expired) lease is a distinct,
    /// supervisor-only act — <c>task.reassign</c> against
    /// <c>/work/tasks/{id}/reclaim</c> (§5.3) — not this endpoint.
    /// </remarks>
    private const string ReleaseSql = """
        UPDATE task
           SET status             = 'ready',
               lease_user_id      = NULL,
               lease_device_id    = NULL,
               lease_id           = NULL,
               lease_expires_at   = NULL,
               lease_heartbeat_at = NULL,
               version            = version + 1
         WHERE lease_id = @leaseId
           AND lease_user_id = @userId
           AND status = 'leased';
        """;

    public async Task<LeasedWork> LeaseAsync(
        NpgsqlConnection connection,
        LeaseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request);

        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        Guid leaseId = Guid.CreateVersion7();
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.Add(LeaseDuration);

        List<LeasedTask> tasks = [];
        await using (NpgsqlCommand command = new(LeaseSql, connection, transaction))
        {
            command.Parameters.AddWithValue("userId", request.UserId);
            command.Parameters.AddWithValue("deviceId", (object?)request.DeviceId ?? DBNull.Value);
            command.Parameters.AddWithValue("leaseId", leaseId);
            command.Parameters.AddWithValue("expiresAt", expiresAt);
            command.Parameters.AddWithValue("warehouseId", request.WarehouseId);
            command.Parameters.AddWithValue("taskTypes", request.TaskTypes.ToArray());
            command.Parameters.AddWithValue("zoneIds", request.ZoneIds.ToArray());
            command.Parameters.AddWithValue("batchSize", request.BatchSize);

            await using NpgsqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tasks.Add(new LeasedTask(
                    reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), []));
            }
        }

        if (tasks.Count == 0)
        {
            // Nothing to do is not a failure. Committing an empty transaction
            // is cheaper than the branch needed to avoid it.
            await transaction.CommitAsync(cancellationToken);
            return new LeasedWork(null, null, [], []);
        }

        string locale = await LocaleAsync(connection, transaction, request.UserId, cancellationToken);

        Dictionary<Guid, List<LeasedLine>> linesByTask = await LoadLinesAsync(
            connection, transaction, [.. tasks.Select(t => t.Id)], locale, cancellationToken);

        IReadOnlyList<LeasedReasonCode> reasonCodes = await LoadReasonCodesAsync(
            connection, transaction, request.TaskTypes, locale, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        LeasedTask[] hydrated =
        [
            .. tasks.Select(t => t with
            {
                Lines = linesByTask.TryGetValue(t.Id, out List<LeasedLine>? lines) ? lines : [],
            }),
        ];

        return new LeasedWork(leaseId, expiresAt, hydrated, reasonCodes);
    }

    /// <summary>
    /// Returns unworked tasks to <c>ready</c> (§5.3).
    /// </summary>
    /// <remarks>
    /// Only tasks still <c>leased</c> are released. One already confirmed has
    /// left the lease's control and must not be resurrected by an operator
    /// tapping "release" after the fact.
    /// </remarks>
    public async Task<int> ReleaseAsync(
        NpgsqlConnection connection,
        Guid leaseId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using NpgsqlCommand command = new(ReleaseSql, connection);
        command.Parameters.AddWithValue("leaseId", leaseId);
        command.Parameters.AddWithValue("userId", userId);

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string> LocaleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command =
            new("SELECT locale FROM app_user WHERE id = @id;", connection, transaction);
        command.Parameters.AddWithValue("id", userId);

        return await command.ExecuteScalarAsync(cancellationToken) as string ?? "en";
    }

    private static async Task<Dictionary<Guid, List<LeasedLine>>> LoadLinesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid[] taskIds,
        string locale,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(LinesSql, connection, transaction);
        command.Parameters.AddWithValue("taskIds", taskIds);
        command.Parameters.AddWithValue("locale", locale);

        Dictionary<Guid, List<LeasedLine>> byTask = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            Guid taskId = reader.GetGuid(0);
            if (!byTask.TryGetValue(taskId, out List<LeasedLine>? lines))
            {
                lines = [];
                byTask[taskId] = lines;
            }

            lines.Add(new LeasedLine(
                Id: reader.GetGuid(1),
                LineNo: reader.GetInt32(2),
                From: reader.IsDBNull(3)
                    ? null
                    : new LeasedLocation(reader.GetGuid(3), reader.GetString(4), reader.GetInt32(5)),
                To: reader.IsDBNull(6)
                    ? null
                    : new LeasedLocation(reader.GetGuid(6), reader.GetString(7), null),
                Item: new LeasedItem(
                    reader.GetGuid(8),
                    reader.GetString(9),
                    reader.GetString(10),
                    reader.GetFieldValue<string[]>(11)),
                RequestedQuantity: reader.GetDecimal(12),
                Uom: reader.GetString(13),
                IsDiscrete: reader.GetBoolean(14)));
        }

        return byTask;
    }

    private static async Task<IReadOnlyList<LeasedReasonCode>> LoadReasonCodesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<string> taskTypes,
        string locale,
        CancellationToken cancellationToken)
    {
        // reason_code.applies_to is keyed by movement type, and for the task
        // types Phase 1A dispatches the two names coincide.
        await using NpgsqlCommand command = new(ReasonCodesSql, connection, transaction);
        command.Parameters.AddWithValue("movementTypes", taskTypes.ToArray());
        command.Parameters.AddWithValue("locale", locale);

        List<LeasedReasonCode> codes = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            codes.Add(new LeasedReasonCode(
                reader.GetString(0), reader.GetString(1),
                reader.GetBoolean(2), reader.GetBoolean(3), reader.GetBoolean(4)));
        }

        return codes;
    }
}
