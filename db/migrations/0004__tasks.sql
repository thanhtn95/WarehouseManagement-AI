-- =====================================================================
--  0004__tasks.sql
--  Duration: instant  (empty database)
--  Backward-compatible with: n/a — initial schema
--
--  The generic task engine. All directed work — putaway, pick, move,
--  count, replenish — shares one model, so adding a work type later costs
--  a strategy and a screen rather than a subsystem.
--
--  Phase 1A only ever creates 'putaway' tasks. The other task_type values
--  are present because the design fixes the full set (§2.4) and text+CHECK
--  makes widening free; nothing in 1A produces them.
-- =====================================================================

CREATE TABLE IF NOT EXISTS task (
    id                  uuid        PRIMARY KEY,
    warehouse_id        uuid        NOT NULL REFERENCES warehouse (id),
    zone_id             uuid        NOT NULL REFERENCES zone (id),
    task_type           text        NOT NULL
                                    CHECK (task_type IN ('putaway', 'pick', 'move',
                                                         'count', 'replenish')),
    status              text        NOT NULL DEFAULT 'ready'
                                    CHECK (status IN ('ready', 'leased', 'in_progress',
                                                      'completed', 'cancelled')),
    priority            int         NOT NULL DEFAULT 100,
    sort_sequence       int         NOT NULL,               -- min(pick_sequence) of its lines

    reference_type      text,
    reference_id        uuid,

    lease_device_id     uuid        REFERENCES device (id),
    lease_user_id       uuid        REFERENCES app_user (id),
    lease_expires_at    timestamptz,
    lease_heartbeat_at  timestamptz,
    lease_id            uuid,                               -- groups a batch leased together

    created_at          timestamptz NOT NULL,
    completed_at        timestamptz,
    version             bigint      NOT NULL DEFAULT 0
);

COMMENT ON COLUMN task.zone_id IS
    'The lease filter. A picker scoped to ambient zones is never offered a '
    'cold-chain task; scope is evaluated against user_role_scope.zone_ids '
    'on every lease.';

COMMENT ON COLUMN task.lease_expires_at IS
    'Deliberately long — it must survive a full shift offline. Expiry does '
    'NOT auto-release the task: a device out of wifi coverage is '
    'indistinguishable from a dead one at the server, and auto-releasing '
    'produces the duplicate pick the lease exists to prevent. Expired '
    'leases go to a supervisor review queue instead.';

COMMENT ON COLUMN task.lease_heartbeat_at IS
    'Last contact, distinct from lease_expires_at. "Last seen 45 minutes '
    'ago" is the field a supervisor actually decides a reclaim on.';

-- The lease query: ready tasks in my warehouse and zones, best first.
-- Partial, because leased and completed rows are the overwhelming majority
-- over time and none of them are candidates.
CREATE INDEX IF NOT EXISTS task_ready_idx
    ON task (warehouse_id, zone_id, priority, sort_sequence)
    WHERE status = 'ready';

-- The expiry sweep (every 5 minutes) scans only outstanding leases.
CREATE INDEX IF NOT EXISTS task_lease_expiry_idx
    ON task (lease_expires_at)
    WHERE status = 'leased';

CREATE TABLE IF NOT EXISTS task_line (
    id                  uuid            PRIMARY KEY,
    task_id             uuid            NOT NULL REFERENCES task (id),
    line_no             int             NOT NULL,
    owner_id            uuid            NOT NULL REFERENCES owner (id),
    item_id             uuid            NOT NULL REFERENCES item (id),
    lot_id              uuid            NOT NULL REFERENCES lot (id),
    from_location_id    uuid            REFERENCES location (id),
    to_location_id      uuid            REFERENCES location (id),
    handling_unit_id    uuid            REFERENCES handling_unit (id),
    requested_quantity  numeric(18, 4)  NOT NULL,
    confirmed_quantity  numeric(18, 4),
    uom_code            text            NOT NULL,
    status              text            NOT NULL DEFAULT 'pending'
                                        CHECK (status IN ('pending', 'confirmed',
                                                          'short', 'skipped')),
    UNIQUE (task_id, line_no)
);

COMMENT ON COLUMN task_line.confirmed_quantity IS
    'What the operator actually confirmed, which may differ from '
    'requested_quantity. A difference is a physical fact, not an error: it '
    'is recorded and raises an inventory_exception rather than being '
    'refused (§7.2).';

CREATE INDEX IF NOT EXISTS task_line_task_idx ON task_line (task_id);

-- --------------------------------------------------------------------
--  Close the forward reference left open by 0003
-- --------------------------------------------------------------------
--  inventory_exception.task_id was created without a foreign key because
--  `task` did not exist yet (tracked in docs/shortcuts.md). It does now.

ALTER TABLE inventory_exception
    DROP CONSTRAINT IF EXISTS inventory_exception_task_id_fkey;

ALTER TABLE inventory_exception
    ADD CONSTRAINT inventory_exception_task_id_fkey
    FOREIGN KEY (task_id) REFERENCES task (id);
