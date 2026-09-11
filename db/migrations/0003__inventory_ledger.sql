-- =====================================================================
--  0003__inventory_ledger.sql
--  Duration: instant  (empty database; partition creation is metadata only)
--  Backward-compatible with: n/a — initial schema
--
--  The two most important tables in the system, plus the exception queue
--  and reconciliation records that keep them honest.
--
--  Stock levels are DERIVED from immutable movement records. There is no
--  `UPDATE inventory SET qty = qty - 5` anywhere in this system.
-- =====================================================================

-- --------------------------------------------------------------------
--  stock_movement — the append-only ledger
-- --------------------------------------------------------------------
--
--  Partitioned on recorded_at (the SERVER clock), never on device time.
--  A handheld with a wrong clock — minutes out, or years out after a
--  battery swap — must never be able to write into an unexpected
--  partition, or into one that does not exist (C4).
--
--  `sequence` is the authoritative ordering for reconciliation and
--  balance derivation. Note it carries no UNIQUE constraint: on a
--  partitioned table every unique constraint must include the partition
--  key, so a global UNIQUE(sequence) is not expressible. The shared
--  identity generator still yields distinct values across all partitions;
--  uniqueness is a property of the generator, not an enforced constraint.

CREATE TABLE IF NOT EXISTS stock_movement (
    id                      uuid            NOT NULL,
    sequence                bigint          GENERATED ALWAYS AS IDENTITY,
    recorded_at             timestamptz     NOT NULL,       -- server clock, authoritative
    device_reported_at      timestamptz,                    -- forensic only, never trusted
    business_date           date            NOT NULL,       -- from warehouse day boundary (M4)

    owner_id                uuid            NOT NULL REFERENCES owner (id),
    item_id                 uuid            NOT NULL REFERENCES item (id),
    lot_id                  uuid            NOT NULL REFERENCES lot (id),
    serial_unit_id          uuid            REFERENCES serial_unit (id),
    stock_status            text            NOT NULL
                                            CHECK (stock_status IN ('available', 'quarantine',
                                                                    'damaged', 'on_hold')),

    from_location_id        uuid            REFERENCES location (id),   -- NULL on receipt
    to_location_id          uuid            REFERENCES location (id),   -- NULL on dispatch
    handling_unit_id        uuid            REFERENCES handling_unit (id),

    quantity_base           numeric(18, 4)  NOT NULL,       -- signed, in item.base_uom
    entered_quantity        numeric(18, 4)  NOT NULL,       -- as the operator entered it
    entered_uom             text            NOT NULL,

    movement_type           text            NOT NULL
                                            CHECK (movement_type IN ('receipt', 'putaway', 'pick',
                                                                     'move', 'adjustment', 'count',
                                                                     'dispatch', 'return')),
    reason_code             text,
    reference_type          text,
    reference_id            uuid,

    actor_user_id           uuid            NOT NULL REFERENCES app_user (id),
    device_id               uuid            REFERENCES device (id),
    authorized_by_user_id   uuid            REFERENCES app_user (id),   -- supervisor override
    idempotency_key         text,

    PRIMARY KEY (id, recorded_at)
) PARTITION BY RANGE (recorded_at);

COMMENT ON TABLE stock_movement IS
    'Append-only. No UPDATE or DELETE grant is issued to the application '
    'roles (wired with the wms_api/wms_worker roles at deployment). This '
    'table IS the inventory audit trail (H2).';

COMMENT ON COLUMN stock_movement.lot_id IS
    'Never NULL. A position that is not lot-tracked points at the sentinel '
    'lot 00000000-0000-0000-0000-000000000000, because lot_id participates '
    'in the stock_balance primary key and NULL cannot (NULL <> NULL).';

COMMENT ON COLUMN stock_movement.authorized_by_user_id IS
    'The supervisor who authorised an exception, distinct from the '
    'actor_user_id who performed the movement. In-place elevation never '
    'changes the device session, so both identities are recorded.';

-- Deliberately NO foreign keys point AT stock_movement (M1): references to
-- it are by id without a constraint, because FKs to a partitioned table are
-- restrictive and would block partition detachment.

-- Monthly partitions, created twelve months ahead. A scheduled job extends
-- this window; this migration seeds the initial range so the first insert
-- has somewhere to land.
--
-- There is deliberately NO default partition. Attaching a proper partition
-- later requires moving any rows that landed in the default out of it first,
-- which is a well-known operational trap. Because partitioning is on the
-- server clock, rows always land in "now" — a missing partition means the
-- maintenance job has been failing for twelve months, which should surface
-- as a loud failure rather than be silently absorbed.
DO $$
DECLARE
    start_month date := date_trunc('month', now())::date;
    part_start  date;
    part_end    date;
    part_name   text;
BEGIN
    FOR i IN 0..12 LOOP
        part_start := start_month + (i || ' months')::interval;
        part_end   := start_month + ((i + 1) || ' months')::interval;
        part_name  := format('stock_movement_%s', to_char(part_start, 'YYYY_MM'));

        IF NOT EXISTS (SELECT 1 FROM pg_class WHERE relname = part_name) THEN
            EXECUTE format(
                'CREATE TABLE %I PARTITION OF stock_movement FOR VALUES FROM (%L) TO (%L)',
                part_name, part_start, part_end);
        END IF;
    END LOOP;
END $$;

-- Ordering and reconciliation read by sequence, never by timestamp.
CREATE INDEX IF NOT EXISTS stock_movement_sequence_idx
    ON stock_movement (sequence);

-- "Where did this item move" and the reconciliation watermark scan (H1).
CREATE INDEX IF NOT EXISTS stock_movement_position_idx
    ON stock_movement (item_id, to_location_id, lot_id, sequence);

-- "What happened against this receipt / task / order."
CREATE INDEX IF NOT EXISTS stock_movement_reference_idx
    ON stock_movement (reference_type, reference_id);

-- --------------------------------------------------------------------
--  stock_balance — the derived projection
-- --------------------------------------------------------------------
--
--  Grain is exactly the StockPosition aggregate boundary (C6), and nothing
--  coarser. There is deliberately no synchronously-maintained SKU-level or
--  warehouse-level total anywhere in this schema: that row would be touched
--  by every movement of that SKU across the entire building, and it is the
--  actual hot row (C1). Warehouse-wide availability is aggregated on demand.
--
--  on_hand and allocated share one row rather than living in two tables so
--  allocation and picking take a single lock instead of a read-modify-write
--  across two — a documented trade-off, not the default move.

CREATE TABLE IF NOT EXISTS stock_balance (
    owner_id            uuid            NOT NULL REFERENCES owner (id),
    item_id             uuid            NOT NULL REFERENCES item (id),
    location_id         uuid            NOT NULL REFERENCES location (id),
    lot_id              uuid            NOT NULL REFERENCES lot (id),
    stock_status        text            NOT NULL
                                        CHECK (stock_status IN ('available', 'quarantine',
                                                                'damaged', 'on_hold')),

    on_hand             numeric(18, 4)  NOT NULL DEFAULT 0,
    allocated           numeric(18, 4)  NOT NULL DEFAULT 0,
    version             bigint          NOT NULL DEFAULT 0,
    updated_at          timestamptz     NOT NULL,

    PRIMARY KEY (owner_id, item_id, location_id, lot_id, stock_status)
);

COMMENT ON TABLE stock_balance IS
    'DERIVED. Maintained only by the same transaction that appends to '
    'stock_movement, via a single atomic upsert — never a read-then-write. '
    'available is computed (on_hand - allocated), never stored.';

COMMENT ON COLUMN stock_balance.on_hand IS
    'MAY GO NEGATIVE, and there is deliberately no CHECK constraint '
    'forbidding it. An offline fact reporting work that physically happened '
    'can drive a balance below zero; that is true information — the '
    'system''s belief was wrong — and a constraint would hide exactly what '
    'the ledger exists to reveal (C3). The exception queue surfaces it.';

-- --------------------------------------------------------------------
--  inventory_exception — a work surface, not an error log
-- --------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS inventory_exception (
    id                      uuid            PRIMARY KEY,
    exception_type          text            NOT NULL
                                            CHECK (exception_type IN ('short_pick', 'over_receipt',
                                                                      'under_receipt', 'negative_balance',
                                                                      'reallocated_task', 'damaged',
                                                                      'location_mismatch')),
    severity                text            NOT NULL,
    warehouse_id            uuid            NOT NULL REFERENCES warehouse (id),
    owner_id                uuid            REFERENCES owner (id),
    item_id                 uuid            REFERENCES item (id),
    location_id             uuid            REFERENCES location (id),
    lot_id                  uuid            REFERENCES lot (id),
    expected_quantity       numeric(18, 4),
    actual_quantity         numeric(18, 4),
    stock_movement_id       uuid,                           -- by id, no FK (M1)
    task_id                 uuid,                           -- FK added in 0004 (tasks)
    raised_at               timestamptz     NOT NULL,
    raised_by_user_id       uuid            REFERENCES app_user (id),
    device_id               uuid            REFERENCES device (id),
    status                  text            NOT NULL DEFAULT 'open'
                                            CHECK (status IN ('open', 'investigating',
                                                              'resolved', 'written_off')),
    resolved_at             timestamptz,
    resolved_by_user_id     uuid            REFERENCES app_user (id),
    resolution_action       text,
    resolution_note         text
);

-- Drives the exception queue and the dashboard's ageing tile; open
-- exceptions are the hot read, resolved ones are history.
CREATE INDEX IF NOT EXISTS inventory_exception_open_idx
    ON inventory_exception (warehouse_id, raised_at)
    WHERE status IN ('open', 'investigating');

COMMENT ON TABLE inventory_exception IS
    'A first-class operational surface with an owner, an ageing alert, and '
    'a resolution workflow — not an error log. The approver of a resolution '
    'may not be the person who raised it (segregation of duties).';

-- --------------------------------------------------------------------
--  Reconciliation — the system's own correctness monitor (H1)
-- --------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS balance_reconciliation_run (
    id                  uuid        PRIMARY KEY,
    warehouse_id        uuid        NOT NULL REFERENCES warehouse (id),
    started_at          timestamptz NOT NULL,
    completed_at        timestamptz,
    from_sequence       bigint      NOT NULL,
    to_sequence         bigint      NOT NULL,
    rows_checked        int         NOT NULL DEFAULT 0,
    variance_count      int         NOT NULL DEFAULT 0,
    status              text        NOT NULL DEFAULT 'running'
                                    CHECK (status IN ('running', 'completed', 'failed'))
);

COMMENT ON TABLE balance_reconciliation_run IS
    'Watermark-based and incremental: each run recomputes only the positions '
    'with movements above last_reconciled_sequence, typically thousands of '
    'rows in seconds. A full recompute of every balance would be a multi-hour '
    'scan and would be switched off within a month of go-live, which removes '
    'the system''s own correctness monitor (H1).';

CREATE TABLE IF NOT EXISTS balance_variance (
    id                  uuid            PRIMARY KEY,
    run_id              uuid            NOT NULL REFERENCES balance_reconciliation_run (id),
    owner_id            uuid            NOT NULL REFERENCES owner (id),
    item_id             uuid            NOT NULL REFERENCES item (id),
    location_id         uuid            NOT NULL REFERENCES location (id),
    lot_id              uuid            NOT NULL REFERENCES lot (id),
    stock_status        text            NOT NULL,
    ledger_quantity     numeric(18, 4)  NOT NULL,
    balance_quantity    numeric(18, 4)  NOT NULL,
    difference          numeric(18, 4)  NOT NULL
);

COMMENT ON TABLE balance_variance IS
    'A variance is never auto-corrected: silent correction hides the bug '
    'that caused it. It raises an alert and is resolved deliberately.';
