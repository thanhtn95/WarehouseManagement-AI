using Npgsql;
using Wms.Modules.Platform.Contracts;

namespace Wms.Modules.Platform.Infrastructure;

/// <inheritdoc />
public sealed class Outbox : IOutbox
{
    private const string EnqueueSql = """
        INSERT INTO outbox_message (id, aggregate_type, aggregate_id,
                                    message_type, payload, created_at)
        VALUES (@id, @aggregateType, @aggregateId, @messageType, @payload::jsonb, now());
        """;

    public async Task EnqueueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string aggregateType,
        Guid aggregateId,
        string messageType,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(EnqueueSql, connection, transaction);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("aggregateType", aggregateType);
        command.Parameters.AddWithValue("aggregateId", aggregateId);
        command.Parameters.AddWithValue("messageType", messageType);
        command.Parameters.AddWithValue("payload", payloadJson);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
