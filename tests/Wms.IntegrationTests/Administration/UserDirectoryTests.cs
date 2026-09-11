using Npgsql;
using Wms.IntegrationTests.Infrastructure;
using Wms.Modules.Identity.Contracts;
using Wms.Modules.Identity.Infrastructure;
using Xunit;

namespace Wms.IntegrationTests.Administration;

/// <summary>
/// User administration, and the privilege boundaries around it (§5.11,
/// findings H12, H13, M6).
/// </summary>
/// <remarks>
/// The seeded role matrix is used as-is rather than mocked, because the whole
/// question here is whether one real role can grant another real role. A
/// Warehouse Manager holds every permission except <c>warehouse.manage</c>,
/// which is precisely what makes System Administrator ungrantable by them —
/// and that is the escalation path under test.
/// </remarks>
public sealed class UserDirectoryTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string SystemAdministrator = "SYSTEM_ADMINISTRATOR";
    private const string WarehouseManager = "WAREHOUSE_MANAGER";
    private const string Supervisor = "SUPERVISOR";
    private const string Receiver = "RECEIVER";

    private IUserDirectory Directory => new UserDirectory(fixture.DataSource, new Argon2PasswordHasher());

    /// <summary>
    /// H13, extended to granting: an administrator may not hand out
    /// permissions they do not themselves hold.
    /// </summary>
    /// <remarks>
    /// The sharpest form of this is self-promotion — a Warehouse Manager
    /// granting themselves System Administrator to acquire
    /// <c>warehouse.manage</c>. The design closed this for role *composition*
    /// and left granting open; without the check, the escalation simply takes
    /// the shorter route.
    /// </remarks>
    [Fact]
    public async Task AWarehouseManagerCannotGrantSystemAdministrator_EvenToThemselves()
    {
        Seeded data = await SeedAsync();
        Guid manager = await SeedUserAsync(data, WarehouseManager);

        GrantResult result = await Directory.GrantRoleScopeAsync(
            new GrantRoleScopeCommand(manager, SystemAdministrator, data.WarehouseId, [], manager),
            CancellationToken.None);

        Assert.Equal(UserAdminOutcome.CannotGrantPermissionYouLack, result.Outcome);
        Assert.Contains("warehouse.manage", result.MissingPermissions!);

        // Nothing was granted.
        Assert.Equal(1, await ScalarAsync<long>(
            "SELECT count(*) FROM user_role_scope WHERE user_id = @p;", manager));

        // And the attempt is on the record. An escalation attempt is exactly
        // what an investigation needs to find.
        Assert.Equal(1, await ScalarAsync<long>(
            """
            SELECT count(*) FROM auth_event
             WHERE actor_user_id = @p AND event_type = 'role_grant_denied'
               AND outcome = 'failure';
            """,
            manager));
    }

    /// <summary>
    /// The check constrains delegation, not legitimate administration.
    /// </summary>
    [Fact]
    public async Task AWarehouseManagerCanGrantEveryRoleTheyEncompass()
    {
        Seeded data = await SeedAsync();
        Guid manager = await SeedUserAsync(data, WarehouseManager);
        Guid target = await SeedUserAsync(data, roleCode: null);

        foreach (string roleCode in new[] { Supervisor, Receiver })
        {
            GrantResult result = await Directory.GrantRoleScopeAsync(
                new GrantRoleScopeCommand(target, roleCode, data.WarehouseId, [], manager),
                CancellationToken.None);

            Assert.Equal(UserAdminOutcome.Succeeded, result.Outcome);
            Assert.NotNull(result.GrantId);
        }

        UserDetail? detail = await Directory.GetAsync(target, CancellationToken.None);
        Assert.Equal(2, detail!.RoleScopes.Count);
    }

    /// <summary>
    /// A Supervisor holds neither <c>user.manage</c> nor the Receiver's full
    /// permission set, so even if the endpoint let them through, the directory
    /// refuses the grant.
    /// </summary>
    [Fact]
    public async Task ASupervisorCannotGrantARoleCarryingPermissionsTheyLack()
    {
        Seeded data = await SeedAsync();
        Guid supervisor = await SeedUserAsync(data, Supervisor);
        Guid target = await SeedUserAsync(data, roleCode: null);

        GrantResult result = await Directory.GrantRoleScopeAsync(
            new GrantRoleScopeCommand(target, SystemAdministrator, data.WarehouseId, [], supervisor),
            CancellationToken.None);

        Assert.Equal(UserAdminOutcome.CannotGrantPermissionYouLack, result.Outcome);
        Assert.NotEmpty(result.MissingPermissions!);
    }

    /// <summary>
    /// The same check applies at creation, and rejects before anything is
    /// written.
    /// </summary>
    [Fact]
    public async Task CreatingAUserWithAnUngrantableRole_WritesNothingAtAll()
    {
        Seeded data = await SeedAsync();
        Guid manager = await SeedUserAsync(data, WarehouseManager);

        long usersBefore = await ScalarAsync<long>("SELECT count(*) FROM app_user;", 0);

        CreateUserResult result = await Directory.CreateAsync(
            new CreateUserCommand(
                "staff", "Would-be admin", null, null, "en", null, null, manager,
                [new RoleScopeGrant(SystemAdministrator, data.WarehouseId, [])]),
            CancellationToken.None);

        Assert.Equal(UserAdminOutcome.CannotGrantPermissionYouLack, result.Outcome);
        Assert.Null(result.UserId);

        // A rejected create leaves no half-built user behind.
        Assert.Equal(usersBefore, await ScalarAsync<long>("SELECT count(*) FROM app_user;", 0));
    }

    /// <summary>
    /// §5.11: operators get a bounded validity by default so departed agency
    /// staff expire on their own.
    /// </summary>
    [Fact]
    public async Task AnOperatorGetsA90DayValidity_WhileStaffStayOpenEnded()
    {
        Seeded data = await SeedAsync();
        Guid manager = await SeedUserAsync(data, WarehouseManager);

        CreateUserResult operatorResult = await Directory.CreateAsync(
            new CreateUserCommand(
                "operator", "Agency worker", null, null, "en", null, null, manager, []),
            CancellationToken.None);

        CreateUserResult staffResult = await Directory.CreateAsync(
            new CreateUserCommand("staff", "Permanent", null, null, "en", null, null, manager, []),
            CancellationToken.None);

        UserDetail? agency = await Directory.GetAsync(operatorResult.UserId!.Value, CancellationToken.None);
        UserDetail? permanent = await Directory.GetAsync(staffResult.UserId!.Value, CancellationToken.None);

        Assert.NotNull(agency!.ValidUntil);
        Assert.Equal(agency.ValidFrom.AddDays(90), agency.ValidUntil);

        // Staff are not auto-expired: an employee whose account silently dies
        // after 90 days is an outage, not a control.
        Assert.Null(permanent!.ValidUntil);
    }

    /// <summary>
    /// H12: revocation has to actually revoke. Suspension bumps
    /// <c>security_stamp</c>, which is what makes already-issued tokens fail
    /// on their next request rather than whenever they happen to expire.
    /// </summary>
    [Fact]
    public async Task SuspendingAUser_BumpsTheSecurityStamp()
    {
        Seeded data = await SeedAsync();
        Guid manager = await SeedUserAsync(data, WarehouseManager);
        Guid target = await SeedUserAsync(data, Receiver);

        UserDetail? before = await Directory.GetAsync(target, CancellationToken.None);

        UpdateUserResult result = await Directory.UpdateAsync(
            new UpdateUserCommand(target, null, null, null, "suspended", manager),
            CancellationToken.None);

        Assert.Equal(UserAdminOutcome.Succeeded, result.Outcome);
        Assert.Equal("suspended", result.Status);

        UserDetail? after = await Directory.GetAsync(target, CancellationToken.None);
        Assert.NotEqual(before!.SecurityStamp, after!.SecurityStamp);

        Assert.Equal(1, await ScalarAsync<long>(
            "SELECT count(*) FROM auth_event WHERE target_user_id = @p AND event_type = 'user_suspended';",
            target));
    }

    /// <summary>
    /// An edit that changes no status must not churn the stamp.
    /// </summary>
    /// <remarks>
    /// Bumping it on every trivial edit would log every operator out whenever
    /// an administrator corrected a spelling — and an organisation that
    /// experiences revocation as random logouts stops trusting it.
    /// </remarks>
    [Fact]
    public async Task RenamingAUser_DoesNotBumpTheSecurityStamp()
    {
        Seeded data = await SeedAsync();
        Guid manager = await SeedUserAsync(data, WarehouseManager);
        Guid target = await SeedUserAsync(data, Receiver);

        UserDetail? before = await Directory.GetAsync(target, CancellationToken.None);

        await Directory.UpdateAsync(
            new UpdateUserCommand(target, "Corrected Name", null, null, null, manager),
            CancellationToken.None);

        UserDetail? after = await Directory.GetAsync(target, CancellationToken.None);
        Assert.Equal(before!.SecurityStamp, after!.SecurityStamp);
        Assert.Equal("Corrected Name", after.DisplayName);
    }

    /// <summary>
    /// M6: suspension reports the unsynced work it strands, without blocking.
    /// </summary>
    [Fact]
    public async Task SuspendingAUserWithAnActiveDevice_ReportsTheStrandedQueueDepth()
    {
        Seeded data = await SeedAsync();
        Guid manager = await SeedUserAsync(data, WarehouseManager);
        Guid target = await SeedUserAsync(data, Receiver);
        await SeedActiveDeviceSessionAsync(data, target, queueDepth: 7);

        UpdateUserResult result = await Directory.UpdateAsync(
            new UpdateUserCommand(target, null, null, null, "suspended", manager),
            CancellationToken.None);

        // Surfaced, not blocking — a genuine security incident must not wait
        // on a device's backlog.
        Assert.Equal(UserAdminOutcome.Succeeded, result.Outcome);
        Assert.Equal(7, result.ActiveDeviceQueueDepth);
    }

    /// <summary>
    /// Permissions must not launder between warehouses.
    /// </summary>
    /// <remarks>
    /// Holding Receiver in one site and Supervisor in another is ordinary —
    /// someone who moved sites and kept both grants, or who covers two. If the
    /// check unions their permissions across sites, that person can grant
    /// Supervisor in the site where they were only ever a Receiver, and
    /// approval authority crosses a boundary nobody intended.
    /// </remarks>
    [Fact]
    public async Task PermissionsDoNotLaunderBetweenWarehouses()
    {
        Seeded tokyo = await SeedAsync();
        Seeded osaka = await SeedAsync();

        Guid actor = await SeedUserAsync(tokyo, Receiver);
        await GrantDirectlyAsync(actor, Supervisor, osaka.WarehouseId);

        Guid target = await SeedUserAsync(tokyo, roleCode: null);

        // Supervisor in Osaka, where they hold it. Allowed.
        GrantResult inOsaka = await Directory.GrantRoleScopeAsync(
            new GrantRoleScopeCommand(target, Supervisor, osaka.WarehouseId, [], actor),
            CancellationToken.None);
        Assert.Equal(UserAdminOutcome.Succeeded, inOsaka.Outcome);

        // Supervisor in Tokyo, where they are only a Receiver. Refused.
        GrantResult inTokyo = await Directory.GrantRoleScopeAsync(
            new GrantRoleScopeCommand(target, Supervisor, tokyo.WarehouseId, [], actor),
            CancellationToken.None);

        Assert.Equal(UserAdminOutcome.CannotGrantPermissionYouLack, inTokyo.Outcome);
        Assert.Contains("receipt.over_receive", inTokyo.MissingPermissions!);
    }

    /// <summary>
    /// An empty <c>zoneIds</c> means all zones (§2.1), so granting it requires
    /// holding all zones — it is a widening, not a copy.
    /// </summary>
    [Fact]
    public async Task AZoneScopedAdministratorCannotGrantWarehouseWideAuthority()
    {
        Seeded data = await SeedAsync();
        Guid zoneA = await SeedZoneAsync(data, "A");
        Guid zoneB = await SeedZoneAsync(data, "B");

        Guid actor = await SeedUserAsync(data, roleCode: null);
        await GrantDirectlyAsync(actor, WarehouseManager, data.WarehouseId, [zoneA]);

        Guid target = await SeedUserAsync(data, roleCode: null);

        // Within the actor's own zone: allowed.
        Assert.Equal(
            UserAdminOutcome.Succeeded,
            (await Directory.GrantRoleScopeAsync(
                new GrantRoleScopeCommand(target, Receiver, data.WarehouseId, [zoneA], actor),
                CancellationToken.None)).Outcome);

        // A zone they do not hold: refused.
        Assert.Equal(
            UserAdminOutcome.CannotGrantPermissionYouLack,
            (await Directory.GrantRoleScopeAsync(
                new GrantRoleScopeCommand(target, Receiver, data.WarehouseId, [zoneB], actor),
                CancellationToken.None)).Outcome);

        // All zones: refused, because that is wider than they hold.
        Assert.Equal(
            UserAdminOutcome.CannotGrantPermissionYouLack,
            (await Directory.GrantRoleScopeAsync(
                new GrantRoleScopeCommand(target, Receiver, data.WarehouseId, [], actor),
                CancellationToken.None)).Outcome);
    }

    /// <summary>
    /// Changing someone's access is the same kind of act as granting them a
    /// role, so it takes the same bound.
    /// </summary>
    /// <remarks>
    /// Without this, a site role carrying only <c>user.manage</c> could end
    /// the one System Administrator's account — or, worse, reactivate an
    /// operator a System Administrator had suspended during a live incident.
    /// </remarks>
    [Fact]
    public async Task AUserManagerCannotSuspendSomeoneWhoOutranksThem()
    {
        Seeded data = await SeedAsync();
        Guid manager = await SeedUserAsync(data, WarehouseManager);
        Guid administrator = await SeedUserAsync(data, SystemAdministrator);

        UpdateUserResult result = await Directory.UpdateAsync(
            new UpdateUserCommand(administrator, null, null, null, "ended", manager),
            CancellationToken.None);

        Assert.Equal(UserAdminOutcome.CannotGrantPermissionYouLack, result.Outcome);

        Assert.Equal("active", await ScalarAsync<string>(
            "SELECT status FROM app_user WHERE id = @p;", administrator));

        Assert.Equal(1, await ScalarAsync<long>(
            """
            SELECT count(*) FROM auth_event
             WHERE actor_user_id = @p AND event_type = 'user_status_change_denied';
            """,
            manager));
    }

    [Fact]
    public async Task NobodyCanChangeTheirOwnStatus()
    {
        Seeded data = await SeedAsync();
        Guid administrator = await SeedUserAsync(data, SystemAdministrator);

        UpdateUserResult result = await Directory.UpdateAsync(
            new UpdateUserCommand(administrator, null, null, null, "suspended", administrator),
            CancellationToken.None);

        Assert.Equal(UserAdminOutcome.Invalid, result.Outcome);
        Assert.Equal("active", await ScalarAsync<string>(
            "SELECT status FROM app_user WHERE id = @p;", administrator));
    }

    /// <summary>
    /// Shortening <c>validUntil</c> is a revocation, so it must revoke.
    /// </summary>
    /// <remarks>
    /// §2.1 tells administrators to time-bound agency labour this way, and the
    /// resolver refuses a principal past the date. Bumping the stamp only on
    /// <c>status</c> would leave an already-issued token working until it
    /// expired — H12's failure on a different column.
    /// </remarks>
    [Fact]
    public async Task ShorteningValidity_RevokesAndIsRecorded()
    {
        Seeded data = await SeedAsync();
        Guid manager = await SeedUserAsync(data, WarehouseManager);
        Guid target = await SeedUserAsync(data, Receiver);

        UserDetail? before = await Directory.GetAsync(target, CancellationToken.None);

        UpdateUserResult result = await Directory.UpdateAsync(
            new UpdateUserCommand(
                target, null, null, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1),
                null, manager),
            CancellationToken.None);

        Assert.Equal(UserAdminOutcome.Succeeded, result.Outcome);

        UserDetail? after = await Directory.GetAsync(target, CancellationToken.None);
        Assert.NotEqual(before!.SecurityStamp, after!.SecurityStamp);

        // "Who cut this person off, and when" must be answerable for the route
        // that never touches `status`.
        Assert.Equal(1, await ScalarAsync<long>(
            """
            SELECT count(*) FROM auth_event
             WHERE target_user_id = @p AND event_type = 'user_validity_changed';
            """,
            target));
    }

    /// <summary>
    /// Re-granting with a narrower zone set must narrow it.
    /// </summary>
    /// <remarks>
    /// The unique key does not include <c>zone_ids</c>, so a conflict that did
    /// nothing would report success, write an audit event describing the
    /// narrower scope, and leave the row carrying the wider one — a revocation
    /// that silently does not revoke.
    /// </remarks>
    [Fact]
    public async Task RegrantingWithFewerZones_ActuallyNarrowsTheScope()
    {
        Seeded data = await SeedAsync();
        Guid zoneA = await SeedZoneAsync(data, "A");
        Guid administrator = await SeedUserAsync(data, SystemAdministrator);
        Guid target = await SeedUserAsync(data, roleCode: null);

        await Directory.GrantRoleScopeAsync(
            new GrantRoleScopeCommand(target, Receiver, data.WarehouseId, [], administrator),
            CancellationToken.None);

        GrantResult narrowed = await Directory.GrantRoleScopeAsync(
            new GrantRoleScopeCommand(target, Receiver, data.WarehouseId, [zoneA], administrator),
            CancellationToken.None);

        Assert.Equal(UserAdminOutcome.Succeeded, narrowed.Outcome);
        Assert.NotNull(narrowed.GrantId);

        UserDetail? detail = await Directory.GetAsync(target, CancellationToken.None);
        RoleScopeRow scope = Assert.Single(detail!.RoleScopes);
        Assert.Equal(["A"], scope.ZoneCodes);
    }

    [Fact]
    public async Task AZoneFromAnotherWarehouse_IsRejectedRatherThanStored()
    {
        Seeded tokyo = await SeedAsync();
        Seeded osaka = await SeedAsync();
        Guid osakaZone = await SeedZoneAsync(osaka, "A");

        Guid administrator = await SeedUserAsync(tokyo, SystemAdministrator);
        Guid target = await SeedUserAsync(tokyo, roleCode: null);

        GrantResult result = await Directory.GrantRoleScopeAsync(
            new GrantRoleScopeCommand(target, Receiver, tokyo.WarehouseId, [osakaZone], administrator),
            CancellationToken.None);

        Assert.Equal(UserAdminOutcome.Invalid, result.Outcome);
        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM user_role_scope WHERE user_id = @p;", target));
    }

    /// <summary>
    /// A refused create must leave the same trace a refused grant does.
    /// </summary>
    /// <remarks>
    /// Otherwise an attacker probing which roles they can mint simply uses the
    /// endpoint that forgets.
    /// </remarks>
    [Fact]
    public async Task ARefusedCreate_IsRecordedLikeARefusedGrant()
    {
        Seeded data = await SeedAsync();
        Guid manager = await SeedUserAsync(data, WarehouseManager);

        await Directory.CreateAsync(
            new CreateUserCommand(
                "staff", "Would-be admin", null, null, "en", null, null, manager,
                [new RoleScopeGrant(SystemAdministrator, data.WarehouseId, [])]),
            CancellationToken.None);

        Assert.Equal(1, await ScalarAsync<long>(
            """
            SELECT count(*) FROM auth_event
             WHERE actor_user_id = @p AND event_type = 'role_grant_denied' AND outcome = 'failure';
            """,
            manager));
    }

    [Fact]
    public async Task ListingUsers_ShowsTheirRoleScopes()
    {
        Seeded data = await SeedAsync();
        Guid manager = await SeedUserAsync(data, WarehouseManager);
        Guid target = await SeedUserAsync(data, Receiver);

        UserPage page = await Directory.ListAsync(
            new UserQuery(data.WarehouseId, null, null, null, 50, null), CancellationToken.None);

        UserRow row = page.Items.Single(u => u.Id == target);
        RoleScopeRow scope = Assert.Single(row.RoleScopes);
        Assert.Equal(Receiver, scope.RoleCode);
        Assert.Equal(data.WarehouseCode, scope.WarehouseCode);

        Assert.Contains(page.Items, u => u.Id == manager);
    }

    [Fact]
    public async Task RolesAndPermissions_AreListedForTheAssignmentUi()
    {
        IReadOnlyList<RoleSummary> roles =
            await Directory.ListRolesAsync(includeInactive: false, CancellationToken.None);

        RoleSummary receiver = roles.Single(r => r.Code == Receiver);
        Assert.True(receiver.IsSystem);
        Assert.Contains("receipt.confirm", receiver.Permissions);

        // The Receiver deliberately lacks this; it is Phase 1A exit criterion 6.
        Assert.DoesNotContain("receipt.over_receive", receiver.Permissions);

        IReadOnlyList<PermissionSummary> permissions =
            await Directory.ListPermissionsAsync("receiving", CancellationToken.None);

        Assert.NotEmpty(permissions);
        Assert.All(permissions, p => Assert.Equal("receiving", p.Category));
    }

    /// <summary>
    /// Inserts a grant directly, bypassing the directory.
    /// </summary>
    /// <remarks>
    /// Used only to construct the <em>starting</em> privilege state a test
    /// needs. Going through <c>GrantRoleScopeAsync</c> would require an actor
    /// who already outranks it, which is the thing under test.
    /// </remarks>
    private async Task GrantDirectlyAsync(
        Guid userId, string roleCode, Guid warehouseId, Guid[]? zoneIds = null)
    {
        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(
            """
            INSERT INTO user_role_scope (id, user_id, role_id, warehouse_id, zone_ids,
                                         granted_by, granted_at)
            SELECT gen_random_uuid(), @userId, r.id, @warehouseId, @zoneIds, @userId, now()
              FROM role r WHERE r.code = @roleCode;
            """);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("warehouseId", warehouseId);
        command.Parameters.AddWithValue("zoneIds", zoneIds ?? []);
        command.Parameters.AddWithValue("roleCode", roleCode);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<Guid> SeedZoneAsync(Seeded data, string code)
    {
        Guid zoneId = Guid.CreateVersion7();

        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(
            """
            INSERT INTO zone (id, warehouse_id, code, name_i18n, zone_type,
                              created_at, updated_at)
            VALUES (@id, @warehouseId, @code, '{"en":"Z"}', 'bulk', now(), now());
            """);
        command.Parameters.AddWithValue("id", zoneId);
        command.Parameters.AddWithValue("warehouseId", data.WarehouseId);
        command.Parameters.AddWithValue("code", code);
        await command.ExecuteNonQueryAsync();

        return zoneId;
    }

    private async Task<Guid> SeedUserAsync(Seeded data, string? roleCode)
    {
        Guid userId = Guid.CreateVersion7();

        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(
            """
            INSERT INTO app_user (id, user_type, display_name, status, security_stamp,
                                  valid_from, created_at, updated_at)
            VALUES (@id, 'staff', 'Seeded', 'active', gen_random_uuid(),
                    current_date, now(), now());

            INSERT INTO user_role_scope (id, user_id, role_id, warehouse_id, granted_by, granted_at)
            SELECT gen_random_uuid(), @id, r.id, @warehouseId, @id, now()
              FROM role r WHERE r.code = @roleCode AND @roleCode IS NOT NULL;
            """);
        command.Parameters.AddWithValue("id", userId);
        command.Parameters.AddWithValue("warehouseId", data.WarehouseId);
        command.Parameters.AddWithValue("roleCode", (object?)roleCode ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();

        return userId;
    }

    private async Task SeedActiveDeviceSessionAsync(Seeded data, Guid userId, int queueDepth)
    {
        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(
            """
            INSERT INTO device (id, label, warehouse_id, status, last_queue_depth,
                                created_at, updated_at)
            VALUES (@deviceId, 'HH-TEST', @warehouseId, 'active', @queueDepth, now(), now());

            INSERT INTO device_session (id, device_id, user_id, started_at)
            VALUES (gen_random_uuid(), @deviceId, @userId, now());
            """);
        command.Parameters.AddWithValue("deviceId", Guid.CreateVersion7());
        command.Parameters.AddWithValue("warehouseId", data.WarehouseId);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("queueDepth", queueDepth);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, object parameter)
    {
        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(sql);
        if (sql.Contains("@p", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("p", parameter);
        }

        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task<Seeded> SeedAsync()
    {
        Seeded data = new(Guid.CreateVersion7());

        await using NpgsqlCommand command = fixture.DataSource.CreateCommand(
            """
            INSERT INTO warehouse (id, code, name, timezone, created_at, updated_at)
            VALUES (@warehouseId, @code, 'T', 'Asia/Tokyo', now(), now());
            """);
        command.Parameters.AddWithValue("warehouseId", data.WarehouseId);
        command.Parameters.AddWithValue("code", data.WarehouseCode);
        await command.ExecuteNonQueryAsync();

        return data;
    }

    private sealed record Seeded(Guid WarehouseId)
    {
        public string WarehouseCode { get; } =
            Guid.CreateVersion7().ToString("N")[^8..].ToUpperInvariant();
    }
}
