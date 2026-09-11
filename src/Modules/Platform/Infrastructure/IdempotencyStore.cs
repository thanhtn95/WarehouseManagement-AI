using Npgsql;
using Wms.Modules.Platform.Contracts;

namespace Wms.Modules.Platform.Infrastructure;

/// <inheritdoc />
public sealed class IdempotencyStore : IIdempotencyStore
{
    /// <summary>
    /// Claim-then-inspect, not inspect-then-claim. Two simultaneous replays
    /// of the same key both reach here; exactly one wins the insert and the
    /// other falls through to the lookup below.
    /// </summary>
    private const string ClaimSql = """
        INSERT INTO idempotency_record (key, user_id, request_hash,
                                        response_status, response_body, created_at)
        VALUES (@key, @userId, @requestHash, 0, NULL, now())
        ON CONFLICT (key, user_id) DO NOTHING
        RETURNING 1;
        """;

    private const string ExistingSql = """
        SELECT request_hash, response_body
          FROM idempotency_record
         WHERE key = @key AND user_id = @userId;
        """;

    private const string CompleteSql = """
        UPDATE idempotency_record
           SET response_status = @responseStatus,
               response_body   = @responseBody::jsonb
         WHERE key = @key AND user_id = @userId;
        """;

    public async Task<IdempotencyClaim> TryClaimAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string key,
        Guid userId,
        string requestHash,
        CancellationToken cancellationToken)
    {
        await using (NpgsqlCommand claim = new(ClaimSql, connection, transaction))
        {
            claim.Parameters.AddWithValue("key", key);
            claim.Parameters.AddWithValue("userId", userId);
            claim.Parameters.AddWithValue("requestHash", requestHash);

            if (await claim.ExecuteScalarAsync(cancellationToken) is not null)
            {
                return new IdempotencyClaim(IdempotencyOutcome.Claimed, null);
            }
        }

        await using NpgsqlCommand existing = new(ExistingSql, connection, transaction);
        existing.Parameters.AddWithValue("key", key);
        existing.Parameters.AddWithValue("userId", userId);

        await using NpgsqlDataReader reader = await existing.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            // The row vanished between the failed insert and this read, which
            // should be impossible — records are never deleted inside a
            // transaction. Treat it as a conflict rather than guessing.
            return new IdempotencyClaim(IdempotencyOutcome.Conflict, null);
        }

        string storedHash = reader.GetString(0);
        string? storedBody = reader.IsDBNull(1) ? null : reader.GetString(1);

        return storedHash == requestHash
            ? new IdempotencyClaim(IdempotencyOutcome.Replay, storedBody)
            : new IdempotencyClaim(IdempotencyOutcome.Conflict, null);
    }

    public async Task CompleteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string key,
        Guid userId,
        int responseStatus,
        string responseBody,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(CompleteSql, connection, transaction);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("responseStatus", responseStatus);
        command.Parameters.AddWithValue("responseBody", responseBody);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
