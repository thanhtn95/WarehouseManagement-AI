namespace Wms.Modules.Identity.Contracts;

/// <summary>
/// Argon2id hashing for every stored secret (§8.1).
/// </summary>
public interface IPasswordHasher
{
    string Hash(string secret);

    bool Verify(string secret, string encoded);
}

/// <summary>
/// Sign-on, token rotation and session end (§5.1, §6.1).
/// </summary>
public interface IAuthenticationService
{
    Task<LoginResult> StaffLoginAsync(
        string email, string password, CancellationToken cancellationToken);

    Task<LoginResult> OperatorLoginAsync(
        OperatorLoginRequest request, CancellationToken cancellationToken);

    Task<LoginResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken);

    Task LogoutAsync(Guid userId, Guid? sessionId, string reason, CancellationToken cancellationToken);
}

/// <param name="Badge">
/// A badge scan on its own is sufficient. A PIN is not: §8.1 states plainly
/// that four digits is ten thousand combinations, so a PIN is either a second
/// factor or a way to resume a session on a device the operator has already
/// claimed — never a primary credential by itself.
/// </param>
public sealed record OperatorLoginRequest(
    Guid DeviceId, string? Badge, string? EmployeeCode, string? Pin);

public sealed record LoginResult(
    LoginOutcome Outcome,
    AuthenticatedSession? Session,
    int? RetryAfterSeconds,
    string? Detail);

public sealed record AuthenticatedSession(
    string AccessToken,
    string RefreshToken,
    int ExpiresInSeconds,
    Guid? SessionId,
    Guid UserId,
    string DisplayName,
    string UserType,
    string Locale,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<OperatorScope> Scopes,
    Guid? DeviceId,
    string? DeviceLabel);

public enum LoginOutcome
{
    Succeeded,

    /// <summary>
    /// Unknown identity or wrong secret — deliberately indistinguishable.
    /// Telling a caller which of the two it was turns the login form into an
    /// oracle for enumerating valid badges and email addresses.
    /// </summary>
    InvalidCredentials,

    /// <summary>Too many failures. Carries a <c>retryAfter</c>.</summary>
    AccountLocked,

    /// <summary>
    /// Outside the validity window — the agency-labour lapse §5.1 names
    /// separately from a wrong password, because the remedy is different: the
    /// operator is who they say they are and needs their access extended.
    /// </summary>
    AccountExpired,

    /// <summary>Another operator holds this device (§5.1).</summary>
    DeviceClaimed,

    /// <summary>Unknown, retired or lost device.</summary>
    UnknownDevice,

    /// <summary>
    /// A refresh token was presented twice. §5.1 requires revoking the whole
    /// family, not just the token — a replayed token means either the client
    /// is broken or it was stolen, and neither is safe to keep serving.
    /// </summary>
    TokenReused,
}

/// <summary>
/// Issues and validates access tokens.
/// </summary>
public interface ITokenIssuer
{
    string IssueAccessToken(
        Guid userId, Guid securityStamp, Guid? sessionId, Guid? deviceId, out int expiresInSeconds);

    Task<TokenClaims?> ValidateAsync(string token);
}

/// <param name="SecurityStamp">
/// Compared against the user's current stamp on every request. A stale value
/// means a role, credential or status change happened since the token was
/// issued, and the token is refused (§8.1) — this is what makes termination
/// take effect in seconds rather than at token expiry.
/// </param>
public sealed record TokenClaims(
    Guid UserId, Guid SecurityStamp, Guid? SessionId, Guid? DeviceId);
