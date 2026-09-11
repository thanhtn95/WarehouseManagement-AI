using Npgsql;
using Wms.IntegrationTests.Infrastructure;
using Wms.Modules.Identity.Contracts;
using Wms.Modules.Identity.Infrastructure;
using Xunit;

namespace Wms.IntegrationTests.Authentication;

/// <summary>
/// Sign-on, token rotation and revocation (§5.1, §6.1, §8.1).
/// </summary>
/// <remarks>
/// These close the development-header shortcut: the API now resolves a
/// principal from a bearer token validated against current database state, and
/// the tests that matter most here are the ones about <em>stopping</em> —
/// lockout, expiry, stale stamp, reuse detection.
/// </remarks>
public sealed class AuthenticationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private readonly Argon2PasswordHasher _hasher = new();
    private readonly ITokenIssuer _tokens =
        new TokenIssuer("a-test-signing-key-that-is-long-enough-32", "wms-test");

    private IAuthenticationService Authentication =>
        new AuthenticationService(fixture.DataSource, _hasher, _tokens);

    private IOperatorPrincipalResolver Resolver =>
        new TokenPrincipalResolver(fixture.DataSource, _tokens);

    [Fact]
    public async Task StaffCanSignInWithAPassword_AndTheTokenResolvesToTheirPermissions()
    {
        Seeded data = await SeedAsync();
        Guid user = await SeedUserAsync(data, "staff", "password", "correct horse", Receiver);

        LoginResult result = await Authentication.StaffLoginAsync(
            data.Email, "correct horse", CancellationToken.None);

        Assert.Equal(LoginOutcome.Succeeded, result.Outcome);
        Assert.Equal(900, result.Session!.ExpiresInSeconds);
        Assert.Contains("receipt.confirm", result.Session.Permissions);

        // Scope travels with the session — invariant 8 is permissions *plus*
        // where they apply.
        OperatorScope scope = Assert.Single(result.Session.Scopes);
        Assert.Equal(data.WarehouseId, scope.WarehouseId);

        OperatorPrincipal? principal = await Resolver.ResolveAsync(
            $"Bearer {result.Session.AccessToken}", null, CancellationToken.None);

        Assert.NotNull(principal);
        Assert.Equal(user, principal!.UserId);
        Assert.True(principal.CanIn("receipt.confirm", data.WarehouseId));

        // Held in one warehouse, therefore not held in another.
        Assert.False(principal.CanIn("receipt.confirm", Guid.CreateVersion7()));
    }

    /// <summary>
    /// §5.1: an unknown identity and a wrong secret must be indistinguishable.
    /// </summary>
    [Fact]
    public async Task AnUnknownEmailAndAWrongPassword_AnswerIdentically()
    {
        Seeded data = await SeedAsync();
        await SeedUserAsync(data, "staff", "password", "correct horse", Receiver);

        LoginResult wrongPassword = await Authentication.StaffLoginAsync(
            data.Email, "wrong", CancellationToken.None);

        LoginResult unknownEmail = await Authentication.StaffLoginAsync(
            $"nobody-{Guid.CreateVersion7():N}@example.com", "wrong", CancellationToken.None);

        // Same outcome, same detail: the login form must not become an oracle
        // for enumerating valid addresses.
        Assert.Equal(LoginOutcome.InvalidCredentials, wrongPassword.Outcome);
        Assert.Equal(LoginOutcome.InvalidCredentials, unknownEmail.Outcome);
        Assert.Equal(unknownEmail.Detail, wrongPassword.Detail);
    }

    /// <summary>
    /// §8.1: a four-digit PIN is ten thousand combinations, so lockout is what
    /// makes it survivable at all.
    /// </summary>
    [Fact]
    public async Task RepeatedFailures_LockTheCredential()
    {
        Seeded data = await SeedAsync();
        await SeedUserAsync(data, "staff", "password", "correct horse", Receiver);

        LoginResult last = new(LoginOutcome.Succeeded, null, null, null);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            last = await Authentication.StaffLoginAsync(data.Email, "wrong", CancellationToken.None);
        }

        Assert.Equal(LoginOutcome.AccountLocked, last.Outcome);
        Assert.True(last.RetryAfterSeconds > 0);

        // Locked means locked, even for the right password.
        LoginResult correct = await Authentication.StaffLoginAsync(
            data.Email, "correct horse", CancellationToken.None);

        Assert.Equal(LoginOutcome.AccountLocked, correct.Outcome);
    }

    /// <summary>
    /// The agency-labour lapse §5.1 names separately from bad credentials.
    /// </summary>
    [Fact]
    public async Task AnExpiredAccount_IsRefusedDistinctlyFromABadPassword()
    {
        Seeded data = await SeedAsync();
        Guid user = await SeedUserAsync(data, "staff", "password", "correct horse", Receiver);

        await ExecuteAsync(
            "UPDATE app_user SET valid_until = current_date - 1 WHERE id = @p;", user);

        LoginResult result = await Authentication.StaffLoginAsync(
            data.Email, "correct horse", CancellationToken.None);

        // The operator is who they say they are; the remedy is an extension,
        // not a password reset, so the two must not collapse into one answer.
        Assert.Equal(LoginOutcome.AccountExpired, result.Outcome);
        Assert.Contains("valid_until", result.Detail!);
    }

    [Fact]
    public async Task AnOperatorCanSignOnByBadge_AndClaimsTheDevice()
    {
        Seeded data = await SeedAsync();
        Guid device = await SeedDeviceAsync(data);
        await SeedUserAsync(data, "operator", "badge", "EMP00412", Receiver, "EMP00412");

        LoginResult result = await Authentication.OperatorLoginAsync(
            new OperatorLoginRequest(device, "EMP00412", null, null), CancellationToken.None);

        Assert.Equal(LoginOutcome.Succeeded, result.Outcome);
        Assert.NotNull(result.Session!.SessionId);
        Assert.Equal(device, result.Session.DeviceId);

        Assert.Equal(1, await ScalarAsync<long>(
            "SELECT count(*) FROM device_session WHERE device_id = @p AND ended_at IS NULL;",
            device));
    }

    /// <summary>
    /// Pooled handhelds are shared, so the question is who is holding it now.
    /// </summary>
    [Fact]
    public async Task ASecondOperatorOnAClaimedDevice_IsRefusedRatherThanDisplacingTheFirst()
    {
        Seeded data = await SeedAsync();
        Guid device = await SeedDeviceAsync(data);
        await SeedUserAsync(data, "operator", "badge", "EMP-A", Receiver, "EMP-A");
        await SeedUserAsync(data, "operator", "badge", "EMP-B", Receiver, "EMP-B");

        await Authentication.OperatorLoginAsync(
            new OperatorLoginRequest(device, "EMP-A", null, null), CancellationToken.None);

        LoginResult second = await Authentication.OperatorLoginAsync(
            new OperatorLoginRequest(device, "EMP-B", null, null), CancellationToken.None);

        Assert.Equal(LoginOutcome.DeviceClaimed, second.Outcome);
    }

    /// <summary>
    /// §5.1: a reused refresh token revokes the entire family.
    /// </summary>
    /// <remarks>
    /// A replayed token means the client is broken or the token was captured,
    /// and nothing distinguishes the two from the server's side. Logging both
    /// parties out is the intended cost of that ambiguity.
    /// </remarks>
    [Fact]
    public async Task AReusedRefreshToken_RevokesTheWholeFamily()
    {
        Seeded data = await SeedAsync();
        await SeedUserAsync(data, "staff", "password", "correct horse", Receiver);

        LoginResult login = await Authentication.StaffLoginAsync(
            data.Email, "correct horse", CancellationToken.None);

        string original = login.Session!.RefreshToken;

        LoginResult rotated = await Authentication.RefreshAsync(original, CancellationToken.None);
        Assert.Equal(LoginOutcome.Succeeded, rotated.Outcome);
        Assert.NotEqual(original, rotated.Session!.RefreshToken);

        // Replay the one already rotated away.
        LoginResult replay = await Authentication.RefreshAsync(original, CancellationToken.None);
        Assert.Equal(LoginOutcome.TokenReused, replay.Outcome);

        // The descendant is dead too — that is what "family" means.
        LoginResult descendant = await Authentication.RefreshAsync(
            rotated.Session.RefreshToken, CancellationToken.None);

        Assert.Equal(LoginOutcome.TokenReused, descendant.Outcome);
    }

    /// <summary>
    /// H12, end to end: termination takes effect in seconds, not at token
    /// expiry.
    /// </summary>
    [Fact]
    public async Task SuspendingAUser_InvalidatesTheirLiveAccessToken()
    {
        Seeded data = await SeedAsync();
        Guid user = await SeedUserAsync(data, "staff", "password", "correct horse", Receiver);

        LoginResult login = await Authentication.StaffLoginAsync(
            data.Email, "correct horse", CancellationToken.None);

        string token = $"Bearer {login.Session!.AccessToken}";
        Assert.NotNull(await Resolver.ResolveAsync(token, null, CancellationToken.None));

        // The stamp bump an administrator's suspension performs.
        await ExecuteAsync(
            "UPDATE app_user SET status = 'suspended', security_stamp = gen_random_uuid() WHERE id = @p;",
            user);

        // The token is still cryptographically valid and unexpired. It is
        // refused anyway, which is the entire point of carrying the stamp.
        Assert.Null(await Resolver.ResolveAsync(token, null, CancellationToken.None));
    }

    [Fact]
    public async Task EndingASession_StopsTheTokenIssuedForIt()
    {
        Seeded data = await SeedAsync();
        Guid device = await SeedDeviceAsync(data);
        Guid user = await SeedUserAsync(data, "operator", "badge", "EMP-C", Receiver, "EMP-C");

        LoginResult login = await Authentication.OperatorLoginAsync(
            new OperatorLoginRequest(device, "EMP-C", null, null), CancellationToken.None);

        string token = $"Bearer {login.Session!.AccessToken}";
        Assert.NotNull(await Resolver.ResolveAsync(token, null, CancellationToken.None));

        await Authentication.LogoutAsync(
            user, login.Session.SessionId, "shift_end", CancellationToken.None);

        // On a pooled handheld the next operator is holding the same unit, so
        // a token that outlived its session would act as the previous user.
        Assert.Null(await Resolver.ResolveAsync(token, null, CancellationToken.None));
    }

    [Fact]
    public async Task AForgedOrTamperedToken_ResolvesToNobody()
    {
        Seeded data = await SeedAsync();
        await SeedUserAsync(data, "staff", "password", "correct horse", Receiver);

        LoginResult login = await Authentication.StaffLoginAsync(
            data.Email, "correct horse", CancellationToken.None);

        string tampered = login.Session!.AccessToken[..^4] + "AAAA";

        Assert.Null(await Resolver.ResolveAsync($"Bearer {tampered}", null, CancellationToken.None));
        Assert.Null(await Resolver.ResolveAsync("Bearer not-a-token", null, CancellationToken.None));
        Assert.Null(await Resolver.ResolveAsync(null, null, CancellationToken.None));

        // Signed with a different key — the signature check is what matters.
        ITokenIssuer forger = new TokenIssuer("a-different-key-also-long-enough-32-b", "wms-test");
        string forged = forger.IssueAccessToken(
            Guid.CreateVersion7(), Guid.CreateVersion7(), null, null, out _);

        Assert.Null(await Resolver.ResolveAsync($"Bearer {forged}", null, CancellationToken.None));
    }

    private const string Receiver = "RECEIVER";

    private async Task<Guid> SeedUserAsync(
        Seeded data, string userType, string credentialType, string secret,
        string roleCode, string? employeeCode = null)
    {
        Guid userId = Guid.CreateVersion7();

        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(
            """
            INSERT INTO app_user (id, user_type, display_name, email, employee_code,
                                  status, security_stamp, valid_from, created_at, updated_at)
            VALUES (@id, @userType, 'Test', @email, @employeeCode, 'active',
                    gen_random_uuid(), current_date, now(), now());

            INSERT INTO credential (id, user_id, credential_type, secret_hash, created_at)
            VALUES (gen_random_uuid(), @id, @credentialType, @secretHash, now());

            INSERT INTO user_role_scope (id, user_id, role_id, warehouse_id, granted_by, granted_at)
            SELECT gen_random_uuid(), @id, r.id, @warehouseId, @id, now()
              FROM role r WHERE r.code = @roleCode;
            """);
        command.Parameters.AddWithValue("id", userId);
        command.Parameters.AddWithValue("userType", userType);
        command.Parameters.AddWithValue(
            "email", userType == "staff" ? data.Email : (object)DBNull.Value);
        command.Parameters.AddWithValue("employeeCode", (object?)employeeCode ?? DBNull.Value);
        command.Parameters.AddWithValue("credentialType", credentialType);
        command.Parameters.AddWithValue("secretHash", _hasher.Hash(secret));
        command.Parameters.AddWithValue("warehouseId", data.WarehouseId);
        command.Parameters.AddWithValue("roleCode", roleCode);

        await command.ExecuteNonQueryAsync();
        return userId;
    }

    private async Task<Guid> SeedDeviceAsync(Seeded data)
    {
        Guid deviceId = Guid.CreateVersion7();

        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(
            """
            INSERT INTO device (id, label, warehouse_id, status, created_at, updated_at)
            VALUES (@id, 'HH-001', @warehouseId, 'active', now(), now());
            """);
        command.Parameters.AddWithValue("id", deviceId);
        command.Parameters.AddWithValue("warehouseId", data.WarehouseId);
        await command.ExecuteNonQueryAsync();

        return deviceId;
    }

    private async Task ExecuteAsync(string sql, object parameter)
    {
        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("p", parameter);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, object parameter)
    {
        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("p", parameter);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task<Seeded> SeedAsync()
    {
        Seeded data = new(Guid.CreateVersion7());

        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(
            """
            INSERT INTO warehouse (id, code, name, timezone, created_at, updated_at)
            VALUES (@warehouseId, right(@warehouseId::text, 12), 'T', 'Asia/Tokyo', now(), now());
            """);
        command.Parameters.AddWithValue("warehouseId", data.WarehouseId);
        await command.ExecuteNonQueryAsync();

        return data;
    }

    private sealed record Seeded(Guid WarehouseId)
    {
        public string Email { get; } = $"user-{Guid.CreateVersion7():N}@example.co.jp";
    }
}
