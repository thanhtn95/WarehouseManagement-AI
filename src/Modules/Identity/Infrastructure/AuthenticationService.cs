using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using Wms.Modules.Identity.Contracts;

namespace Wms.Modules.Identity.Infrastructure;

/// <summary>
/// Sign-on, token rotation and session end (§5.1, §6.1, §8.1).
/// </summary>
public sealed class AuthenticationService(
    NpgsqlDataSource dataSource,
    IPasswordHasher hasher,
    ITokenIssuer tokens) : IAuthenticationService
{
    /// <summary>
    /// Exponential lockout per credential (§8.1).
    /// </summary>
    /// <remarks>
    /// Five attempts before the first lock, then the window doubles. A PIN has
    /// ten thousand combinations, so without this an attacker walks the whole
    /// space in minutes; with it, they get a handful of guesses an hour.
    /// </remarks>
    private const int AttemptsBeforeLock = 5;

    private const int BaseLockSeconds = 60;

    private const int RefreshTokenDays = 30;

    private const string StaffCredentialSql = """
        SELECT u.id, c.id, c.secret_hash, c.failed_attempts, c.locked_until,
               u.status, u.valid_from, u.valid_until
          FROM app_user u
          JOIN credential c ON c.user_id = u.id AND c.credential_type = 'password'
         WHERE lower(u.email) = lower(@email) AND u.user_type = 'staff'
           FOR UPDATE OF c;
        """;

    private const string OperatorCredentialSql = """
        SELECT u.id, c.id, c.secret_hash, c.failed_attempts, c.locked_until,
               u.status, u.valid_from, u.valid_until
          FROM app_user u
          JOIN credential c ON c.user_id = u.id AND c.credential_type = @credentialType
         WHERE u.employee_code = @employeeCode AND u.user_type = 'operator'
           FOR UPDATE OF c;
        """;

    private const string RecordAttemptSql = """
        UPDATE credential
           SET failed_attempts = CASE WHEN @success THEN 0 ELSE failed_attempts + 1 END,
               locked_until    = CASE
                   WHEN @success THEN NULL
                   WHEN failed_attempts + 1 >= @threshold
                   THEN now() + make_interval(
                            secs => @baseSeconds
                                    * power(2, least(failed_attempts + 1 - @threshold, 6)))
                   ELSE locked_until END,
               last_used_at    = CASE WHEN @success THEN now() ELSE last_used_at END
         WHERE id = @credentialId
        RETURNING locked_until;
        """;

    /// <summary>
    /// A device may carry one live session (§5.1).
    /// </summary>
    /// <remarks>
    /// Handhelds are pooled and shared across a shift, so the question is not
    /// "who owns this device" but "who is holding it right now". A second
    /// operator signing on while the first is still claimed gets a <c>409</c>
    /// naming them, rather than silently displacing work someone else has
    /// queued.
    /// </remarks>
    private const string DeviceSql = """
        SELECT d.id, d.label, d.status,
               s.user_id, u.display_name, s.started_at
          FROM device d
          LEFT JOIN device_session s ON s.device_id = d.id AND s.ended_at IS NULL
          LEFT JOIN app_user u       ON u.id = s.user_id
         WHERE d.id = @deviceId
           FOR UPDATE OF d;
        """;

    private const string OpenSessionSql = """
        INSERT INTO device_session (id, device_id, user_id, started_at)
        VALUES (@id, @deviceId, @userId, now());
        """;

    private const string EndSessionSql = """
        UPDATE device_session SET ended_at = now(), end_reason = @reason
         WHERE user_id = @userId AND ended_at IS NULL
           AND (@sessionId::uuid IS NULL OR id = @sessionId);
        """;

    private const string IssueRefreshSql = """
        INSERT INTO refresh_token (id, user_id, device_id, token_hash, expires_at,
                                   rotated_from, created_at)
        VALUES (@id, @userId, @deviceId, @tokenHash, @expiresAt, @rotatedFrom, now());
        """;

    private const string FindRefreshSql = """
        SELECT id, user_id, device_id, expires_at, revoked_at
          FROM refresh_token
         WHERE token_hash = @tokenHash
           FOR UPDATE;
        """;

    /// <summary>
    /// Reuse detection revokes the whole family (§5.1).
    /// </summary>
    /// <remarks>
    /// A rotated token presented a second time means the client is broken or
    /// the token was captured, and neither is safe to keep serving. Walking
    /// <c>rotated_from</c> in both directions revokes every descendant and
    /// ancestor, so the thief and the legitimate holder are both stopped and
    /// the user re-authenticates.
    /// </remarks>
    private const string RevokeFamilySql = """
        WITH RECURSIVE family AS (
            SELECT id, rotated_from FROM refresh_token WHERE id = @tokenId
            UNION
            SELECT rt.id, rt.rotated_from
              FROM refresh_token rt JOIN family f
                ON rt.rotated_from = f.id OR rt.id = f.rotated_from
        )
        UPDATE refresh_token SET revoked_at = now()
         WHERE id IN (SELECT id FROM family) AND revoked_at IS NULL;
        """;

    private const string RevokeTokenSql = """
        UPDATE refresh_token SET revoked_at = now() WHERE id = @tokenId;
        """;

    private const string PrincipalSql = """
        SELECT u.display_name, u.user_type, u.locale, u.security_stamp,
               urs.warehouse_id, urs.zone_ids, rp.permission_code
          FROM app_user u
          LEFT JOIN user_role_scope urs ON urs.user_id = u.id
          LEFT JOIN role r              ON r.id = urs.role_id AND r.is_active
          LEFT JOIN role_permission rp  ON rp.role_id = r.id
         WHERE u.id = @userId;
        """;

    private const string AuthEventSql = """
        INSERT INTO auth_event (id, occurred_at, event_type, actor_user_id,
                                target_user_id, device_id, outcome, detail)
        VALUES (gen_random_uuid(), now(), @eventType, @userId, @userId,
                @deviceId, @outcome, @detail::jsonb);
        """;

    public async Task<LoginResult> StaffLoginAsync(
        string email, string password, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        await using NpgsqlCommand lookup = new(StaffCredentialSql, connection, transaction);
        lookup.Parameters.AddWithValue("email", email ?? string.Empty);

        Candidate? candidate = await ReadCandidateAsync(lookup, cancellationToken);

        LoginResult? refusal = await RefuseAsync(
            connection, transaction, candidate, password, null, cancellationToken);

        if (refusal is not null)
        {
            return refusal;
        }

        AuthenticatedSession session = await IssueAsync(
            connection, transaction, candidate!.UserId, null, null, null, cancellationToken);

        await WriteAuthEventAsync(
            connection, transaction, "login", candidate.UserId, null, "success",
            new { method = "password" }, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new LoginResult(LoginOutcome.Succeeded, session, null, null);
    }

    public async Task<LoginResult> OperatorLoginAsync(
        OperatorLoginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        bool byBadge = !string.IsNullOrWhiteSpace(request.Badge);
        string? secret = byBadge ? request.Badge : request.Pin;
        string? employeeCode = byBadge ? request.Badge : request.EmployeeCode;

        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(employeeCode))
        {
            return new LoginResult(LoginOutcome.InvalidCredentials, null, null, null);
        }

        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        DeviceState? device = await ReadDeviceAsync(
            connection, transaction, request.DeviceId, cancellationToken);

        if (device is null || device.Status != "active")
        {
            return new LoginResult(
                LoginOutcome.UnknownDevice, null, null, "Unknown or inactive device.");
        }

        await using NpgsqlCommand lookup = new(OperatorCredentialSql, connection, transaction);
        lookup.Parameters.AddWithValue("employeeCode", employeeCode);
        lookup.Parameters.AddWithValue("credentialType", byBadge ? "badge" : "pin");

        Candidate? candidate = await ReadCandidateAsync(lookup, cancellationToken);

        LoginResult? refusal = await RefuseAsync(
            connection, transaction, candidate, secret, request.DeviceId, cancellationToken);

        if (refusal is not null)
        {
            return refusal;
        }

        // Checked after the credential, so a wrong badge on a claimed device
        // still answers "invalid credentials" rather than disclosing who holds
        // the handheld.
        if (device.HolderUserId is Guid holder && holder != candidate!.UserId)
        {
            return new LoginResult(
                LoginOutcome.DeviceClaimed, null, null,
                $"{device.Label} is claimed by {device.HolderDisplayName}.");
        }

        Guid sessionId = Guid.CreateVersion7();
        if (device.HolderUserId is null)
        {
            await using NpgsqlCommand open = new(OpenSessionSql, connection, transaction);
            open.Parameters.AddWithValue("id", sessionId);
            open.Parameters.AddWithValue("deviceId", request.DeviceId);
            open.Parameters.AddWithValue("userId", candidate!.UserId);
            await open.ExecuteNonQueryAsync(cancellationToken);
        }

        AuthenticatedSession session = await IssueAsync(
            connection, transaction, candidate!.UserId, request.DeviceId, device.Label,
            device.HolderUserId is null ? sessionId : device.SessionId, cancellationToken);

        await WriteAuthEventAsync(
            connection, transaction, "login", candidate.UserId, request.DeviceId, "success",
            new { method = byBadge ? "badge" : "pin" }, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new LoginResult(LoginOutcome.Succeeded, session, null, null);
    }

    public async Task<LoginResult> RefreshAsync(
        string refreshToken, CancellationToken cancellationToken)
    {
        string hash = HashToken(refreshToken);

        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        Guid tokenId;
        Guid userId;
        Guid? deviceId;
        DateTimeOffset expiresAt;
        bool revoked;

        await using (NpgsqlCommand find = new(FindRefreshSql, connection, transaction))
        {
            find.Parameters.AddWithValue("tokenHash", hash);

            await using NpgsqlDataReader reader = await find.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return new LoginResult(LoginOutcome.InvalidCredentials, null, null, null);
            }

            tokenId = reader.GetGuid(0);
            userId = reader.GetGuid(1);
            deviceId = reader.IsDBNull(2) ? null : reader.GetGuid(2);
            expiresAt = reader.GetFieldValue<DateTimeOffset>(3);
            revoked = !reader.IsDBNull(4);
        }

        if (revoked)
        {
            // Already rotated or already revoked: this is a replay. Revoke the
            // whole family — the legitimate holder is logged out too, which is
            // the intended cost of not knowing which party is the thief.
            await using (NpgsqlCommand revoke = new(RevokeFamilySql, connection, transaction))
            {
                revoke.Parameters.AddWithValue("tokenId", tokenId);
                await revoke.ExecuteNonQueryAsync(cancellationToken);
            }

            await WriteAuthEventAsync(
                connection, transaction, "refresh_token_reused", userId, deviceId, "failure",
                new { tokenId }, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new LoginResult(
                LoginOutcome.TokenReused, null, null, "Token family revoked.");
        }

        if (expiresAt <= DateTimeOffset.UtcNow)
        {
            return new LoginResult(LoginOutcome.InvalidCredentials, null, null, null);
        }

        await using (NpgsqlCommand revoke = new(RevokeTokenSql, connection, transaction))
        {
            revoke.Parameters.AddWithValue("tokenId", tokenId);
            await revoke.ExecuteNonQueryAsync(cancellationToken);
        }

        AuthenticatedSession session = await IssueAsync(
            connection, transaction, userId, deviceId, null, null, cancellationToken, tokenId);

        await transaction.CommitAsync(cancellationToken);
        return new LoginResult(LoginOutcome.Succeeded, session, null, null);
    }

    public async Task LogoutAsync(
        Guid userId, Guid? sessionId, string reason, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        await using (NpgsqlCommand end = new(EndSessionSql, connection, transaction))
        {
            end.Parameters.AddWithValue("userId", userId);
            end.Parameters.AddWithValue("sessionId", (object?)sessionId ?? DBNull.Value);
            end.Parameters.AddWithValue(
                "reason", reason is "logout" or "shift_end" or "forced" ? reason : "logout");
            await end.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (NpgsqlCommand revoke = new(
            "UPDATE refresh_token SET revoked_at = now() WHERE user_id = @userId AND revoked_at IS NULL;",
            connection, transaction))
        {
            revoke.Parameters.AddWithValue("userId", userId);
            await revoke.ExecuteNonQueryAsync(cancellationToken);
        }

        await WriteAuthEventAsync(
            connection, transaction, "logout", userId, null, "success",
            new { reason }, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// The single refusal path, so every failure mode answers consistently.
    /// </summary>
    /// <remarks>
    /// Unknown identity and wrong secret both return
    /// <see cref="LoginOutcome.InvalidCredentials"/> — §5.1 requires them to
    /// be indistinguishable, or the login form becomes an oracle for
    /// enumerating valid badges. The lockout counter is still incremented for
    /// a known identity, because that is what makes a PIN survivable.
    /// </remarks>
    private async Task<LoginResult?> RefuseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Candidate? candidate,
        string secret,
        Guid? deviceId,
        CancellationToken cancellationToken)
    {
        if (candidate is null)
        {
            return new LoginResult(LoginOutcome.InvalidCredentials, null, null, null);
        }

        if (candidate.LockedUntil is DateTimeOffset locked && locked > DateTimeOffset.UtcNow)
        {
            return new LoginResult(
                LoginOutcome.AccountLocked, null,
                (int)(locked - DateTimeOffset.UtcNow).TotalSeconds, null);
        }

        if (!hasher.Verify(secret, candidate.SecretHash))
        {
            DateTimeOffset? lockedUntil = await RecordAttemptAsync(
                connection, transaction, candidate.CredentialId, success: false, cancellationToken);

            await WriteAuthEventAsync(
                connection, transaction, "login_failed", candidate.UserId, deviceId, "failure",
                new { reason = "bad_secret" }, cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return lockedUntil is DateTimeOffset until && until > DateTimeOffset.UtcNow
                ? new LoginResult(
                    LoginOutcome.AccountLocked, null,
                    (int)(until - DateTimeOffset.UtcNow).TotalSeconds, null)
                : new LoginResult(LoginOutcome.InvalidCredentials, null, null, null);
        }

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (candidate.Status != "active"
            || candidate.ValidFrom > today
            || (candidate.ValidUntil is DateOnly until2 && until2 < today))
        {
            // Distinct from a wrong secret on purpose: the operator is who
            // they say they are, and the remedy is an administrator extending
            // their validity rather than a password reset.
            await WriteAuthEventAsync(
                connection, transaction, "login_failed", candidate.UserId, deviceId, "failure",
                new { reason = "not_within_validity" }, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new LoginResult(
                LoginOutcome.AccountExpired, null, null,
                candidate.ValidUntil is DateOnly v ? $"valid_until {v:yyyy-MM-dd}" : null);
        }

        await RecordAttemptAsync(
            connection, transaction, candidate.CredentialId, success: true, cancellationToken);

        return null;
    }

    private async Task<AuthenticatedSession> IssueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        Guid? deviceId,
        string? deviceLabel,
        Guid? sessionId,
        CancellationToken cancellationToken,
        Guid? rotatedFrom = null)
    {
        PrincipalSnapshot snapshot = await ReadPrincipalAsync(
            connection, transaction, userId, cancellationToken);

        string accessToken = tokens.IssueAccessToken(
            userId, snapshot.SecurityStamp, sessionId, deviceId, out int expiresIn);

        // Opaque and random, stored only as a hash: a database disclosure must
        // not hand an attacker usable refresh tokens.
        string refreshToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

        await using NpgsqlCommand issue = new(IssueRefreshSql, connection, transaction);
        issue.Parameters.AddWithValue("id", Guid.CreateVersion7());
        issue.Parameters.AddWithValue("userId", userId);
        issue.Parameters.AddWithValue("deviceId", (object?)deviceId ?? DBNull.Value);
        issue.Parameters.AddWithValue("tokenHash", HashToken(refreshToken));
        issue.Parameters.AddWithValue("expiresAt", DateTimeOffset.UtcNow.AddDays(RefreshTokenDays));
        issue.Parameters.AddWithValue("rotatedFrom", (object?)rotatedFrom ?? DBNull.Value);
        await issue.ExecuteNonQueryAsync(cancellationToken);

        return new AuthenticatedSession(
            accessToken, refreshToken, expiresIn, sessionId, userId,
            snapshot.DisplayName, snapshot.UserType, snapshot.Locale,
            [.. snapshot.Permissions], snapshot.Scopes, deviceId, deviceLabel);
    }

    /// <summary>
    /// A refresh token is hashed, not encrypted: it only ever needs comparing.
    /// </summary>
    /// <remarks>
    /// SHA-256 rather than Argon2id, deliberately. It is 256 bits of
    /// cryptographic randomness, not a human-chosen secret, so there is
    /// nothing to brute-force and a deliberately slow hash would only add
    /// latency to every refresh.
    /// </remarks>
    private static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    private static async Task<Candidate?> ReadCandidateAsync(
        NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new Candidate(
                UserId: reader.GetGuid(0),
                CredentialId: reader.GetGuid(1),
                SecretHash: reader.GetString(2),
                FailedAttempts: reader.GetInt32(3),
                LockedUntil: reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                Status: reader.GetString(5),
                ValidFrom: reader.GetFieldValue<DateOnly>(6),
                ValidUntil: reader.IsDBNull(7) ? null : reader.GetFieldValue<DateOnly>(7))
            : null;
    }

    private static async Task<DateTimeOffset?> RecordAttemptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid credentialId,
        bool success,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(RecordAttemptSql, connection, transaction);
        command.Parameters.AddWithValue("credentialId", credentialId);
        command.Parameters.AddWithValue("success", success);
        command.Parameters.AddWithValue("threshold", AttemptsBeforeLock);
        command.Parameters.AddWithValue("baseSeconds", (double)BaseLockSeconds);

        // NOT `as DateTimeOffset?`. Npgsql boxes timestamptz as DateTime, so a
        // type test against DateTimeOffset silently yields null — the lock is
        // written to the database and then reported as "wrong password",
        // which is a lockout that does not lock.
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return result switch
        {
            DateTimeOffset offset => offset,
            DateTime instant => new DateTimeOffset(
                DateTime.SpecifyKind(instant, DateTimeKind.Utc)),
            _ => null,
        };
    }

    private static async Task<DeviceState?> ReadDeviceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(DeviceSql, connection, transaction);
        command.Parameters.AddWithValue("deviceId", deviceId);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new DeviceState(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                null)
            : null;
    }

    private static async Task<PrincipalSnapshot> ReadPrincipalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(PrincipalSql, connection, transaction);
        command.Parameters.AddWithValue("userId", userId);

        string displayName = string.Empty;
        string userType = "operator";
        string locale = "en";
        Guid stamp = Guid.Empty;
        HashSet<string> permissions = new(StringComparer.Ordinal);
        Dictionary<Guid, (Guid[] Zones, HashSet<string> Permissions)> byWarehouse = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            displayName = reader.GetString(0);
            userType = reader.GetString(1);
            locale = reader.GetString(2);
            stamp = reader.GetGuid(3);

            if (await reader.IsDBNullAsync(6, cancellationToken))
            {
                continue;
            }

            string permission = reader.GetString(6);
            permissions.Add(permission);

            Guid warehouseId = reader.GetGuid(4);
            if (!byWarehouse.TryGetValue(warehouseId, out var scope))
            {
                scope = (reader.GetFieldValue<Guid[]>(5), new HashSet<string>(StringComparer.Ordinal));
                byWarehouse[warehouseId] = scope;
            }

            scope.Permissions.Add(permission);
        }

        return new PrincipalSnapshot(
            displayName, userType, locale, stamp, permissions,
            [.. byWarehouse.Select(kv =>
                new OperatorScope(kv.Key, kv.Value.Zones, kv.Value.Permissions))]);
    }

    private static async Task WriteAuthEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string eventType,
        Guid userId,
        Guid? deviceId,
        string outcome,
        object detail,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(AuthEventSql, connection, transaction);
        command.Parameters.AddWithValue("eventType", eventType);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("deviceId", (object?)deviceId ?? DBNull.Value);
        command.Parameters.AddWithValue("outcome", outcome);
        command.Parameters.AddWithValue("detail", JsonSerializer.Serialize(detail));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record Candidate(
        Guid UserId, Guid CredentialId, string SecretHash, int FailedAttempts,
        DateTimeOffset? LockedUntil, string Status, DateOnly ValidFrom, DateOnly? ValidUntil);

    private sealed record DeviceState(
        Guid Id, string Label, string Status,
        Guid? HolderUserId, string? HolderDisplayName, Guid? SessionId);

    private sealed record PrincipalSnapshot(
        string DisplayName, string UserType, string Locale, Guid SecurityStamp,
        HashSet<string> Permissions, IReadOnlyList<OperatorScope> Scopes);
}
