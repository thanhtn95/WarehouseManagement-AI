namespace Wms.Modules.Identity.Contracts;

/// <summary>
/// User administration (§5.11): create, list, amend, and grant role scopes.
/// </summary>
/// <remarks>
/// There is deliberately no delete. A user is deactivated, never removed —
/// every movement and auth event they are <c>actor_user_id</c> or
/// <c>raised_by_user_id</c> on must stay attributed, and a deleted row turns
/// an audit trail into a set of orphaned ids.
/// </remarks>
public interface IUserDirectory
{
    Task<CreateUserResult> CreateAsync(CreateUserCommand command, CancellationToken cancellationToken);

    Task<UserPage> ListAsync(UserQuery query, CancellationToken cancellationToken);

    Task<UserDetail?> GetAsync(Guid userId, CancellationToken cancellationToken);

    Task<UpdateUserResult> UpdateAsync(UpdateUserCommand command, CancellationToken cancellationToken);

    Task<GrantResult> GrantRoleScopeAsync(GrantRoleScopeCommand command, CancellationToken cancellationToken);

    Task<IReadOnlyList<RoleSummary>> ListRolesAsync(
        bool includeInactive, CancellationToken cancellationToken);

    Task<IReadOnlyList<PermissionSummary>> ListPermissionsAsync(
        string? category, CancellationToken cancellationToken);
}

/// <param name="ValidUntil">
/// Null means the caller did not specify one. For operators the directory
/// applies a 90-day default rather than leaving it open-ended, because the
/// accumulation of live credentials for departed agency staff is the
/// documented shrinkage risk (§5.11, risk table).
/// </param>
public sealed record CreateUserCommand(
    string UserType,
    string DisplayName,
    string? EmployeeCode,
    string? Email,
    string Locale,
    DateOnly? ValidFrom,
    DateOnly? ValidUntil,
    Guid ActorUserId,
    IReadOnlyList<RoleScopeGrant> RoleScopes,
    IReadOnlyList<NewCredential>? Credentials = null);

/// <param name="Secret">
/// The plaintext, hashed with Argon2id before it touches the database and
/// never stored or logged in any other form.
/// </param>
public sealed record NewCredential(string Type, string Secret);

public sealed record RoleScopeGrant(string RoleCode, Guid WarehouseId, IReadOnlyList<Guid> ZoneIds);

public sealed record CreateUserResult(
    UserAdminOutcome Outcome,
    Guid? UserId,
    string? Status,
    IReadOnlyList<string>? MissingPermissions,
    string? Detail);

public sealed record UserQuery(
    Guid? WarehouseId, string? UserType, string? Status, string? Search, int Limit, Guid? AfterId);

public sealed record UserPage(IReadOnlyList<UserRow> Items, Guid? NextAfterId, bool HasMore);

public sealed record UserRow(
    Guid Id,
    string DisplayName,
    string? EmployeeCode,
    string UserType,
    string Status,
    DateOnly? ValidUntil,
    IReadOnlyList<RoleScopeRow> RoleScopes);

public sealed record RoleScopeRow(string RoleCode, string WarehouseCode, IReadOnlyList<string> ZoneCodes);

public sealed record UserDetail(
    Guid Id,
    string DisplayName,
    string? EmployeeCode,
    string UserType,
    string Locale,
    string Status,
    DateOnly ValidFrom,
    DateOnly? ValidUntil,
    Guid SecurityStamp,
    IReadOnlyList<RoleScopeRow> RoleScopes);

public sealed record UpdateUserCommand(
    Guid UserId,
    string? DisplayName,
    string? Locale,
    DateOnly? ValidUntil,
    string? Status,
    Guid ActorUserId);

/// <param name="ActiveDeviceQueueDepth">
/// M6: unsynced facts sitting on a device the suspended user has a session
/// on. Physical work already done that can never sync afterwards, because the
/// device cannot obtain a valid token again for that identity. It does not
/// block the suspension — a genuine security incident should not wait — it
/// makes the risk visible at the moment of the decision rather than as an
/// unexplained reconciliation gap weeks later.
/// </param>
public sealed record UpdateUserResult(
    UserAdminOutcome Outcome,
    string? Status,
    int ActiveDeviceQueueDepth,
    string? Detail);

public sealed record GrantRoleScopeCommand(
    Guid UserId, string RoleCode, Guid WarehouseId, IReadOnlyList<Guid> ZoneIds, Guid ActorUserId);

public sealed record GrantResult(
    UserAdminOutcome Outcome,
    Guid? GrantId,
    Guid? GrantedBy,
    DateTimeOffset? GrantedAt,
    IReadOnlyList<string>? MissingPermissions,
    string? Detail);

public enum UserAdminOutcome
{
    Succeeded,
    NotFound,
    Invalid,

    /// <summary>
    /// The actor tried to grant a role carrying permissions they do not
    /// themselves hold (H13). Rejected rather than silently accepted, because
    /// the alternative is privilege escalation by delegation — and the
    /// sharpest case is an administrator granting it to themselves.
    /// </summary>
    CannotGrantPermissionYouLack,

    /// <summary>A role code that does not exist, or is no longer active.</summary>
    UnknownRole,
}

public sealed record RoleSummary(
    Guid Id,
    string Code,
    string Name,
    bool IsSystem,
    bool IsActive,
    IReadOnlyList<string> Permissions);

public sealed record PermissionSummary(string Code, string Category, string Description);
