-- =====================================================================
--  0002__identity.sql
--  Duration: instant  (empty database)
--  Backward-compatible with: n/a — initial schema
--
--  Two populations, two authentication models: staff (email + password)
--  and floor operators (badge or PIN, on pooled shared devices). Depends
--  on 0001 for warehouse/zone, which role scope points at.
--
--  No tenant_id anywhere: this product is single-tenant, isolated at the
--  process and database boundary (D1b), which is stronger than row-level
--  security and needs no per-query discipline.
-- =====================================================================

CREATE TABLE IF NOT EXISTS app_user (
    id                  uuid        PRIMARY KEY,
    user_type           text        NOT NULL CHECK (user_type IN ('staff', 'operator')),
    display_name        text        NOT NULL,
    employee_code       text        UNIQUE,                 -- badge / payroll identifier
    email               text        UNIQUE,                 -- staff only
    locale              text        NOT NULL DEFAULT 'en',
    status              text        NOT NULL DEFAULT 'active'
                                    CHECK (status IN ('active', 'suspended', 'ended')),
    security_stamp      uuid        NOT NULL,
    valid_from          date        NOT NULL,
    valid_until         date,                               -- NULL = open-ended
    last_login_at       timestamptz,
    created_at          timestamptz NOT NULL,
    updated_at          timestamptz NOT NULL
);

COMMENT ON COLUMN app_user.security_stamp IS
    'Bumped on any credential, role, or status change. Stale-stamp tokens '
    'are rejected on the next request, so termination takes effect in '
    'seconds rather than at token expiry (§8.1).';

COMMENT ON COLUMN app_user.valid_until IS
    'Defaults to 90 days for operators at creation time. Warehouses run on '
    'agency and seasonal labour; accounts that expire by default prevent '
    'the slow accumulation of live credentials belonging to people who left.';

-- There is no DELETE path for app_user: a user is deactivated via status,
-- never removed, because stock_movement.actor_user_id and auth_event
-- reference them permanently.

CREATE TABLE IF NOT EXISTS credential (
    id                  uuid        PRIMARY KEY,
    user_id             uuid        NOT NULL REFERENCES app_user (id),
    credential_type     text        NOT NULL
                                    CHECK (credential_type IN ('password', 'pin', 'badge')),
    secret_hash         text        NOT NULL,               -- Argon2id
    failed_attempts     int         NOT NULL DEFAULT 0,
    locked_until        timestamptz,
    last_used_at        timestamptz,
    created_at          timestamptz NOT NULL,
    UNIQUE (user_id, credential_type)
);

COMMENT ON TABLE credential IS
    'Rotation is recorded as an auth_event (credential_reset), not as a '
    'second timestamp on this row.';

-- --------------------------------------------------------------------
--  Roles, permissions, and scope
-- --------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS role (
    id                  uuid        PRIMARY KEY,
    code                text        NOT NULL UNIQUE,        -- PICKER, SUPERVISOR, ...
    name_i18n           jsonb       NOT NULL,
    is_system           boolean     NOT NULL DEFAULT false,
    is_active           boolean     NOT NULL DEFAULT true,
    version             bigint      NOT NULL DEFAULT 0,
    created_at          timestamptz NOT NULL,
    updated_at          timestamptz NOT NULL
);

COMMENT ON COLUMN role.is_system IS
    'System roles are seeded, non-deletable, and their permission set is '
    'fixed — PATCH /roles/{id} refuses them outright. Custom roles composed '
    'for a site are deactivated via is_active, never deleted, because '
    'user_role_scope may reference them historically.';

-- permission.code is the primary key rather than a uuid surrogate: the code
-- is already the stable public identity everything refers to (role_permission,
-- application code, config), so a surrogate would be indirection between a
-- value and itself.
CREATE TABLE IF NOT EXISTS permission (
    code                text        PRIMARY KEY,            -- receipt.over_receive
    category            text        NOT NULL,
    description_i18n    jsonb       NOT NULL
);

CREATE TABLE IF NOT EXISTS role_permission (
    role_id             uuid        NOT NULL REFERENCES role (id),
    permission_code     text        NOT NULL REFERENCES permission (code),
    PRIMARY KEY (role_id, permission_code)
);

CREATE TABLE IF NOT EXISTS user_role_scope (
    id                  uuid        PRIMARY KEY,
    user_id             uuid        NOT NULL REFERENCES app_user (id),
    role_id             uuid        NOT NULL REFERENCES role (id),
    warehouse_id        uuid        NOT NULL REFERENCES warehouse (id),
    zone_ids            uuid[]      NOT NULL DEFAULT '{}',  -- empty = all zones
    granted_by          uuid        NOT NULL REFERENCES app_user (id),
    granted_at          timestamptz NOT NULL,
    UNIQUE (user_id, role_id, warehouse_id)
);

COMMENT ON COLUMN user_role_scope.zone_ids IS
    'An array, not a join table: zone scope is read on every task lease and '
    'never joined against, so this avoids a join on the hottest '
    'authorisation path. GIN-indexed for containment queries.';

CREATE INDEX IF NOT EXISTS user_role_scope_zones_idx
    ON user_role_scope USING gin (zone_ids);

CREATE INDEX IF NOT EXISTS user_role_scope_user_idx
    ON user_role_scope (user_id);

-- Supports the H12 propagation job: when a role's permission set changes,
-- every holder's security_stamp is bumped via the outbox.
CREATE INDEX IF NOT EXISTS user_role_scope_role_idx
    ON user_role_scope (role_id);

-- --------------------------------------------------------------------
--  Devices and shared-device sessions
-- --------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS device (
    id                  uuid        PRIMARY KEY,
    label               text        NOT NULL,               -- "HH-014", printed on the unit
    warehouse_id        uuid        NOT NULL REFERENCES warehouse (id),
    status              text        NOT NULL DEFAULT 'active'
                                    CHECK (status IN ('active', 'retired', 'lost')),
    platform            text,                               -- android/13, chrome/121
    app_version         text,
    last_seen_at        timestamptz,
    last_queue_depth    int,
    created_at          timestamptz NOT NULL,
    updated_at          timestamptz NOT NULL
);

COMMENT ON COLUMN device.last_queue_depth IS
    'Written from the most recent heartbeat so sync backlog is queryable '
    'from the admin API and dashboard, not only from the metrics pipeline.';

CREATE TABLE IF NOT EXISTS device_session (
    id                  uuid        PRIMARY KEY,
    device_id           uuid        NOT NULL REFERENCES device (id),
    user_id             uuid        NOT NULL REFERENCES app_user (id),
    started_at          timestamptz NOT NULL,
    ended_at            timestamptz,
    end_reason          text        CHECK (end_reason IN ('logout', 'shift_end',
                                                          'idle_timeout', 'forced', 'expired'))
);

-- A device is claimed by at most one operator at a time.
CREATE UNIQUE INDEX IF NOT EXISTS device_session_active_idx
    ON device_session (device_id) WHERE ended_at IS NULL;

CREATE TABLE IF NOT EXISTS refresh_token (
    id                  uuid        PRIMARY KEY,
    user_id             uuid        NOT NULL REFERENCES app_user (id),
    device_id           uuid        REFERENCES device (id),
    token_hash          text        NOT NULL,
    expires_at          timestamptz NOT NULL,
    rotated_from        uuid        REFERENCES refresh_token (id),
    revoked_at          timestamptz,
    created_at          timestamptz NOT NULL
);

-- Marking a device lost revokes every token bound to it (H11).
CREATE INDEX IF NOT EXISTS refresh_token_device_active_idx
    ON refresh_token (device_id) WHERE revoked_at IS NULL;

-- --------------------------------------------------------------------
--  Authentication audit — deliberately separate from the stock ledger
-- --------------------------------------------------------------------

-- Append-only, like stock_movement: no updated_at, because a row here is
-- never updated and the column would be a standing invitation to violate that.
CREATE TABLE IF NOT EXISTS auth_event (
    id                  uuid        PRIMARY KEY,
    occurred_at         timestamptz NOT NULL,
    event_type          text        NOT NULL,               -- login, login_failed,
                                                            -- elevate_granted, role_granted, ...
    actor_user_id       uuid        REFERENCES app_user (id),
    target_user_id      uuid        REFERENCES app_user (id),
    device_id           uuid        REFERENCES device (id),
    ip_address          inet,
    outcome             text        NOT NULL CHECK (outcome IN ('success', 'failure')),
    detail              jsonb
);

CREATE INDEX IF NOT EXISTS auth_event_target_idx
    ON auth_event (target_user_id, occurred_at DESC);

COMMENT ON TABLE auth_event IS
    'Role assignment changes matter disproportionately here: "who granted '
    'this person adjustment-approval rights, and when" is among the first '
    'questions in any shrinkage investigation, and is unanswerable if role '
    'changes are applied without history.';
