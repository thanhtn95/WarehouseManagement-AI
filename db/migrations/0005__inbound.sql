-- =====================================================================
--  0005__inbound.sql
--  Duration: instant  (empty database)
--  Backward-compatible with: n/a — initial schema
--
--  Receiving. Phase 1A ships blind receipt only; 'against_asn' and
--  'return' are in the CHECK because the design fixes the set (§2.5) and
--  no 1A code path produces them (ASN is Phase 3, returns are Phase 3).
--
--  The quantities here are the reason this module exists: expected is what
--  the paperwork claimed, received is what was physically counted, and the
--  two disagreeing is a supervisor's problem surfaced through the
--  exception queue — never an error returned to the device.
-- =====================================================================

CREATE TABLE IF NOT EXISTS receipt (
    id                  uuid        PRIMARY KEY,
    warehouse_id        uuid        NOT NULL REFERENCES warehouse (id),
    receipt_number      text        NOT NULL UNIQUE,
    receipt_type        text        NOT NULL
                                    CHECK (receipt_type IN ('blind', 'against_asn', 'return')),
    supplier_reference  text,
    owner_id            uuid        NOT NULL REFERENCES owner (id),
    status              text        NOT NULL DEFAULT 'draft'
                                    CHECK (status IN ('draft', 'in_progress', 'received',
                                                      'putaway', 'closed', 'cancelled')),
    expected_at         timestamptz,
    started_at          timestamptz,
    completed_at        timestamptz,
    created_by          uuid        NOT NULL REFERENCES app_user (id),
    version             bigint      NOT NULL DEFAULT 0,
    created_at          timestamptz NOT NULL,
    updated_at          timestamptz NOT NULL
);

COMMENT ON COLUMN receipt.status IS
    'in_progress -> received is a deliberate operator action via '
    'POST /receipts/{id}/complete, never automatic on the last line being '
    'touched (M8). A short shipment must not silently read as complete '
    'just because every line was counted once.';

CREATE INDEX IF NOT EXISTS receipt_open_idx
    ON receipt (warehouse_id, expected_at)
    WHERE status IN ('draft', 'in_progress');

CREATE TABLE IF NOT EXISTS receipt_line (
    id                  uuid            PRIMARY KEY,
    receipt_id          uuid            NOT NULL REFERENCES receipt (id),
    line_no             int             NOT NULL,
    item_id             uuid            NOT NULL REFERENCES item (id),
    owner_id            uuid            NOT NULL REFERENCES owner (id),
    expected_quantity   numeric(18, 4),                     -- NULL for a blind receipt
    received_quantity   numeric(18, 4),
    uom_code            text            NOT NULL,
    lot_code            text,
    expiry_date         date,
    discrepancy_type    text            CHECK (discrepancy_type IN ('over', 'under',
                                                                   'damaged', 'none')),
    status              text            NOT NULL DEFAULT 'pending'
                                        CHECK (status IN ('pending', 'confirmed', 'cancelled')),
    UNIQUE (receipt_id, line_no)
);

COMMENT ON COLUMN receipt_line.expected_quantity IS
    'NULL on a blind receipt, where nothing was claimed in advance. A NULL '
    'here means "no expectation", which is different from an expectation '
    'of zero — the discrepancy calculation must not conflate them.';

CREATE INDEX IF NOT EXISTS receipt_line_receipt_idx ON receipt_line (receipt_id);
