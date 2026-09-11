using Wms.Modules.Identity.Contracts;

namespace Wms.Api.Administration;

/// <summary>
/// User administration (§5.11).
/// </summary>
/// <remarks>
/// There is deliberately no <c>DELETE /users/{id}</c>. A user is deactivated,
/// never removed: every movement and auth event they are attributed on must
/// keep resolving to a real person.
/// </remarks>
public static class UserEndpoints
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    public static void MapUserAdministration(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder users = routes.MapGroup("/api/v1/users").WithTags("Administration");

        users.MapPost("/", CreateAsync)
            .WithName("CreateUser")
            .WithSummary("Create a user with role scopes.")
            .WithDescription(
                "Operators default to a 90-day validity rather than open-ended, so a " +
                "departed agency worker's credentials expire on their own.");

        users.MapGet("/", ListAsync).WithName("ListUsers");
        users.MapGet("/{id:guid}", GetAsync).WithName("GetUser");

        users.MapPatch("/{id:guid}", UpdateAsync)
            .WithName("UpdateUser")
            .WithSummary("Amend a user, or change their status.")
            .WithDescription(
                "Suspending or ending bumps security_stamp, so existing tokens fail on " +
                "their next request. The response reports any unsynced device queue depth " +
                "that this strands.");

        users.MapPost("/{id:guid}/role-scopes", GrantAsync)
            .WithName("GrantRoleScope")
            .WithSummary("Grant a role within a warehouse scope.")
            .WithDescription(
                "Refused if the role carries permissions the granting administrator does " +
                "not themselves hold.");

        routes.MapGet("/api/v1/roles", ListRolesAsync)
            .WithTags("Administration").WithName("ListRoles");

        routes.MapGet("/api/v1/permissions", ListPermissionsAsync)
            .WithTags("Administration").WithName("ListPermissions");
    }

    private static async Task<IResult> CreateAsync(
        CreateUserRequest? request,
        HttpContext http,
        IOperatorPrincipalResolver principals,
        IUserDirectory users,
        CancellationToken cancellationToken)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.DisplayName)
            || string.IsNullOrWhiteSpace(request.UserType))
        {
            return Problem(StatusCodes.Status400BadRequest, ProblemTypes.MalformedRequest,
                "userType and displayName are required.");
        }

        (OperatorPrincipal? actor, IResult? refusal) =
            await AuthorizeAsync(http, principals, "user.manage", cancellationToken);

        if (refusal is not null)
        {
            return refusal;
        }

        // user.manage alone opens an account; assigning it access or a way to
        // log in are separate acts, gated by the same permissions their
        // dedicated endpoints require. Without this, user.manage would be an
        // identity-minting capability — create a user, hand it a role, set
        // its password — with none of the checks role.manage and
        // credential.manage exist to enforce (invariant 9).
        if (request.RoleScopes is { Count: > 0 } && !actor!.Can("role.manage"))
        {
            return Problem(StatusCodes.Status403Forbidden, ProblemTypes.InsufficientPermission,
                "Assigning role scopes at creation also requires 'role.manage'.");
        }

        if (request.Credentials is { Count: > 0 } && !actor!.Can("credential.manage"))
        {
            return Problem(StatusCodes.Status403Forbidden, ProblemTypes.InsufficientPermission,
                "Setting a credential at creation also requires 'credential.manage'.");
        }

        CreateUserResult result = await users.CreateAsync(
            new CreateUserCommand(
                UserType: request.UserType,
                DisplayName: request.DisplayName,
                EmployeeCode: request.EmployeeCode,
                Email: request.Email,
                Locale: string.IsNullOrWhiteSpace(request.Locale) ? "en" : request.Locale,
                ValidFrom: request.ValidFrom,
                ValidUntil: request.ValidUntil,
                ActorUserId: actor!.UserId,
                RoleScopes: [.. (request.RoleScopes ?? []).Select(s =>
                    new RoleScopeGrant(s.RoleCode, s.WarehouseId, s.ZoneIds ?? []))],
                Credentials: [.. (request.Credentials ?? []).Select(c =>
                    new NewCredential(c.Type, c.Secret))]),
            cancellationToken);

        return result.Outcome switch
        {
            UserAdminOutcome.Succeeded => Results.Created(
                $"/api/v1/users/{result.UserId}",
                new { id = result.UserId, employeeCode = request.EmployeeCode, status = result.Status }),

            UserAdminOutcome.CannotGrantPermissionYouLack => Escalation(result.MissingPermissions),

            UserAdminOutcome.UnknownRole => Problem(
                StatusCodes.Status400BadRequest, ProblemTypes.MalformedRequest, result.Detail!),

            _ => Problem(
                StatusCodes.Status400BadRequest, ProblemTypes.MalformedRequest,
                result.Detail ?? "Invalid request."),
        };
    }

    private static async Task<IResult> GrantAsync(
        Guid id,
        GrantRoleScopeRequest? request,
        HttpContext http,
        IOperatorPrincipalResolver principals,
        IUserDirectory users,
        CancellationToken cancellationToken)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.RoleCode)
            || request.WarehouseId == Guid.Empty)
        {
            return Problem(StatusCodes.Status400BadRequest, ProblemTypes.MalformedRequest,
                "roleCode and warehouseId are required.");
        }

        (OperatorPrincipal? actor, IResult? refusal) =
            await AuthorizeAsync(http, principals, "role.manage", cancellationToken);

        if (refusal is not null)
        {
            return refusal;
        }

        GrantResult result = await users.GrantRoleScopeAsync(
            new GrantRoleScopeCommand(
                id, request.RoleCode, request.WarehouseId, request.ZoneIds ?? [], actor!.UserId),
            cancellationToken);

        return result.Outcome switch
        {
            UserAdminOutcome.Succeeded => Results.Created(
                $"/api/v1/users/{id}",
                new { id = result.GrantId, grantedBy = result.GrantedBy, grantedAt = result.GrantedAt }),

            UserAdminOutcome.CannotGrantPermissionYouLack => Escalation(result.MissingPermissions),

            UserAdminOutcome.NotFound => Problem(
                StatusCodes.Status404NotFound, ProblemTypes.NotFound, "Unknown user."),

            _ => Problem(
                StatusCodes.Status400BadRequest, ProblemTypes.MalformedRequest,
                result.Detail ?? "Invalid request."),
        };
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateUserRequest? request,
        HttpContext http,
        IOperatorPrincipalResolver principals,
        IUserDirectory users,
        CancellationToken cancellationToken)
    {
        (OperatorPrincipal? actor, IResult? refusal) =
            await AuthorizeAsync(http, principals, "user.manage", cancellationToken);

        if (refusal is not null)
        {
            return refusal;
        }

        UpdateUserResult result = await users.UpdateAsync(
            new UpdateUserCommand(
                id, request?.DisplayName, request?.Locale, request?.ValidUntil,
                request?.Status, actor!.UserId),
            cancellationToken);

        return result.Outcome switch
        {
            // activeDeviceQueueDepth is surfaced, never used to block: a
            // genuine security incident must not wait on a device's backlog
            // (M6). It exists so the administrator sees the cost at the moment
            // they decide, not as an unexplained reconciliation gap later.
            UserAdminOutcome.Succeeded => Results.Ok(new
            {
                id,
                status = result.Status,
                activeDeviceQueueDepth = result.ActiveDeviceQueueDepth,
            }),

            UserAdminOutcome.NotFound => Problem(
                StatusCodes.Status404NotFound, ProblemTypes.NotFound, "Unknown user."),

            _ => Problem(
                StatusCodes.Status400BadRequest, ProblemTypes.MalformedRequest,
                result.Detail ?? "Invalid request."),
        };
    }

    private static async Task<IResult> ListAsync(
        HttpContext http,
        IOperatorPrincipalResolver principals,
        IUserDirectory users,
        CancellationToken cancellationToken,
        Guid? warehouseId = null,
        string? userType = null,
        string? status = null,
        string? q = null,
        int? limit = null,
        Guid? afterId = null)
    {
        (OperatorPrincipal? _, IResult? refusal) =
            await AuthorizeAsync(http, principals, "user.manage", cancellationToken);

        if (refusal is not null)
        {
            return refusal;
        }

        UserPage page = await users.ListAsync(
            new UserQuery(warehouseId, userType, status, q, Clamp(limit), afterId),
            cancellationToken);

        return Results.Ok(new
        {
            items = page.Items,
            nextCursor = page.HasMore ? new { afterId = page.NextAfterId } : null,
            hasMore = page.HasMore,
        });
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        HttpContext http,
        IOperatorPrincipalResolver principals,
        IUserDirectory users,
        CancellationToken cancellationToken)
    {
        (OperatorPrincipal? _, IResult? refusal) =
            await AuthorizeAsync(http, principals, "user.manage", cancellationToken);

        if (refusal is not null)
        {
            return refusal;
        }

        UserDetail? detail = await users.GetAsync(id, cancellationToken);

        return detail is null
            ? Problem(StatusCodes.Status404NotFound, ProblemTypes.NotFound, "Unknown user.")
            : Results.Ok(detail);
    }

    private static async Task<IResult> ListRolesAsync(
        HttpContext http,
        IOperatorPrincipalResolver principals,
        IUserDirectory users,
        CancellationToken cancellationToken,
        bool includeInactive = false)
    {
        (OperatorPrincipal? _, IResult? refusal) =
            await AuthorizeAsync(http, principals, "role.manage", cancellationToken);

        return refusal
            ?? Results.Ok(new { items = await users.ListRolesAsync(includeInactive, cancellationToken) });
    }

    private static async Task<IResult> ListPermissionsAsync(
        HttpContext http,
        IOperatorPrincipalResolver principals,
        IUserDirectory users,
        CancellationToken cancellationToken,
        string? category = null)
    {
        (OperatorPrincipal? _, IResult? refusal) =
            await AuthorizeAsync(http, principals, "role.manage", cancellationToken);

        return refusal
            ?? Results.Ok(new { items = await users.ListPermissionsAsync(category, cancellationToken) });
    }

    /// <summary>
    /// The refusal H13 specifies, naming exactly what was missing.
    /// </summary>
    /// <remarks>
    /// The missing codes are returned deliberately. The caller already holds
    /// <c>role.manage</c> and can list the whole permission catalogue, so this
    /// discloses nothing they could not read — and without it the only way to
    /// discover why a grant failed is to try permissions one at a time.
    /// </remarks>
    private static IResult Escalation(IReadOnlyList<string>? missing) =>
        Results.Json(
            new
            {
                type = ProblemTypes.CannotGrantPermissionYouLack,
                title = "Cannot grant a permission you do not hold.",
                status = StatusCodes.Status403Forbidden,
                missingPermissions = missing ?? [],
            },
            statusCode: StatusCodes.Status403Forbidden,
            contentType: "application/problem+json");

    private static int Clamp(int? limit) => Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

    private static async Task<(OperatorPrincipal?, IResult?)> AuthorizeAsync(
        HttpContext http,
        IOperatorPrincipalResolver principals,
        string permission,
        CancellationToken cancellationToken)
    {
        OperatorPrincipal? principal = await principals.ResolveAsync(
            http.Request.Headers.Authorization.ToString(),
            http.Request.Headers["X-Device-Id"].ToString(),
            cancellationToken);

        if (principal is null)
        {
            return (null, Problem(
                StatusCodes.Status401Unauthorized, ProblemTypes.Unauthenticated,
                "No authenticated operator."));
        }

        return principal.Can(permission)
            ? (principal, null)
            : (null, Problem(
                StatusCodes.Status403Forbidden, ProblemTypes.InsufficientPermission,
                $"Requires '{permission}'."));
    }

    private static IResult Problem(int status, string type, string detail) =>
        Results.Problem(detail: detail, statusCode: status, type: type);
}

public sealed record CreateUserRequest(
    string UserType,
    string DisplayName,
    string? EmployeeCode,
    string? Email,
    string? Locale,
    DateOnly? ValidFrom,
    DateOnly? ValidUntil,
    IReadOnlyList<RoleScopeRequest>? RoleScopes,
    IReadOnlyList<CredentialRequest>? Credentials);

public sealed record CredentialRequest(string Type, string Secret);

public sealed record RoleScopeRequest(string RoleCode, Guid WarehouseId, IReadOnlyList<Guid>? ZoneIds);

public sealed record GrantRoleScopeRequest(
    string RoleCode, Guid WarehouseId, IReadOnlyList<Guid>? ZoneIds);

public sealed record UpdateUserRequest(
    string? DisplayName, string? Locale, DateOnly? ValidUntil, string? Status);
