-- =====================================================================
--  0001__master_data.sql
--  Duration: instant  (empty database)
--  Backward-compatible with: n/a — first migration
--
--  Sites, storage topology, stock ownership, and the item master.
--  These come first because the ledger (0003) carries foreign keys into
--  nearly all of them.
--
--  `id` columns carry no DEFAULT: UUIDv7 is generated application-side
--  (database-design skill), which keeps the schema independent of the
--  server's PostgreSQL major version and lets a client mint an id before
--  the row exists.
-- =====================================================================

-- --------------------------------------------------------------------
--  Sites and storage topology
-- --------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS warehouse (
    id                  uuid        PRIMARY KEY,
    code                text        NOT NULL UNIQUE,
    name                text        NOT NULL,
    timezone            text        NOT NULL,               -- IANA, e.g. Asia/Tokyo
    day_boundary_time   time        NOT NULL DEFAULT '00:00',
    default_locale      text        NOT NULL DEFAULT 'en',
    status              text        NOT NULL DEFAULT 'active'
                                    CHECK (status IN ('active', 'inactive')),
    version             bigint      NOT NULL DEFAULT 0,
    created_at          timestamptz NOT NULL,
    updated_at          timestamptz NOT NULL
);

COMMENT ON COLUMN warehouse.day_boundary_time IS
    'Derives business_date on every movement (M4). Changing it affects only '
    'movements recorded after the change; it never rewrites history.';

CREATE TABLE IF NOT EXISTS zone (
    id                  uuid        PRIMARY KEY,
    warehouse_id        uuid        NOT NULL REFERENCES warehouse (id),
    code                text        NOT NULL,
    name_i18n           jsonb       NOT NULL,
    zone_type           text        NOT NULL
                                    CHECK (zone_type IN ('receiving', 'bulk', 'pick',
                                                         'staging', 'quarantine',
                                                         'returns', 'damage')),
    temperature_class   text,
    status              text        NOT NULL DEFAULT 'active'
                                    CHECK (status IN ('active', 'retired')),
    version             bigint      NOT NULL DEFAULT 0,
    created_at          timestamptz NOT NULL,
    updated_at          timestamptz NOT NULL,
    UNIQUE (warehouse_id, code)
);

CREATE TABLE IF NOT EXISTS location (
    id                  uuid        PRIMARY KEY,
    warehouse_id        uuid        NOT NULL REFERENCES warehouse (id),
    zone_id             uuid        NOT NULL REFERENCES zone (id),
    code                text        NOT NULL,               -- "A-12-03-2"
    location_type       text        NOT NULL
                                    CHECK (location_type IN ('bin', 'staging',
                                                             'dock', 'virtual')),
    aisle               text,
    bay                 text,
    level               text,
    pick_sequence       int         NOT NULL,               -- physical walk order
    max_weight_g        bigint,
    max_volume_cm3      bigint,
    allows_mixed_item   boolean     NOT NULL DEFAULT true,
    allows_mixed_lot    boolean     NOT NULL DEFAULT true,
    status              text        NOT NULL DEFAULT 'active'
                                    CHECK (status IN ('active', 'blocked', 'retired')),
    version             bigint      NOT NULL DEFAULT 0,
    created_at          timestamptz NOT NULL,
    updated_at          timestamptz NOT NULL,
    UNIQUE (warehouse_id, code)
);

COMMENT ON COLUMN location.aisle IS
    'Flat addressing columns, not an entity hierarchy: no business rule '
    'attaches to an aisle, and a six-level hierarchy would add five joins '
    'to every location read. Zone is a real entity because roles scope to it.';

-- --------------------------------------------------------------------
--  Stock ownership (D4a) — consignment / vendor-managed inventory
-- --------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS owner (
    id                  uuid        PRIMARY KEY,
    code                text        NOT NULL UNIQUE,
    name                text        NOT NULL,
    is_default          boolean     NOT NULL DEFAULT false,
    created_at          timestamptz NOT NULL,
    updated_at          timestamptz NOT NULL
);

-- Exactly one default owner per deployment.
CREATE UNIQUE INDEX IF NOT EXISTS owner_default_idx
    ON owner (is_default) WHERE is_default;

-- --------------------------------------------------------------------
--  Item master and unit-of-measure hierarchy (D5a: each/inner/case/pallet)
-- --------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS item (
    id                  uuid        PRIMARY KEY,
    sku_code            text        NOT NULL UNIQUE,
    name_i18n           jsonb       NOT NULL,
    base_uom            text        NOT NULL,
    is_lot_tracked      boolean     NOT NULL DEFAULT false,
    is_expiry_tracked   boolean     NOT NULL DEFAULT false,
    is_serial_tracked   boolean     NOT NULL DEFAULT false,
    shelf_life_days     int,
    abc_class           char(1),
    storage_class       text,
    status              text        NOT NULL DEFAULT 'active'
                                    CHECK (status IN ('active', 'blocked', 'discontinued')),
    version             bigint      NOT NULL DEFAULT 0,
    created_at          timestamptz NOT NULL,
    updated_at          timestamptz NOT NULL
);

COMMENT ON COLUMN item.base_uom IS
    'IMMUTABLE once any movement exists for the item — changing it silently '
    'reinterprets every historical quantity in the ledger. Enforced in the '
    'domain layer (PATCH /items returns 409 immutable-field), not by a '
    'constraint, because the check needs a movement-count lookup.';

CREATE TABLE IF NOT EXISTS item_uom (
    id                      uuid            PRIMARY KEY,
    item_id                 uuid            NOT NULL REFERENCES item (id),
    uom_code                text            NOT NULL,       -- EACH / INNER / CASE / PALLET
    qty_in_base             numeric(18, 4)  NOT NULL,
    length_mm               int,
    width_mm                int,
    height_mm               int,
    gross_weight_g          bigint,
    is_receiving_default    boolean         NOT NULL DEFAULT false,
    is_picking_default      boolean         NOT NULL DEFAULT false,
    is_discrete             boolean         NOT NULL DEFAULT true,
    created_at              timestamptz     NOT NULL,
    updated_at              timestamptz     NOT NULL,
    UNIQUE (item_id, uom_code)
);

COMMENT ON COLUMN item_uom.is_discrete IS
    'Rejects fractional quantities on a countable unit. A weight-tracked '
    'item sets this false; eaches never do.';

CREATE TABLE IF NOT EXISTS item_barcode (
    id                  uuid        PRIMARY KEY,
    item_id             uuid        NOT NULL REFERENCES item (id),
    item_uom_id         uuid        NOT NULL REFERENCES item_uom (id),
    barcode             text        NOT NULL UNIQUE,        -- global: a scan resolves to one row
    barcode_type        text,
    created_at          timestamptz NOT NULL,
    updated_at          timestamptz NOT NULL
);

-- --------------------------------------------------------------------
--  Lot / batch (D3) and the "no lot" sentinel
-- --------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS lot (
    id                  uuid        PRIMARY KEY,
    item_id             uuid        REFERENCES item (id),
    lot_code            text        NOT NULL,
    expiry_date         date,
    manufactured_date   date,
    status              text        NOT NULL DEFAULT 'available'
                                    CHECK (status IN ('available', 'quarantine',
                                                      'expired', 'blocked')),
    created_at          timestamptz NOT NULL,
    updated_at          timestamptz NOT NULL,
    UNIQUE (item_id, lot_code)
);

-- item_id is NOT NULL for every real lot. Exactly one row is exempt: the
-- "no lot" sentinel, which belongs to no item because it represents the
-- absence of one — the way a date dimension carries a single "unknown
-- date" member.
--
-- Enforced structurally rather than by a CHECK against a hard-coded id:
-- a unique index on a constant, restricted to item-less rows, permits
-- exactly one of them to exist. Nothing here needs to know the sentinel's
-- literal UUID for the invariant to hold.
CREATE UNIQUE INDEX IF NOT EXISTS lot_sentinel_singleton_idx
    ON lot ((1)) WHERE item_id IS NULL;

-- The "no lot" sentinel (design doc §2.3). stock_balance.lot_id is part of
-- the primary key, and NULL cannot participate in a key the way we need
-- (NULL <> NULL), so an untracked position points here instead.
INSERT INTO lot (id, item_id, lot_code, status, created_at, updated_at)
VALUES ('00000000-0000-0000-0000-000000000000'::uuid,
        NULL, '(no lot)', 'available', now(), now())
ON CONFLICT (id) DO NOTHING;

-- --------------------------------------------------------------------
--  Handling units (LPN) and serialised stock
-- --------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS handling_unit (
    id                  uuid        PRIMARY KEY,
    lpn                 text        NOT NULL UNIQUE,
    hu_type             text        NOT NULL,               -- pallet / cage / tote
    parent_hu_id        uuid        REFERENCES handling_unit (id),
    current_location_id uuid        REFERENCES location (id),
    status              text        NOT NULL DEFAULT 'active'
                                    CHECK (status IN ('active', 'retired', 'lost')),
    gross_weight_g      bigint,
    created_at          timestamptz NOT NULL,
    updated_at          timestamptz NOT NULL,
    CONSTRAINT handling_unit_no_self_nesting CHECK (parent_hu_id IS NULL OR parent_hu_id <> id)
);

-- Schema now, feature in Phase 3 (H9): serialised stock is a separate
-- aggregate rather than quantity-ledger rows of 1, so "where is unit X now"
-- is a lookup instead of a scan of movement history.
CREATE TABLE IF NOT EXISTS serial_unit (
    id                          uuid        PRIMARY KEY,
    item_id                     uuid        NOT NULL REFERENCES item (id),
    serial_number               text        NOT NULL,
    lot_id                      uuid        REFERENCES lot (id),
    owner_id                    uuid        REFERENCES owner (id),
    current_location_id         uuid        REFERENCES location (id),
    current_handling_unit_id    uuid        REFERENCES handling_unit (id),
    status                      text        NOT NULL DEFAULT 'expected'
                                            CHECK (status IN ('expected', 'on_hand', 'allocated',
                                                              'shipped', 'returned', 'scrapped')),
    version                     bigint      NOT NULL DEFAULT 0,
    created_at                  timestamptz NOT NULL,
    updated_at                  timestamptz NOT NULL,
    UNIQUE (item_id, serial_number)
);
