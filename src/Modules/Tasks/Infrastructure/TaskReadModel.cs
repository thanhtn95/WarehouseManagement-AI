using Npgsql;
using Wms.Modules.Tasks.Contracts;

namespace Wms.Modules.Tasks.Infrastructure;

/// <inheritdoc />
public sealed class TaskReadModel(NpgsqlDataSource dataSource) : ITaskReadModel
{
    private const string Sql = """
        WITH day AS (
            SELECT (((now() AT TIME ZONE w.timezone) - w.day_boundary_time))::date AS business_date,
                   w.timezone, w.day_boundary_time
              FROM warehouse w WHERE w.id = @warehouseId
        )
        SELECT
            count(*) FILTER (WHERE t.status = 'ready')::int,
            count(*) FILTER (WHERE t.status = 'leased')::int,
            count(*) FILTER (
                WHERE t.status = 'completed'
                  AND (((t.completed_at AT TIME ZONE d.timezone) - d.day_boundary_time))::date
                      = d.business_date)::int
          FROM task t, day d
         WHERE t.warehouse_id = @warehouseId
           AND t.task_type = 'putaway';
        """;

    public async Task<PutawaySummary> GetPutawaySummaryAsync(
        Guid warehouseId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("warehouseId", warehouseId);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new PutawaySummary(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2))
            : new PutawaySummary(0, 0, 0);
    }
}
