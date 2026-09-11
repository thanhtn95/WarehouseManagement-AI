using Npgsql;
using Wms.Modules.Identity.Contracts;

namespace Wms.Modules.Identity.Infrastructure;

/// <summary>
/// The real principal resolver: a bearer token, validated, then resolved
/// against current database state.
/// </summary>
/// <remarks>
/// <para>
/// Permissions are read per request rather than taken from the token, and the
/// token's <c>security_stamp</c> is compared with the user's current one. That
/// pairing is what §8.1's "termination takes effect in seconds" actually
/// means: suspending a user, changing their validity, or altering a role
/// invalidates every token already in circulation at the next request, without
/// waiting for it to expire.
/// </para>
/// <para>
/// It costs one indexed query per request. That is the price of revocation
/// being real, and it is the same query the development stand-in was already
/// making.
/// </para>
/// </remarks>
public sealed class TokenPrincipalResolver(NpgsqlDataSource dataSource, ITokenIssuer tokens)
    : IOperatorPrincipalResolver
{
    /// <summary>
    /// Identity, current stamp, and scoped permissions in one round trip.
    /// </summary>
    /// <remarks>
    /// The validity window and status are filtered here exactly as they are in
    /// the directory's own checks, so an expired account stops resolving the
    /// moment the date passes rather than when someone remembers to suspend
    /// it.
    /// </remarks>
    private const string ResolveSql = """
        SELECT u.security_stamp, urs.warehouse_id, urs.zone_ids, rp.permission_code
          FROM app_user u
          LEFT JOIN user_role_scope urs ON urs.user_id = u.id
          LEFT JOIN role r              ON r.id = urs.role_id AND r.is_active
          LEFT JOIN role_permission rp  ON rp.role_id = r.id
         WHERE u.id = @userId
           AND u.status = 'active'
           AND u.valid_from <= current_date
           AND (u.valid_until IS NULL OR u.valid_until >= current_date);
        """;

    /// <summary>
    /// A device session that has ended stops authorising its token.
    /// </summary>
    /// <remarks>
    /// Without this, logging out of a shared handheld would leave the access
    /// token working until it expired — and on pooled devices the next
    /// operator is holding that same unit.
    /// </remarks>
    private const string SessionOpenSql = """
        SELECT 1 FROM device_session WHERE id = @sessionId AND ended_at IS NULL;
        """;

    public async Task<OperatorPrincipal?> ResolveAsync(
        string? authorization,
        string? deviceId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(authorization))
        {
            return null;
        }

        const string bearer = "Bearer ";
        string token = authorization.StartsWith(bearer, StringComparison.OrdinalIgnoreCase)
            ? authorization[bearer.Length..]
            : authorization;

        TokenClaims? claims = await tokens.ValidateAsync(token);
        if (claims is null)
        {
            return null;
        }

        if (claims.SessionId is Guid session
            && !await SessionIsOpenAsync(session, cancellationToken))
        {
            return null;
        }

        await using NpgsqlCommand command = dataSource.CreateCommand(ResolveSql);
        command.Parameters.AddWithValue("userId", claims.UserId);

        HashSet<string> permissions = new(StringComparer.Ordinal);
        Dictionary<Guid, (Guid[] Zones, HashSet<string> Permissions)> byWarehouse = [];
        Guid? currentStamp = null;

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            currentStamp = reader.GetGuid(0);

            if (await reader.IsDBNullAsync(3, cancellationToken))
            {
                continue;
            }

            string permission = reader.GetString(3);
            permissions.Add(permission);

            Guid warehouseId = reader.GetGuid(1);
            if (!byWarehouse.TryGetValue(warehouseId, out var scope))
            {
                scope = (reader.GetFieldValue<Guid[]>(2), new HashSet<string>(StringComparer.Ordinal));
                byWarehouse[warehouseId] = scope;
            }

            scope.Permissions.Add(permission);
        }

        // A stale stamp is the revocation signal. Refusing here, rather than
        // letting the token run to expiry, is the whole point of carrying it.
        if (currentStamp is not Guid stamp || stamp != claims.SecurityStamp)
        {
            return null;
        }

        return new OperatorPrincipal(
            claims.UserId,
            claims.DeviceId,
            permissions,
            [.. byWarehouse.Select(kv =>
                new OperatorScope(kv.Key, kv.Value.Zones, kv.Value.Permissions))],
            claims.SessionId,
            stamp);
    }

    private async Task<bool> SessionIsOpenAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = dataSource.CreateCommand(SessionOpenSql);
        command.Parameters.AddWithValue("sessionId", sessionId);

        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }
}
