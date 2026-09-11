-- =====================================================================
--  0006__platform.sql
--  Duration: instant  (empty database)
--  Backward-compatible with: n/a — initial schema
--
--  Cross-cutting infrastructure: the outbox that carries every
--  cross-aggregate effect, the idempotency ledger that makes retries safe,
--  the reason-code catalogue, and per-day document numbering.
--
--  DEFERRED, deliberately: `configuration` and `app_setting_current`.
--  The design's forward-compatibility rule exists because retrofitting
--  lot/serial/owner would mean migrating the two largest tables in the
--  system (§0). Configuration has no such property — adding it in Phase 3
--  when the config UI lands is a plain CREATE TABLE against an empty
--  referent, so creating it now would be scaffolding for a phase we are
--  not in.
-- =====================================================================

-- --------------------------------------------------------------------
--  Outbox — the only sanctioned cross-aggregate channel
-- --------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS outbox_message (
    id                  uuid        PRIMARY KEY,
    aggregate_type      text        NOT NULL,
    aggregate_id        uuid        NOT NULL,
    message_type        text        NOT NULL,
    payload             jsonb       NOT NULL,
    created_at          timestamptz NOT NULL,
    processed_at        timestamptz,
    attempts            int         NOT NULL DEFAULT 0,
    last_error          text
);

COMMENT ON TABLE outbox_message IS
    'Written in the same transaction as the mutation that produced it, so '
    'a cross-module effect can never be lost or applied without its cause. '
    'At-least-once delivery: every consumer must be idempotent.';

-- The drain query. Partial, because processed rows accumulate indefinitely
-- and are never candidates.
CREATE INDEX IF NOT EXISTS outbox_pending_idx
    ON outbox_message (created_at)
    WHERE processed_at IS NULL;

-- --------------------------------------------------------------------
--  Idempotency (H2) — what makes an offline retry safe
-- --------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS idempotency_record (
    key                 text        NOT NULL,
    user_id             uuid        NOT NULL REFERENCES app_user (id),
    request_hash        text        NOT NULL,
    response_status     int         NOT NULL,
    response_body       jsonb,
    created_at          timestamptz NOT NULL,
    PRIMARY KEY (key, user_id)
);

COMMENT ON TABLE idempotency_record IS
    'Inserted as the FIRST statement of the transaction. A unique violation '
    'means this is a replay: return the stored response. A matching key '
    'with a different request_hash is a client bug and returns 409 rather '
    'than silently accepting either version. Scoped by user_id so one '
    'client can never replay another''s key. Retained 7 days — comfortably '
    'longer than the maximum offline window — by a scheduled job.';

-- Supports the 7-day expiry sweep.
CREATE INDEX IF NOT EXISTS idempotency_record_created_idx
    ON idempotency_record (created_at);

-- --------------------------------------------------------------------
--  Reason codes — why a discrepancy happened, in the operator's words
-- --------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS reason_code (
    code                text        PRIMARY KEY,
    category            text        NOT NULL
                                    CHECK (category IN ('receipt_discrepancy', 'pick_discrepancy',
                                                        'damage', 'count_variance',
                                                        'admin_correction', 'disposition', 'expiry')),
    label_i18n          jsonb       NOT NULL,
    applies_to          text[]      NOT NULL,               -- movement_types this code is valid for
    requires_note       boolean     NOT NULL DEFAULT false,
    requires_photo      boolean     NOT NULL DEFAULT false,
    requires_approval   boolean     NOT NULL DEFAULT false,
    is_shrinkage        boolean     NOT NULL DEFAULT false,
    is_active           boolean     NOT NULL DEFAULT true,
    sort_order          int         NOT NULL DEFAULT 100
);

COMMENT ON COLUMN reason_code.is_shrinkage IS
    'Separates loss the business bears from surplus. Receipt discrepancies '
    'are NOT shrinkage — that loss sits with the supplier and is '
    'recoverable through a claim; marking it shrinkage inflates apparent '
    'internal loss and points investigations in the wrong direction. Count '
    'overage is not shrinkage either, and is never netted against shortage.';

COMMENT ON COLUMN reason_code.applies_to IS
    'Filters what a handheld offers for the movement in progress, so a pick '
    'discrepancy screen shows six options rather than the whole catalogue.';

-- --------------------------------------------------------------------
--  Document numbering — per warehouse, per type, per business day
-- --------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS document_sequence (
    warehouse_id        uuid        NOT NULL REFERENCES warehouse (id),
    document_type       text        NOT NULL,               -- receipt, order, shipment, ...
    business_date       date        NOT NULL,
    last_number         int         NOT NULL DEFAULT 0,
    PRIMARY KEY (warehouse_id, document_type, business_date)
);

COMMENT ON TABLE document_sequence IS
    'Allocated with UPDATE ... RETURNING, which is concurrency-safe without '
    'an explicit lock. Gap-tolerant by default: gapless numbering requires '
    'serialising allocation across the transaction boundary and forbids '
    'rollback gaps, so where an auditor demands it, it is a per-deployment '
    'configuration with a stated throughput cost.';

-- --------------------------------------------------------------------
--  Close the forward reference left open by 0003
-- --------------------------------------------------------------------
--  stock_movement.reason_code was created without a foreign key because
--  `reason_code` did not exist yet (tracked in docs/shortcuts.md). A
--  foreign key FROM a partitioned table is supported and cascades to every
--  partition, including ones created later by the maintenance job.

ALTER TABLE stock_movement
    DROP CONSTRAINT IF EXISTS stock_movement_reason_code_fkey;

ALTER TABLE stock_movement
    ADD CONSTRAINT stock_movement_reason_code_fkey
    FOREIGN KEY (reason_code) REFERENCES reason_code (code);
