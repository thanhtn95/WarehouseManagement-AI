using Npgsql;
using Wms.Modules.Identity.Contracts;

namespace Wms.Modules.Identity.Infrastructure;

/// <summary>
/// A development-only stand-in for real authentication: it trusts a header
/// naming the operator, then resolves that operator's <em>real</em>
/// permissions from the database.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This authenticates nobody.</strong> Anyone who can reach the API
/// can name any user. It exists so the fact endpoint and the admin screens
/// can be built and tested against the real permission model before badge
/// and PIN login (§6.1) lands, and it is registered only in the Development
/// environment — see <c>Program.cs</c>, which substitutes a resolver that
/// authenticates nothing at all everywhere else. Tracked in
/// <c>docs/shortcuts.md</c>.
/// </para>
/// <para>
/// Authorization, by contrast, is <strong>not</strong> stubbed. Permissions
/// come from <c>user_role_scope</c> joined through <c>role_permission</c>,
/// so a Receiver really does lack <c>receipt.over_receive</c> here exactly as
/// they will in production. Faking permissions too would have made every
/// authorization test meaningless, and the seeded role matrix is the part
/// most worth exercising early.
/// </para>
/// </remarks>
public sealed class DevelopmentPrincipalResolver : IOperatorPrincipalResolver
{
    /// <summary>
    /// The header naming the operator. Named to be impossible to mistake for
    /// a real credential in a log or a proxy configuration.
    /// </summary>
    public const string OperatorHeader = "X-Dev-Operator-Id";

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>
    /// Refuses to exist outside Development, and refuses to exist unless
    /// switched on deliberately.
    /// </summary>
    /// <remarks>
    /// The guard lives in the constructor rather than only at the
    /// registration site because the registration site is a line in a file
    /// nobody is editing when the mistake gets made — when the Worker needs a
    /// resolver, or Identity grows an <c>AddIdentity(...)</c>, or a test host
    /// wires its own. Two independent conditions are required so that one
    /// stray environment variable copied from a development compose file into
    /// a customer's is not sufficient to turn header-trust on.
    /// </remarks>
    public DevelopmentPrincipalResolver(
        NpgsqlDataSource dataSource, string environmentName, bool explicitlyEnabled)
    {
        if (!string.Equals(environmentName, "Development", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{nameof(DevelopmentPrincipalResolver)} trusts a request header instead of "
                + $"verifying a credential and must never be constructed outside Development "
                + $"(environment was '{environmentName}').");
        }

        if (!explicitlyEnabled)
        {
            throw new InvalidOperationException(
                $"{nameof(DevelopmentPrincipalResolver)} requires "
                + "Wms:Auth:AllowDevelopmentHeaderPrincipal to be set to true.");
        }

        _dataSource = dataSource;
    }

    /// <summary>
    /// Only a currently-valid, active user resolves.
    /// </summary>
    /// <remarks>
    /// The validity window is enforced, not just <c>status</c>. §2.1 defines
    /// <c>valid_until</c> as "NULL = open-ended; set for agency labour", so
    /// access that was meant to end on a date must end on that date rather
    /// than whenever someone remembers to change <c>status</c> by hand —
    /// otherwise a revocation that was scheduled simply does not revoke.
    /// This SQL is the template the real resolver will start from, which is
    /// why it is worth getting right while it is four lines.
    /// </remarks>
    private const string ResolveSql = """
        SELECT urs.warehouse_id, urs.zone_ids, rp.permission_code
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
    /// A device is only ever adopted from the database, and only while it is
    /// active.
    /// </summary>
    /// <remarks>
    /// Taking it from the header unchecked would mean the audit trail §8.1
    /// promises — "actor <em>and device</em>" — records whatever the caller
    /// typed, so an operator could attribute a mis-receipt to another
    /// handheld. It would also make a device marked <c>lost</c>
    /// indistinguishable from a live one, which is finding H11 being rebuilt
    /// before the sessions that are supposed to fix it. Once device sessions
    /// exist (§6.1) the device comes from the session binding and this query
    /// goes away entirely.
    /// </remarks>
    private const string ResolveDeviceSql = """
        SELECT id FROM device WHERE id = @deviceId AND status = 'active';
        """;

    public async Task<OperatorPrincipal?> ResolveAsync(
        string? authorization,
        string? deviceId,
        CancellationToken cancellationToken)
    {
        // `authorization` carries the operator id here instead of a token.
        if (!Guid.TryParse(authorization, out Guid userId))
        {
            return null;
        }

        Guid? device = Guid.TryParse(deviceId, out Guid claimed)
            ? await ResolveDeviceAsync(claimed, cancellationToken)
            : null;

        await using NpgsqlCommand command = _dataSource.CreateCommand(ResolveSql);
        command.Parameters.AddWithValue("userId", userId);

        HashSet<string> permissions = new(StringComparer.Ordinal);
        Dictionary<Guid, (Guid[] Zones, HashSet<string> Permissions)> byWarehouse = [];
        bool userExists = false;

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            // The LEFT JOINs mean an active user with no roles still returns
            // one row, with a null permission. That is a real state — a user
            // created but not yet granted anything — and it must resolve to
            // an authenticated principal holding nothing, not to a 401.
            userExists = true;
            if (await reader.IsDBNullAsync(2, cancellationToken))
            {
                continue;
            }

            string permission = reader.GetString(2);
            permissions.Add(permission);

            Guid warehouseId = reader.GetGuid(0);
            if (!byWarehouse.TryGetValue(warehouseId, out var scope))
            {
                scope = (reader.GetFieldValue<Guid[]>(1), new HashSet<string>(StringComparer.Ordinal));
                byWarehouse[warehouseId] = scope;
            }

            scope.Permissions.Add(permission);
        }

        return userExists
            ? new OperatorPrincipal(
                userId, device, permissions,
                [.. byWarehouse.Select(kv => new OperatorScope(kv.Key, kv.Value.Zones, kv.Value.Permissions))])
            : null;
    }

    private async Task<Guid?> ResolveDeviceAsync(Guid claimed, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(ResolveDeviceSql);
        command.Parameters.AddWithValue("deviceId", claimed);

        // An unregistered, retired or lost device resolves to no device at
        // all rather than to an error: the fact still happened and must still
        // be recorded, it simply carries no trustworthy device attribution.
        return await command.ExecuteScalarAsync(cancellationToken) as Guid?;
    }
}

/// <summary>
/// The resolver used everywhere except Development: it authenticates nobody,
/// so every authenticated endpoint answers <c>401</c>.
/// </summary>
/// <remarks>
/// Registered deliberately rather than leaving the service missing, because
/// a missing registration fails at the first request with a dependency-
/// injection error — a <c>500</c> that reads like a crash. This fails
/// <em>closed</em> and legibly: the API starts, health checks pass, and
/// anything needing an operator says plainly that nobody is authenticated
/// until the Identity module lands.
/// </remarks>
public sealed class UnconfiguredPrincipalResolver : IOperatorPrincipalResolver
{
    public Task<OperatorPrincipal?> ResolveAsync(
        string? authorization,
        string? deviceId,
        CancellationToken cancellationToken) => Task.FromResult<OperatorPrincipal?>(null);
}
