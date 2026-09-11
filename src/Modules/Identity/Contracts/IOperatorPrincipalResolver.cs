namespace Wms.Modules.Identity.Contracts;

/// <summary>
/// Turns whatever a request carries — a bearer token today's implementation
/// understands, a device header — into the operator the server will act as.
/// </summary>
/// <remarks>
/// Deliberately framework-free: it takes header values rather than an
/// <c>HttpContext</c>, so Identity stays a plain class library and the same
/// resolution can be driven from the Worker or a test without an HTTP
/// pipeline.
///
/// Returning <see langword="null"/> means "not authenticated" and must
/// produce a <c>401</c>. It never means "authenticated but not allowed" —
/// that is a permission question, answered from
/// <see cref="OperatorPrincipal.Permissions"/> by the caller.
/// </remarks>
public interface IOperatorPrincipalResolver
{
    Task<OperatorPrincipal?> ResolveAsync(
        string? authorization,
        string? deviceId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Who the server is acting as, and what they are allowed to do.
/// </summary>
/// <param name="UserId">
/// The actor recorded on every movement. It comes from the credential, never
/// from a request payload — a device that could name its own actor could
/// attribute a movement to anyone (invariant 8).
/// </param>
/// <param name="DeviceId">
/// The registered handheld, when the request came from one. Forensic: it
/// answers "which unit was this scanned on" after the fact — which means it
/// is only worth recording if it cannot be chosen by the caller.
/// <para>
/// An implementation must therefore <strong>never</strong> take this from a
/// request header unchecked. It belongs to the device session, so that
/// marking a device <c>lost</c> actually cuts it off (review finding H11);
/// until sessions exist, the minimum bar is a lookup requiring
/// <c>device.status = 'active'</c>, with anything else resolving to no
/// device rather than to the value supplied.
/// </para>
/// </param>
/// <param name="Permissions">
/// Resolved permission codes, already reduced from role and scope. Callers
/// check these, **never** a role name — a role-name comparison is what
/// invariant 8 and the architecture test exist to forbid.
/// </param>
/// <param name="Scopes">
/// Where those permissions apply. Invariant 8 is "permissions **plus
/// scope**", and until this was populated the system could answer *what* an
/// operator may do but not *where* — which is half a check.
/// </param>
public sealed record OperatorPrincipal(
    Guid UserId,
    Guid? DeviceId,
    IReadOnlySet<string> Permissions,
    IReadOnlyList<OperatorScope> Scopes,
    Guid? SessionId = null,
    Guid? SecurityStamp = null)
{
    /// <summary>
    /// Whether the operator holds this permission <em>anywhere</em>.
    /// </summary>
    /// <remarks>
    /// Correct only where the request names no warehouse. Anywhere a
    /// warehouse is known, <see cref="CanIn"/> is the right question: someone
    /// scoped to Osaka holding <c>receipt.confirm</c> must not thereby confirm
    /// a receipt in Tokyo.
    /// </remarks>
    public bool Can(string permission) => Permissions.Contains(permission);

    public bool CanIn(string permission, Guid warehouseId) =>
        Scopes.Any(scope =>
            scope.WarehouseId == warehouseId && scope.Permissions.Contains(permission));
}

/// <param name="ZoneIds">
/// Empty means every zone in the warehouse (§2.1).
/// </param>
public sealed record OperatorScope(
    Guid WarehouseId,
    IReadOnlyList<Guid> ZoneIds,
    IReadOnlySet<string> Permissions);
