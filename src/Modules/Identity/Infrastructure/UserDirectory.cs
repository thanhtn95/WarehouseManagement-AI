using System.Data;
using System.Text.Json;
using Npgsql;
using Wms.Modules.Identity.Contracts;

namespace Wms.Modules.Identity.Infrastructure;

/// <inheritdoc />
public sealed class UserDirectory(NpgsqlDataSource dataSource, IPasswordHasher hasher)
    : IUserDirectory
{
    /// <summary>
    /// §5.11: operators default to 90 days rather than open-ended.
    /// </summary>
    /// <remarks>
    /// Agency staff leave without anyone telling the system. An expiry that
    /// must be actively renewed fails closed; one that must be actively
    /// revoked fails open, and the accumulated live credentials of departed
    /// staff are a documented shrinkage route.
    /// </remarks>
    private const int OperatorValidityDays = 90;

    /// <summary>
    /// The actor's effective permissions <strong>within one warehouse</strong>,
    /// honouring their own status and validity window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Scope is part of the answer, not decoration. An earlier version of this
    /// query joined <c>user_role_scope</c> and then never used its
    /// <c>warehouse_id</c> — which unioned an actor's permissions across every
    /// site they hold anything in. Someone holding Receiver in Tokyo and
    /// Supervisor in Osaka — an ordinary state for anyone who moved sites or
    /// covers two — could then grant Supervisor <em>in Tokyo</em>, laundering
    /// authority between warehouses. Invariant 8 is "permissions plus scope",
    /// and it applies to the delegation path exactly as it applies to a scan.
    /// </para>
    /// <para>
    /// The zone condition is asymmetric on purpose. An empty
    /// <c>zone_ids</c> means <em>all zones</em> (§2.1), so a grant of "all
    /// zones" requires the actor to hold all zones themselves; a grant of
    /// specific zones requires those zones to be within the actor's. Treating
    /// the empty array as a plain subset would let an actor scoped to zone A
    /// grant warehouse-wide authority, which is a widening dressed as a copy.
    /// </para>
    /// <para>
    /// Status and validity are filtered here too, so this agrees with
    /// <see cref="DevelopmentPrincipalResolver"/> rather than being quietly
    /// more permissive than the resolver that admits the request.
    /// </para>
    /// </remarks>
    private const string ScopedPermissionsSql = """
        SELECT DISTINCT rp.permission_code
          FROM app_user u
          JOIN user_role_scope urs ON urs.user_id = u.id
          JOIN role r              ON r.id = urs.role_id AND r.is_active
          JOIN role_permission rp  ON rp.role_id = r.id
         WHERE u.id = @userId
           AND u.status = 'active'
           AND u.valid_from <= current_date
           AND (u.valid_until IS NULL OR u.valid_until >= current_date)
           AND urs.warehouse_id = @warehouseId
           AND (CASE WHEN cardinality(@zoneIds::uuid[]) = 0
                     THEN cardinality(urs.zone_ids) = 0
                     ELSE cardinality(urs.zone_ids) = 0
                          OR @zoneIds::uuid[] <@ urs.zone_ids
                END);
        """;

    /// <summary>
    /// Permissions the target holds that the actor cannot match
    /// <em>in the same warehouse</em>.
    /// </summary>
    /// <remarks>
    /// Used before any status or validity change. Changing someone's access is
    /// the same kind of act as granting them a role, so it takes the same
    /// bound — otherwise a site-composed role carrying only <c>user.manage</c>
    /// could end the one System Administrator's account, or reactivate an
    /// operator a System Administrator had suspended during a live incident.
    /// The target's own status is deliberately <em>not</em> filtered: an
    /// already-suspended administrator still outranks their would-be
    /// reactivator.
    /// </remarks>
    private const string PermissionsTargetHoldsBeyondActorSql = """
        WITH target AS (
            SELECT urs.warehouse_id, rp.permission_code
              FROM user_role_scope urs
              JOIN role r             ON r.id = urs.role_id AND r.is_active
              JOIN role_permission rp ON rp.role_id = r.id
             WHERE urs.user_id = @targetUserId
        ),
        actor AS (
            SELECT urs.warehouse_id, rp.permission_code
              FROM app_user u
              JOIN user_role_scope urs ON urs.user_id = u.id
              JOIN role r              ON r.id = urs.role_id AND r.is_active
              JOIN role_permission rp  ON rp.role_id = r.id
             WHERE u.id = @actorUserId
               AND u.status = 'active'
               AND u.valid_from <= current_date
               AND (u.valid_until IS NULL OR u.valid_until >= current_date)
        )
        SELECT DISTINCT t.permission_code
          FROM target t
         WHERE NOT EXISTS (
             SELECT 1 FROM actor a
              WHERE a.warehouse_id = t.warehouse_id
                AND a.permission_code = t.permission_code)
         ORDER BY 1;
        """;

    /// <summary>
    /// Every zone named must exist, be active, and belong to the warehouse the
    /// grant is scoped to.
    /// </summary>
    /// <remarks>
    /// <c>zone_ids</c> is a <c>uuid[]</c>, so no foreign key can enforce this
    /// and the check has to live in code. §2.1 has this array read on every
    /// task lease — a zone id from another warehouse stored here becomes a
    /// scope-widening artefact the moment scope enforcement lands, and until
    /// then it is invisible because the read model joins zones by id and
    /// simply renders fewer codes than were stored.
    /// </remarks>
    private const string ValidateZonesSql = """
        SELECT count(*)::int FROM zone
         WHERE id = ANY(@zoneIds) AND warehouse_id = @warehouseId AND status = 'active';
        """;

    private const string WarehouseExistsSql = """
        SELECT 1 FROM warehouse WHERE id = @warehouseId;
        """;

    private const string RolePermissionsSql = """
        SELECT r.id, rp.permission_code
          FROM role r
          LEFT JOIN role_permission rp ON rp.role_id = r.id
         WHERE r.code = @roleCode AND r.is_active;
        """;

    private const string InsertUserSql = """
        INSERT INTO app_user (id, user_type, display_name, employee_code, email, locale,
                              status, security_stamp, valid_from, valid_until,
                              created_at, updated_at)
        VALUES (@id, @userType, @displayName, @employeeCode, @email, @locale,
                'active', gen_random_uuid(), @validFrom, @validUntil, now(), now());
        """;

    /// <summary>
    /// Re-granting the same role in the same warehouse updates the scope.
    /// </summary>
    /// <remarks>
    /// The unique key is <c>(user_id, role_id, warehouse_id)</c> and does not
    /// include <c>zone_ids</c>, so <c>DO NOTHING</c> here turned a scope
    /// <em>narrowing</em> into a silent no-op: an administrator tightening a
    /// user from all zones to zone A alone would have received a success, an
    /// audit event describing the narrower scope, and a row still carrying
    /// the wider one. A revocation that does not revoke while reporting that
    /// it did is worse than one that fails loudly.
    /// </remarks>
    /// <summary>
    /// One credential per type per user, hashed with Argon2id (§8.1).
    /// </summary>
    /// <remarks>
    /// The plaintext reaches this method and nowhere else — it is not logged,
    /// not echoed in the response, and not stored. A badge secret is hashed
    /// exactly like a password: a badge number printed on a card is still a
    /// credential, and a database disclosure must not hand over a working one.
    /// </remarks>
    private const string InsertCredentialSql = """
        INSERT INTO credential (id, user_id, credential_type, secret_hash, created_at)
        VALUES (@id, @userId, @credentialType, @secretHash, now());
        """;

    private const string InsertRoleScopeSql = """
        INSERT INTO user_role_scope (id, user_id, role_id, warehouse_id, zone_ids,
                                     granted_by, granted_at)
        VALUES (@id, @userId, @roleId, @warehouseId, @zoneIds, @grantedBy, now())
        ON CONFLICT (user_id, role_id, warehouse_id) DO UPDATE
            SET zone_ids   = EXCLUDED.zone_ids,
                granted_by = EXCLUDED.granted_by,
                granted_at = now()
        RETURNING id;
        """;

    /// <summary>
    /// Every grant and every status change writes one of these.
    /// </summary>
    /// <remarks>
    /// "Who gave this person approval rights, and when" is the first question
    /// in any shrinkage investigation, and it cannot be reconstructed from the
    /// current state — <c>user_role_scope</c> shows what is true now, not what
    /// was done.
    /// </remarks>
    private const string InsertAuthEventSql = """
        INSERT INTO auth_event (id, occurred_at, event_type, actor_user_id,
                                target_user_id, outcome, detail)
        VALUES (gen_random_uuid(), now(), @eventType, @actorUserId,
                @targetUserId, @outcome, @detail::jsonb);
        """;

    /// <summary>
    /// Suspension and expiry must take effect within seconds, not at the next
    /// token expiry.
    /// </summary>
    /// <remarks>
    /// Bumping <c>security_stamp</c> is what makes every already-issued token
    /// for this identity fail its next request (§8.1). Without it,
    /// "deactivated" means "deactivated whenever their current token happens
    /// to run out", which is not what anyone suspending an account believes
    /// they are doing.
    /// </remarks>
    private const string UpdateUserSql = """
        UPDATE app_user
           SET display_name   = COALESCE(@displayName, display_name),
               locale         = COALESCE(@locale, locale),
               valid_until    = COALESCE(@validUntil, valid_until),
               status         = COALESCE(@status, status),
               -- Both columns are access boundaries, so both revoke.
               -- valid_until is how §2.1 tells administrators to time-bound
               -- agency labour, and the resolver refuses a principal past it —
               -- so shortening it IS a revocation. Bumping only on `status`
               -- would leave an already-issued token working until it expired,
               -- which is H12 on a different column. Bumping on an extension
               -- too is harmless and one fewer condition to get wrong.
               security_stamp = CASE
                   WHEN (@status IS NOT NULL AND @status <> status)
                     OR (@validUntil IS NOT NULL AND @validUntil IS DISTINCT FROM valid_until)
                   THEN gen_random_uuid() ELSE security_stamp END,
               updated_at     = now()
         WHERE id = @userId
        RETURNING status;
        """;

    /// <summary>
    /// M6: what the user's devices were still holding when they were cut off.
    /// </summary>
    private const string QueueDepthSql = """
        SELECT COALESCE(sum(d.last_queue_depth), 0)::int
          FROM device_session ds
          JOIN device d ON d.id = ds.device_id
         WHERE ds.user_id = @userId AND ds.ended_at IS NULL;
        """;

    public async Task<CreateUserResult> CreateAsync(
        CreateUserCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.UserType is not ("staff" or "operator"))
        {
            return new CreateUserResult(
                UserAdminOutcome.Invalid, null, null, null,
                "userType must be staff or operator.");
        }

        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // Every role in the request is checked BEFORE anything is written, so
        // a rejected create leaves no half-built user behind.
        List<(Guid RoleId, RoleScopeGrant Grant)> resolved = [];
        foreach (RoleScopeGrant grant in command.RoleScopes)
        {
            string? scopeProblem = await ValidateScopeAsync(
                connection, transaction, grant.WarehouseId, grant.ZoneIds, cancellationToken);

            if (scopeProblem is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new CreateUserResult(
                    UserAdminOutcome.Invalid, null, null, null, scopeProblem);
            }

            (Guid? roleId, HashSet<string> rolePermissions) =
                await RoleAsync(connection, transaction, grant.RoleCode, cancellationToken);

            if (roleId is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                await RecordDeniedAsync(
                    command.ActorUserId, "role_grant_denied",
                    new { roleCode = grant.RoleCode, reason = "unknown_role" }, cancellationToken);

                return new CreateUserResult(
                    UserAdminOutcome.UnknownRole, null, null, null,
                    $"Unknown or inactive role '{grant.RoleCode}'.");
            }

            // Scoped to the warehouse and zones this grant actually targets.
            HashSet<string> actorPermissions = await ScopedPermissionsAsync(
                connection, transaction, command.ActorUserId,
                grant.WarehouseId, grant.ZoneIds, cancellationToken);

            string[] missing = [.. rolePermissions.Except(actorPermissions).Order()];
            if (missing.Length > 0)
            {
                await transaction.RollbackAsync(cancellationToken);

                // Recorded on its own connection, because this transaction is
                // being discarded. The grant path commits its denial instead;
                // both routes must leave a trace, or an attacker simply probes
                // through whichever one forgets.
                await RecordDeniedAsync(
                    command.ActorUserId, "role_grant_denied",
                    new { roleCode = grant.RoleCode, warehouseId = grant.WarehouseId, missing },
                    cancellationToken);

                return new CreateUserResult(
                    UserAdminOutcome.CannotGrantPermissionYouLack, null, null, missing,
                    $"Cannot grant '{grant.RoleCode}': it carries permissions you do not hold.");
            }

            resolved.Add((roleId.Value, grant));
        }

        Guid userId = Guid.CreateVersion7();
        DateOnly validFrom = command.ValidFrom ?? DateOnly.FromDateTime(DateTime.UtcNow);
        DateOnly? validUntil = command.ValidUntil
            ?? (command.UserType == "operator"
                ? validFrom.AddDays(OperatorValidityDays)
                : null);

        await using (NpgsqlCommand insert = new(InsertUserSql, connection, transaction))
        {
            insert.Parameters.AddWithValue("id", userId);
            insert.Parameters.AddWithValue("userType", command.UserType);
            insert.Parameters.AddWithValue("displayName", command.DisplayName);
            insert.Parameters.AddWithValue(
                "employeeCode", (object?)command.EmployeeCode ?? DBNull.Value);
            insert.Parameters.AddWithValue("email", (object?)command.Email ?? DBNull.Value);
            insert.Parameters.AddWithValue("locale", command.Locale);
            insert.Parameters.AddWithValue("validFrom", validFrom);
            insert.Parameters.AddWithValue("validUntil", (object?)validUntil ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach ((Guid roleId, RoleScopeGrant grant) in resolved)
        {
            await InsertRoleScopeAsync(
                connection, transaction, userId, roleId, grant, command.ActorUserId, cancellationToken);

            await WriteAuthEventAsync(
                connection, transaction, "role_granted", command.ActorUserId, userId,
                new { roleCode = grant.RoleCode, warehouseId = grant.WarehouseId },
                cancellationToken);
        }

        foreach (NewCredential credential in command.Credentials ?? [])
        {
            if (credential.Type is not ("password" or "pin" or "badge"))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new CreateUserResult(
                    UserAdminOutcome.Invalid, null, null, null,
                    "credential type must be password, pin or badge.");
            }

            await using NpgsqlCommand insert = new(InsertCredentialSql, connection, transaction);
            insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("userId", userId);
            insert.Parameters.AddWithValue("credentialType", credential.Type);
            insert.Parameters.AddWithValue("secretHash", hasher.Hash(credential.Secret));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await WriteAuthEventAsync(
            connection, transaction, "user_created", command.ActorUserId, userId,
            new
            {
                userType = command.UserType,
                validUntil,

                // The TYPES are recorded, never the secrets.
                credentialTypes = (command.Credentials ?? []).Select(c => c.Type),
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new CreateUserResult(UserAdminOutcome.Succeeded, userId, "active", null, null);
    }

    public async Task<GrantResult> GrantRoleScopeAsync(
        GrantRoleScopeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        if (!await UserExistsAsync(connection, transaction, command.UserId, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new GrantResult(UserAdminOutcome.NotFound, null, null, null, null, "Unknown user.");
        }

        (Guid? roleId, HashSet<string> rolePermissions) =
            await RoleAsync(connection, transaction, command.RoleCode, cancellationToken);

        if (roleId is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new GrantResult(
                UserAdminOutcome.UnknownRole, null, null, null, null,
                $"Unknown or inactive role '{command.RoleCode}'.");
        }

        string? scopeProblem = await ValidateScopeAsync(
            connection, transaction, command.WarehouseId, command.ZoneIds, cancellationToken);

        if (scopeProblem is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new GrantResult(UserAdminOutcome.Invalid, null, null, null, null, scopeProblem);
        }

        HashSet<string> actorPermissions = await ScopedPermissionsAsync(
            connection, transaction, command.ActorUserId,
            command.WarehouseId, command.ZoneIds, cancellationToken);

        // H13, extended from role composition to role GRANTING. The review
        // closed the composition path — an admin must not bundle permissions
        // they lack into a new role — but granting an existing role is the
        // same escalation by a shorter route, and the sharpest case is an
        // administrator granting it to themselves. With the seeded roles a
        // Warehouse Manager holds everything except warehouse.manage, so this
        // permits every legitimate delegation and blocks exactly one thing:
        // minting a System Administrator.
        string[] missing = [.. rolePermissions.Except(actorPermissions).Order()];
        if (missing.Length > 0)
        {
            await WriteAuthEventAsync(
                connection, transaction, "role_grant_denied", command.ActorUserId, command.UserId,
                new { roleCode = command.RoleCode, warehouseId = command.WarehouseId, missing },
                cancellationToken, outcome: "failure");

            // Committed, not rolled back: the refusal is the only thing this
            // transaction wrote, and an attempted escalation is precisely the
            // event an investigation needs to see.
            await transaction.CommitAsync(cancellationToken);

            return new GrantResult(
                UserAdminOutcome.CannotGrantPermissionYouLack, null, null, null, missing,
                $"Cannot grant '{command.RoleCode}': it carries permissions you do not hold.");
        }

        Guid? grantId = await InsertRoleScopeAsync(
            connection, transaction, command.UserId, roleId.Value,
            new RoleScopeGrant(command.RoleCode, command.WarehouseId, command.ZoneIds),
            command.ActorUserId, cancellationToken);

        await WriteAuthEventAsync(
            connection, transaction, "role_granted", command.ActorUserId, command.UserId,
            new { roleCode = command.RoleCode, warehouseId = command.WarehouseId },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new GrantResult(
            UserAdminOutcome.Succeeded, grantId, command.ActorUserId, DateTimeOffset.UtcNow, null, null);
    }

    public async Task<UpdateUserResult> UpdateAsync(
        UpdateUserCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Status is not null
            and not ("active" or "suspended" or "ended"))
        {
            return new UpdateUserResult(
                UserAdminOutcome.Invalid, null, 0, "status must be active, suspended or ended.");
        }

        // Changing your own status has no legitimate use: self-suspension is a
        // footgun, and self-reactivation is nonsense because a suspended actor
        // cannot authenticate to attempt it.
        if (command.Status is not null && command.ActorUserId == command.UserId)
        {
            return new UpdateUserResult(
                UserAdminOutcome.Invalid, null, 0, "You cannot change your own status.");
        }

        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        if (!await UserExistsAsync(connection, transaction, command.UserId, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new UpdateUserResult(UserAdminOutcome.NotFound, null, 0, "Unknown user.");
        }

        // H13 completed on the status axis. Altering another identity's access
        // is the same kind of act as granting them a role, so it takes the
        // same bound: a site role carrying only `user.manage` must not be able
        // to end the one System Administrator's account, nor reactivate an
        // operator a System Administrator suspended during a live incident.
        if (command.Status is not null || command.ValidUntil is not null)
        {
            string[] beyond = await PermissionsTargetHoldsBeyondActorAsync(
                connection, transaction, command.ActorUserId, command.UserId, cancellationToken);

            if (beyond.Length > 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                await RecordDeniedAsync(
                    command.ActorUserId, "user_status_change_denied",
                    new { targetUserId = command.UserId, beyond }, cancellationToken);

                return new UpdateUserResult(
                    UserAdminOutcome.CannotGrantPermissionYouLack, null, 0,
                    "This user holds permissions you do not, so you cannot change their access.");
            }
        }

        // Read the queue depth BEFORE the status change, while the session is
        // still open — afterwards the information M6 exists to surface would
        // be exactly what the change destroyed.
        int queueDepth = await QueueDepthAsync(
            connection, transaction, command.UserId, cancellationToken);

        DateOnly? previousValidUntil = await PreviousValidUntilAsync(
            connection, transaction, command.UserId, cancellationToken);

        string? status;
        await using (NpgsqlCommand update = new(UpdateUserSql, connection, transaction))
        {
            update.Parameters.AddWithValue("userId", command.UserId);
            update.Parameters.AddWithValue(
                "displayName", (object?)command.DisplayName ?? DBNull.Value);
            update.Parameters.AddWithValue("locale", (object?)command.Locale ?? DBNull.Value);
            update.Parameters.AddWithValue(
                "validUntil", (object?)command.ValidUntil ?? DBNull.Value);
            update.Parameters.AddWithValue("status", (object?)command.Status ?? DBNull.Value);

            status = await update.ExecuteScalarAsync(cancellationToken) as string;
        }

        if (status is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new UpdateUserResult(UserAdminOutcome.NotFound, null, 0, "Unknown user.");
        }

        if (command.Status is not null)
        {
            await WriteAuthEventAsync(
                connection, transaction, $"user_{command.Status}", command.ActorUserId,
                command.UserId, new { queueDepth }, cancellationToken);
        }

        // A validity change is a revocation expressed a different way, so it
        // is recorded like one. Without this, "who cut this person off, and
        // when" is unanswerable for the route that never touches `status`.
        if (command.ValidUntil is not null && command.ValidUntil != previousValidUntil)
        {
            await WriteAuthEventAsync(
                connection, transaction, "user_validity_changed", command.ActorUserId,
                command.UserId,
                new { from = previousValidUntil, to = command.ValidUntil, queueDepth },
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return new UpdateUserResult(
            UserAdminOutcome.Succeeded, status,
            command.Status is "suspended" or "ended" ? queueDepth : 0, null);
    }

    /// <summary>
    /// The user list, with each user's role scopes aggregated in the same
    /// query.
    /// </summary>
    /// <remarks>
    /// <c>q</c> matches display name or employee code. <c>ILIKE</c> with a
    /// leading wildcard cannot use a B-tree index, which is acceptable at the
    /// scale of a staff list — a warehouse has hundreds of users, not millions
    /// — and is called out here so nobody copies the pattern onto a table
    /// where it would matter.
    /// </remarks>
    private const string ListUsersSql = """
        SELECT u.id, u.display_name, u.employee_code, u.user_type, u.status, u.valid_until,
               COALESCE(
                   (SELECT jsonb_agg(jsonb_build_object(
                                'roleCode', r.code,
                                'warehouseCode', w.code,
                                'zoneCodes', COALESCE(
                                    (SELECT jsonb_agg(z.code ORDER BY z.code)
                                       FROM zone z WHERE z.id = ANY(urs.zone_ids)),
                                    '[]'::jsonb))
                            ORDER BY r.code)
                      FROM user_role_scope urs
                      JOIN role r      ON r.id = urs.role_id
                      JOIN warehouse w ON w.id = urs.warehouse_id
                     WHERE urs.user_id = u.id),
                   '[]'::jsonb) AS role_scopes
          FROM app_user u
         WHERE (@userType::text IS NULL OR u.user_type = @userType)
           AND (@status::text   IS NULL OR u.status = @status)
           AND (@search::text   IS NULL
                OR u.display_name ILIKE '%' || @search || '%'
                OR u.employee_code ILIKE '%' || @search || '%')
           AND (@warehouseId::uuid IS NULL
                OR EXISTS (SELECT 1 FROM user_role_scope urs
                            WHERE urs.user_id = u.id AND urs.warehouse_id = @warehouseId))
           AND (@afterId::uuid IS NULL OR u.id > @afterId)
         ORDER BY u.id
         LIMIT @limit;
        """;

    private const string GetUserSql = """
        SELECT u.id, u.display_name, u.employee_code, u.user_type, u.locale, u.status,
               u.valid_from, u.valid_until, u.security_stamp
          FROM app_user u WHERE u.id = @userId;
        """;

    private const string UserRoleScopesSql = """
        SELECT r.code, w.code,
               COALESCE((SELECT array_agg(z.code ORDER BY z.code)
                           FROM zone z WHERE z.id = ANY(urs.zone_ids)), '{}')
          FROM user_role_scope urs
          JOIN role r      ON r.id = urs.role_id
          JOIN warehouse w ON w.id = urs.warehouse_id
         WHERE urs.user_id = @userId
         ORDER BY r.code;
        """;

    private const string ListRolesSql = """
        SELECT r.id, r.code, COALESCE(r.name_i18n->>'en', r.code),
               r.is_system, r.is_active,
               COALESCE((SELECT array_agg(rp.permission_code ORDER BY rp.permission_code)
                           FROM role_permission rp WHERE rp.role_id = r.id), '{}')
          FROM role r
         WHERE (@includeInactive OR r.is_active)
         ORDER BY r.code;
        """;

    private const string ListPermissionsSql = """
        SELECT code, category, COALESCE(description_i18n->>'en', code)
          FROM permission
         WHERE (@category::text IS NULL OR category = @category)
         ORDER BY category, code;
        """;

    public async Task<UserPage> ListAsync(UserQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        List<UserRow> rows = [];
        await using (NpgsqlCommand command = dataSource.CreateCommand(ListUsersSql))
        {
            command.Parameters.AddWithValue("userType", (object?)query.UserType ?? DBNull.Value);
            command.Parameters.AddWithValue("status", (object?)query.Status ?? DBNull.Value);
            command.Parameters.AddWithValue("search", (object?)query.Search ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "warehouseId", (object?)query.WarehouseId ?? DBNull.Value);
            command.Parameters.AddWithValue("afterId", (object?)query.AfterId ?? DBNull.Value);
            command.Parameters.AddWithValue("limit", query.Limit + 1);

            await using NpgsqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new UserRow(
                    Id: reader.GetGuid(0),
                    DisplayName: reader.GetString(1),
                    EmployeeCode: reader.IsDBNull(2) ? null : reader.GetString(2),
                    UserType: reader.GetString(3),
                    Status: reader.GetString(4),
                    ValidUntil: reader.IsDBNull(5) ? null : reader.GetFieldValue<DateOnly>(5),
                    RoleScopes: JsonSerializer.Deserialize<List<RoleScopeRow>>(
                        reader.GetString(6), JsonOptions) ?? []));
            }
        }

        bool hasMore = rows.Count > query.Limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return new UserPage(rows, rows.Count > 0 ? rows[^1].Id : null, hasMore);
    }

    public async Task<UserDetail?> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        UserDetail? detail = null;

        await using (NpgsqlCommand command = dataSource.CreateCommand(GetUserSql))
        {
            command.Parameters.AddWithValue("userId", userId);

            await using NpgsqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                detail = new UserDetail(
                    Id: reader.GetGuid(0),
                    DisplayName: reader.GetString(1),
                    EmployeeCode: reader.IsDBNull(2) ? null : reader.GetString(2),
                    UserType: reader.GetString(3),
                    Locale: reader.GetString(4),
                    Status: reader.GetString(5),
                    ValidFrom: reader.GetFieldValue<DateOnly>(6),
                    ValidUntil: reader.IsDBNull(7) ? null : reader.GetFieldValue<DateOnly>(7),
                    SecurityStamp: reader.GetGuid(8),
                    RoleScopes: []);
            }
        }

        if (detail is null)
        {
            return null;
        }

        List<RoleScopeRow> scopes = [];
        await using (NpgsqlCommand command = dataSource.CreateCommand(UserRoleScopesSql))
        {
            command.Parameters.AddWithValue("userId", userId);

            await using NpgsqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                scopes.Add(new RoleScopeRow(
                    reader.GetString(0), reader.GetString(1), reader.GetFieldValue<string[]>(2)));
            }
        }

        return detail with { RoleScopes = scopes };
    }

    public async Task<IReadOnlyList<RoleSummary>> ListRolesAsync(
        bool includeInactive, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = dataSource.CreateCommand(ListRolesSql);
        command.Parameters.AddWithValue("includeInactive", includeInactive);

        List<RoleSummary> roles = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            roles.Add(new RoleSummary(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.GetBoolean(3), reader.GetBoolean(4),
                reader.GetFieldValue<string[]>(5)));
        }

        return roles;
    }

    public async Task<IReadOnlyList<PermissionSummary>> ListPermissionsAsync(
        string? category, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = dataSource.CreateCommand(ListPermissionsSql);
        command.Parameters.AddWithValue("category", (object?)category ?? DBNull.Value);

        List<PermissionSummary> permissions = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            permissions.Add(new PermissionSummary(
                reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return permissions;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Writes a denial on a connection of its own.
    /// </summary>
    /// <remarks>
    /// Used where the refusing transaction is being rolled back. An escalation
    /// attempt is the single event an investigation most needs, and discarding
    /// it with the transaction would make one of the two refusal paths a
    /// silent probe.
    /// </remarks>
    private async Task RecordDeniedAsync(
        Guid actorUserId, string eventType, object detail, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlCommand command = new(InsertAuthEventSql, connection);
        command.Parameters.AddWithValue("eventType", eventType);
        command.Parameters.AddWithValue("actorUserId", actorUserId);
        command.Parameters.AddWithValue("targetUserId", DBNull.Value);
        command.Parameters.AddWithValue("outcome", "failure");
        command.Parameters.AddWithValue("detail", JsonSerializer.Serialize(detail));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<DateOnly?> PreviousValidUntilAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command =
            new("SELECT valid_until FROM app_user WHERE id = @id;", connection, transaction);
        command.Parameters.AddWithValue("id", userId);

        return await command.ExecuteScalarAsync(cancellationToken) as DateOnly?;
    }

    private static async Task<Guid?> InsertRoleScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        Guid roleId,
        RoleScopeGrant grant,
        Guid grantedBy,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(InsertRoleScopeSql, connection, transaction);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("roleId", roleId);
        command.Parameters.AddWithValue("warehouseId", grant.WarehouseId);
        command.Parameters.AddWithValue("zoneIds", grant.ZoneIds.ToArray());
        command.Parameters.AddWithValue("grantedBy", grantedBy);

        return await command.ExecuteScalarAsync(cancellationToken) as Guid?;
    }

    private static async Task WriteAuthEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string eventType,
        Guid actorUserId,
        Guid targetUserId,
        object detail,
        CancellationToken cancellationToken,
        string outcome = "success")
    {
        await using NpgsqlCommand command = new(InsertAuthEventSql, connection, transaction);
        command.Parameters.AddWithValue("eventType", eventType);
        command.Parameters.AddWithValue("actorUserId", actorUserId);
        command.Parameters.AddWithValue("targetUserId", targetUserId);
        command.Parameters.AddWithValue("outcome", outcome);
        command.Parameters.AddWithValue("detail", JsonSerializer.Serialize(detail));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<HashSet<string>> ScopedPermissionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        Guid warehouseId,
        IReadOnlyList<Guid> zoneIds,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(ScopedPermissionsSql, connection, transaction);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("warehouseId", warehouseId);
        command.Parameters.AddWithValue("zoneIds", zoneIds.ToArray());

        HashSet<string> permissions = new(StringComparer.Ordinal);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            permissions.Add(reader.GetString(0));
        }

        return permissions;
    }

    private static async Task<string[]> PermissionsTargetHoldsBeyondActorAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actorUserId,
        Guid targetUserId,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command =
            new(PermissionsTargetHoldsBeyondActorSql, connection, transaction);
        command.Parameters.AddWithValue("actorUserId", actorUserId);
        command.Parameters.AddWithValue("targetUserId", targetUserId);

        List<string> missing = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            missing.Add(reader.GetString(0));
        }

        return [.. missing];
    }

    /// <summary>
    /// Validates the warehouse and every zone named by a grant.
    /// </summary>
    private static async Task<string?> ValidateScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid warehouseId,
        IReadOnlyList<Guid> zoneIds,
        CancellationToken cancellationToken)
    {
        await using (NpgsqlCommand warehouse = new(WarehouseExistsSql, connection, transaction))
        {
            warehouse.Parameters.AddWithValue("warehouseId", warehouseId);
            if (await warehouse.ExecuteScalarAsync(cancellationToken) is null)
            {
                // Checked rather than left to the foreign key, which would
                // surface as a 500 for what is plainly a bad request.
                return $"Unknown warehouse {warehouseId}.";
            }
        }

        if (zoneIds.Count == 0)
        {
            return null;
        }

        await using NpgsqlCommand command = new(ValidateZonesSql, connection, transaction);
        command.Parameters.AddWithValue("zoneIds", zoneIds.ToArray());
        command.Parameters.AddWithValue("warehouseId", warehouseId);

        int found = (int)(await command.ExecuteScalarAsync(cancellationToken))!;
        return found == zoneIds.Distinct().Count()
            ? null
            : "Every zone must exist, be active, and belong to the warehouse of the grant.";
    }

    private static async Task<(Guid? RoleId, HashSet<string> Permissions)> RoleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string roleCode,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(RolePermissionsSql, connection, transaction);
        command.Parameters.AddWithValue("roleCode", roleCode);

        Guid? roleId = null;
        HashSet<string> permissions = new(StringComparer.Ordinal);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            roleId = reader.GetGuid(0);
            if (!await reader.IsDBNullAsync(1, cancellationToken))
            {
                permissions.Add(reader.GetString(1));
            }
        }

        return (roleId, permissions);
    }

    private static async Task<bool> UserExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command =
            new("SELECT 1 FROM app_user WHERE id = @id;", connection, transaction);
        command.Parameters.AddWithValue("id", userId);

        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<int> QueueDepthAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(QueueDepthSql, connection, transaction);
        command.Parameters.AddWithValue("userId", userId);

        return (int)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
