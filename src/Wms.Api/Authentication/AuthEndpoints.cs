using Wms.Modules.Identity.Contracts;

namespace Wms.Api.Authentication;

/// <summary>
/// Sign-on, token rotation and session end (§5.1).
/// </summary>
public static class AuthEndpoints
{
    public static void MapAuthentication(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder auth = routes.MapGroup("/api/v1/auth").WithTags("Authentication");

        auth.MapPost("/staff/login", StaffLoginAsync)
            .WithName("StaffLogin")
            .WithSummary("Email and password sign-on for office staff.")
            .WithDescription(
                "An unknown email and a wrong password are deliberately indistinguishable.");

        auth.MapPost("/operator/login", OperatorLoginAsync)
            .WithName("OperatorLogin")
            .WithSummary("Badge scan or employee code plus PIN, bound to a registered device.");

        auth.MapPost("/refresh", RefreshAsync)
            .WithName("RefreshToken")
            .WithSummary("Rotate an access token.")
            .WithDescription("A reused refresh token revokes the entire family.");

        auth.MapPost("/logout", LogoutAsync).WithName("Logout");
        auth.MapGet("/me", MeAsync).WithName("Me");
    }

    private static async Task<IResult> StaffLoginAsync(
        StaffLoginRequest? request,
        IAuthenticationService authentication,
        CancellationToken cancellationToken)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.Email)
            || string.IsNullOrWhiteSpace(request.Password))
        {
            return Problem(StatusCodes.Status400BadRequest, ProblemTypes.MalformedRequest,
                "email and password are required.");
        }

        return Render(await authentication.StaffLoginAsync(
            request.Email, request.Password, cancellationToken));
    }

    private static async Task<IResult> OperatorLoginAsync(
        OperatorLoginBody? request,
        IAuthenticationService authentication,
        CancellationToken cancellationToken)
    {
        if (request is null || request.DeviceId == Guid.Empty)
        {
            return Problem(StatusCodes.Status400BadRequest, ProblemTypes.MalformedRequest,
                "deviceId is required.");
        }

        return Render(await authentication.OperatorLoginAsync(
            new OperatorLoginRequest(
                request.DeviceId, request.Badge, request.EmployeeCode, request.Pin),
            cancellationToken));
    }

    private static async Task<IResult> RefreshAsync(
        RefreshRequest? request,
        IAuthenticationService authentication,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return Problem(StatusCodes.Status400BadRequest, ProblemTypes.MalformedRequest,
                "refreshToken is required.");
        }

        LoginResult result = await authentication.RefreshAsync(
            request.RefreshToken, cancellationToken);

        // A rotation returns only tokens — re-sending permissions here would
        // invite a client to treat refresh as the moment authorisation
        // changes, when in fact it changes whenever an administrator says so.
        return result.Outcome == LoginOutcome.Succeeded
            ? Results.Ok(new
            {
                accessToken = result.Session!.AccessToken,
                refreshToken = result.Session.RefreshToken,
                expiresIn = result.Session.ExpiresInSeconds,
            })
            : Render(result);
    }

    private static async Task<IResult> LogoutAsync(
        LogoutRequest? request,
        HttpContext http,
        IOperatorPrincipalResolver principals,
        IAuthenticationService authentication,
        CancellationToken cancellationToken)
    {
        OperatorPrincipal? principal = await Resolve(http, principals, cancellationToken);
        if (principal is null)
        {
            return Problem(StatusCodes.Status401Unauthorized, ProblemTypes.Unauthenticated,
                "No authenticated operator.");
        }

        await authentication.LogoutAsync(
            principal.UserId, principal.SessionId, request?.Reason ?? "logout", cancellationToken);

        return Results.NoContent();
    }

    private static async Task<IResult> MeAsync(
        HttpContext http,
        IOperatorPrincipalResolver principals,
        CancellationToken cancellationToken)
    {
        OperatorPrincipal? principal = await Resolve(http, principals, cancellationToken);

        // Clients poll this on reconnect: a changed securityStamp means cached
        // authorisation must be discarded (§5.1).
        return principal is null
            ? Problem(StatusCodes.Status401Unauthorized, ProblemTypes.Unauthenticated,
                "No authenticated operator.")
            : Results.Ok(new
            {
                user = new { id = principal.UserId },
                permissions = principal.Permissions.Order(),
                scopes = principal.Scopes.Select(s => new
                {
                    warehouseId = s.WarehouseId,
                    zoneIds = s.ZoneIds,
                }),
                session = new { id = principal.SessionId, deviceId = principal.DeviceId },
                securityStamp = principal.SecurityStamp,
            });
    }

    private static Task<OperatorPrincipal?> Resolve(
        HttpContext http, IOperatorPrincipalResolver principals, CancellationToken cancellationToken) =>
        principals.ResolveAsync(
            http.Request.Headers.Authorization.ToString(),
            http.Request.Headers["X-Device-Id"].ToString(),
            cancellationToken);

    private static IResult Render(LoginResult result) => result.Outcome switch
    {
        LoginOutcome.Succeeded => Results.Ok(new
        {
            accessToken = result.Session!.AccessToken,
            refreshToken = result.Session.RefreshToken,
            expiresIn = result.Session.ExpiresInSeconds,
            sessionId = result.Session.SessionId,
            user = new
            {
                id = result.Session.UserId,
                displayName = result.Session.DisplayName,
                userType = result.Session.UserType,
                locale = result.Session.Locale,
            },
            permissions = result.Session.Permissions,
            scopes = result.Session.Scopes.Select(s => new
            {
                warehouseId = s.WarehouseId,
                zoneIds = s.ZoneIds,
            }),
            device = result.Session.DeviceId is null
                ? null
                : new { id = result.Session.DeviceId, label = result.Session.DeviceLabel },
        }),

        LoginOutcome.AccountLocked => Results.Json(
            new
            {
                type = ProblemTypes.AccountLocked,
                title = "Too many failed attempts.",
                status = StatusCodes.Status423Locked,
                retryAfter = result.RetryAfterSeconds,
            },
            statusCode: StatusCodes.Status423Locked,
            contentType: "application/problem+json"),

        LoginOutcome.AccountExpired => Problem(
            StatusCodes.Status403Forbidden, ProblemTypes.AccountExpired,
            result.Detail ?? "Account is outside its validity window."),

        LoginOutcome.DeviceClaimed => Problem(
            StatusCodes.Status409Conflict, ProblemTypes.DeviceClaimed, result.Detail!),

        LoginOutcome.UnknownDevice => Problem(
            StatusCodes.Status404NotFound, ProblemTypes.NotFound, result.Detail!),

        LoginOutcome.TokenReused => Problem(
            StatusCodes.Status401Unauthorized, ProblemTypes.TokenReused, result.Detail!),

        // Unknown identity and wrong secret land here together, by design.
        _ => Problem(
            StatusCodes.Status401Unauthorized, ProblemTypes.Unauthenticated, "Invalid credentials"),
    };

    private static IResult Problem(int status, string type, string detail) =>
        Results.Problem(detail: detail, statusCode: status, type: type);
}

public sealed record StaffLoginRequest(string Email, string Password);

public sealed record OperatorLoginBody(
    Guid DeviceId, string? Badge, string? EmployeeCode, string? Pin);

public sealed record RefreshRequest(string RefreshToken);

public sealed record LogoutRequest(string? Reason);
