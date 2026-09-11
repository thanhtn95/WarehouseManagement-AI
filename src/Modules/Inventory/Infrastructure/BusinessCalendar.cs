using Npgsql;
using Wms.Modules.Inventory.Contracts;

namespace Wms.Modules.Inventory.Infrastructure;

/// <inheritdoc />
public sealed class BusinessCalendar : IBusinessCalendar
{
    private const string Sql = """
        SELECT (((now() AT TIME ZONE w.timezone) - w.day_boundary_time))::date
          FROM warehouse w
         WHERE w.id = @warehouseId;
        """;

    public async Task<DateOnly> ResolveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid warehouseId,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(Sql, connection, transaction);
        command.Parameters.AddWithValue("warehouseId", warehouseId);

        // Npgsql maps PostgreSQL `date` to DateOnly, not DateTime.
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return result is DateOnly businessDate
            ? businessDate
            : throw new InvalidOperationException($"Unknown warehouse {warehouseId}.");
    }
}
