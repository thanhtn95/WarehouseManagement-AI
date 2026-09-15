# Warehouse Management System — Detailed Design

**Scope:** All phases — 1A, 1B, 2, 3, 4
**Status:** Draft v0.2 — for review
**Companion documents:** `wms-project-proposal.md`, `wms-architecture-review.md`

---

## 0. Scope and Reading Notes

This document specifies the system across all four delivery phases.

**Confidence is not uniform, and is marked.** Phases 1A, 1B and 2 are specified to build depth — schema, endpoints, and flows are intended to be implemented as written. Phases 3 and 4 fix the **contracts and schema shape** so that earlier phases build toward them correctly, but their internal detail will move as real customers reveal what they need. Where a Phase 3 or 4 decision is genuinely open, it says so rather than inventing certainty.

The **schema is designed forward-compatible throughout**. Lot, expiry, serial, owner, and handling-unit structures exist from the first migration even where the features using them arrive in Phase 3, because adding them afterwards means migrating the two largest tables in the system.

Design decisions carried in from the review documents are referenced by their finding IDs (C1–C7, H1–H10, M1–M5) rather than re-argued here.

**Document layout:**

| Section | Covers | Phases |
|---|---|---|
| 1–2 | Architecture and data models | 1A / 1B |
| 3 | API conventions, including the command/fact rule | All |
| 4 | Fact registry — every physical event the system accepts | All |
| 5 | Full API reference — request and response bodies for every endpoint | All |
| 6 | Data flows, 22 of them, with transaction boundaries marked | All |
| 7 | Error catalogue, and what never returns an error | All |
| 8 | Non-functional requirements | 1A / 1B baseline |
| 9 | **Phase 2** — operational core | 2 |
| 10 | **Phase 3** — operational maturity | 3 |
| 11 | **Phase 4** — commercial readiness | 4 |
| 12 | How the non-functional picture evolves | All |

Sections 3–7 are the **API and flow reference for every phase**, consolidated so there is one place to look rather than a specification fragmented across phase sections.

**Conventions used throughout:**

| Convention | Value |
|---|---|
| Identifiers | UUID v7 (time-ordered, index-friendly) |
| Money / quantity | `numeric(18,4)` — never floating point |
| Timestamps | `timestamptz`, stored UTC |
| Naming | `snake_case`, singular table names |
| Soft delete | Not used. Status columns and history tables instead |
| Enumerations | PostgreSQL `text` with `CHECK` constraints, not native enums (alterable without table rewrite) |

---

## 1. High-Level Architecture

### 1.1 Component overview

```
                    ┌──────────────────────────────────────────┐
                    │  React SPA  (one build, two route trees)  │
                    │                                          │
                    │  /admin/*        │  /operator/*          │
                    │  dense tables    │  one task per screen  │
                    │  dashboards      │  scanner-driven       │
                    │  reports         │  service worker +     │
                    │                  │  IndexedDB queue      │
                    └────────┬─────────────────────┬───────────┘
                             │ HTTPS               │ HTTPS
                    ┌────────▼─────────────────────▼───────────┐
                    │  Caddy — TLS, routing, static, cache      │
                    └────────┬─────────────────────────────────┘
                             │
              ┌──────────────▼───────────────┐   ┌────────────────────────┐
              │  API  (ASP.NET Core, ×2)     │   │  Worker (×N)           │
              │  ─────────────────────────    │   │  ────────────────────  │
              │  Identity · Catalog          │   │  Allocation consumer   │
              │  Inventory · Tasks           │   │   (1 per warehouse,    │
              │  Inbound   · Outbound        │   │    advisory-locked)    │
              │  Sync (facts)                │   │  Outbox drain          │
              │  statement_timeout 5s        │   │  Reconciliation        │
              │  reserved connection pool    │   │  statement_timeout 300s│
              └──────────────┬───────────────┘   └───────────┬────────────┘
                             │                               │
                    ┌────────▼───────────────────────────────▼───────┐
                    │  PgBouncer — transaction mode, per-role pools   │
                    └────────┬───────────────────────────────────────┘
                             │
        ┌────────────────────▼──────────┐      ┌──────────────────────────┐
        │  PostgreSQL primary            │─────▶│  Streaming replica       │
        │  partitioned ledger, balances  │      │  reports · backups · DR  │
        └────────────────────────────────┘      └──────────────────────────┘
```

### 1.2 Frontend

One React application, one build, one deployment, served from one origin. Two route trees with different layout shells (D15).

**Built with Vite + TypeScript.** A client-side SPA bundler fits this shape — service worker, IndexedDB fact queue, PWA install — better than a server-rendered framework, and the build output is a static bundle Caddy serves behind the one origin (§1.1). Directory layout:

```
web/
  src/routes/admin/     dense tables, dashboards, reports
  src/routes/operator/  single-task screens, service worker, IndexedDB queue
  src/shared/           generated API client, i18n catalogue, design tokens, auth context
```

| | `/admin/*` | `/operator/*` |
|---|---|---|
| Layout shell | Sidebar, data grids, filters | Single-task, full-bleed, large targets |
| State | TanStack Query, server-authoritative | TanStack Query + IndexedDB fact queue |
| Offline | None | Service worker, background sync |
| Auth | Staff session, browser storage | Device-bound session |
| Delivery | Standard SPA | Installable PWA, `display: standalone` |

**Shared:** generated TypeScript API client, i18n catalogue (English/Japanese), design tokens, auth context, permission gate component.

**Scan-input abstraction.** All barcode input flows through one `useScanner()` hook regardless of source:

- **Keyboard wedge** (Bluetooth scanners, Zebra DataWedge) — detected by keystroke burst timing: rapid characters terminated by `Enter`, distinguished from human typing by inter-key interval under ~30ms
- **Camera** — `BarcodeDetector` where available, ZXing-WASM fallback
- **Manual entry** — always available, always audited as manual

The hook emits `{ raw, symbology, source }`. No screen knows which device produced the scan, which is what makes the D10 hardware decision reversible.

### 1.3 Backend

Modular monolith, one solution, two deployed processes.

**Modules** (enforced by NetArchTest in CI): `Identity`, `Catalog`, `Inventory`, `Tasks`, `Inbound`, `Outbound`, `Platform`.

Dependency rule: modules depend only on `SharedKernel` and on other modules' `Contracts` projects. No module references another's internals. Cross-module effects propagate through the outbox, with the exceptions detailed in §4.3 (fact confirmation and allocation).

**API process** — HTTP endpoints, authentication, authorisation, request validation, and the transactional write path. Runs on `wms_api` database role with a 5-second statement timeout and a reserved connection pool that other workloads cannot starve (C7).

**Worker process** — three distinct responsibilities, deliberately separable later:

1. **Allocation consumer.** Exactly one active per warehouse, enforced by `pg_advisory_lock(hashtext('alloc:' || warehouse_id))`. Multiple worker instances may run; the lock decides which handles which warehouse. This is the serialisation point that makes overselling impossible (C2).
2. **Outbox drain.** Polls `outbox_message` with `SKIP LOCKED`, publishes, marks processed.
3. **Scheduled jobs.** Balance reconciliation, lease expiry sweep, idempotency record expiry, session cleanup.
4. **Role-permission-change propagation.** Consumes `RolePermissionsChanged` outbox messages and bumps `security_stamp` for every `app_user` reachable through `user_role_scope` for that role, one small transaction per user — the outbox pattern applied to H12, not a new exception to the one-aggregate-per-transaction rule (C6).

### 1.4 Data layer

- **EF Core** for aggregate writes — change tracking and unit of work suit domain mutations
- **Dapper** for hot read paths — pick lists, barcode lookup, dashboards
- **DbUp** for migrations — plain versioned SQL files, because the schema needs declarative partitioning, partial indexes, and `SKIP LOCKED` semantics that EF migrations obstruct
- **Reporting** reads the streaming replica through a separate context that has no write methods and no grant on the primary (H6, C7)

---

## 2. Data Models

### 2.1 Identity and access

```sql
app_user
  id                uuid PK
  user_type         text NOT NULL CHECK (user_type IN ('staff','operator'))
  display_name      text NOT NULL
  employee_code     text UNIQUE            -- badge / payroll identifier
  email             text UNIQUE            -- staff only
  locale            text NOT NULL DEFAULT 'en'
  status            text NOT NULL CHECK (status IN ('active','suspended','ended'))
  security_stamp    uuid NOT NULL          -- bump invalidates all tokens
  valid_from        date NOT NULL
  valid_until       date                   -- NULL = open-ended; set for agency labour
  created_at        timestamptz NOT NULL
  updated_at        timestamptz NOT NULL

credential
  id                uuid PK
  user_id           uuid FK -> app_user
  credential_type   text CHECK (credential_type IN ('password','pin','badge'))
  secret_hash       text NOT NULL          -- Argon2id
  must_change       boolean NOT NULL DEFAULT false  -- password only; true from
                                           -- POST /users until the holder
                                           -- completes POST /auth/credential-change
  failed_attempts   int  NOT NULL DEFAULT 0
  locked_until      timestamptz
  last_used_at      timestamptz
  created_at        timestamptz NOT NULL   -- when this credential was set; rotation
                                           -- is an auth_event (credential_reset), not
                                           -- a second timestamp on this row
  UNIQUE (user_id, credential_type)

role
  id                uuid PK
  code              text UNIQUE            -- PICKER, SUPERVISOR, ...
  name_i18n         jsonb NOT NULL         -- {"en": "...", "ja": "..."}
  is_system         boolean NOT NULL       -- system roles are not deletable
                                            -- or editable; seeded, permissions fixed
  is_active         boolean NOT NULL DEFAULT true  -- custom roles only; deactivated,
                                            -- never deleted — user_role_scope may
                                            -- still reference it historically
  version           bigint NOT NULL DEFAULT 0

permission
  code              text PK                -- receipt.over_receive
  category          text NOT NULL
  description_i18n  jsonb NOT NULL

role_permission
  role_id           uuid FK -> role
  permission_code   text FK -> permission
  PRIMARY KEY (role_id, permission_code)

user_role_scope
  id                uuid PK
  user_id           uuid FK -> app_user
  role_id           uuid FK -> role
  warehouse_id      uuid FK -> warehouse
  zone_ids          uuid[] NOT NULL DEFAULT '{}'   -- empty = all zones in warehouse
  granted_by        uuid FK -> app_user
  granted_at        timestamptz NOT NULL
  UNIQUE (user_id, role_id, warehouse_id)

device
  id                uuid PK
  label             text NOT NULL          -- "HH-014", printed on the unit
  warehouse_id      uuid FK -> warehouse
  status            text CHECK (status IN ('active','retired','lost'))
  platform          text                   -- android/13, chrome/121
  app_version       text
  last_seen_at      timestamptz
  last_queue_depth  int                     -- from the most recent heartbeat;
                                            -- makes sync backlog queryable from
                                            -- the admin API, not metrics-only

device_session
  id                uuid PK
  device_id         uuid FK -> device
  user_id           uuid FK -> app_user
  started_at        timestamptz NOT NULL
  ended_at          timestamptz
  end_reason        text CHECK (end_reason IN ('logout','shift_end','idle_timeout','forced','expired'))

refresh_token
  id                uuid PK
  user_id           uuid FK
  device_id         uuid FK
  token_hash        text NOT NULL
  expires_at        timestamptz NOT NULL
  rotated_from      uuid                   -- reuse detection
  revoked_at        timestamptz

auth_event
  id                uuid PK
  occurred_at       timestamptz NOT NULL
  event_type        text NOT NULL          -- login, login_failed, elevate_granted,
                                           -- role_granted, credential_reset, ...
  actor_user_id     uuid
  target_user_id    uuid
  device_id         uuid
  ip_address        inet
  outcome           text CHECK (outcome IN ('success','failure'))
  detail            jsonb
```

**Note on `zone_ids` as an array.** Zone scoping is read on every task lease and never joined against. An array column avoids a join table on the hottest authorisation path; a GIN index supports containment queries. If zone-level reporting is ever needed, that reads from the replica.

### 2.2 Master data

```sql
warehouse
  id, code UNIQUE, name, timezone text NOT NULL,
  day_boundary_time time NOT NULL DEFAULT '00:00',   -- business_date derivation (M4)
  default_locale text,
  status text CHECK (status IN ('active','inactive')) NOT NULL DEFAULT 'active',
  version bigint NOT NULL DEFAULT 0                  -- optimistic concurrency, §5.11

zone
  id, warehouse_id FK, code, name_i18n jsonb,
  zone_type text CHECK (zone_type IN ('receiving','bulk','pick','staging',
                                      'quarantine','returns','damage')),
  temperature_class text,
  status text CHECK (status IN ('active','retired')) NOT NULL DEFAULT 'active',
  version bigint NOT NULL DEFAULT 0
  UNIQUE (warehouse_id, code)

location
  id                uuid PK
  warehouse_id      uuid FK
  zone_id           uuid FK
  code              text NOT NULL          -- "A-12-03-2"
  location_type     text CHECK (location_type IN ('bin','staging','dock','virtual'))
  aisle, bay, level text                   -- flat columns, not a table hierarchy
  pick_sequence     int NOT NULL           -- physical walk order; drives routing
  max_weight_g      bigint
  max_volume_cm3    bigint
  allows_mixed_item boolean NOT NULL DEFAULT true
  allows_mixed_lot  boolean NOT NULL DEFAULT true
  status            text CHECK (status IN ('active','blocked','retired'))
  version           bigint NOT NULL DEFAULT 0         -- optimistic concurrency, §5.11
  UNIQUE (warehouse_id, code)
```

**Why aisle/bay/level are columns, not tables.** A six-level entity hierarchy adds five joins to every location read and buys nothing: no business rule attaches to an aisle. `zone` is a real entity because roles scope to it and rules differ by it. Everything below zone is addressing.

```sql
owner
  id, code UNIQUE, name, is_default boolean
  -- one row seeded per deployment; supports consignment / VMI (D4a)

item
  id                uuid PK
  sku_code          text UNIQUE
  name_i18n         jsonb NOT NULL
  base_uom          text NOT NULL          -- the unit quantities are stored in
  is_lot_tracked    boolean NOT NULL
  is_expiry_tracked boolean NOT NULL
  is_serial_tracked boolean NOT NULL       -- immutable once stock exists
  -- base_uom is likewise IMMUTABLE once any movement exists for the item:
  -- changing it silently reinterprets every historical quantity in the ledger.
  shelf_life_days   int
  abc_class         char(1)
  storage_class     text
  status            text CHECK (status IN ('active','blocked','discontinued'))
  created_at, updated_at

item_uom
  id, item_id FK, uom_code text,           -- EACH / INNER / CASE / PALLET
  qty_in_base       numeric(18,4) NOT NULL,
  length_mm, width_mm, height_mm int,
  gross_weight_g    bigint,
  is_receiving_default, is_picking_default boolean
  is_discrete       boolean NOT NULL DEFAULT true   -- rejects fractional quantities
  UNIQUE (item_id, uom_code)

item_barcode
  id, item_id FK, item_uom_id FK,
  barcode text NOT NULL, barcode_type text,
  UNIQUE (barcode)                          -- global uniqueness; scan resolves to one row

lot
  id, item_id FK, lot_code text,
  expiry_date date, manufactured_date date,
  status text CHECK (status IN ('available','quarantine','expired','blocked')),
  UNIQUE (item_id, lot_code)
  -- item_id is nullable for exactly one row: the "no lot" sentinel (below).
  -- A partial unique index enforces that only one such row can ever exist.

handling_unit                               -- LPN
  id, lpn text UNIQUE, hu_type text,
  parent_hu_id uuid FK -> handling_unit,    -- nesting: cases on a pallet
  current_location_id uuid FK,
  status text CHECK (status IN ('active','retired','lost')), gross_weight_g bigint

serial_unit                                 -- (schema now, feature Phase 3)
  id, item_id FK, serial_number text,
  lot_id, owner_id, current_location_id, current_handling_unit_id,
  status text CHECK (status IN ('expected','on_hand','allocated','shipped',
                                'returned','scrapped')),
  version bigint,
  UNIQUE (item_id, serial_number)
```

### 2.3 Inventory core

The two most important tables in the system.

```sql
stock_movement
  id                  uuid          NOT NULL
  sequence            bigint        NOT NULL   -- authoritative ordering (C4)
  recorded_at         timestamptz   NOT NULL   -- server clock, authoritative
  device_reported_at  timestamptz              -- forensic only, never trusted
  business_date       date          NOT NULL   -- from warehouse day boundary (M4)

  owner_id            uuid          NOT NULL
  item_id             uuid          NOT NULL
  lot_id              uuid          NOT NULL   -- sentinel UUID when not lot-tracked
  serial_unit_id      uuid
  stock_status        text          NOT NULL

  from_location_id    uuid                     -- NULL on receipt
  to_location_id      uuid                     -- NULL on dispatch
  handling_unit_id    uuid

  quantity_base       numeric(18,4) NOT NULL   -- signed, in item.base_uom
  entered_quantity    numeric(18,4) NOT NULL   -- as the operator entered it
  entered_uom         text          NOT NULL

  movement_type       text NOT NULL CHECK (movement_type IN
                        ('receipt','putaway','pick','move','adjustment',
                         'count','dispatch','return'))
  reason_code         text
  reference_type      text
  reference_id        uuid

  actor_user_id       uuid NOT NULL
  device_id           uuid
  authorized_by_user_id uuid                   -- supervisor override, if any
  idempotency_key     text

  PRIMARY KEY (id, recorded_at)
PARTITION BY RANGE (recorded_at);
```

**Partitioned on `recorded_at`, not on device time.** A device with a wrong clock must never be able to write into an unexpected partition, or into one that does not exist. Device time is captured for forensics and never used for routing, ordering, or reporting (C4).

Monthly partitions created twelve months ahead by a scheduled job. At the D5 volume of roughly 40–50 million rows per year, this is a first-migration requirement, not a deferred optimisation (M1).

```sql
stock_balance
  owner_id      uuid          NOT NULL
  item_id       uuid          NOT NULL
  location_id   uuid          NOT NULL
  lot_id        uuid          NOT NULL      -- sentinel when not lot-tracked
  stock_status  text          NOT NULL

  on_hand       numeric(18,4) NOT NULL DEFAULT 0
  allocated     numeric(18,4) NOT NULL DEFAULT 0
  version       bigint        NOT NULL DEFAULT 0
  updated_at    timestamptz   NOT NULL

  PRIMARY KEY (owner_id, item_id, location_id, lot_id, stock_status)
```

**Three deliberate design points:**

1. **No `CHECK (on_hand >= 0)`.** Offline facts may drive a balance negative. A negative balance is true information — it means the system's belief was wrong — and suppressing it destroys the audit trail the ledger exists to provide (C3, F2). The exception queue surfaces it; the constraint would hide it.
2. **`on_hand` and `allocated` on one row.** One lock covers the whole invariant, so allocation and picking never need a read-modify-write across two tables.
3. **`lot_id` uses a sentinel UUID rather than NULL**, so it can participate in the primary key. `00000000-0000-0000-0000-000000000000` is seeded as "no lot".

**What the sentinel row actually is, since leaving this implicit invites
each implementer to invent it differently.** The sentinel is a row in
`lot`, so `stock_balance.lot_id` keeps a real foreign key — an untracked
position points at a row that exists, not at a magic value the database
knows nothing about. It represents *the absence of a lot*, which means it
belongs to no item, so `lot.item_id` is **nullable for that row alone**:

```sql
lot.item_id  uuid NULL REFERENCES item (id)

-- At most one item-less row can ever exist. This is what makes "nullable"
-- safe: it is not an optional column, it is a single dimension member
-- meaning "not applicable", the way a date dimension carries one
-- "unknown date" row.
CREATE UNIQUE INDEX lot_sentinel_singleton_idx ON lot ((1)) WHERE item_id IS NULL;
```

Rejected alternatives, both of which look tidier and are worse: a
**sentinel `item`** to hang the sentinel lot off keeps `item_id NOT NULL`
but puts a fake SKU in the item master, where it surfaces in search,
reports, and barcode lookup — polluting a business-visible table to keep a
technical one clean. **Dropping the foreign key** and treating the
sentinel as a bare magic UUID removes the pollution but also removes
referential integrity, which is the thing the sentinel existed to
preserve.

Grain is exactly the StockPosition aggregate boundary from C6. Contention is scoped to one physical bin, which is why the synchronous update is acceptable (C1).

```sql
inventory_exception
  id                uuid PK
  exception_type    text NOT NULL CHECK (exception_type IN
                      ('short_pick','over_receipt','under_receipt','negative_balance',
                       'reallocated_task','damaged','location_mismatch'))
  severity          text NOT NULL
  warehouse_id      uuid NOT NULL
  owner_id, item_id, location_id, lot_id  uuid
  expected_quantity numeric(18,4)
  actual_quantity   numeric(18,4)
  stock_movement_id uuid
  task_id           uuid
  raised_at         timestamptz NOT NULL
  raised_by_user_id uuid
  device_id         uuid
  status            text CHECK (status IN ('open','investigating','resolved','written_off'))
  resolved_at       timestamptz
  resolved_by_user_id uuid
  resolution_action text
  resolution_note   text
```

This is a **first-class operational surface**, not an error log. It has an owner, an ageing alert, and a resolution workflow (C3 reconciliation).

```sql
balance_reconciliation_run                   -- H1
  id, warehouse_id, started_at, completed_at,
  from_sequence bigint, to_sequence bigint,
  rows_checked int, variance_count int, status text
```

**`from_sequence`/`to_sequence` are bounded by transaction visibility, never
by `max(sequence)`** — see ADR 0002. `sequence` is assigned at INSERT but the
row becomes visible at COMMIT, so under concurrent writers a watermark taken
from `max()` permanently skips every movement whose transaction was still
open at that instant. A reconciliation that omits rows and then reports zero
variance is worse than no reconciliation, because its clean result is what
stops anyone looking further.

```sql

balance_variance
  id, run_id FK, owner_id, item_id, location_id, lot_id, stock_status,
  ledger_quantity numeric, balance_quantity numeric, difference numeric
```

### 2.4 Tasks

```sql
task
  id                uuid PK
  warehouse_id      uuid NOT NULL
  zone_id           uuid NOT NULL          -- lease filter; matches user scope
  task_type         text NOT NULL CHECK (task_type IN
                      ('putaway','pick','move','count','replenish'))
  status            text NOT NULL CHECK (status IN
                      ('ready','leased','in_progress','completed','cancelled'))
  priority          int  NOT NULL DEFAULT 100
  sort_sequence     int  NOT NULL          -- min(pick_sequence) of its lines

  reference_type    text
  reference_id      uuid

  lease_device_id   uuid
  lease_user_id     uuid
  lease_expires_at  timestamptz            -- long: survives a full shift offline
  lease_heartbeat_at timestamptz           -- last contact; informs reclaim decisions
  lease_id          uuid                   -- groups a batch leased together

  created_at        timestamptz NOT NULL
  completed_at      timestamptz
  version           bigint NOT NULL DEFAULT 0

task_line
  id                uuid PK
  task_id           uuid FK
  line_no           int NOT NULL
  owner_id, item_id, lot_id  uuid
  from_location_id  uuid
  to_location_id    uuid
  handling_unit_id  uuid
  requested_quantity numeric(18,4) NOT NULL
  confirmed_quantity numeric(18,4)
  uom_code          text NOT NULL
  status            text CHECK (status IN ('pending','confirmed','short','skipped'))
```

Partial index supporting the lease query:

```sql
CREATE INDEX task_ready_idx ON task (warehouse_id, zone_id, priority, sort_sequence)
  WHERE status = 'ready';
CREATE INDEX task_lease_expiry_idx ON task (lease_expires_at)
  WHERE status = 'leased';
```

### 2.5 Inbound

```sql
receipt
  id, warehouse_id, receipt_number text UNIQUE,
  receipt_type text CHECK (receipt_type IN ('blind','against_asn','return')),
  supplier_reference text, owner_id uuid,
  status text CHECK (status IN ('draft','in_progress','received','putaway','closed','cancelled')),
  expected_at, started_at, completed_at timestamptz,
  created_by uuid

receipt_line
  id, receipt_id FK, line_no int,
  item_id, owner_id uuid,
  expected_quantity numeric(18,4),          -- NULL for blind receipt
  received_quantity numeric(18,4),         -- accumulated across confirmations
  uom_code text,
  lot_code text, expiry_date date,
  discrepancy_type text CHECK (discrepancy_type IN ('over','under','damaged','none')),
  status text
```

### 2.6 Outbound (Phase 1B)

```sql
sales_order
  id, warehouse_id, order_number text UNIQUE, owner_id,
  status text CHECK (status IN ('draft','released','allocating','allocated',
                                'partially_allocated','picking','picked','cancelled')),
  priority int, required_by timestamptz, created_at, created_by

order_line
  id, order_id FK, line_no int, item_id,
  ordered_quantity, allocated_quantity, picked_quantity numeric(18,4),
  uom_code text, status text

allocation
  id                uuid PK
  order_line_id     uuid FK
  owner_id, item_id, location_id, lot_id  uuid NOT NULL
  stock_status      text NOT NULL
  quantity          numeric(18,4) NOT NULL
  status            text CHECK (status IN ('held','picked','released','cancelled'))
  task_line_id      uuid
  allocated_at      timestamptz NOT NULL

allocation_request                           -- the serialised queue (C2)
  id, order_id FK, warehouse_id,
  status text CHECK (status IN ('queued','processing','done','failed')),
  requested_at, processed_at timestamptz,
  attempts int NOT NULL DEFAULT 0,
  last_error text
```

### 2.7 Platform

```sql
outbox_message
  id, aggregate_type text, aggregate_id uuid,
  message_type text, payload jsonb,
  created_at timestamptz, processed_at timestamptz,
  attempts int, last_error text
CREATE INDEX outbox_pending_idx ON outbox_message (created_at) WHERE processed_at IS NULL;

idempotency_record                           -- H2
  key             text NOT NULL
  user_id         uuid NOT NULL
  request_hash    text NOT NULL              -- sha256 of canonical body
  response_status int NOT NULL
  response_body   jsonb
  created_at      timestamptz NOT NULL
  PRIMARY KEY (key, user_id)
-- expired after 7 days; comfortably longer than the maximum offline window

configuration
  id, scope_type text CHECK (scope_type IN ('deployment','warehouse','zone')),
  scope_id uuid, config_key text, config_value jsonb,
  revision int NOT NULL, updated_by uuid, updated_at timestamptz,
  UNIQUE (scope_type, scope_id, config_key, revision)

app_setting_current                          -- materialised current revision, cached in-process

reason_code                                  -- resolves Open Item 3
  code              text PK
  category          text NOT NULL CHECK (category IN
                      ('receipt_discrepancy','pick_discrepancy','damage',
                       'count_variance','admin_correction','disposition','expiry'))
  label_i18n        jsonb NOT NULL
  applies_to        text[] NOT NULL          -- movement_types this code is valid for
  requires_note     boolean NOT NULL DEFAULT false
  requires_photo    boolean NOT NULL DEFAULT false
  requires_approval boolean NOT NULL DEFAULT false
  is_shrinkage      boolean NOT NULL DEFAULT false   -- counts against loss reporting
  is_active         boolean NOT NULL DEFAULT true
  sort_order        int

document_sequence                            -- resolves Open Item 4
  warehouse_id      uuid NOT NULL
  document_type     text NOT NULL            -- receipt, order, shipment, count_sheet, rma
  business_date     date NOT NULL
  last_number       int NOT NULL DEFAULT 0
  PRIMARY KEY (warehouse_id, document_type, business_date)
```

**`is_shrinkage` is the field that earns its place.** It separates "found extra" from "lost", and without it every variance report is undifferentiated noise. Roughly 25 codes are seeded; customers extend them through configuration.

**Document numbers** are allocated with `UPDATE … RETURNING` against `document_sequence`, which is concurrency-safe without an explicit lock. Format is a configurable template resolving to e.g. `RCV-TKY-260909-0042` — short enough to read over a radio, unique enough to be safe.

Numbering is **gap-tolerant by default**. Gapless numbering requires serialising allocation across the transaction boundary and forbids rollback gaps; where an auditor demands it, it is a per-deployment configuration with a stated throughput cost.

```sql
```

---

## 3. API Conventions

### 3.1 Transport

| Aspect | Value |
|---|---|
| Base path | `/api/v1` |
| Format | JSON, UTF-8 |
| Auth | `Authorization: Bearer <jwt>`, ~15 minute lifetime |
| Errors | RFC 7807 `application/problem+json` |
| Casing | `camelCase` in JSON, `snake_case` in the database |
| Dates | ISO 8601 with offset. Business dates as `YYYY-MM-DD` |
| Quantities | JSON number, max 4 decimal places, never a float in storage |

### 3.2 Required headers

| Header | Where | Purpose |
|---|---|---|
| `Authorization` | All except login | Bearer token |
| `Idempotency-Key` | Stock-mutating **commands** | Client-generated at the moment of user action. **Not used by `/sync/facts`**, where each fact carries its own `clientFactId` (§4.1) — one header cannot key twenty facts |
| `X-Client-Version` | All | Version gate; below minimum returns `426` |
| `X-Device-Id` | Operator routes | Binds the request to a registered device |
| `X-Elevation-Grant` | Elevated actions | Single-use supervisor grant (§5.4) |
| `If-Match` | Admin PATCH | Optimistic concurrency on `version` |
| `Accept-Language` | All | Resolves `*_i18n` fields |

### 3.3 The command / fact split

**The governing rule of this API.** Every endpoint is classified before it is built.

| | Commands | Facts |
|---|---|---|
| Path prefix | `/work`, `/orders`, `/waves`, `/config`, … | `/sync/facts` |
| Requires connectivity | Yes | No |
| Server may refuse | Yes | **Never**, except malformed or unauthenticated |
| Offline behaviour | Blocked, surfaced to the user | Queued, guaranteed acceptance |
| On conflict with state | `409` with machine-readable type | `202`, movement written, exception raised |

The test for classification: **would refusing ask someone to undo something they cannot undo?** If yes, it is a fact.

Two boundary cases that look wrong and are not:

- **Carton content scanning is a command.** Rejecting a wrong item at packing is legitimate — the item is in the packer's hand, not yet in the carton. Nothing physical has happened.
- **Weight verification blocks rather than warns.** A divergence means a miss-pick that scanning did not catch, and letting it through defeats the control.

### 3.4 Pagination

Keyset only on large tables. `OFFSET` is not used anywhere on `stock_movement`, `task`, or `auth_event`.

```
GET /inventory/movements?afterSequence=8837211&limit=200
→ { "items": [...], "nextCursor": { "afterSequence": 8837411 }, "hasMore": true }
```

### 3.5 Permission naming

`<domain>.<action>[.<qualifier>]` — e.g. `receipt.over_receive`, `inventory.adjust.approve`, `count.variance.approve`.

Authorisation is always checked against **permissions plus scope** (warehouse, zones), never against role names. An architecture test fails the build on any comparison to a role string.

### 3.6 OpenAPI document and Swagger UI

The proposal already depends on an OpenAPI document existing — the
TypeScript API client is generated from it in CI (§7.7), because
hand-maintained API types drift and drift surfaces as a runtime crash on a
handheld mid-shift. This section specifies the document itself, and the
interactive UI over it.

**The document** is generated from the API at build time and published as
a CI artefact. It is the input to client generation, so it is produced in
every environment including production builds — generating it is not
optional and not environment-dependent.

**The UI is.** Swagger UI is served at `/swagger` by the API process for
**manual endpoint testing during development**, and is:

| Environment | Swagger UI |
|---|---|
| Local / `docker compose` dev stack | **On.** This is the point — Phase 1A has ~50 endpoints and, for most of them, no frontend yet. |
| Shared staging | **On only behind normal authentication.** It is not a public console. |
| Any customer deployment | **Off.** Config-gated, default off. Caddy does not route `/swagger` in the prod override. |

The gate is configuration, not a compile-time flag, so enabling it on a
staging box never requires a different build than the one under test.

**Three things the document must declare, or the UI is useless in
practice:**

1. **The bearer security scheme**, so the UI's *Authorize* button works.
   The manual flow is: call `POST /auth/staff/login`, copy `accessToken`,
   paste it into *Authorize*. Tokens are ~15 minutes (§8.1) — expect to
   repeat that during a long session, and expect a `401` rather than
   anything more descriptive when it lapses.
2. **The required headers from §3.2 as explicit parameters** —
   `X-Client-Version` (all requests), `Idempotency-Key` (every
   stock-mutating POST), `X-Device-Id` (operator routes), `If-Match`
   (admin PATCH), `X-Elevation-Grant` (elevated actions). A default
   generator configuration will not infer these, and without them every
   mutating call from the UI fails — `426` for a missing client version,
   `400` for a missing idempotency key — which reads as "the API is
   broken" rather than "the console is misconfigured."
3. **The RFC 7807 problem shape** as the documented error response, so a
   failure renders as the machine-readable `type` the client is supposed
   to branch on rather than an opaque blob.

**`/sync/facts` is not a read-only explore.** Submitting a fact from the
UI writes real `stock_movement` rows, can raise real `inventory_exception`
records, and is idempotent only per `clientFactId` — a resubmit reusing the
same id returns the stored response rather than posting twice, and one
generated fresh on each "Execute" posts again every time (§4.1, §4.3). That is correct behaviour, not a quirk, but it means the
console mutates inventory truth in whatever environment it is pointed at.

**Library choice is deferred to implementation, deliberately.** The
candidates (Swashbuckle, NSwag, or the framework's own OpenAPI document
generation plus a separate UI package) shift between .NET releases, and
§7.10 of the proposal is explicit that current licence terms must be
verified before adopting any .NET dependency rather than assumed from
familiarity. Pick it when the API project is scaffolded, verify the
licence then, and record the choice with `/adr` if it is not the obvious
default at that time.

---

## 4. Fact Registry

Every physical event the system accepts, across all phases. These are the payloads submitted to `POST /sync/facts` and the **only** way stock quantities change through operator action.

### 4.1 Envelope

```json
POST /api/v1/sync/facts
{
  "deviceId": "018f2c...",
  "clientBatchId": "018f2c...",
  "facts": [
    {
      "clientFactId": "018f2c...",
      "type": "pick_confirmed",
      "occurredAtDevice": "2026-09-09T04:12:07.221Z",
      "payload": { }
    }
  ]
}
```

Response is **always `202`** unless malformed, unauthenticated, or below minimum client version:

```json
{
  "results": [
    {
      "clientFactId": "018f2c...",
      "status": "accepted",
      "movementIds": ["018f2d..."],
      "exceptionIds": ["018f2e..."],
      "serverSequence": 8837412
    }
  ]
}
```

`status` ∈ `accepted` | `duplicate` | `rejected`. **`rejected` covers malformed payloads only** — never a business-rule refusal.

**Rejection is always per fact, never per batch.** One bad fact among twenty must not fail the other nineteen, and must never reach a handheld as a `5xx` — a device cannot act on either. A handler therefore *returns* a rejection rather than throwing, and the two things that qualify are: a payload referencing something the server has never heard of (`unknownReceiptLine` and its equivalents), and a `clientFactId` reused with a different payload. This is where **§4.3 step 1's `409` lands inside a batch**: the status code describes a single-fact submission, but in the envelope the same condition is one `rejected` entry beside nineteen `accepted` ones. Both roll the fact's transaction back, which also releases the idempotency claim — a rejected id is not permanently poisoned.

Anything the server itself got wrong — a row vanishing mid-transaction, a claimed key with no stored response — still throws, and should. Those are server bugs, and turning them into a per-fact `rejected` would tell a device to give up on work that was never its fault.

**`clientFactId` is the idempotency key.** Not the request's `Idempotency-Key` header: one header covers a batch of twenty, so using it would record only the first fact and replay-suppress the other nineteen — losing nineteen movements while reporting success. Invariant 7 additionally requires the id to be generated at the moment of the operator's action, not at send time, so that a queue drained after a reboot replays the same ids.

### 4.2 Fact types by phase

| Type | Phase | Payload fields | Raises exception when |
|---|---|---|---|
| `receipt_confirmed` | 1A | `receiptLineId, itemId, quantity, uom, lotCode?, expiryDate?, toLocationId, reasonCode?` | Quantity ≠ expected |
| `putaway_confirmed` | 1A | `taskLineId, itemId, fromLocationId, toLocationId, quantity, uom, handlingUnitId?` | Location differs from directed |
| `task_skipped` | 1A | `taskLineId, reasonCode, note?` | Always |
| `pick_confirmed` | 1B | `taskLineId, itemId, fromLocationId, lotId?, quantity, uom, containerId?` | Quantity < requested |
| `replen_confirmed` | 2 | `taskLineId, itemId, fromLocationId, toLocationId, quantity, uom` | Quantity < requested |
| `move_confirmed` | 2 | `itemId, fromLocationId, toLocationId, quantity, uom, handlingUnitId?, reasonCode` | Never |
| `container_staged` | 2 | `containerId, stagingLocationId, consolidationId?` | Consolidation short |
| `count_confirmed` | 3 | `countLineId, locationId, itemId, lotId?, countedQuantity, uom` | Variance beyond tolerance |
| `return_received` | 3 | `rmaLineId, itemId, quantity, uom, condition, mediaIds[]` | Quantity ≠ expected |
| `serial_scanned` | 3 | `serialNumber, itemId, context, taskLineId?` | Serial unknown or in wrong state |

**Nothing is added to this table without deciding its exception behaviour first.** A fact type with no defined divergence path is a fact type that will silently swallow a discrepancy.

### 4.3 Server processing contract

Each fact is processed in **one transaction**, in this order:

```
1. Idempotency check      → hit + matching hash: return stored response
                          → hit + differing hash: 409 (client bug, not a retry)
2. Mutate stock_balance   → NO guard clause on decrements
3. Append stock_movement  → sequence, recorded_at, business_date assigned server-side
4. Update work state      → task_line, task, receipt_line, count_line as applicable
5. Divergence check       → inventory_exception if reality ≠ expectation
6. Outbox message
7. Idempotency record
COMMIT
```

Step 2 having no guard is the load-bearing detail. The goods have physically moved; a `WHERE on_hand >= qty` clause would make the statement fail and force the client to handle a rejection it cannot act on.

**Note on `adjustment_recorded` (removed from §4.2).** An earlier draft listed adjustment as a fact type. It is not one: `POST /inventory/adjustments` (§5.5, §6.6) is a command with a `pending_approval` state and a refusable approve step, which contradicts "facts may not be refused" (invariant 2). Stock adjustments are administrative corrections, not physical events — nothing was scanned or moved — so they belong on the command path, not `/sync/facts`.

**Note on step 4 and the one-aggregate-per-transaction rule (C6).** C6 names pick confirmation and allocation as the only two transactions permitted to mutate more than one aggregate. Read literally, step 4 above violates that for every other fact type: `receipt_confirmed`, `putaway_confirmed`, `count_confirmed`, and `return_received` each mutate `StockPosition` (`stock_balance` + `stock_movement`) *and* the work-item aggregate the fact confirms against (`receipt`/`receipt_line`, `task`/`task_line`, `count_line`) in the same transaction — see §6.2, §6.3, §6.15, §6.16.

This is intentional, and C6's "only two" framing is superseded here: **fact confirmation is a structural exception alongside allocation, not a narrower one limited to picking.** Every fact type that confirms against a work item shares pick confirmation's justification — the transaction is short, each aggregate is touched on a single row, and locks are taken in a fixed order (invariant 7). The alternative, propagating the work-state update through the outbox instead, would let a task or receipt line sit `pending` after its movement has already posted, which is wrong for the one-task-per-screen operator UX (§1.2) and for `GET /work/leases` consistency. Allocation remains the other exception, for the different reason given in C6: it touches many `StockPosition` rows under one `Order`, contained by the single-consumer advisory lock (§1.3, invariant 6) rather than by per-transaction lock ordering.

---

## 5. API Reference

Format: `→` request body, `←` response. Permission and phase follow each path. Fields marked `?` are optional. All responses omit `null` optional fields rather than emitting them.

### 5.1 Authentication and session

---

**`POST /auth/staff/login`** · — · 1A

```jsonc
→ { "email": "kimura@example.co.jp", "password": "…" }

← 200 {
    "accessToken": "eyJhbGci…",
    "refreshToken": "018f2c4a…",
    "expiresIn": 900,
    "user": {
      "id": "018f2c4a-…", "displayName": "木村 由美",
      "userType": "staff", "locale": "ja"
    },
    "permissions": ["order.release", "inventory.adjust.approve", "report.read"],
    "scopes": [
      { "warehouseId": "018f1a…", "zoneIds": [] }   // empty = all zones
    ],
    "minClientVersion": "1.4.0"
  }

← 401 { "type": "…/unauthenticated", "title": "Invalid credentials" }
← 423 { "type": "…/account-locked", "retryAfter": 900 }
← 403 { "type": "…/credential-change-required",
        "changeToken": "018f3c…", "expiresIn": 300 }
```

Failure responses are deliberately identical for unknown email and wrong password.

**A password credential created with `must_change = true` never issues an access/refresh token pair on a correct password.** Instead of the normal `200`, a correct password against such a credential returns `403 .../credential-change-required` with a single-use `changeToken` — the same short-lived-grant shape as `POST /auth/elevate`'s `grantToken`, not a session. The password was still verified (this is not a failure response, and it does not count against lockout), but nothing beyond "set a new password" is reachable until the change completes. Set on every password credential `POST /users` creates; cleared, along with the flag, by a successful `POST /auth/credential-change`. PIN and badge credentials never carry this — operator credentials are issued by an admin/supervisor for repeated shared-device access rather than a personal secret the holder is expected to keep private and periodically rotate, so `POST /auth/operator/login` never returns this response regardless of `must_change` (which stays `false` for those credential types).

---

**`POST /auth/credential-change`** · — (bearer is a `changeToken`, not a session) · 1A

```jsonc
→ { "changeToken": "018f3c…", "newSecret": "…" }

← 200 { "id": "…" }                       // must_change cleared; log in normally next
← 401 { "type": "…/unauthenticated", "title": "Invalid or expired changeToken" }
```

Single-use, bound to the credential it was issued for — same discipline as `POST /auth/elevate`'s `grantToken` (§6.5). Writes `auth_event(credential_reset)`. No `security_stamp` bump: no token was ever issued off this credential's `must_change` state for the stamp to invalidate, so there is nothing yet in circulation to revoke.

---

**`POST /auth/operator/login`** · — · 1A

```jsonc
→ { "deviceId": "018f1b…", "badge": "EMP00412" }
   // or
→ { "deviceId": "018f1b…", "employeeCode": "EMP00412", "pin": "4821" }

← 200 {
    "accessToken": "…", "refreshToken": "…", "expiresIn": 900,
    "sessionId": "018f2d…",
    "user": { "id": "018f2c…", "displayName": "田中 健",
              "userType": "operator", "locale": "ja" },
    "permissions": ["task.lease", "pick.execute", "putaway.execute",
                    "receipt.confirm"],
    "scopes": [{ "warehouseId": "018f1a…",
                 "zoneIds": ["018f1c…", "018f1d…"] }],
    "device": { "id": "018f1b…", "label": "HH-014" },
    "minClientVersion": "1.4.0",
    "settings": { "idleSuspendSeconds": 180, "sessionMaxHours": 12 }
  }

← 403 { "type": "…/account-expired",
        "detail": "valid_until 2026-08-31" }      // agency labour lapse
← 409 { "type": "…/device-claimed",
        "detail": "HH-014 is claimed by another operator",
        "claimedBy": "佐藤 美咲", "since": "2026-09-09T06:02:11+09:00" }
```

---

**`POST /auth/refresh`** · — · 1A

```jsonc
→ { "refreshToken": "018f2c…" }
← 200 { "accessToken": "…", "refreshToken": "…", "expiresIn": 900 }
← 401 { "type": "…/token-reused",
        "detail": "Token family revoked" }
```

Reuse detection revokes the entire token family, not just the presented token.

---

**`POST /auth/logout`** · authenticated · 1A

```jsonc
→ { "reason": "shift_end" }        // logout | shift_end | forced
← 204
```

Unworked leases held by the session are returned to the pool. Leases with confirmations still queued on the device are **not** released — see §6.10.

---

**`GET /auth/me`** · authenticated · 1A

```jsonc
← 200 {
    "user": { "id": "…", "displayName": "田中 健", "locale": "ja",
              "userType": "operator" },
    "permissions": ["task.lease", "pick.execute", "…"],
    "scopes": [{ "warehouseId": "…", "warehouseCode": "TKY",
                 "zoneIds": ["…"], "zoneCodes": ["A", "B"] }],
    "session": { "id": "…", "deviceId": "…",
                 "startedAt": "2026-09-09T06:02:11+09:00" },
    "securityStamp": "018f2e…"
  }
```

Clients poll this on reconnect. A changed `securityStamp` means permissions were altered and cached authorisation must be discarded.

---

**`POST /auth/elevate`** · — · 1A

```jsonc
→ { "badge": "SUP0007",
    "action": "receipt.over_receive",
    "contextId": "018f3a…",              // the receipt line
    "contextType": "receipt_line" }

← 200 { "grantToken": "018f3b…", "expiresIn": 120,
        "grantedBy": { "id": "…", "displayName": "山本 誠" } }

← 403 { "type": "…/same-actor",
        "title": "Approver may not be the actor" }
← 403 { "type": "…/insufficient-permission",
        "detail": "SUP0007 does not hold receipt.over_receive in zone A" }
← 503 { "type": "…/service-unavailable",
        "detail": "Elevation requires connectivity" }
```

Single-use, bound to `(action, contextId, requestingUser)`. A grant for one over-receipt cannot authorise the next.

---

**`POST /devices/{id}/claim`** · `device.claim` · 1A
**`POST /devices/{id}/release`** · `device.claim` · 1A

```jsonc
→ { "sessionId": "018f2d…" }
← 200 { "deviceId": "…", "label": "HH-014",
        "claimedBy": "018f2c…", "claimedAt": "…" }
← 204   // release
```

---

**`POST /devices/{id}/heartbeat`** · authenticated · 1A

```jsonc
→ { "appVersion": "1.4.2", "platform": "android/13 chrome/121",
    "queueDepth": 7, "batteryPercent": 62 }
← 200 { "serverTime": "2026-09-09T07:14:02+09:00",
        "minClientVersion": "1.4.0",
        "leaseRenewals": [{ "leaseId": "…", "expiresAt": "…" }] }
```

`queueDepth` feeds the sync-backlog metric (§8.4) **and** is written to
`device.last_queue_depth` in the same request — making it queryable from
`GET /devices` and the admin dashboard (§5.12), not only visible through
the metrics/logging pipeline. `serverTime` lets the device measure its own
clock drift without ever being trusted for ordering.

### 5.2 Work assignment

---

**`POST /work/leases`** · `task.lease` · 1A

```jsonc
→ { "warehouseId": "018f1a…",
    "zoneIds": ["018f1c…"],
    "taskTypes": ["pick"],
    "batchSize": 20 }

← 200 {
    "leaseId": "018f4a…",
    "expiresAt": "2026-09-09T18:00:00+09:00",
    "tasks": [{
      "id": "018f4b…", "taskType": "pick", "priority": 100,
      "waveId": "018f4c…",
      "container": { "id": "018f4d…", "code": "TOTE-0412", "position": 1 },
      "lines": [{
        "id": "018f4e…", "lineNo": 1,
        "location": { "id": "…", "code": "A-12-03-2", "pickSequence": 1203 },
        "item": {
          "id": "…", "skuCode": "SKU-88213",
          "name": "ステンレス製水筒 500ml",
          "barcodes": ["4901234567890", "14901234567897"],
          "thumbKey": "a3f9c1…/thumb.webp"
        },
        "lot": { "id": "…", "lotCode": "L2609A", "expiryDate": "2027-03-31" },
        "requestedQuantity": 12,
        "uom": "EACH",
        "isDiscrete": true
      }]
    }],
    "reasonCodes": [
      { "code": "PICK_SHORT", "label": "ロケーション在庫不足",
        "requiresNote": false, "requiresPhoto": false },
      { "code": "PICK_NOT_FOUND", "label": "ロケーション在庫なし",
        "requiresNote": false, "requiresPhoto": false }
    ]
  }

← 200 { "leaseId": null, "tasks": [] }     // no work available — not a 404
```

**Self-contained by design.** Item names in the operator's locale, barcodes, image keys, and the applicable reason codes are all included, because the device must complete every leased task with no further server contact.

---

**`GET /work/leases/{id}`** · `task.lease` · 1A — identical payload; used after reinstall or cache loss.

---

**`POST /work/leases/{id}/renew`** · `task.lease` · 1A

```jsonc
→ { "completedTaskIds": ["018f4b…"] }       // lets the server release early
← 200 { "expiresAt": "2026-09-10T02:00:00+09:00", "remainingTasks": 13 }
← 410 { "type": "…/lease-reclaimed",
        "detail": "Reclaimed by 山本 誠 at 2026-09-09T11:20:00+09:00",
        "reclaimedTaskIds": ["018f4b…"] }
```

A `410` does **not** mean queued confirmations are lost. They still submit and are still accepted (§6.4 case D).

---

**`DELETE /work/leases/{id}`** · `task.lease` · 1A → `204`. Unworked tasks return to `ready`.
Scoped to the caller's own lease: naming a lease held by someone else is a
silent no-op (still `204`, for the same retry-safety reason releasing an
already-empty lease is), never a way to pull another operator's active work
back to the queue. That is `task.reassign` against a lease that has expired
(`GET /work/leases/expired`, `POST /work/tasks/{id}/reclaim` below) — a
distinct, supervisor-only act.

---

**`GET /work/leases/expired`** · `task.reassign` · 1A

```jsonc
← 200 { "items": [{
    "leaseId": "…", "taskId": "…", "taskType": "pick",
    "zoneCode": "A",
    "user": { "id": "…", "displayName": "田中 健" },
    "device": { "id": "…", "label": "HH-014" },
    "leaseExpiresAt": "2026-09-09T18:00:00+09:00",
    "lastHeartbeatAt": "2026-09-09T10:35:00+09:00",
    "minutesSinceContact": 45,
    "linesConfirmed": 3, "linesTotal": 8
  }] }
```

`minutesSinceContact` is the field the supervisor actually decides on. Reclaim is never automatic (§6.10).

---

**`POST /work/tasks/{id}/reclaim`** · `task.reassign` · 1A

```jsonc
→ { "note": "Device HH-014 returned to charger, operator went home" }
← 200 { "taskId": "…", "status": "ready" }
```

---

**`POST /work/tasks/{id}/cancel`** · `task.cancel` · 1A

```jsonc
→ { "reasonCode": "PICK_INACCESSIBLE", "note": "Aisle A-12 blocked by pallet" }
← 200 { "taskId": "…", "status": "cancelled",
        "releasedAllocations": 3 }
```

### 5.3 Fact submission

---

**`POST /sync/facts`** · varies by fact type · 1A

Envelope and per-type payloads: §4.1–4.2. Repeated here only for the response contract.

```jsonc
← 202 {
    "results": [
      { "clientFactId": "018f5a…", "status": "accepted",
        "movementIds": ["018f5b…"], "serverSequence": 8837412,
        "exceptionIds": [] },
      { "clientFactId": "018f5c…", "status": "duplicate",
        "movementIds": ["018f5d…"], "serverSequence": 8837390 },
      { "clientFactId": "018f5e…", "status": "rejected",
        "error": { "type": "…/malformed-request",
                   "detail": "quantity must be a positive number" } }
    ],
    "serverTime": "2026-09-09T07:20:14+09:00"
  }

← 400   // whole batch malformed
← 401   // token expired — client refreshes and resubmits the identical batch
← 426 { "type": "…/client-too-old", "minClientVersion": "1.4.0" }
```

`202` is returned even when every fact raised an exception. Exceptions are outcomes, not failures.

---

**`GET /sync/status`** · authenticated · 1A

```jsonc
← 200 { "deviceId": "…", "lastAcceptedSequence": 8837412,
        "lastAcceptedAt": "…", "pendingServerSide": 0 }
```

Lets a device that lost its local queue determine what actually landed.

`lastAcceptedSequence` is subject to ADR 0002: it is advanced by transaction
visibility, not by `max(sequence)`, or a device is told it is caught up while
a concurrently-committing movement sits permanently below its cursor.

### 5.4 Inbound

---

**`POST /receipts`** · `receipt.create` · 1A

```jsonc
→ { "warehouseId": "018f1a…",
    "receiptType": "blind",              // blind | against_asn | return
    "ownerId": "018f1e…",
    "supplierReference": "PO-2026-4471",
    "expectedAt": "2026-09-09T09:00:00+09:00",
    "lines": [                            // omitted entirely for blind
      { "itemId": "…", "expectedQuantity": 100, "uom": "EACH" }
    ] }

← 201 { "id": "018f6a…", "receiptNumber": "RCV-TKY-260909-0042",
        "status": "draft", "lines": [ … ] }
```

---

**`GET /receipts`** · `receipt.read` · 1A

```
?status=in_progress&warehouseId=…&businessDateFrom=2026-09-01
&businessDateTo=2026-09-09&supplierReference=PO-2026-4471&limit=50
```

```jsonc
← 200 { "items": [{ "id": "…", "receiptNumber": "RCV-TKY-260909-0042",
                    "status": "in_progress", "supplierReference": "PO-2026-4471",
                    "linesTotal": 12, "linesConfirmed": 7,
                    "discrepancyCount": 1,
                    "expectedAt": "…", "startedAt": "…" }],
        "nextCursor": { "afterId": "…" }, "hasMore": false }
```

---

**`GET /receipts/{id}`** · `receipt.read` · 1A

```jsonc
← 200 {
    "id": "…", "receiptNumber": "RCV-TKY-260909-0042",
    "status": "in_progress", "receiptType": "blind",
    "owner": { "id": "…", "code": "OWN-DEFAULT" },
    "supplierReference": "PO-2026-4471",
    "lines": [{
      "id": "…", "lineNo": 1,
      "item": { "id": "…", "skuCode": "SKU-88213", "name": "…" },
      "expectedQuantity": 100, "receivedQuantity": 106,
      "uom": "EACH", "lotCode": "L2609A", "expiryDate": "2027-03-31",
      "discrepancyType": "over", "status": "confirmed",
      "exceptionId": "018f6c…"
    }],
    "version": 4
  }
```

---

**`POST /receipts/{id}/start`** · `receipt.confirm` · 1A

```jsonc
← 200 { "id": "…", "status": "in_progress", "taskIds": ["018f6d…"] }
← 409 { "type": "…/invalid-transition", "from": "received", "to": "in_progress" }
```

---

**`POST /receipts/{id}/lines`** · `receipt.confirm` · 1A — adds a line during blind receipt.

```jsonc
→ { "itemId": "018f7a…", "uom": "CASE", "expectedQuantity": null }
← 201 { "id": "…", "lineNo": 13 }
```

Quantities are **not** set here. The line is a placeholder; the count arrives as a `receipt_confirmed` fact.

---

**`POST /receipts/{id}/complete`** · `receipt.complete` · 1A

```jsonc
→ { "generatePutaway": true }
← 200 { "id": "…", "status": "received",
        "putawayTasksCreated": 12,
        "openExceptions": 1,
        "movementsPosted": 12 }
← 409 { "type": "…/lines-unconfirmed", "unconfirmedLineNos": [4, 9] }
```

Putaway tasks are generated for **actual** received quantities, never expected.

---

**`POST /receipts/{id}/cancel`** · `receipt.create` · 1A

```jsonc
→ { "reasonCode": "ADJ_REVERSAL", "note": "Duplicate of RCV-TKY-260909-0041" }
← 200 { "id": "…", "status": "cancelled" }
← 409 { "type": "…/movements-exist",
        "detail": "3 confirmations already posted; reverse individually" }
```

### 5.5 Inventory

---

**`GET /inventory/balances`** · `inventory.read` · 1A

```
?itemId=…&locationId=…&zoneId=…&ownerId=…&lotId=…&stockStatus=available
&includeZero=false&limit=200&afterKey=…
```

```jsonc
← 200 { "items": [{
    "owner": { "id": "…", "code": "OWN-DEFAULT" },
    "item": { "id": "…", "skuCode": "SKU-88213", "name": "…" },
    "location": { "id": "…", "code": "A-12-03-2", "zoneCode": "A" },
    "lot": { "id": "…", "lotCode": "L2609A", "expiryDate": "2027-03-31" },
    "stockStatus": "available",
    "onHand": 88, "allocated": 12, "available": 76,
    "uom": "EACH", "version": 41, "updatedAt": "…"
  }],
  "totals": { "onHand": 88, "allocated": 12, "available": 76 },
  "nextCursor": { "afterKey": "…" }, "hasMore": true }
```

`available` is computed, never stored. `onHand` **may be negative** — that is real information, not an error (§2.3).

---

**`GET /inventory/movements`** · `inventory.read` · 1A

```
?itemId=…&locationId=…&movementType=pick&referenceId=…
&afterSequence=8837211&businessDateFrom=…&limit=200
```

```jsonc
← 200 { "items": [{
    "id": "…", "sequence": 8837412,
    "recordedAt": "2026-09-09T07:20:14+09:00",
    "deviceReportedAt": "2026-09-09T07:12:07+09:00",
    "businessDate": "2026-09-09",
    "movementType": "pick",
    "item": { "id": "…", "skuCode": "SKU-88213" },
    "lot": { "lotCode": "L2609A" },
    "fromLocation": { "code": "A-12-03-2" }, "toLocation": null,
    "quantityBase": -12, "enteredQuantity": 12, "enteredUom": "EACH",
    "reasonCode": null,
    "reference": { "type": "task_line", "id": "…" },
    "actor": { "id": "…", "displayName": "田中 健" },
    "device": { "id": "…", "label": "HH-014" },
    "authorizedBy": null
  }],
  "nextCursor": { "afterSequence": 8837611 }, "hasMore": true }
```

Ordered by `sequence`, never by timestamp. `deviceReportedAt` is present for forensics and is never used for ordering.

---

**`GET /inventory/exceptions`** · `exception.read` · 1A

```
?status=open&exceptionType=short_pick&warehouseId=…&minAgeHours=2&limit=50
```

```jsonc
← 200 { "items": [{
    "id": "018f8a…", "exceptionType": "short_pick", "severity": "medium",
    "raisedAt": "2026-09-09T07:20:14+09:00", "ageHours": 3.2,
    "item": { "skuCode": "SKU-88213", "name": "…" },
    "location": { "code": "A-12-03-2" },
    "expectedQuantity": 12, "actualQuantity": 6, "difference": -6,
    "stockMovementId": "…", "taskId": "…",
    "raisedBy": { "displayName": "田中 健" },
    "device": { "label": "HH-014" },
    "status": "open"
  }],
  "summary": { "open": 14, "oldestAgeHours": 9.4,
               "byType": { "short_pick": 8, "over_receipt": 3,
                           "negative_balance": 3 } } }
```

The `summary` block drives the operational dashboard; the exception queue is a work surface, not an error log.

---

**`POST /inventory/exceptions/{id}/resolve`** · `exception.resolve` · 1A

```jsonc
→ { "action": "adjust_to_counted",     // adjust_to_counted | accept |
                                        // reallocate | write_off | escalate
    "note": "Recount confirmed 6. Balance corrected.",
    "reasonCode": "CNT_SHORT",
    "mediaIds": [] }

← 200 { "id": "…", "status": "resolved",
        "adjustmentMovementId": "018f8b…" }
← 403 { "type": "…/same-actor",
        "detail": "Cannot resolve an exception you raised" }
```

`adjust_to_counted` and `write_off` create a movement. `accept` and `reallocate` do not.

---

**`POST /inventory/adjustments`** · `inventory.adjust` · 1A

```jsonc
→ { "ownerId": "…", "itemId": "…", "locationId": "…", "lotId": null,
    "stockStatus": "available",
    "quantityDelta": -3, "uom": "EACH",
    "reasonCode": "DMG_HANDLING",
    "note": "Forklift damage, pallet corner crushed",
    "mediaIds": ["018f8c…"] }

← 202 { "id": "018f8d…", "status": "pending_approval",
        "requiresApproval": true, "approvalPermission": "inventory.adjust.approve" }
← 200 { "id": "…", "status": "approved",
        "movementId": "…" }                    // when the code needs no approval
← 400 { "type": "…/media-required",
        "detail": "DMG_HANDLING requires at least one photo" }
```

Validation is driven from `reason_code`: `requires_note`, `requires_photo`, `requires_approval`.

---

**`POST /inventory/adjustments/{id}/approve`** · `inventory.adjust.approve` · 1A

```jsonc
→ { "note": "Verified on site" }
← 200 { "id": "…", "status": "approved", "movementId": "018f8e…",
        "balanceAfter": { "onHand": 85, "allocated": 12 } }
← 403 { "type": "…/same-actor" }
← 409 { "type": "…/already-resolved" }
```

---

**`GET /inventory/reconciliation/runs`** · `inventory.read` · 1A

```jsonc
← 200 { "items": [{
    "id": "…", "warehouseId": "…",
    "startedAt": "…", "completedAt": "…",
    "fromSequence": 8800000, "toSequence": 8837412,
    "rowsChecked": 41230, "varianceCount": 0, "status": "completed"
  }] }
```

`varianceCount` should always be zero. Non-zero means a bug, and it alerts (§8.4).

---

**`GET /lookup/barcode/{code}`** · authenticated · 1A

```jsonc
← 200 { "barcode": "14901234567897",
        "item": { "id": "…", "skuCode": "SKU-88213",
                  "name": "ステンレス製水筒 500ml", "baseUom": "EACH",
                  "isLotTracked": true, "isSerialTracked": false },
        "uom": { "code": "CASE", "qtyInBase": 24, "isDiscrete": true } }
← 404 { "type": "…/barcode-unknown", "barcode": "…" }
```

On the hot path for every scanning screen; served from an in-process cache invalidated by the outbox.

### 5.6 Outbound

---

**`POST /orders`** · `order.create` · 1B

```jsonc
→ { "warehouseId": "…", "ownerId": "…",
    "externalReference": "SO-88213",
    "priority": 100,
    "requiredBy": "2026-09-10T12:00:00+09:00",
    "shipTo": { "name": "…", "postalCode": "150-0002",
                "prefecture": "東京都", "line1": "…", "country": "JP" },
    "lines": [{ "itemId": "…", "orderedQuantity": 12, "uom": "EACH" }] }

← 201 { "id": "018f9a…", "orderNumber": "SO-TKY-260909-0117",
        "status": "draft", "lines": [{ "id": "…", "lineNo": 1,
          "orderedQuantity": 12, "allocatedQuantity": 0,
          "pickedQuantity": 0, "status": "pending" }] }
```

---

**`POST /orders/{id}/release`** · `order.release` · 1B

```jsonc
→ { "waveId": null }                    // set when releasing within a wave
← 202 { "id": "…", "status": "allocating",
        "allocationRequestId": "018f9b…",
        "pollUrl": "/api/v1/orders/018f9a…" }
← 409 { "type": "…/invalid-transition", "from": "cancelled" }
```

**Allocation is asynchronous by design** (§6.7). The response is `202` and carries no allocation result — a synchronous answer would mean allocating on the request thread, which is exactly the workload-isolation failure §8.2 exists to prevent.

---

**`GET /orders/{id}`** · `order.read` · 1B

```jsonc
← 200 {
    "id": "…", "orderNumber": "SO-TKY-260909-0117",
    "status": "partially_allocated",
    "waveId": "018f9c…",
    "lines": [{
      "id": "…", "lineNo": 1,
      "item": { "skuCode": "SKU-88213", "name": "…" },
      "orderedQuantity": 12, "allocatedQuantity": 8, "pickedQuantity": 0,
      "status": "partially_allocated",
      "shortfallReason": "insufficient_available"
    }],
    "allocation": { "requestedAt": "…", "processedAt": "…",
                    "attempts": 1, "lastError": null },
    "version": 3
  }
```

---

**`GET /orders/{id}/allocations`** · `order.read` · 1B

```jsonc
← 200 { "items": [{
    "id": "…", "orderLineId": "…",
    "location": { "code": "A-12-03-2", "pickSequence": 1203 },
    "lot": { "lotCode": "L2609A", "expiryDate": "2027-03-31" },
    "quantity": 8, "status": "held",
    "taskLineId": "…", "allocatedAt": "…"
  }] }
```

Answers "where is this order's stock coming from", which is the first question asked when a pick goes wrong.

---

**`POST /orders/{id}/cancel`** · `order.cancel` · 1B

```jsonc
→ { "reasonCode": "ADJ_REVERSAL", "note": "Customer cancelled" }
← 200 { "id": "…", "status": "cancelled",
        "allocationsReleased": 2, "tasksCancelled": 1,
        "tasksInProgressAllowedToFinish": 1 }
```

Tasks already `in_progress` finish. Recalling an operator mid-aisle costs more than the stock it frees.

---

**`POST /waves`** · `wave.plan` · 2

```jsonc
→ { "warehouseId": "…", "waveType": "carrier_cutoff",
    "orderIds": ["…", "…"],
    "strategy": { "pickMethod": "zone",
                  "grouping": "by_zone_affinity",
                  "maxOrders": 200 } }

← 201 { "id": "018fa1…", "waveNumber": "WAV-TKY-260909-0007",
        "status": "planning", "orderCount": 48,
        "estimatedLines": 213, "estimatedZones": ["A", "B", "C"] }
```

---

**`POST /waves/{id}/release`** · `wave.release` · 2

```jsonc
← 202 { "id": "…", "status": "allocating",
        "allocationRequestIds": ["…"], "orderCount": 48 }
```

---

**`GET /waves/{id}/progress`** · `wave.plan` · 2

```jsonc
← 200 {
    "id": "…", "waveNumber": "WAV-TKY-260909-0007", "status": "picking",
    "orders": { "total": 48, "allocated": 46, "partiallyAllocated": 2,
                "picked": 31, "packed": 12 },
    "tasks": { "ready": 4, "leased": 9, "completed": 62 },
    "byZone": [{ "zoneCode": "A", "tasksTotal": 25, "tasksCompleted": 22,
                 "linesShort": 1 }],
    "consolidations": { "open": 17, "complete": 29, "short": 2 },
    "exceptions": { "shortPick": 3 }
  }
```

---

**`POST /consolidations/{id}/confirm`** · `pack.execute` · 2

```jsonc
→ { "containerId": "018fa2…", "stagingLocationId": "…" }
← 200 { "id": "…", "receivedContainers": 3, "expectedContainers": 3,
        "status": "complete", "orderId": "…", "readyToPack": true }
← 200 { "…", "status": "short",
        "missingZones": ["C"], "reason": "zone C fully short-picked" }
```

`short` is a terminal state, not a hang. The order proceeds to packing with what arrived; the shortfall is already recorded as a `short_pick` exception.

### 5.7 Replenishment (Phase 2)

---

**`POST /replenishment/rules`** · `replen.rule.write`

```jsonc
→ { "locationId": "…", "itemId": "…", "ownerId": "…",
    "minQuantity": 24, "maxQuantity": 144, "replenUom": "CASE",
    "sourceZoneId": "018f1f…", "priority": 100 }
← 201 { "locationId": "…", "isActive": true }
← 409 { "type": "…/rule-exists", "detail": "Location already has a rule" }
```

---

**`GET /replenishment/requests`** · `inventory.read`

```jsonc
?status=queued&triggerType=demand
← 200 { "items": [{
    "id": "…", "triggerType": "demand",
    "location": { "code": "A-12-03-2" },
    "item": { "skuCode": "SKU-88213" },
    "requiredQuantity": 48, "status": "tasked",
    "taskId": "…", "priority": 10,
    "createdAt": "…", "blockedOrderIds": ["018f9a…"] }] }
```

`blockedOrderIds` is why the demand path exists — it names the orders waiting on this refill.

---

**`POST /replenishment/run`** · `replen.trigger` → `202 { "requestsCreated": 7 }`

### 5.8 Packing and shipping

---

**`POST /shipments/{id}/cartons`** · `pack.execute` · 2

```jsonc
→ { "cartonTypeId": "…" }               // omit to accept the suggestion
← 201 { "id": "018fb1…", "cartonNumber": 1,
        "cartonType": { "code": "BOX-M", "tareWeightG": 240 },
        "expectedWeightG": 0, "status": "open",
        "suggestedTypeCode": "BOX-M",
        "suggestionReason": "smallest_fitting" }
```

---

**`POST /cartons/{id}/contents`** · `pack.execute` · 2

```jsonc
→ { "barcode": "4901234567890", "quantity": 12, "orderLineId": "…" }
← 200 { "cartonId": "…", "itemId": "…", "quantity": 12,
        "expectedWeightG": 3120,
        "remainingByLine": [{ "orderLineId": "…", "remaining": 0 }] }

← 409 { "type": "…/wrong-item",
        "detail": "SKU-88999 is not on this shipment",
        "scannedItem": { "skuCode": "SKU-88999" } }
← 409 { "type": "…/quantity-exceeds-line",
        "orderLineId": "…", "remaining": 4, "attempted": 12 }
```

**A legitimate refusal.** The item is in the packer's hand, not in the carton — nothing physical has happened, so rejecting asks nobody to undo anything.

---

**`POST /cartons/{id}/weigh`** · `pack.execute` · 2

```jsonc
→ { "actualWeightG": 3410 }
← 200 { "cartonId": "…", "expectedWeightG": 3360, "actualWeightG": 3410,
        "varianceG": 50, "tolerancePercent": 2.5, "status": "verified" }

← 409 { "type": "…/weight-mismatch",
        "expectedWeightG": 3360, "actualWeightG": 6480,
        "varianceG": 3120, "tolerancePercent": 2.5,
        "exceptionId": "018fb2…",
        "likelyCause": "double_pick",
        "overridePermission": "pack.override_weight" }
```

Blocks rather than warns. Scan verification cannot catch a double-pick where one scan accompanies two units; weight can.

---

**`POST /cartons/{id}/weigh/override`** · `pack.override_weight` · 2

```jsonc
→ { "actualWeightG": 6480, "reasonCode": "GEN_OTHER",
    "note": "Customer requested duplicate insert card, weight confirmed" }
← 200 { "status": "verified", "authorizedBy": "…", "exceptionId": "…" }
```

Records both the packer and the authoriser; the exception is resolved, not deleted.

---

**`POST /shipments/{id}/rate`** · `pack.execute` · 3

```jsonc
→ { "cartonIds": ["…"] }
← 200 { "quotes": [{ "carrierCode": "YMT", "serviceCode": "STD",
                     "costMinor": 98000, "currency": "JPY",
                     "transitDays": 2, "quoteId": "…",
                     "expiresAt": "…" }] }
← 503 { "type": "…/carrier-unreachable", "carrierCode": "YMT",
        "fallback": "tracking_number_pool" }
```

---

**`POST /cartons/{id}/label`** · `pack.execute` · 3

```jsonc
→ { "quoteId": "…" }
← 200 { "labelId": "…", "format": "zpl",
        "trackingNumber": "4471-8823-9910",
        "payloadUrl": "/api/v1/labels/018fb3…/payload",
        "printerHint": "ZEBRA-PACK-03",
        "source": "carrier" }        // carrier | pool
← 503 { "type": "…/carrier-unreachable",
        "detail": "No pooled numbers remain for YMT; dispatch will queue" }
```

ZPL goes to the printer over raw TCP 9100. Rendering to PDF and printing through a driver loses barcode fidelity, and a label that will not scan is not a label.

---

**`POST /shipments/{id}/dispatch`** · `shipment.dispatch` · 2

```jsonc
→ { "vehicleReference": "TRK-88", "dispatchedAt": "…" }
← 200 { "id": "…", "status": "dispatched",
        "cartonCount": 3, "movementIds": ["…"],
        "totalWeightG": 9840 }
← 409 { "type": "…/cartons-unverified", "unverifiedCartonNumbers": [2] }
← 409 { "type": "…/labels-missing", "cartonNumbers": [3] }
```

### 5.9 Media (Phase 2)

---

**`POST /media/presign`** · `media.upload`

```jsonc
→ { "contentType": "image/jpeg", "byteSize": 248113,
    "sha256": "a3f9c1…", "retentionClass": "evidence" }

← 200 { "assetId": "018fc1…", "deduplicated": false,
        "uploadUrl": "https://…?X-Amz-Signature=…",
        "uploadExpiresIn": 900, "maxByteSize": 10485760 }
← 200 { "assetId": "018fc0…", "deduplicated": true }   // hash already stored
← 413 { "type": "…/payload-too-large", "maxByteSize": 10485760 }
```

Deduplication by hash means re-imported catalogue images cost nothing.

---

**`POST /media/confirm`** · `media.upload`

```jsonc
→ { "assetId": "018fc1…" }
← 202 { "assetId": "…", "status": "processing" }
← 409 { "type": "…/upload-not-found", "detail": "No object at storage key" }
```

---

**`GET /media/{sha256}/{variant}`** · contextual · variant ∈ `thumb` | `card` | `full`

```
← 200  image/webp
       Cache-Control: public, max-age=31536000, immutable
← 302  → presigned URL, where the asset is access-controlled
← 404  rendition not ready yet — client falls back to blurhash
```

Content-addressed URLs are immutable, so caching needs no invalidation logic.

---

**`DELETE /media/{id}`** · `media.delete` → `204`, or `409 …/legal-hold`.
**`POST /media/{id}/legal-hold`** · `media.delete`

```jsonc
→ { "hold": true, "note": "Carrier claim CLM-2026-0441" }
← 200 { "id": "…", "legalHold": true, "expiresAt": null }
```

Setting a hold clears `expires_at`, exempting the asset from the retention sweep.

### 5.10 Counting and returns (Phase 3)

---

**`POST /count/plans`** · `count.plan.write`

```jsonc
→ { "warehouseId": "…", "name": "A-class monthly",
    "planType": "abc", "abcClasses": ["A"],
    "zoneIds": ["018f1c…"],
    "scheduleCron": "0 2 1 * *",
    "scheduleWindow": [{ "from": "22:00", "to": "05:00" }],
    "tolerancePercent": 0.5, "requiresRecount": true }
← 201 { "id": "…", "isActive": true, "nextRunAt": "…" }
```

`scheduleWindow` keeps fast-moving pick faces from being frozen during peak picking (Open Item 8).

---

**`POST /count/plans/{id}/generate`** · `count.plan.write` → `202 { "sheetIds": [...], "lineCount": 412, "locationsFrozen": 96 }`

---

**`GET /count/sheets/{id}`** · `count.execute`

```jsonc
← 200 {
    "id": "…", "sheetNumber": "CNT-TKY-260909-0003",
    "status": "counting", "zoneCode": "A", "dueDate": "2026-09-10",
    "lines": [{
      "id": "…",
      "location": { "code": "A-12-03-2" },
      "item": { "skuCode": "SKU-88213", "name": "…" },
      "lot": { "lotCode": "L2609A" },
      "uom": "EACH",
      "status": "pending"
    }],
    "progress": { "total": 96, "counted": 41, "variance": 3 }
  }
```

**`systemQuantity` is absent from the response schema entirely.** Blind counting is enforced by the contract, not by the UI, so a client bug cannot leak it.

---

**`POST /count/sheets/{id}/approve`** · `count.variance.approve`

```jsonc
→ { "lineDecisions": [
      { "countLineId": "…", "action": "accept_count",
        "reasonCode": "CNT_SHORT", "note": "Recount confirmed" },
      { "countLineId": "…", "action": "keep_system",
        "reasonCode": "CNT_MISSLOT", "note": "Found in A-12-04-1" }
    ] }

← 200 { "id": "…", "status": "approved",
        "adjustmentsPosted": 3,
        "movementIds": ["…"],
        "netShrinkageUnits": -7, "netOverageUnits": 2,
        "locationsUnfrozen": 96 }
← 403 { "type": "…/same-actor",
        "detail": "You counted 12 lines on this sheet" }
```

Shrinkage and overage are reported **separately, never netted**. A location running +50 and −50 has an accuracy problem that a net of zero conceals.

---

**`POST /count/lines/{id}/recount`** · `count.plan.write`

```jsonc
→ { "assignToUserId": null }            // null = any operator except the original
← 200 { "countLineId": "…", "recountTaskId": "…" }
```

---

**`POST /rma`** · `return.receive` · 3

```jsonc
→ { "warehouseId": "…", "originalOrderId": "…",
    "customerReference": "RET-88213",
    "expectedAt": "…",
    "lines": [{ "itemId": "…", "expectedQuantity": 2, "uom": "EACH" }] }
← 201 { "id": "…", "rmaNumber": "RMA-TKY-260909-0011", "status": "expected" }
```

---

**`POST /rma/{id}/lines/{lineId}/disposition`** · `return.disposition` · 3

```jsonc
→ { "disposition": "repair",           // restock | repair | return_to_vendor | scrap | hold
    "quantity": 2,
    "reasonCode": "DSP_REPAIR",
    "note": "Lid seal failure",
    "targetLocationId": "…",
    "mediaIds": ["018fc4…"] }

← 200 { "lineId": "…", "disposition": "repair",
        "movementIds": ["…"],
        "toLocation": { "code": "REPAIR-01" } }
← 400 { "type": "…/media-required",
        "detail": "Condition 'damaged' requires a photo" }
```

`scrap` moves to a virtual scrap location rather than deleting rows — units leave inventory without vanishing from the ledger.

---

**`GET /serials/{serialNumber}/genealogy`** · `inventory.read` · 3

```jsonc
← 200 {
    "serialNumber": "SN-4471882390",
    "item": { "skuCode": "SKU-88213", "name": "…" },
    "currentStatus": "shipped",
    "currentLocation": null,
    "chain": [
      { "sequence": 8102344, "movementType": "receipt",
        "recordedAt": "…", "toLocation": "RCV-01",
        "actor": "田中 健", "device": "HH-014",
        "reference": { "type": "receipt", "id": "…",
                       "number": "RCV-TKY-260812-0019" } },
      { "sequence": 8837412, "movementType": "dispatch",
        "recordedAt": "…", "fromLocation": "STG-02",
        "actor": "佐藤 美咲", "device": "HH-021",
        "reference": { "type": "shipment", "number": "SHP-TKY-260909-0221",
                       "trackingNumber": "4471-8823-9910" } }
    ]
  }
```

A single indexed query, not a report. If it is slow, the feature customers bought serial tracking for is not delivered.

### 5.11 Configuration and administration

---

**`GET /config/schema`** · `config.read` · 3

```jsonc
?category=allocation
← 200 { "items": [{
    "key": "allocation.default_strategy",
    "scopeLevels": ["deployment", "warehouse", "zone"],
    "valueType": "string",
    "jsonSchema": { "type": "string",
                    "enum": ["fefo", "fifo", "lifo", "nearest_bin",
                             "whole_case_first", "single_location_preferred"] },
    "defaultValue": "fifo",
    "label": "既定の引当戦略",
    "description": "…",
    "requiresRestart": false }] }
```

**The configuration UI is generated from `jsonSchema`.** Hand-building a settings screen per key does not survive past the first fifty, and this system will have several hundred.

---

**`PUT /config/{key}`** · `config.write` · 3

```jsonc
→ { "scopeType": "warehouse", "scopeId": "018f1a…",
    "value": "fefo", "note": "Customer requires expiry rotation" }

← 200 { "key": "allocation.default_strategy", "value": "fefo",
        "revision": 4, "effectiveFrom": "…",
        "requiresRestart": false, "previousValue": "fifo" }
← 400 { "type": "…/schema-violation",
        "errors": { "value": ["must be one of fefo, fifo, …"] } }
```

---

**`GET /config/export`** · `config.write` · 3

```jsonc
← 200 { "exportedAt": "…", "schemaVersion": "2026.09.1",
        "deploymentId": "…",
        "entries": [{ "key": "…", "scopeType": "warehouse",
                      "scopeRef": "TKY", "value": "fefo", "revision": 4 }],
        "checksum": "sha256:…" }
```

Scopes export by **code**, not by UUID, so the artefact imports into a clean deployment where ids differ.

---

**`POST /config/import`** · `config.write` · 3

```jsonc
→ { "dryRun": true, "artefact": { … } }
← 200 { "dryRun": true, "wouldApply": 41, "wouldSkip": 3,
        "conflicts": [{ "key": "…", "reason": "unknown warehouse code OSK" }] }
```

---

**`POST /warehouses`** · `warehouse.manage` · 1A

```jsonc
→ { "code": "TKY", "name": "Tokyo DC1", "timezone": "Asia/Tokyo",
    "dayBoundaryTime": "00:00", "defaultLocale": "ja" }
← 201 { "id": "018f0a…", "code": "TKY", "status": "active", "version": 1 }
← 409 { "type": "…/code-in-use", "code": "TKY" }
```

Seeded once at provisioning (§6.20); this endpoint is what lets a customer
add a second site later with no migration — the schema has carried
`warehouse_id` scoping since the first table specifically so this is a
row insert, not a schema change (invariant-equivalent reasoning to C7's
tenancy point, applied to sites instead of tenants).

---

**`GET /warehouses`** · `warehouse.read` · 1A

```jsonc
← 200 { "items": [{ "id": "…", "code": "TKY", "name": "Tokyo DC1",
                    "timezone": "Asia/Tokyo", "status": "active" }] }
```

---

**`PATCH /warehouses/{id}`** · `warehouse.manage` · 1A — requires `If-Match: <version>`.

```jsonc
→ { "name": "Tokyo DC1 (Ōta)", "dayBoundaryTime": "05:00" }
← 200 { "id": "…", "version": 2 }
← 412 { "type": "…/version-conflict", "currentVersion": 3 }
```

Changing `dayBoundaryTime` only affects `business_date` derivation (M4)
for movements recorded *after* the change — it never rewrites history.

---

**`POST /zones`** · `zone.manage` · 1A

```jsonc
→ { "warehouseId": "018f0a…", "code": "A",
    "name": { "en": "Zone A", "ja": "Aゾーン" },
    "zoneType": "pick", "temperatureClass": null }
← 201 { "id": "018f0b…", "code": "A", "status": "active", "version": 1 }
```

---

**`GET /zones`** · `zone.read` · 1A

```jsonc
?warehouseId=018f0a…
← 200 { "items": [{ "id": "…", "code": "A", "zoneType": "pick", "status": "active" }] }
```

---

**`PATCH /zones/{id}`** · `zone.manage` · 1A — requires `If-Match: <version>`.

```jsonc
→ { "status": "retired" }
← 200 { "id": "…", "status": "retired", "version": 2 }
← 409 { "type": "…/zone-has-active-locations",
        "detail": "12 active locations reference this zone" }
```

A zone with active locations can't retire out from under them — retire or
relocate its locations first. Existing tasks and balances referencing an
already-retired zone are untouched; retirement only blocks *new* work from
being directed there.

---

**`POST /items`** · `item.write` · 1A

```jsonc
→ { "skuCode": "SKU-88213",
    "name": { "en": "Stainless water bottle 500ml",
              "ja": "ステンレス製水筒 500ml" },
    "baseUom": "EACH",
    "isLotTracked": true, "isExpiryTracked": true, "isSerialTracked": false,
    "shelfLifeDays": 540, "abcClass": "A",
    "uoms": [
      { "uomCode": "EACH", "qtyInBase": 1, "grossWeightG": 260,
        "isDiscrete": true, "isPickingDefault": true },
      { "uomCode": "CASE", "qtyInBase": 24, "grossWeightG": 6480,
        "isDiscrete": true, "isReceivingDefault": true }
    ],
    "barcodes": [
      { "barcode": "4901234567890", "uomCode": "EACH", "barcodeType": "EAN13" },
      { "barcode": "14901234567897", "uomCode": "CASE", "barcodeType": "ITF14" }
    ] }

← 201 { "id": "…", "skuCode": "SKU-88213", "version": 1 }
← 409 { "type": "…/barcode-in-use",
        "barcode": "4901234567890", "existingSkuCode": "SKU-11002" }
```

---

**`PATCH /items/{id}`** · `item.write` · 1A — requires `If-Match: <version>`.

```jsonc
← 409 { "type": "…/immutable-field",
        "detail": "baseUom cannot change once movements exist",
        "field": "baseUom", "movementCount": 41230 }
← 412 { "type": "…/version-conflict", "currentVersion": 7 }
```

`baseUom` and `isSerialTracked` are immutable once stock exists — changing either silently reinterprets historical quantities.

---

**`POST /locations`** · `location.write` · 1A

```jsonc
→ { "warehouseId": "…", "zoneId": "…", "code": "A-12-03-2",
    "locationType": "bin", "aisle": "A-12", "bay": "03", "level": "2",
    "pickSequence": 1203,
    "maxWeightG": 250000, "maxVolumeCm3": 480000,
    "allowsMixedItem": false, "allowsMixedLot": false }
← 201 { "id": "…", "code": "A-12-03-2", "status": "active", "version": 1 }
```

---

**`PATCH /locations/{id}`** · `location.write` · 1A — requires `If-Match: <version>`.

```jsonc
→ { "status": "blocked", "maxWeightG": 200000 }
← 200 { "id": "…", "status": "blocked", "version": 5 }
```

`status: blocked` excludes a location from putaway direction and
allocation without touching any balance row already there — a damaged
rack gets taken out of rotation, not emptied.

---

**`POST /handling-units`** · `handlingunit.write` · 1A

```jsonc
→ { "lpn": "PLT-004471", "huType": "pallet", "parentHuId": null,
    "currentLocationId": "018f0c…" }
← 201 { "id": "018f0d…", "lpn": "PLT-004471", "status": "active" }
← 409 { "type": "…/lpn-in-use", "lpn": "PLT-004471" }
```

Registers the barcode identity before it's ever scanned — a receipt or
putaway fact referencing an unregistered LPN is rejected as malformed
(§7.1), not silently accepted as a phantom container.

---

**`GET /handling-units`** · `handlingunit.read` · 1A

```jsonc
?currentLocationId=018f0c…&status=active
← 200 { "items": [{ "id": "…", "lpn": "PLT-004471", "huType": "pallet",
                    "currentLocation": { "code": "STG-02" } }] }
```

---

**`POST /devices`** · `device.manage` · 1A

```jsonc
→ { "warehouseId": "018f0a…", "label": "HH-014" }
← 201 { "id": "018f1b…", "label": "HH-014", "status": "active" }
```

An unregistered device cannot authenticate (§6.1) — this is the step
before the first `POST /auth/operator/login` from a new handheld can ever
succeed.

---

**`GET /devices`** · `device.manage` · 1A

```jsonc
?warehouseId=018f0a…&status=active
← 200 { "items": [{ "id": "…", "label": "HH-014", "status": "active",
                    "lastSeenAt": "…", "queueDepth": 2,
                    "claimedBy": "田中 健" }] }
```

`claimedBy` reflects the current `device_session`, if any — this is the
screen a supervisor checks before assuming a handheld is idle.

---

**`PATCH /devices/{id}`** · `device.manage` · 1A

```jsonc
→ { "status": "retired" }
← 200 { "id": "…", "status": "retired" }
← 409 { "type": "…/device-claimed",
        "detail": "Cannot retire a device with an active session; end it first" }

→ { "status": "lost" }
← 200 { "id": "…", "status": "lost",
        "sessionEnded": true, "refreshTokensRevoked": 2 }
```

**`retired` and `lost` are not the same precondition (H11).** `retired` is
planned decommissioning and still requires no active session — routine
housekeeping. `lost` is an emergency and has no precondition at all: it
**cascades**, ending the device's active `device_session`
(`end_reason = 'forced'`) and revoking every `refresh_token` bound to it,
in the same transaction. This is one device's own session and tokens — a
tight, device-scoped transaction, not a fan-out into other devices or
users.

---

**`POST /imports`** · `item.write` · 2

```jsonc
→ { "importType": "opening_balance", "fileKey": "uploads/018fd1…" }
← 202 { "id": "018fd2…", "status": "validating" }
```

**`GET /imports/{id}`**

```jsonc
← 200 { "id": "…", "importType": "opening_balance",
        "status": "validated",        // validating|validated|failed|applying|applied
        "totalRows": 41230, "processedRows": 41230, "errorCount": 3,
        "errors": [{ "rowNumber": 118, "columnName": "locationCode",
                     "message": "Unknown location A-99-01-1",
                     "rawRow": { … } }],
        "applyUrl": "/api/v1/imports/018fd2…/apply" }
```

**Validate-then-apply.** Nothing is written until validation passes entirely. A half-applied opening balance is worse than a rejected one (§6.22).

---

**`POST /users`** · `user.manage`, plus `role.manage` when `roleScopes` is non-empty and `credential.manage` when `credentials` is non-empty · 1A

```jsonc
→ { "userType": "operator", "displayName": "田中 健",
    "employeeCode": "EMP00412", "locale": "ja",
    "validFrom": "2026-09-01", "validUntil": "2026-12-31",
    "credentials": [{ "type": "badge", "secret": "…" },
                    { "type": "pin", "secret": "4821" }],
    "roleScopes": [{ "roleCode": "PICKER", "warehouseId": "…",
                     "zoneIds": ["018f1c…"] }] }
← 201 { "id": "…", "employeeCode": "EMP00412", "status": "active" }
← 403 { "type": "…/insufficient-permission",
        "detail": "Assigning role scopes at creation also requires 'role.manage'." }
```

`validUntil` defaults to 90 days for operators, preventing the accumulation of live credentials for departed agency staff.

Every `password` credential created here is seeded with `must_change = true` (§2.1, §5.1) — the account's very first staff login returns `.../credential-change-required` rather than a session, regardless of who set the initial value. `pin` and `badge` credentials are never flagged this way (§5.1). This covers the *first-login* case only; an admin-initiated reset of an *existing* credential is the separate "future credential-reset endpoint" this section has long anticipated, and remains its own open item, not resolved here.

**`user.manage` alone opens an account; it does not staff it.** Assigning a
role or setting a credential are the same acts `POST /users/{id}/role-scopes`
and a future credential-reset endpoint gate — reachable here by a shorter
route, so they carry the same permission. Without this, `user.manage` would
be an identity-minting capability: create a user, hand it a role, set its
password, all under one permission, which is invariant 9's segregation of
duties failing at the one place it matters most. The H13 subset check
(`role.manage` holders may still only grant permissions they themselves
hold) applies underneath exactly as it does on the dedicated grant
endpoint — `role.manage` clears the gate, the subset check bounds the
damage.

---

**`POST /users/{id}/role-scopes`** · `role.manage` · 1A

```jsonc
→ { "roleCode": "SUPERVISOR", "warehouseId": "…", "zoneIds": [] }
← 201 { "id": "…", "grantedBy": "…", "grantedAt": "…" }
```

Every grant writes an `auth_event`. "Who gave this person approval rights, and when" is the first question in any shrinkage investigation.

---

**`GET /users`** · `user.manage` · 1A

```jsonc
?warehouseId=018f0a…&userType=operator&status=active&q=田中
← 200 { "items": [{ "id": "…", "displayName": "田中 健",
                    "employeeCode": "EMP00412", "userType": "operator",
                    "status": "active", "validUntil": "2026-12-31",
                    "roleScopes": [{ "roleCode": "PICKER", "warehouseCode": "TKY" }] }],
        "nextCursor": { "afterId": "…" }, "hasMore": false }
```

---

**`GET /users/{id}`** · `user.manage` · 1A

```jsonc
← 200 { "id": "…", "displayName": "田中 健", "userType": "operator",
        "locale": "ja", "status": "active",
        "validFrom": "2026-09-01", "validUntil": "2026-12-31",
        "roleScopes": [{ "roleCode": "PICKER", "warehouseCode": "TKY",
                         "zoneCodes": ["A"] }],
        "securityStamp": "018f2e…" }
```

---

**`PATCH /users/{id}`** · `user.manage` · 1A

```jsonc
→ { "displayName": "田中 健一", "validUntil": "2027-03-31" }
← 200 { "id": "…", "status": "active" }

→ { "status": "suspended" }
← 200 { "id": "…", "status": "suspended",
        "activeDeviceQueueDepth": 7 }
```

`status: suspended`/`ended` bumps `security_stamp`, rejected on the very
next request from any of that user's tokens (§8.1) — including
`/sync/facts`, which `unauthenticated` already permits refusing (§3.3,
§4.1). **`activeDeviceQueueDepth` surfaces the risk that creates (M6):**
if the user's device had unsynced queued facts (from its last heartbeat,
§5.1) at the moment of suspension, that physical work can never sync
afterward — the device can't obtain a valid token again for this
identity. A nonzero value doesn't block the suspension (a genuine security
incident shouldn't wait), it just means the admin sees the risk at the
moment they act instead of discovering an unexplained reconciliation gap
later. Also writes an `auth_event`. **There is no `DELETE /users/{id}`.**
A user is deactivated, never removed: every movement and auth event
they're `actor_user_id`/`raised_by_user_id` on stays attributed.

---

**`GET /roles`** · `role.manage` · 1A

```jsonc
?includeInactive=false
← 200 { "items": [{ "code": "PICKER",
                    "name": { "en": "Picker", "ja": "ピッカー" },
                    "isSystem": true, "isActive": true,
                    "permissions": ["task.lease", "pick.execute",
                                    "putaway.execute", "receipt.confirm"] },
                  { "code": "SENIOR_PICKER",
                    "name": { "en": "Senior Picker", "ja": "シニアピッカー" },
                    "isSystem": false, "isActive": true,
                    "permissions": ["task.lease", "pick.execute",
                                    "putaway.execute", "pick.short",
                                    "task.reassign"] }] }
```

Populates the role-assignment dropdown behind `POST /users/{id}/role-scopes`.
Both seeded system roles and site-composed custom roles are returned
together — a role is a role to anything consuming this list; `isSystem`
only matters to the three endpoints below.

---

**`GET /permissions`** · `role.manage` · 1A

```jsonc
?category=picking
← 200 { "items": [{ "code": "pick.execute", "category": "picking",
                    "description": { "en": "Execute a pick task", "ja": "…" } },
                  { "code": "pick.short", "category": "picking",
                    "description": { "en": "Confirm a short pick", "ja": "…" } }] }
```

The full permission catalogue — what a custom role is actually composed
*from*. Grouped by `category` so the role-builder UI doesn't present one
flat list of everything the system can do.

---

**`POST /roles`** · `role.manage` · 1A

```jsonc
→ { "code": "SENIOR_PICKER",
    "name": { "en": "Senior Picker", "ja": "シニアピッカー" },
    "permissionCodes": ["task.lease", "pick.execute", "putaway.execute",
                        "pick.short", "task.reassign"] }
← 201 { "id": "018f3c…", "code": "SENIOR_PICKER", "isSystem": false,
        "isActive": true, "version": 1 }
← 409 { "type": "…/code-in-use", "code": "SENIOR_PICKER" }
← 400 { "type": "…/unknown-permission",
        "detail": "task.reassignx is not a recognised permission code" }
← 403 { "type": "…/cannot-grant-permission-you-lack",
        "detail": "Requesting admin does not hold task.reassign",
        "permissionCodes": ["task.reassign"] }
```

A custom role is a **site-specific arrangement** of existing permissions
(proposal §10.4) — this endpoint never creates a new permission, only a
named bundle of ones that already exist in the catalogue. **A grantor can
never bundle in a permission they don't themselves currently hold (H13)**
— `permissionCodes` is validated as a subset of the requesting admin's own
effective permission set before the role is created. System
Administrators hold the full catalogue by design and are unaffected; this
constrains delegation, not top-level administration.

---

**`PATCH /roles/{id}`** · `role.manage` · 1A — requires `If-Match: <version>`.

```jsonc
→ { "permissionCodes": ["task.lease", "pick.execute", "putaway.execute",
                        "pick.short"] }
← 200 { "id": "…", "version": 2, "propagating": true }

→ { "isActive": false }
← 200 { "id": "…", "isActive": false }

← 403 { "type": "…/system-role-immutable",
        "detail": "SUPERVISOR is a system role; its permission set is fixed" }
← 403 { "type": "…/cannot-grant-permission-you-lack",
        "permissionCodes": ["inventory.adjust.approve"] }
```

The same grant-what-you-hold check as `POST /roles` applies to any
permission being *added*. A permission change writes one
`outbox_message` (`RolePermissionsChanged`) in the same transaction as the
update — `propagating: true` means the Worker's role-permission-change job
(§1.3) will bump `security_stamp` for every current holder shortly after,
rather than each of them keeping a stale-permission token for up to
~15 minutes (H12). This is the outbox pattern already used for every
other cross-aggregate effect, not a new exception to C6.

**A system role (`isSystem: true`) can never be edited or deactivated
through this endpoint** — that's the entire distinction `is_system`
exists to enforce (§2.1). Deactivating a custom role is the only removal
path; there is no `DELETE /roles/{id}`, the same "deactivate, never
delete" rule as every other entity `user_role_scope` can reference — an
operator's history of having held a now-retired role stays attributed.

---

**`GET /reason-codes`** · `user.read` · 1A

```jsonc
?movementType=pick&category=pick_discrepancy
← 200 { "items": [{ "code": "PICK_NOT_FOUND",
                    "label": "ロケーション在庫なし",
                    "category": "pick_discrepancy",
                    "requiresNote": false, "requiresPhoto": false,
                    "requiresApproval": false, "sortOrder": 210 }] }
```

Filtered by `applies_to` so a handheld shows six options, not thirty-four.

### 5.12 Reporting

All reads execute against the **replica** and group on `business_date`.
**If the replica is deferred as a Phase 1A shortcut** (`docs/shortcuts.md`
— reasonable before real concurrent load exists to isolate from), these
same reads execute against the primary instead; nothing about their
contract changes, only where they run.

---

**`GET /dashboard`** · `report.read` · 1A

```jsonc
?warehouseId=018f0a…
← 200 {
    "warehouse": { "id": "…", "code": "TKY" },
    "generatedAt": "2026-09-09T07:20:14+09:00",
    "receiving": { "openReceipts": 4, "receiptsCompletedToday": 12,
                   "openDiscrepancies": 3 },
    "putaway": { "tasksReady": 12, "tasksLeased": 3,
                 "tasksCompletedToday": 87 },
    "exceptions": { "open": 14, "oldestAgeHours": 9.4,
                    "byType": { "over_receipt": 3, "under_receipt": 1,
                                "location_mismatch": 1,
                                "negative_balance": 2 } },
    "sync": { "devicesActive": 6, "devicesWithBacklog": 1,
              "maxQueueDepth": 4 },
    "reconciliation": { "lastRunAt": "…", "varianceCount": 0 },
    "stockOnHand": { "skuCount": 1204, "onHand": 88213 }
  }
```

**The admin site's home screen, in one call.** Every field is assembled
from data the design already tracks elsewhere — `exceptions` is the same
summary block as `GET /inventory/exceptions` (§5.5), `reconciliation` the
latest row of `GET /inventory/reconciliation/runs` (§5.5), `sync` an
aggregate over `GET /devices`' `queueDepth` (§5.1) — so this endpoint adds
no new source of truth, only a single aggregated read scoped to Phase
1A's own surfaces (receiving, putaway, the ledger, the exception queue).
It deliberately excludes anything from picking, packing, shipping, or
allocation — those aren't real until Phase 1B, and a dashboard tile for
them here would be the same "building Phase 1B during 1A" process bug as
building the feature itself early.

---

**`GET /reports/stock-on-hand`** · `report.read` · 1A

```
?warehouseId=…&zoneId=…&abcClass=A&groupBy=zone|item|owner&asOfDate=…
```

```jsonc
← 200 { "asOfDate": "2026-09-09", "generatedAt": "…",
        "rows": [{ "zoneCode": "A", "skuCount": 1204,
                   "onHand": 88213, "allocated": 4102, "available": 84111,
                   "valueMinor": null }],
        "totals": { … } }
```

---

**`GET /reports/shrinkage`** · `report.read` · 2

```jsonc
?businessDateFrom=2026-08-01&businessDateTo=2026-08-31&groupBy=reasonCode
← 200 { "rows": [{ "reasonCode": "PICK_NOT_FOUND",
                   "label": "ロケーション在庫なし",
                   "events": 214, "unitsLost": 481 }],
        "totals": { "shrinkageUnits": 1102, "overageUnits": 340 } }
```

Driven by `reason_code.is_shrinkage`. Overage is reported alongside, never netted against it.

---

**`GET /reports/inventory-accuracy`** · `report.read` · 3

```jsonc
← 200 { "period": "2026-08",
        "locationsCounted": 4120, "locationsAccurate": 4061,
        "accuracyPercent": 98.57,
        "phantomRate": { "pickNotFoundEvents": 214, "picksTotal": 88213,
                         "percent": 0.24 } }
```

`phantomRate` derives from `PICK_NOT_FOUND` and is the best continuously-available proxy for inventory accuracy between counts.

---

**`POST /reports/{name}/export`** · `report.export` · 1A

```jsonc
→ { "format": "csv", "parameters": { … } }
← 202 { "jobId": "018fe1…", "status": "queued",
        "statusUrl": "/api/v1/reports/exports/018fe1…" }
```

**`GET /reports/exports/{jobId}`**

```jsonc
← 200 { "jobId": "…", "status": "ready", "rowCount": 412300,
        "downloadUrl": "https://…", "expiresAt": "…" }
```

Always asynchronous. A synchronous export of a year of movements would hit the API's own 5-second statement timeout.

### 5.13 Fleet (Phase 4, control plane)

---

**`GET /_fleet/health`** · control-plane credential · 4

```jsonc
← 200 {
    "deploymentId": "…", "customerCode": "ACME-TKY",
    "appVersion": "2.4.1", "schemaVersion": "2026.09.1",
    "configRevision": 41, "uptimeSeconds": 918233,
    "queues": { "outboxPending": 3, "allocationBacklog": 0,
                "exceptionsOpen": 14, "exceptionMaxAgeHours": 9.4,
                "syncBacklogMax": 7 },
    "reconciliation": { "lastRunAt": "…", "varianceCount": 0 },
    "database": { "connectionsUsed": 18, "connectionsMax": 40,
                  "replicaLagSeconds": 0.4 }
  }
```

**The only control-plane coupling.** No customer data crosses this boundary — once a control plane can read operational data "just for support", single-tenant isolation is decorative.

---

**`POST /fleet/releases/{version}/rollout`** · operator console · 4

```jsonc
→ { "ring": "canary", "soakHours": 48,
    "haltOn": { "errorRateIncreasePercent": 20,
                "scanP95IncreasePercent": 50,
                "anyBalanceVariance": true } }
← 202 { "rolloutId": "…", "deploymentCount": 2, "status": "in_progress" }
```

**`POST /fleet/rollouts/{id}/halt`** → `200 { "status": "halted", "upgraded": 1, "remaining": 1 }`

Halt is also triggered automatically when any `haltOn` condition is met.

---

## 6. Data Flows

Transaction boundaries are marked `[TX]`. Anything not inside one is eventually consistent by design.

### 6.1 Operator sign-on (Phase 1A)

```
Operator          Device                API                     DB
   │ scan badge ────▶│                    │                      │
   │                 ├─ POST /auth/operator/login ──────────────▶│
   │                 │                    ├─[TX] verify credential
   │                 │                    │     check valid_from / valid_until
   │                 │                    │     check failed_attempts / locked_until
   │                 │                    │     INSERT device_session
   │                 │                    │     INSERT auth_event(login)
   │                 │                    │     issue access + refresh ──────▶│
   │                 │◀── 200 + permissions, scope, locale, minClientVersion  │
   │                 ├─ cache to IndexedDB
   │                 ├─ set UI locale from user.locale
```

Failure paths: unknown badge → `401` with a deliberately generic message; locked → `423` with `retryAfter`; client below `minClientVersion` → `426`.

### 6.2 Blind receipt with over-receipt (Phase 1A)

The flow that proves the command/fact split, and Phase 1A exit criterion 5.

```
1. Supervisor: POST /receipts { type: "blind", warehouseId }        [command]
2. Supervisor: POST /receipts/{id}/start
3. Operator:   POST /work/leases { taskTypes: ["receive"] }         [command]
4. Operator scans item barcode → resolved locally from cached map
5. Operator counts 106 against an expected 100
6. Device queues fact:
     { type: "receipt_confirmed", clientFactId,
       payload: { receiptLineId, itemId, quantity: 106,
                  expectedQuantity: 100, uom: "EACH",
                  toLocationId: <receiving>, reasonCode: "RCV_SUPPLIER_OVER" } }
7. POST /sync/facts
     [TX] idempotency check
          UPDATE stock_balance  on_hand += 106      (receiving location)
          INSERT stock_movement quantity_base = +106, movement_type = 'receipt'
          UPDATE receipt_line   received_quantity = 106,
                                discrepancy_type = 'over'
          INSERT inventory_exception (over_receipt, expected 100, actual 106)
          INSERT outbox_message  ReceiptLineConfirmed
          INSERT idempotency_record
     COMMIT
8. ← 202 { status: "accepted", exceptionIds: [...] }
9. Device advances. No error dialogue.
10. Supervisor: POST /receipts/{id}/complete
     → generates putaway tasks for the ACTUAL 106 units
```

Step 7 is where the design either holds or fails. The discrepancy is a supervisor's problem, surfaced through the exception queue — not a picker's problem, surfaced as a modal on a handheld.

`RCV_SUPPLIER_OVER` carries `requires_approval = true`, so in practice step 6 is preceded by an elevation (§6.5).

**A line may be confirmed more than once, and `received_quantity` accumulates.** Two receivers working a single pallet line, or one device draining a queue holding two partial counts, both produce a second `receipt_confirmed` against the same `receiptLineId` with its own idempotency key — so neither is a replay, and invariant 4 forbids refusing it. `received_quantity` is therefore `received_quantity + quantity`, never `= quantity`; assigning would let the second fact overwrite the first and leave the line disagreeing with the ledger. `discrepancy_type` follows from the **accumulated total** rather than from the incoming fact, so 40-then-60 against an expected 100 ends as `none` — the second confirmation clears the first's under-receipt instead of adding another. The `inventory_exception` an over- or under-receipt raises records the line's running total as `actual_quantity`, so a supervisor compares like with like against `expected_quantity`.

The step-7 sketch above shows `received_quantity = 106` because it narrates a single confirmation, not because a line may only be confirmed once.

### 6.3 Directed putaway, offline (Phase 1A)

```
[online]  POST /work/leases { taskTypes: ["putaway"], batchSize: 20 }
          ← tasks with from/to locations, item names, barcodes, thumbnails
          device writes the batch to IndexedDB

[offline] operator scans source location  → validated against cached task_line
          scans item barcode              → validated against cached barcode map
          scans destination location      → validated against directed to_location
          confirms quantity
          → fact queued locally with clientFactId generated NOW
          → UI advances immediately; the operator never waits

          ... repeat for 20 tasks, entirely offline ...

[online]  service worker drains the queue:
          POST /sync/facts { facts: [ ...20 items... ] }
          [TX per fact]
              UPDATE stock_balance  −qty at source, +qty at destination
              INSERT stock_movement (from_location, to_location)
              UPDATE task_line, task
              INSERT outbox_message, idempotency_record
          ← 202 with 20 individual results
          device clears acknowledged entries
```

**Both positions move in one statement, in a fixed order.** A putaway decrements the source and increments the destination — two `stock_balance` rows in one transaction. They are written by a single upsert whose feeding subplan is ordered by the conflict-target tuple, because two operators moving stock A→B and B→A at the same moment would otherwise acquire the two rows in opposite orders and one side would die with SQLSTATE `40P01` mid-shift. That ordering is a system-wide rule for any movement touching more than one position, not a local detail of putaway, and it is covered by `OpposingTransfersBetweenTwoBins_NeverDeadlock`.

**A partial putaway is not a divergence, and raises nothing.** If the operator moves 60 of a directed 100, the ledger records −60 at the source and +60 at the destination, and the residual 40 is still in the source location where the balance already shows it. Nothing is lost or unaccounted for, so there is nothing for a supervisor to resolve — the task line closes as `short` rather than staying `pending`, because sending the operator back to a bin they have already worked is worse than closing it. This is the difference from a short *pick*, which does raise: there the order cannot be fulfilled, and someone must decide what happens next.

**A line confirmed twice raises `reallocated_task`.** Two operators can both physically carry stock from the same source before either syncs. Both movements are real and both are written; the second drives the source negative, which is true information (C1, C3), and the exception is what sends someone to find out which bin the stock actually ended up in.

**Destination mismatch is a fact, not an error.** If the operator put stock in `A-14-02` when directed to `A-12-03` — because the directed bin was full — the movement records where it actually went, and a `location_mismatch` exception is raised. Rejecting it would leave the system believing stock is somewhere it is not, which is strictly worse than an exception.

### 6.4 Sync conflict and recovery (Phase 1A)

```
Case A — duplicate submission (retry after a timeout)
  POST /sync/facts with the same clientFactId
  [TX] idempotency hit, request hash matches
  ← 202 { status: "duplicate", movementIds: [ <original> ] }
  Device clears the entry. Exactly one movement exists.

Case B — same key, different payload (client bug)
  [TX] idempotency hit, hash differs
  ← 409 { type: ".../idempotency-conflict" }
  Device quarantines the entry and reports to diagnostics.

Case C — device reboot mid-shift
  IndexedDB survives. On next load the queue is replayed.
  Any facts already accepted return "duplicate". No double posting.

Case D — task reclaimed while the device was offline
  Both operators picked. Both movements are recorded.
  A reallocated_task exception is raised.
  This is correct: two people genuinely removed stock.

Case E — batch containing one malformed fact
  Nine accepted, one rejected individually.
  One bad payload never blocks a shift's queued work.
```

### 6.5 Supervisor override (Phase 1A)

```
1. Operator hits an action lacking permission
     → 403 { type: ".../insufficient-permission",
             elevation: { possible: true, action: "receipt.over_receive" } }
2. UI presents a badge-scan modal
3. POST /auth/elevate { badge, action, contextId }              [requires network]
     [TX] verify supervisor credential
          confirm supervisor holds the permission in this scope
          confirm supervisor.id != operator.id     ← segregation of duties
          INSERT auth_event(elevate_granted)
     ← { grantToken, expiresIn: 120 }
4. Original request resubmitted with X-Elevation-Grant
5. Movement records actor_user_id = operator,
                    authorized_by_user_id = supervisor
6. Device session unchanged. Operator remains logged in.
```

Grant tokens are single-use and bound to `(action, contextId, operator)`. A grant for one over-receipt cannot authorise the next.

### 6.6 Manual adjustment with approval (Phase 1A)

```
1. POST /inventory/adjustments { itemId, locationId, quantityDelta: -3,
                                 reasonCode: "DMG_HANDLING", note, mediaIds }
   → reason_code.requires_approval = true, requires_photo = true
   → validation rejects if mediaIds is empty
   [TX] INSERT adjustment (status = pending_approval)
   ← 202 { adjustmentId, status: "pending_approval" }

2. POST /inventory/adjustments/{id}/approve                  [different user]
   [TX] assert approver != actor
        UPDATE stock_balance  on_hand −3
        INSERT stock_movement movement_type = 'adjustment', reason DMG_HANDLING
        UPDATE adjustment status = approved
        INSERT outbox_message
   ← 200
```

The adjustment is a **command** throughout, because nothing physical is being reported — the operator is asserting the record is wrong. That is refusable in a way a completed pick is not.

### 6.7 Order release and allocation (Phase 1B)

The system's hardest correctness problem.

```
Admin                API                        Alloc Worker              DB
  ├─ POST /orders/{id}/release ─▶│                                          │
  │                   [TX] authorise order.release                          │
  │                        INSERT allocation_request(queued) ──────────────▶│
  │                        UPDATE order.status = 'allocating'               │
  │◀── 202 Accepted ──┤                                                     │
  │                                │                                        │
  │                                ├─ pg_advisory_lock(hash('alloc:'||wh))  │
  │                                ├─ SELECT allocation_request
  │                                │    FOR UPDATE SKIP LOCKED
  │                                │
  │                                ├─[TX] for each order_line:
  │                                │   candidates = strategy.rank(
  │                                │        available balances, config)
  │                                │   for candidate in sort_by_primary_key(candidates):
  │                                │       UPDATE stock_balance
  │                                │         SET allocated = allocated + q
  │                                │       WHERE ... AND on_hand - allocated >= q
  │                                │       RETURNING *          ← 0 rows: next candidate
  │                                │       INSERT allocation
  │                                │   UPDATE order_line.allocated_quantity
  │                                │
  │                                │   INSERT task + task_line (status='ready')
  │                                │   INSERT outbox_message OrderAllocated
  │                                │   UPDATE allocation_request → done
  │                                └─ COMMIT
```

Three details carry the correctness:

- **The advisory lock** gives exactly one allocator per warehouse, so allocation never races itself.
- **The conditional `UPDATE`** determines sufficiency atomically, with no read-modify-write. Zero rows means insufficient stock.
- **Primary-key lock ordering** prevents deadlock against concurrent pick confirmations touching the same balance rows.

Partial allocation sets `partially_allocated` and leaves the remainder unallocated. It does not fail the order — a partially picked order is usually more useful than none.

### 6.8 Pick and confirm (Phase 1B)

```
1. POST /work/leases { taskTypes: ["pick"] }
     [TX] UPDATE task SET status='leased', lease_* 
          WHERE id IN (SELECT ... FOR UPDATE SKIP LOCKED LIMIT 20)
     ← self-contained task payload

2. [offline-capable] scan bin → scan item → confirm quantity

3. POST /sync/facts { type: "pick_confirmed", quantity: 12 }
     [TX] idempotency check
          UPDATE stock_balance
            SET on_hand   = on_hand - 12,
                allocated = GREATEST(allocated - 12, 0)
            WHERE ...                          ← NO guard clause
          INSERT stock_movement quantity_base = -12, movement_type='pick'
          UPDATE task_line confirmed_quantity = 12, status='confirmed'
          UPDATE task status='completed' WHERE no pending lines
          UPDATE allocation status='picked'
          INSERT outbox_message, idempotency_record
     COMMIT
   ← 202
```

### 6.9 Short pick (Phase 1B)

Phase 1B exit criterion 2, and the reason `/sync/facts` exists.

```
Sent for 12. Found 6.

POST /sync/facts { type: "pick_confirmed", quantity: 6,
                   reasonCode: "PICK_SHORT" }
  [TX] UPDATE stock_balance  on_hand −6, allocated −6
       INSERT stock_movement −6
       UPDATE task_line confirmed_quantity=6, status='short'
       INSERT inventory_exception (short_pick, expected 12, actual 6)
       UPDATE allocation SET status='released' for the remaining 6
       ── if on_hand now < 0:
            INSERT inventory_exception (negative_balance)
  COMMIT
← 202 { status: "accepted", exceptionIds: [...] }
```

The remaining 6 are **released, not reallocated automatically**. Reallocating inside the pick transaction would extend it across aggregates and could send a second picker to a bin that is now known to be wrong. Re-allocation happens through the allocation worker, on the next cycle, with current information.

`PICK_SHORT` and `PICK_NOT_FOUND` are deliberately friction-free: no note, no photo, no approval. A picker at an empty bin needs to move on, and friction here means they pick a different code or skip the task, destroying the accuracy signal.

### 6.10 Lease expiry and reclaim (Phase 1A/1B)

```
Sweep job (every 5 min):
  SELECT tasks WHERE status='leased' AND lease_expires_at < now()
  → does NOT auto-release. Writes to a supervisor review queue.

Supervisor opens GET /work/leases/expired:
  each row shows lease_heartbeat_at → "last seen 45 minutes ago"

POST /work/tasks/{id}/reclaim         [permission: task.reassign]
  [TX] UPDATE task SET status='ready', lease_* = NULL
       INSERT auth_event(task_reclaimed)

If the original holder later syncs a confirmation for that task:
  the fact is STILL accepted (§6.4 case D)
  → movement recorded, reallocated_task exception raised
```

Automatic reclamation is deliberately not implemented. A device out of wifi coverage is indistinguishable from a dead one at the server, and auto-releasing produces the duplicate pick the lease exists to prevent.

### 6.11 Wave release and zone picking (Phase 2)

The only genuinely new correctness problem in Phase 2: one order is now picked by several people in parallel and must be reassembled.

```
1. POST /waves { orderIds, waveType: "carrier_cutoff", strategy: {...} }   [command]
     [TX] INSERT wave (planning), INSERT wave_order rows

2. POST /waves/{id}/release
     [TX] INSERT allocation_request per order, tagged wave_id
          UPDATE wave.status = 'allocating'
     ← 202

3. Allocation worker processes each request under the SAME per-warehouse
   advisory lock as §4.7. No new concurrency mechanism is introduced —
   waves change what is allocated and in what order, not how the race resolves.

4. Grouping step (after all orders reach a terminal allocation state):
     [TX] group task_line rows by zone
          INSERT task per (wave, zone), assign pick_container
          INSERT consolidation per order
            expected_containers = count(distinct zone in that order)
          UPDATE wave.status = 'released'

5. Pickers in zones A, B, C lease independently (§6.8) and pick in parallel.

6. Each finished tote is staged:
     POST /sync/facts { type: "container_staged",
                        containerId, stagingLocationId, consolidationId }
     [TX] UPDATE consolidation received_containers += 1
          IF received = expected → status = 'complete'

7. Packing is blocked until consolidation.status = 'complete'.
   A short-picked zone sets status = 'short' rather than hanging forever —
   the order proceeds to packing with what arrived, and the shortfall is
   already recorded as a short_pick exception from step 5.
```

Wave cancellation releases held allocations and cancels `ready` tasks. Tasks already `in_progress` are allowed to finish: recalling an operator mid-aisle costs more than the stock it frees.

### 6.12 Replenishment (Phase 2)

Two trigger paths, deliberately different in priority.

```
THRESHOLD  (scheduled sweep, every few minutes)
  SELECT pick faces WHERE on_hand < min_quantity
  for each:
    find bulk source: same item + owner, source_zone constrained,
                      oldest lot first under FEFO
    [TX] INSERT replenishment_request(threshold, qty = max - on_hand)
         INSERT task(task_type='replenish', priority = rule.priority)

DEMAND-DRIVEN  (immediate, raised by the allocation worker)
  Allocation fails at a pick face but stock exists in bulk:
    [TX] INSERT replenishment_request(demand, required_quantity)
         INSERT task(task_type='replenish', priority = 10)   ← elevated
         leave order_line partially allocated

  The order is NOT held waiting. It allocates what it can now and the
  remainder is picked up on the next allocation cycle, after the
  replenishment lands.
```

The demand path is what matters at 50 operators. Waiting for the next threshold sweep means a picker standing at an empty bin, which is the most expensive thing that happens in a warehouse.

Confirmation is an ordinary fact (`replen_confirmed`) through the same endpoint, leased through the same engine. This is the payoff for the generic task engine: a new work type costs a strategy and a screen, not a subsystem.

### 6.13 Pack, weigh, label, dispatch (Phase 2–3)

```
1. Packer scans arriving tote
     GET /shipments?containerId=TOTE-0412
     → resolves through task.pick_container_id

2. POST /shipments/{id}/cartons { cartonTypeId }        [command]
     carton_selection strategy proposes a type from item cube + weight
     [TX] INSERT carton, compute expected_weight_g
            = Σ(item gross weight × qty) + carton_type.tare_weight_g

3. For each item: POST /cartons/{id}/contents { barcode, quantity }   [command]
     ← 409 on wrong item — LEGITIMATE refusal, nothing physical has happened
     [TX] INSERT carton_content

4. POST /cartons/{id}/weigh { actualWeightG }           [command]
     |actual − expected| within tolerance → proceed
     beyond tolerance →
       [TX] INSERT inventory_exception(weight_mismatch)
            UPDATE carton.status = 'blocked'
       ← 409 { type: ".../weight-mismatch",
               expected, actual, tolerance }
     Override requires pack.override_weight and records both users.

5. Packing evidence photo (§6.14), linked via carton_media

6. POST /cartons/{id}/label                             [command, Phase 3]
     rate → select service → carrier adapter CreateLabel
     ← ZPL payload, sent to the printer over raw TCP 9100
     If the carrier is unreachable: draw from tracking_number_pool
     where supported; otherwise the shipment queues and dispatch waits.

7. POST /shipments/{id}/dispatch                        [command]
     [TX] INSERT stock_movement movement_type='dispatch' per line
          UPDATE stock_balance  on_hand −qty at staging
          UPDATE shipment.status = 'dispatched'
          INSERT outbox_message ShipmentDispatched → ERP
```

**Weight verification is the highest-value control in this flow.** Scan verification cannot catch a double-pick where the same item is scanned once and two units are placed; weight can.

### 6.14 Media capture (Phase 2)

```
1. POST /media/presign { contentType, byteSize, sha256, retentionClass }
     ← { uploadUrl, assetId }
     If sha256 already exists → ← { assetId, deduplicated: true }, no upload

2. Client compresses locally (1600px, q80) — mandatory, not optional.
   A 12MP handheld photo is 4–6 MB and ~250 KB after. Over warehouse
   wifi that difference decides whether the feature is usable.

3. PUT directly to object storage. The API is not in the data path.

4. POST /media/confirm { assetId }
     [TX] INSERT media_asset (status = 'processing')
          INSERT outbox_message MediaUploaded

5. Worker: strip EXIF, validate content type by magic bytes (never by
   extension), generate thumb/card/full as WebP, compute blurhash
     [TX] INSERT media_rendition ×3, UPDATE status = 'ready'

6. Link: POST /cartons/{id}/media { mediaId, role: "packing_evidence" }

7. Serve: GET /media/{sha256}/thumb.webp
   Content-addressed → immutable → Cache-Control: immutable, one year
```

Offline capture queues the upload alongside facts. The fact referencing `mediaIds` may sync before the image does; the link row is written on confirm, and a nightly job reports facts referencing assets stuck in `processing`.

### 6.15 Cycle count with recount and approval (Phase 3)

```
1. Scheduled generation, constrained by count_plan.schedule_window:
     [TX] INSERT count_sheet
          INSERT count_line per (location, item, lot)
            system_quantity = snapshot from stock_balance
          UPDATE location SET status = 'blocked_for_count'   ← the freeze

2. Counter leases the count task.
   GET /count/sheets/{id} — response OMITS system_quantity entirely.
   Blind counting is enforced by the response schema, not by the UI.

3. POST /sync/facts { type: "count_confirmed", countedQuantity: 47 }
     [TX] UPDATE count_line counted_quantity = 47,
                            variance = 47 − system_quantity
          IF |variance| within tolerance → status='counted'
          ELSE → status='variance'
               IF plan.requires_recount → INSERT recount line,
                  assigned preferentially to a DIFFERENT operator

4. Persistent variance → sheet moves to pending_approval

5. POST /count/sheets/{id}/approve                [count.variance.approve]
     [TX] assert approver != any counter on the sheet
          per variance line:
            UPDATE stock_balance  on_hand = counted_quantity
            INSERT stock_movement movement_type='count',
                   reason CNT_SHORT or CNT_OVER
          UPDATE count_line status='adjusted'
          UPDATE location SET status='active'      ← freeze released
```

`CNT_SHORT` is shrinkage; `CNT_OVER` is not. They are **never netted** — a location running +50 and −50 has an accuracy problem that a net of zero conceals entirely.

### 6.16 Return receipt and disposition (Phase 3)

```
1. POST /rma { originalOrderId, customerReference, lines }     [command]

2. Goods arrive. Operator leases the return task.
   POST /sync/facts { type: "return_received", quantity, condition,
                      mediaIds: [...] }
     [TX] INSERT stock_movement into the RETURNS zone (not available stock)
          UPDATE rma_line received_quantity, condition
          IF condition != 'sellable' AND mediaIds empty → reject as malformed
          IF quantity != expected → INSERT inventory_exception

3. Inspector dispositions:
   POST /rma/{id}/lines/{lineId}/disposition
        { disposition: "repair", reasonCode: "DSP_REPAIR" }   [command]
     [TX] movement from RETURNS to the disposition target:
            restock  → available stock, reason DSP_RESTOCK
            repair   → repair location, reason DSP_REPAIR
            rtv      → outbound to vendor, reason DSP_RTV
            scrap    → virtual scrap location, reason DSP_SCRAP (shrinkage)
```

Scrap moves to a **virtual location** rather than deleting rows. Units leave inventory without vanishing from the ledger, which is what keeps the reconciliation job meaningful.

### 6.17 Serial capture and genealogy (Phase 3)

```
Capture points, each roughly doubling scan effort for serialised items:
  receipt → putaway → pick → pack → dispatch → return

POST /sync/facts { type: "serial_scanned", serialNumber, context: "pick" }
  [TX] UPDATE serial_unit SET current_location_id, status
       INSERT serial_movement linked to the stock_movement
       IF serial unknown OR in an incompatible state
          → still recorded, exception raised

Balance derivation for is_serial_tracked items:
  on_hand = count(serial_unit WHERE status='on_hand' AND location=…)
  A scheduled check asserts this agrees with stock_balance.
  Divergence is a bug and raises a variance.

GET /serials/{serialNumber}/genealogy
  ← ordered chain: every movement, actor, device, timestamp,
    from receipt to dispatch, indexed — a query, not a report.
```

Genealogy is the feature customers actually buy serial tracking for. If it is slow, the feature is not delivered.

### 6.18 Configuration change (Phase 3)

```
1. GET /config/schema?category=allocation
     ← json_schema per key; the UI is GENERATED from this,
       not hand-built per key

2. PUT /config/allocation.default_strategy { value: "fefo" }   [command]
     [TX] validate against config_schema.json_schema
          UPDATE configuration SET is_current=false WHERE key=… 
          INSERT configuration (revision + 1, is_current=true)
          INSERT config_change_log (old, new, changed_by, note)
          INSERT outbox_message ConfigurationChanged
     ← 200

3. In-process configuration cache invalidated by the outbox consumer.
   Keys with requires_restart=true are flagged in the response and
   surfaced to the operator rather than applied silently.

4. GET /config/export → single versioned artefact
   POST /config/import → validate-then-apply, dry-run supported
```

Export and import are what make fleet support tractable: a customer's setup can be reproduced in a test deployment without access to their data.

### 6.19 ERP inbound order (Phase 3)

```
1. ERP posts to a webhook, or a file lands via SFTP.
     [TX] INSERT integration_message (status='received', raw payload)
     ← 202 immediately

   Raw payload is persisted FIRST. When an ERP sends malformed data at
   2am — and it will — the message is on disk for diagnosis rather than
   lost inside a stack trace.

2. Worker normalises:
     [TX] resolve external → internal ids via entity_mapping
          UPDATE integration_message SET normalized, status='processing'
          unmapped identifier → status='failed', error recorded,
                                queue NOT blocked behind it

3. Apply:
     [TX] INSERT sales_order + order_line
          UPDATE integration_message status='processed'
          INSERT outbox_message OrderReceived

4. Outbound is the reverse: the outbox already exists from Phase 1A,
   so integration adds consumers, not schema.
```

`entity_mapping` is not optional. External SKU and location codes will not match internal ones, and embedding external identifiers in core tables couples the domain to whichever ERP the first customer happens to run.

### 6.20 Deployment provisioning (Phase 4)

Idempotent and re-runnable end to end.

```
1. IaC: database, object storage, compute, DNS, TLS
2. Migrations from zero
3. Seed: permissions, system roles, reason codes (0042), sentinel lot UUID,
         default owner, warehouse, day_boundary_time, locale
4. First administrator — `POST /users`, same `must_change` mechanism every password credential gets (§2.1, §5.1), not a provisioning-specific behaviour
5. Configuration defaults from config_schema, then customer overrides
     POST /config/import { artefact }
6. Register with control plane; first /_fleet/health confirms success
7. Smoke test: create item → create location → receive 1 → putaway →
   reconcile → assert zero variance → clean up
   FAIL LOUDLY rather than handing over a broken deployment
```

### 6.21 Fleet upgrade (Phase 4)

```
internal ─(soak)→ canary (1–2 named customers) ─(soak)→ general

Per deployment:
  1. Control plane: POST /fleet/releases/{v}/rollout { ring }
  2. Deployment pulls the pinned image tag
  3. docker compose run --rm migrator
       migrations are idempotent and resumable; content hashing
       requires a custom journal (see §11.3) — DbUp's default records
       script names only
       non-zero exit → job fails, API containers UNTOUCHED
  4. API and worker restart, one replica at a time
  5. /_fleet/health reports the new appVersion and schemaVersion
  6. Soak monitors: error rate, scan p95, balance variance
       any regression → automatic halt, no further deployments upgrade
```

Migrations must declare an expected duration class. One taking 200 ms on a pilot customer can take 40 minutes on the largest, and long ones run as background operations rather than blocking a deploy.

### 6.22 Go-live opening balance (Phase 4)

The highest-risk activity in any WMS implementation.

```
1. FREEZE. No movements between count and go-live. Usually a weekend.

2. Full physical count, location by location, on the new devices —
   it doubles as final training.

3. POST /imports { importType: "opening_balance", fileKey }
     VALIDATE ENTIRELY FIRST: every location exists, every SKU exists,
     every UoM resolves, no duplicate (location, item, lot)
     → error report produced, NOTHING written
     A half-applied opening balance is worse than a rejected one.

4. Reconcile against the outgoing system. Explain EVERY variance before
   proceeding. Unexplained variance here becomes permanent, and every
   future discrepancy investigation runs into it.

5. Apply:
     [TX per batch] INSERT stock_movement
                      movement_type='adjustment',
                      reason_code='ADJ_OPENING'
                    UPDATE stock_balance
     The ledger's first entries are permanently identifiable.

6. Run reconciliation. Assert zero variance. Only then go live.
```

Where a full freeze is impossible, count by zone across several nights and freeze each zone as it is counted.

---

## 7. Error Catalogue

### 7.1 Standard responses

| Status | Type suffix | When | Client action |
|---|---|---|---|
| `400` | `/malformed-request` | Schema violation | Fix and resubmit; never auto-retry |
| `401` | `/unauthenticated` | Missing or expired token | Refresh once, then re-login |
| `403` | `/insufficient-permission` | Lacks permission or scope | Offer elevation if `elevation.possible` |
| `403` | `/same-actor` | Approver equals actor | Find a different supervisor |
| `403` | `/credential-change-required` | Password correct but `must_change` flagged | `POST /auth/credential-change` with the returned `changeToken`, then log in again |
| `404` | `/not-found` | Unknown id | Refresh cached master data |
| `409` | `/insufficient-stock` | Allocation could not satisfy | Partial allocation already applied |
| `409` | `/idempotency-conflict` | Same key, different payload | Quarantine; this is a client bug |
| `409` | `/weight-mismatch` | Carton weight beyond tolerance | Recount contents or request override |
| `409` | `/wrong-item` | Scanned item not on the carton line | Rescan |
| `412` | `/version-conflict` | `If-Match` stale | Refetch and show a diff |
| `423` | `/account-locked` | Failed attempt lockout | Show `retryAfter` |
| `426` | `/client-too-old` | Below `minClientVersion` | Force update |
| `429` | `/rate-limited` | Credential or lookup throttle | Backoff per `Retry-After` |
| `503` | `/service-unavailable` | Dependency down | Queue if a fact; block if a command |

**The `type` prefix is `urn:wms:problem:`** — so `/malformed-request` above is emitted as `urn:wms:problem:malformed-request`. RFC 7807 does not require the URI to resolve, and a `https://` URL that looks dereferenceable but 404s in every customer deployment is worse than a URN that never claimed to be. Clients branch on this string, so it is part of the API contract: changing one is a breaking change, and the constants live in one place (`Wms.Api/ProblemTypes.cs`) rather than as literals at each throw site.

**Inside a `/sync/facts` batch these appear per fact, not as the response status** — a rejected fact carries `error.type` from this same catalogue while its neighbours are still `accepted`, and the response remains `202`. See §4.1.

### 7.2 What never returns an error

Facts submitted to `/sync/facts` never return a business-rule error. The complete list of divergences that produce `202` plus an exception rather than a failure:

| Divergence | Exception type |
|---|---|
| Picked less than requested | `short_pick` |
| Received more or less than expected | `over_receipt` / `under_receipt` |
| Balance driven below zero | `negative_balance` |
| Put away to a different location than directed | `location_mismatch` |
| Task already completed by another operator | `reallocated_task` |
| Counted quantity beyond tolerance | `count_variance` |
| Serial scanned in an unexpected state | `serial_state_mismatch` |

Adding a fact type without deciding which row of this table it produces means adding a silent discrepancy.

---

## 8. Non-Functional Requirements

### 8.1 Security

**Authentication.** Argon2id password and PIN hashing. Access tokens ~15 minutes; refresh tokens device-bound with rotation and reuse detection — a reused refresh token revokes the entire family. `security_stamp` on `app_user` is bumped on any credential, role, or status change, and stale-stamp tokens are rejected on the next request, so termination takes effect in seconds rather than at token expiry.

**PIN weakness, stated plainly.** A four-digit PIN has ten thousand combinations. It is therefore either a second factor to a badge scan, or restricted to resuming a session on a device the user has already claimed. It never grants approval-level permissions alone. Rate limiting is per `(user, device)` with exponential lockout.

**Authorisation.** Every endpoint declares required permissions; a filter enforces them against server-resolved permissions and warehouse/zone scope. Checks test **permissions, never role names** — an architecture test fails the build on any comparison against a role string. Client-side hiding is UX only; the server re-checks every request.

**Segregation of duties** is enforced in the domain layer: the approver of a variance or adjustment may not be its actor.

**Transport and data.** TLS terminated at Caddy including on internal networks. Secrets injected at deploy, never in compose files or version control. Full audit trail on every stock-affecting action with actor, device, timestamp, and reason code.

**Single-tenant isolation.** One deployment per customer means isolation at the process and database boundary, which is stronger than row-level security and requires no per-query discipline. There is no `tenant_id` column.

**Input handling.** Parameterised queries throughout. Barcode input is treated as untrusted string data — it comes from a scanner reading a label that anyone can print.

### 8.2 Scaling

**Design ceiling:** 50+ concurrent operators, multiple zones, roughly 60,000 order lines per day, 40–50 million movements per year.

| Concern | Approach |
|---|---|
| Ledger growth | Monthly range partitions on `recorded_at`, created 12 months ahead |
| Balance contention | Grain is one physical bin; concurrency is limited by physics before it is limited by locks (C1) |
| Task dispatch | `SKIP LOCKED` — no lock contention regardless of operator count |
| Allocation | Single consumer per warehouse. Scales horizontally *across* warehouses via advisory locks |
| Read load | Reports on the streaming replica; the primary carries only transactional work |
| Connection pressure | PgBouncer transaction mode, per-role pools, reserved API capacity (C7) |
| API | Stateless, horizontally scalable. Polling rather than SignalR on the handheld path (C5) |

**The failure mode being designed against** is not raw throughput. It is a single long-running query — a wave allocation, a bulk import, an unbounded report — consuming the connection pool and stalling every scan in the building. Hence separate database roles with separate pools and statement timeouts (5s API, 300s worker, 600s report), and a reserved API allocation that no other workload can exhaust.

**Deliberately not built:** caching layers, read-through caches, message brokers, sharding. Each is available if measurement demands it. None is justified by 50 operators.

### 8.3 Error handling

**The governing principle:** commands may fail, facts may not.

| Class | Behaviour |
|---|---|
| Malformed request | `400` problem+json, field-level errors |
| Unauthenticated / expired | `401`, client refreshes and retries once |
| Insufficient permission | `403`, client may offer supervisor elevation |
| Version conflict (admin edit) | `412`, client refetches and shows a diff |
| Business rule refusal (commands) | `409` with a machine-readable `type` |
| Client below minimum version | `426 Upgrade Required` (H4) |
| **Physical fact contradicting state** | **`202` accepted, exception raised** |

**Retry semantics.** All fact submission is idempotent by `clientFactId`, generated at the moment of user action. Exponential backoff with jitter, capped at 60 seconds. The queue is unbounded in practice — it drains when connectivity returns.

**Partial batch handling.** A batch of ten facts where the fourth is malformed accepts the other nine and reports the fourth individually. One bad payload never blocks a shift's worth of queued work.

**Transactional integrity.** One aggregate mutated per transaction, with the exceptions detailed in §4.3 (fact confirmation and allocation). Cross-aggregate effects go through the outbox with at-least-once delivery, so consumers must be idempotent.

**Degraded operation.** When the system is unreachable, an hourly job exports open pick lists, putaway tasks, and current bin contents to PDF **on a separate host from the application stack**. Staff work from paper; movements are keyed in afterwards through a bulk reconciliation screen, flagged as manually entered and excluded from productivity metrics. An export living on the box that just died is not a fallback.

### 8.4 Observability

Minimum instrumentation before first pilot — without these, "the system is slow" is unanswerable:

- **Scan-to-confirm latency**, p50/p95/p99, split by fact type
- **Sync queue depth and age**, per device — the leading indicator of a coverage problem
- **Task queue depth** by warehouse and zone
- **Exception queue depth and ageing** — an operational metric, not a technical one
- **Allocation cycle time** and `allocation_request` backlog
- **Outbox lag** and failure count
- **Balance variance count** per reconciliation run — should be zero, and an alert if not
- **Connection pool saturation** per database role

Structured logging with correlation IDs propagated from client through API to worker via outbox metadata.

### 8.5 Testing

**Testcontainers against real PostgreSQL, always.** `SKIP LOCKED` dispatch, partition routing, and advisory locks do not exist in SQLite or an in-memory provider, and those are precisely the mechanisms carrying the correctness burden.

Mandatory concurrency tests:

1. Two devices leasing the same zone concurrently never receive the same task.
2. Two orders for the last remaining unit — exactly one allocates. *(Written in Phase 1A, ahead of the feature it covers.)*
3. Multi-line allocation concurrent with pick confirmations produces no deadlock.
4. A replayed fact with the same `clientFactId` produces exactly one movement.
5. A short pick produces a movement and an exception, never an error response.
6. Reconciliation over a synthetic movement history matches to zero variance.

Architecture tests: module boundary enforcement, no role-name comparisons, no write context in reporting code paths.

### 8.6 Internationalisation

Interface in English and Japanese from Phase 1A. `*_i18n` JSONB columns on user-facing master data; UI strings in resource catalogues resolved by `Accept-Language` and user locale.

Japanese specifics that need handling rather than discovering: text expansion in fixed-width table columns, `ja-JP` date and number formatting, and CJK font loading on the handheld — which matters because the operator PWA must work offline, so fonts have to be cached by the service worker rather than fetched on demand.

### 8.7 Layout and responsiveness

**Neither route tree may clip, overlap, or force whole-page horizontal scroll at any viewport width in its own realistic range.** This is scoped to what each tree actually runs on, not "support every screen size" in the abstract:

- **`/operator/*`** runs on handheld scanners and tablets (§1.2, D10) in **either orientation** — a screen rotated mid-task is a real event on a warehouse floor, not an edge case, and the layout must reflow rather than break. D10 is explicitly a reversible hardware decision, so the UI can't assume one fixed device or resolution either.
- **`/admin/*`** runs in a desktop browser window that gets resized, not maximised-and-forgotten. A narrowed window degrades gracefully: the sidebar collapses to an icon rail or drawer before it overlaps content, and a dense data grid or wide table scrolls horizontally **inside its own container**, never by scrolling the whole page sideways.

**Breakpoints follow [Material Design 3's window size classes](https://m3.material.io/foundations/layout/applying-layout/window-size-classes)** — compact (<600dp, the operator floor), medium (600–840dp, a narrowed admin window or a tablet), expanded (>840dp, standard admin desktop) — a current, widely-used reference rather than numbers invented for this project. CSS uses relative units and flexbox/grid; no layout assumes one fixed pixel width.

**Verification:** a Playwright check that resizes the viewport mid-test and asserts no overflow or clipping is mandatory for each route tree's primary screens (frontend-testing skill) — rendering once at a single fixed viewport size proves nothing about this requirement.

---
---

# Part II — Phase 2: Operational Core

*Specification depth: build-ready.*

Phase 2 is what makes the system viable at the D5 scale of 50+ operators. Waves, zone picking and replenishment are not maturity features at that size — without them, discrete picking produces aisle congestion and pick faces empty faster than people can refill them by hand.

## 9.1 Waves and pick methods

### Data model

```sql
wave
  id                uuid PK
  warehouse_id      uuid NOT NULL
  wave_number       text UNIQUE
  wave_type         text CHECK (wave_type IN ('scheduled','carrier_cutoff','priority','manual'))
  status            text CHECK (status IN ('planning','allocating','released',
                                           'picking','picked','completed','cancelled'))
  strategy_config   jsonb NOT NULL      -- grouping + pick method, resolved at planning
  planned_at, released_at, completed_at timestamptz
  created_by        uuid

wave_order
  wave_id           uuid FK
  order_id          uuid FK
  sequence          int
  PRIMARY KEY (wave_id, order_id)

pick_container                            -- tote, carton, or cart position
  id                uuid PK
  code              text UNIQUE           -- barcode on the physical tote
  container_type    text CHECK (container_type IN ('tote','carton','cart','pallet'))
  position_count    int NOT NULL DEFAULT 1   -- >1 for cluster carts
  status            text

consolidation                             -- zone picking rendezvous
  id                uuid PK
  order_id          uuid FK
  staging_location_id uuid FK
  expected_containers int
  received_containers int
  status            text CHECK (status IN ('open','complete','short'))
```

Additions to existing tables:

```sql
ALTER TABLE task      ADD COLUMN wave_id uuid;
ALTER TABLE task      ADD COLUMN pick_container_id uuid;
ALTER TABLE task_line ADD COLUMN container_position int;
ALTER TABLE task_line ADD COLUMN order_line_id uuid;    -- links pick back to demand
```

`task.reference_type` was generic from Phase 1B precisely so this attaches without altering its meaning.

### Pick methods

| Method | Grouping | Container | When it fits |
|---|---|---|---|
| **Discrete** | One order per task | Single tote | Low volume, large orders |
| **Batch** *(deferred)* | N orders per task, one pass | Multi-position cart | Many single-line orders |
| **Zone** | Order split by zone, one task per zone | Tote per zone, consolidated at staging | Large warehouse, dense zones |
| **Cluster** *(deferred)* | N orders picked simultaneously into N positions | Cart with numbered positions | High single-line volume |

**Phase 2 ships discrete and zone only** (Open Item 7). Zone is what scales at 50+ operators; discrete is the fallback and the training mode. Batch and cluster are optimisations whose value depends entirely on average lines per order — below ~2 they pay off enormously, above ~5 they are worse than zone — and tuning them against guessed order profiles is wasted work. The container and position columns are in the schema now so adding them later is a strategy, not a migration.

Default per deployment: **zone** where the warehouse has more than one pick zone, **discrete** where it does not.

Zone picking introduces the only genuinely new correctness problem in Phase 2: an order is now picked by several people in parallel and must be reassembled. `consolidation` tracks the rendezvous; the order cannot progress to packing until `received_containers = expected_containers`, and a short-picked zone marks the consolidation `short` rather than blocking it indefinitely.

### Wave release flow

1. Planner selects orders by criteria — carrier cutoff, priority, required-by date, zone affinity.
2. `POST /waves` creates the wave in `planning` with resolved strategy configuration.
3. `POST /waves/{id}/release` enqueues one `allocation_request` per order, tagged with `wave_id`, and sets the wave to `allocating`.
4. The allocation worker processes them under the same per-warehouse advisory lock as Phase 1B. **No new concurrency mechanism is introduced** — waves change what gets allocated and in what order, not how the race is resolved.
5. Once all orders reach a terminal allocation state, a grouping step assembles `task` rows according to the pick method, assigns containers and positions, and sets tasks to `ready`.
6. Wave moves to `released`. Tasks become leasable.

**Wave cancellation** releases held allocations and cancels unstarted tasks. Tasks already `in_progress` are allowed to finish — recalling an operator mid-aisle costs more than the stock it frees.

### Endpoints

Endpoints: §5.6. Flow: §6.11.

## 9.2 Replenishment

### Data model

```sql
location_replenishment
  location_id       uuid PK FK -> location
  item_id           uuid NOT NULL
  owner_id          uuid NOT NULL
  min_quantity      numeric(18,4) NOT NULL     -- trigger point
  max_quantity      numeric(18,4) NOT NULL     -- refill target
  replen_uom        text NOT NULL              -- usually CASE or PALLET
  source_zone_id    uuid                       -- constrain the bulk search
  priority          int NOT NULL DEFAULT 100
  is_active         boolean NOT NULL DEFAULT true

replenishment_request
  id                uuid PK
  location_id       uuid FK
  item_id, owner_id uuid
  trigger_type      text CHECK (trigger_type IN ('threshold','demand','manual'))
  required_quantity numeric(18,4)
  status            text CHECK (status IN ('queued','tasked','completed','cancelled'))
  task_id           uuid
  created_at, completed_at timestamptz
```

### Two trigger paths

**Threshold-driven** (scheduled, every few minutes): scan pick faces where `on_hand < min_quantity`, create a request for `max_quantity - on_hand`, source from the nearest bulk location holding the same item and owner, preferring the oldest lot under FEFO.

**Demand-driven** (immediate, higher priority): when allocation fails at a pick face but stock exists in bulk, raise an emergency replenishment request at elevated priority and leave the order line partially allocated. This is the path that matters at 50 operators — waiting for the next threshold sweep means a picker standing at an empty bin.

Replenishment tasks are ordinary `task` rows with `task_type = 'replenish'`, leased through the same mechanism. This is the payoff for building a generic task engine in Phase 1A: a new work type costs a strategy and a screen, not a subsystem.

### Endpoints

Endpoints: §5.7. Flow: §6.12.

## 9.3 Allocation strategies

Phase 1B ships one strategy with the ordering already parameterised. Phase 2 makes the parameter real.

```sql
strategy_binding
  id                uuid PK
  scope_type        text CHECK (scope_type IN ('deployment','warehouse','zone','item_class'))
  scope_id          uuid
  strategy_slot     text CHECK (strategy_slot IN ('allocation','putaway','replenishment',
                                                  'wave_grouping','carton_selection'))
  strategy_code     text NOT NULL
  parameters        jsonb NOT NULL DEFAULT '{}'
  revision          int NOT NULL
  UNIQUE (scope_type, scope_id, strategy_slot, revision)
```

Resolution is most-specific-wins: item class, then zone, then warehouse, then deployment.

| Slot | Strategies shipped in Phase 2 |
|---|---|
| `allocation` | `fefo`, `fifo`, `lifo`, `nearest_bin`, `whole_case_first`, `single_location_preferred` |
| `putaway` | `first_empty`, `consolidate_existing`, `nearest_to_pick_face`, `capacity_optimised`, `zone_constrained` |
| `replenishment` | `threshold`, `demand_driven`, `hybrid` |
| `wave_grouping` | `by_carrier_cutoff`, `by_zone_affinity`, `by_order_size`, `by_priority` |
| `carton_selection` | `smallest_fitting`, `single_carton_preferred`, `weight_balanced` |

Each strategy is a named class implementing one interface, registered at startup. **A strategy never mutates state** — it returns an ordered list of candidates, and the allocation worker performs the conditional `UPDATE`. This keeps the correctness-critical code in one place regardless of how many strategies exist.

`single_location_preferred` deserves a note: allocating an order line from one location rather than three cuts pick time substantially, and is worth trying before falling back to a strictly FEFO split. This is a real trade-off between stock rotation and labour, so it is a configuration decision, not a code decision.

## 9.4 Packing and shipping

### Data model

```sql
carton_type
  id, code UNIQUE, name,
  internal_length_mm, internal_width_mm, internal_height_mm int,
  tare_weight_g, max_weight_g bigint,
  cost_cents int, is_active boolean

shipment
  id                uuid PK
  warehouse_id      uuid NOT NULL
  shipment_number   text UNIQUE
  order_id          uuid FK
  carrier_code      text
  service_code      text
  status            text CHECK (status IN ('pending','packing','packed','labelled',
                                           'manifested','dispatched','cancelled'))
  ship_to           jsonb NOT NULL          -- snapshot; addresses change, shipments do not
  total_weight_g    bigint
  packed_at, dispatched_at timestamptz
  tracking_number   text

carton
  id                uuid PK
  shipment_id       uuid FK
  carton_number     int NOT NULL
  carton_type_id    uuid FK
  gross_weight_g    bigint
  expected_weight_g bigint                  -- computed; drives verification
  tracking_number   text
  status            text
  UNIQUE (shipment_id, carton_number)

carton_content
  id                uuid PK
  carton_id         uuid FK
  order_line_id     uuid FK
  item_id, lot_id, serial_unit_id uuid
  quantity          numeric(18,4) NOT NULL

packing_station
  id, warehouse_id, code, location_id,
  label_printer_id text, scale_device_id text, is_active
```

### Packing flow

1. Packer scans the arriving tote. System resolves it to a shipment through `task.pick_container_id`.
2. `carton_selection` strategy proposes a carton type from item cube and weight.
3. Packer scans each item into the carton — **scan-verified**, not trusted. A wrong item scan is rejected here, and this is a legitimate rejection because nothing physical has happened yet.
4. Carton is weighed. Actual weight is compared against `expected_weight_g` (sum of item gross weights plus carton tare) within a configured tolerance.
5. **Weight mismatch raises an exception and blocks the carton**, because it means a miss-pick that scanning did not catch — a double-pick, or an item scanned twice and packed once.
6. Packing evidence photo captured (§9.5), linked to the carton.
7. Label requested; `movement_type = 'dispatch'` movements written against the staging location.
8. Shipment moves to `packed`.

Weight verification is the highest-value control in this flow. It catches errors that scan verification structurally cannot.

### Carrier boundary — Phase 2 scope

Phase 2 defines the **adapter interface** and implements a manual/generic label path. Real carrier integrations land in Phase 3 (§7.5), because each carrier is its own project and shipping them serially against real customer demand beats guessing which three to build.

Endpoints: §5.8. Flow: §6.13.

## 9.5 Media subsystem

Schema as specified in the proposal, implemented here.

```sql
media_asset
  id                uuid PK
  storage_key       text NOT NULL
  content_type      text NOT NULL
  byte_size         bigint NOT NULL
  width, height     int
  sha256            text UNIQUE NOT NULL      -- deduplication
  blurhash          text
  status            text CHECK (status IN ('pending','processing','ready','failed'))
  retention_class   text CHECK (retention_class IN ('catalogue','operational','evidence'))
  legal_hold        boolean NOT NULL DEFAULT false
  expires_at        date                      -- derived from retention_class
  uploaded_by       uuid
  created_at        timestamptz

media_rendition
  id, asset_id FK, variant text CHECK (variant IN ('thumb','card','full')),
  storage_key text, width, height int, byte_size bigint
  UNIQUE (asset_id, variant)

item_media          (media_id, item_id, role, sort_order, is_primary)
receipt_line_media  (media_id, receipt_line_id, role)
carton_media        (media_id, carton_id, role)
rma_line_media      (media_id, rma_line_id, role)      -- Phase 3
location_media      (media_id, location_id, role)
```

```sql
CREATE UNIQUE INDEX item_media_primary_idx ON item_media (item_id) WHERE is_primary;
```

Separate link tables per entity rather than a polymorphic table, so foreign keys remain real.

### Pipeline

Endpoints: §5.9. Full pipeline flow: §6.14.

Client-side compression before upload is mandatory, not optional: a 12MP handheld photo is 4–6 MB and roughly 250 KB after resizing to 1600px at q80. Over warehouse wifi that difference decides whether the feature is usable.

Search read models carry a denormalised `primary_thumb_key` and `blurhash` so grid views never join to media or issue one request per row.

### Retention

| Class | Default retention | Rationale |
|---|---|---|
| `catalogue` | Indefinite | 50k SKUs × 3 images ≈ 30 GB. Negligible |
| `operational` | 90 days | Putaway, location, working photos |
| `evidence` | 180 days | Packing, damage, returns — dispute window |

Nightly job moves expired objects to cold storage then deletes, **skipping anything with `legal_hold = true`**. At moderate throughput, packing evidence alone accumulates on the order of a gigabyte per day indefinitely; introducing retention after two years of accumulation is materially harder than building it now.

On ingest: EXIF stripped (GPS and size), content type validated by magic-byte sniffing rather than file extension, size capped at the presign step.

## 9.6 Master data at scale

```sql
import_job
  id, import_type text CHECK (import_type IN ('item','location','barcode',
                                              'replenishment_rule','opening_balance')),
  file_key text, status text, total_rows, processed_rows, error_count int,
  created_by uuid, started_at, completed_at timestamptz

import_error
  id, job_id FK, row_number int, column_name text,
  message_i18n jsonb, raw_row jsonb
```

Imports are **validate-then-apply**: the whole file is validated and an error report produced before any row is written. A half-applied item master is worse than a rejected one.

`opening_balance` is listed here because it is the import that matters most — it is how a customer's existing stock enters the system at go-live (§11.6).

## 9.7 Phase 2 exit criteria

1. A wave of 50 orders releases, allocates, and generates zone-picked tasks across three zones.
2. Consolidation blocks packing until all zone totes arrive at staging, and reports short rather than hanging when one does not.
3. A pick face falling below minimum generates a replenishment task automatically; an allocation failure at a pick face generates a demand-driven one at elevated priority.
4. Switching the allocation strategy from `fifo` to `fefo` through configuration changes allocation behaviour with no deployment.
5. A carton whose actual weight diverges from expected beyond tolerance is blocked and raises an exception.
6. An item photo captured on a handheld appears as a thumbnail in admin search within 30 seconds.
7. Retention job deletes expired operational media and skips legal-hold assets.

---

# Part III — Phase 3: Operational Maturity

*Specification depth: contracts and schema fixed; internal detail will move with real customer requirements.*

## 10.1 Cycle counting

### Data model

```sql
count_plan
  id                uuid PK
  warehouse_id      uuid NOT NULL
  name              text NOT NULL
  plan_type         text CHECK (plan_type IN ('abc','location_sweep','item','ad_hoc','triggered'))
  schedule_cron     text
  schedule_window   tstzrange[]              -- permitted counting hours (Open Item 8)
  zone_ids          uuid[]
  abc_classes       char(1)[]
  tolerance_percent numeric(5,2) NOT NULL DEFAULT 0
  tolerance_value   numeric(18,4)
  requires_recount  boolean NOT NULL DEFAULT true
  is_active         boolean

count_sheet
  id, plan_id FK, warehouse_id, sheet_number text UNIQUE,
  status text CHECK (status IN ('generated','counting','pending_recount',
                                'pending_approval','approved','cancelled')),
  assigned_zone_id uuid, generated_at, due_date, completed_at

count_line
  id                uuid PK
  sheet_id          uuid FK
  location_id, item_id, lot_id, owner_id uuid
  system_quantity   numeric(18,4) NOT NULL   -- snapshot at generation; NEVER sent to device
  counted_quantity  numeric(18,4)
  recount_quantity  numeric(18,4)
  variance          numeric(18,4)
  status            text CHECK (status IN ('pending','counted','variance',
                                           'recounted','approved','adjusted'))
  counted_by, counted_at
  approved_by, approved_at
  adjustment_movement_id uuid
```

### Blind counting

`system_quantity` is captured at sheet generation and **never transmitted to the counting device**. A counter who knows the expected number will find the expected number. This is not a UI preference — the API response for a count task omits the field entirely, so it cannot leak through a client bug.

### Flow

1. Scheduled job generates sheets from the plan. ABC plans count A items monthly, B quarterly, C annually.
2. Counter leases a count task, scans the location, scans each item, enters quantity.
3. Variance beyond tolerance sets `status = 'variance'`; if `requires_recount`, a recount line is generated and assigned — **preferentially to a different operator**.
4. Persistent variance goes to `pending_approval`.
5. Approval writes an adjustment movement with `movement_type = 'count'` and a reason code.
6. **The approver may not be the counter.** Enforced in the domain layer, using the same segregation-of-duties mechanism as adjustments.

Counting is another `task_type`, leased through the same engine, offline-capable through the same fact endpoint.

### Interaction with live stock

A count is a snapshot of a location that operators may still be picking from. Two workable positions:

- **Freeze the location** for the duration of the count — blocks allocation against it, adds a `blocked_for_count` stock status.
- **Count live and reconcile by sequence** — compare counted quantity against the balance as of the ledger sequence at count time.

Phase 3 ships the freeze, because it is comprehensible to warehouse staff and the reconciliation approach produces variances nobody can explain.

**But the freeze is scheduled, not opportunistic** (Open Item 8). Counting A-class items monthly means repeatedly freezing the fastest-moving pick faces, which at 50 operators is measurable throughput loss. `count_plan.schedule_window` constrains when each plan may run: A-item pick-face counts outside peak picking hours, bulk and C-class locations during the day. The freeze stays — scoped to one location, timed to when it costs least.

## 10.2 Returns and reverse logistics

```sql
rma
  id, warehouse_id, rma_number text UNIQUE,
  original_order_id uuid, customer_reference text, owner_id uuid,
  status text CHECK (status IN ('expected','receiving','inspecting','dispositioned','closed')),
  expected_at, received_at timestamptz

rma_line
  id, rma_id FK, line_no int,
  item_id, lot_id, serial_unit_id uuid,
  expected_quantity, received_quantity numeric(18,4),
  condition text CHECK (condition IN ('sellable','damaged','defective',
                                      'missing_parts','wrong_item')),
  disposition text CHECK (disposition IN ('restock','repair','return_to_vendor','scrap','hold')),
  disposition_by uuid, disposition_at timestamptz,
  restock_location_id uuid
```

Returns receipt is a receipt variant, reusing `receipt` with `receipt_type = 'return'` where the goods arrive without an RMA. Condition photos are mandatory on any condition other than `sellable`, linked through `rma_line_media`.

Disposition drives the movement: `restock` moves to available stock; `scrap` moves to a virtual scrap location so the units leave inventory without vanishing from the ledger.

## 10.3 Serial number tracking

`serial_unit` exists from the first migration. Phase 3 exposes it.

```sql
serial_unit                              -- defined in §2.2, now active
  id, item_id, serial_number, lot_id, owner_id,
  current_location_id, current_handling_unit_id,
  status text CHECK (status IN ('expected','on_hand','allocated','shipped',
                                'returned','scrapped')),
  version bigint

serial_movement
  id, serial_unit_id FK, stock_movement_id FK,
  from_location_id, to_location_id uuid,
  movement_type text, occurred_sequence bigint
```

**Dual-path balance derivation.** For `is_serial_tracked` items, on-hand derives from `count(serial_unit WHERE status='on_hand')` rather than from `stock_balance`. A scheduled check asserts the two agree for items where both exist; divergence is a bug and raises a variance.

Capture points: receipt, putaway, pick, pack, dispatch, return. Every one roughly doubles scan effort for serialised items, which is why serial tracking is per-item and not global.

**Genealogy query** — given a serial, return the full chain from receipt to dispatch including every actor and device. This is the feature customers actually buy serial tracking for, and it should be a single indexed query, not a report.

## 10.4 Configuration as data

The Phase 3 item with the largest effect on whether this survives as a product. In a single-tenant product, the alternative to configuration is per-customer code branches — comfortable for the first two customers, fatal by the sixth.

```sql
config_schema
  key               text PK              -- allocation.default_strategy
  scope_levels      text[] NOT NULL      -- which scopes may set it
  value_type        text NOT NULL
  json_schema       jsonb NOT NULL       -- validation, drives UI generation
  default_value     jsonb NOT NULL
  category          text
  label_i18n        jsonb
  description_i18n  jsonb
  requires_restart  boolean NOT NULL DEFAULT false

configuration
  id, scope_type, scope_id, config_key FK -> config_schema,
  config_value jsonb NOT NULL,
  revision int NOT NULL, is_current boolean NOT NULL,
  updated_by uuid, updated_at timestamptz

config_change_log
  id, config_key, scope_type, scope_id,
  old_value, new_value jsonb, changed_by uuid, changed_at timestamptz, note text
```

**The UI is generated from `json_schema`.** Hand-building a settings screen per configuration key does not scale past the first fifty keys, and this system will have several hundred.

Configuration is **versioned and exportable** as a single artefact, so a customer's setup can be reproduced in a test environment without access to their data — which is what makes support tractable across a fleet (§11.4).

Endpoints: §5.11. Flow: §6.18.

## 10.5 Carrier integration

```sql
carrier
  id, code UNIQUE, name, adapter_type text,
  credentials_ref text,                -- pointer to secret store, never the secret
  is_active boolean

carrier_service
  id, carrier_id FK, code, name_i18n jsonb, transit_days int, is_active

rate_quote
  id, shipment_id FK, carrier_service_id FK,
  cost_minor int, currency char(3), quoted_at timestamptz, expires_at timestamptz

shipping_label
  id, shipment_id, carton_id FK,
  format text CHECK (format IN ('zpl','pdf','png')),
  payload_key text,                    -- object storage, not a database blob
  tracking_number text, created_at, voided_at timestamptz

tracking_number_pool                   -- outage tolerance
  id, carrier_id, tracking_number text UNIQUE,
  status text CHECK (status IN ('available','assigned','used','voided')),
  assigned_shipment_id uuid, allocated_at
```

The adapter interface is `Rate`, `CreateLabel`, `Void`, `Manifest`, `Track`. Each carrier is an implementation; none is in the core.

**No carrier ships in the base product** (Open Item 9). Choosing three carriers before knowing who you are selling to guarantees building at least one nobody uses. Instead: the interface and a manual label path ship in Phase 2, and each real carrier adapter is added for — and funded by — the deal that requires it. Budget two to four weeks per carrier.

One design constraint on the first two adapters: **make them deliberately different transports** — one REST API, one file or SFTP based. An interface proven against two REST carriers is not proven general, and the third integration is where that becomes expensive to discover.

**`tracking_number_pool` is the interesting table.** Carrier APIs are external, so an internet fault stops label generation and therefore dispatch. Carriers that support pre-allocated tracking number ranges let you draw from a local pool during an outage and reconcile afterwards. Where a carrier does not support it, dispatch queues and that is documented as a known limitation rather than discovered on the day.

ZPL is generated locally and sent directly to Zebra printers over raw TCP 9100. Rendering to PDF and printing through a driver loses barcode fidelity — a scannable label is not a design detail.

## 10.6 ERP and external integration

```sql
integration_endpoint
  id, system_code text, direction text CHECK (direction IN ('inbound','outbound','both')),
  transport text CHECK (transport IN ('rest','sftp','sqs','webhook','file_drop')),
  config jsonb, credentials_ref text, is_active boolean

integration_message
  id, endpoint_id FK, direction text, message_type text,
  external_id text, correlation_id uuid,
  payload jsonb, normalized jsonb,
  status text CHECK (status IN ('received','processing','processed','failed','skipped')),
  attempts int, last_error text,
  received_at, processed_at timestamptz

entity_mapping
  id, system_code text, entity_type text,
  internal_id uuid NOT NULL, external_id text NOT NULL,
  UNIQUE (system_code, entity_type, external_id),
  UNIQUE (system_code, entity_type, internal_id)
```

`entity_mapping` is not optional. External SKU codes, location codes, and order numbers will not match internal ones, and embedding external identifiers in core tables couples the domain to whichever ERP the first customer happens to run.

Outbound messages are produced by the **outbox**, already present from Phase 1A, so integration adds consumers rather than schema.

Inbound messages are **staged then applied**: raw payload persisted first, normalised second, applied third. When an ERP sends malformed data at 2am — and it will — the raw message is on disk for diagnosis rather than lost in a stack trace.

## 10.7 Reporting

Phase 3 moves reporting onto the replica in earnest.

- Materialised views on the replica for the heavy aggregates: daily stock position, movement summary by business date, operator productivity, exception ageing.
- Refresh on a schedule with `REFRESH MATERIALIZED VIEW CONCURRENTLY`, or the shadow-table swap pattern where a concurrent refresh is too slow.
- **No projection pipeline.** The replica has no drift by construction; a hand-built projection has drift as a permanent class of bug (H6).
- Export to CSV and Excel through a queued job writing to object storage — never a synchronous request, because the first customer to export a year of movements will otherwise discover the 5-second API timeout.

## 10.8 Phase 3 exit criteria

1. A blind ABC count sheet generates, counts, recounts on variance, and requires a different approver than the counter.
2. A returned unit is received, photographed, dispositioned to `repair`, and leaves available stock with a traceable movement.
3. A serialised item is traced from receipt to dispatch in one query, with actor and device at each step.
4. An allocation strategy change made through the configuration UI takes effect with no deployment, and appears in `config_change_log`.
5. A ZPL label prints to a Zebra printer and scans successfully.
6. An ERP order arrives, is staged, normalised, mapped through `entity_mapping`, and creates a valid order.
7. A malformed ERP message is persisted with its raw payload and does not block the queue behind it.

---

# Part IV — Phase 4: Commercial Readiness

*Specification depth: contracts and operating model. Detail here will move most, because it is shaped by how many customers you have and how they buy.*

Phase 4 is where the system stops being software and becomes a product. Most of it lives **outside the customer deployment**.

## 11.1 Fleet control plane

A separate service with its own database, operated by you, not deployed per customer.

```sql
-- control plane database
deployment
  id                uuid PK
  customer_code     text UNIQUE
  display_name      text
  region            text
  endpoint_url      text
  app_version       text
  schema_version    text
  config_revision   int
  ring              text CHECK (ring IN ('internal','canary','general'))
  status            text CHECK (status IN ('provisioning','active','suspended','decommissioned'))
  last_heartbeat_at timestamptz
  contract_rpo_minutes int
  contract_rto_minutes int

deployment_event
  id, deployment_id FK, event_type text,
  from_version, to_version text,
  status text, started_at, completed_at timestamptz, error text

release
  id, version text UNIQUE, released_at timestamptz,
  migration_count int, min_client_version text,
  requires_downtime boolean, notes text

deployment_metric                        -- rolled up, not raw
  deployment_id, business_date date,
  scan_p95_ms int, exception_open_count int, exception_max_age_hours int,
  sync_backlog_max int, balance_variance_count int,
  orders_shipped int, lines_picked int
```

Each customer deployment exposes a narrow, authenticated endpoint:

```
GET /_fleet/health
→ { appVersion, schemaVersion, configRevision, uptime,
    queues: { outboxPending, allocationBacklog, exceptionsOpen, syncBacklogMax },
    reconciliation: { lastRunAt, varianceCount } }
```

This is the **only** control-plane coupling. The control plane observes and orchestrates; it never reaches into customer data. That boundary is what keeps single-tenant isolation real rather than nominal.

**Hosted in a different region from the deployments it manages** (Open Item 11). Sharing a failure domain means a regional outage removes both your customers and your ability to see them, which is precisely when visibility matters most. Heartbeats are low-frequency, so cross-region latency is irrelevant, and data residency does not apply because the control plane holds no customer data by design.

## 11.2 Provisioning

New-customer onboarding must be automated end to end, because manual provisioning is the constraint that caps how many customers the business can carry.

Bootstrap sequence, idempotent and re-runnable:

1. Infrastructure from IaC — database, object storage, compute, DNS, TLS.
2. Schema from migration zero.
3. Seed: permission catalogue, system roles, sentinel lot UUID, default owner, warehouse record, day-boundary configuration, locale.
4. First administrator with a forced credential change — the general `must_change` mechanism (§2.1, §5.1), not something special-cased for provisioning.
5. Default configuration set from `config_schema` defaults, then customer overrides applied from an import artefact.
6. Register with the control plane; first heartbeat confirms success.
7. Smoke test: create an item, a location, receive one unit, put it away, reconcile, delete. Fail loudly rather than handing over a broken deployment.

## 11.3 Upgrade rings

```
internal  →  canary (1–2 named customers)  →  general
```

Each ring has a **soak period** and automatic halt criteria: an error-rate regression, a scan-latency regression, or any balance variance halts the rollout and pages.

Migrations must be **idempotent and resumable**. A migration failing halfway on customer 14 of 30 cannot require manual repair on each.

**Content hashing is a Phase 4 requirement, not something the tooling gives for free — corrected here after being verified against a real database.** DbUp's default journal (`schemaversions`) records only the script name and the time it was applied; it does not hash script content. So an already-applied migration that was edited afterwards is *not* detectable by the migrator, and across a fleet you cannot tell whether customer 14 received the same `0042` as customer 3. Until a custom journal carrying a content hash is built, the immutability guarantee comes from two things earlier in the pipeline: the `guard-applied-migration.sh` hook, which blocks the edit at authoring time, and the architecture-test backstop for when the hook is bypassed. Both are local to a developer's machine — neither can detect an already-divergent fleet, which is precisely why the hashing journal belongs in Phase 4 alongside the rest of fleet management.

**Expand-then-contract remains mandatory** and gets harder here: a migration may need to be compatible across two application versions *and* run unattended against dozens of databases with different data volumes and different query plans. A migration that takes 200ms on a pilot customer can take 40 minutes on the largest one, so every migration declares an expected duration class and long ones run as background operations rather than blocking a deploy.

## 11.4 Cross-deployment observability

Per-deployment metrics aggregated into one operator view. Minimum set, all sliced by deployment:

- Scan-to-confirm p95, with an alert on regression against that customer's own baseline rather than a global threshold — a customer on older hardware has a legitimately different normal
- Exception queue depth and maximum age
- Sync backlog maximum across devices
- Balance variance count, which should always be zero
- Allocation backlog and cycle time
- Version and configuration revision drift across the fleet

Support requires reproducing a customer's setup without their data. That is what `GET /config/export` (§10.4) is for: version, configuration artefact, and device fleet are enough to rebuild the environment in a test deployment.

## 11.5 Additional Phase 4 capability

### Labour management

```sql
labour_standard
  id, warehouse_id, task_type, activity text,
  fixed_seconds int, per_unit_seconds numeric(8,2), per_metre_seconds numeric(8,2),
  effective_from date

operator_activity
  id, user_id, device_id, task_id, activity_type text,
  started_at, ended_at timestamptz, units numeric, distance_m int,
  business_date date

productivity_summary                      -- materialised on the replica
  user_id, business_date, task_type,
  tasks_completed int, units int, active_seconds int,
  standard_seconds int, performance_percent numeric(5,2)
```

**Resolved: the capture and the scorecard are separate products** (Open Item 10).

| Component | Packaging | Rationale |
|---|---|---|
| `operator_activity` capture | Base product, always on | Needed for slotting, wave sizing, and exception attribution regardless |
| **Aggregate** capacity reporting — throughput per zone, per shift, per task type | Base product | Staffing and capacity planning. Uncontroversial, and genuinely useful |
| **Individual** performance scorecards | Separately licensed module, **off by default** | Where the monitoring obligations attach |

This split matters commercially, not just ethically. Individual performance monitoring carries legal and industrial-relations obligations that vary by jurisdiction, and Japan has employee-consultation expectations around it. Bundling it into the base product makes the whole system a deal-blocker in accounts where it would never be switched on. Separating it keeps the value and isolates the risk.

Where the individual module is enabled: metrics are visible to the operators they describe, and "can this be turned off" is a functional requirement rather than a settings toggle.

### EDI

```sql
edi_partner
  id, code, name, isa_qualifier, isa_id, transport, config jsonb, is_active

edi_document
  id, partner_id FK, doc_type text,        -- 856, 940, 945, 214, 850, 810
  direction text, control_number text,
  raw_payload text, parsed jsonb,
  status text, error text,
  received_at, processed_at timestamptz
```

Retail customers make EDI non-optional. Raw payload is retained because EDI disputes are settled on exact bytes.

### Slotting analysis

```sql
slotting_analysis
  id, warehouse_id, run_at, parameters jsonb, status,
  projected_saving_seconds bigint

slotting_recommendation
  id, analysis_id FK, item_id,
  from_location_id, to_location_id uuid,
  reason text, projected_saving_seconds int,
  status text CHECK (status IN ('proposed','accepted','rejected','applied')),
  applied_task_id uuid
```

Inputs come from data already captured: pick velocity from `stock_movement`, item cube from `item_uom`, co-pick affinity from `task_line` groupings, travel distance from `location.pick_sequence`.

**Recommendations, never automatic execution.** Accepted recommendations generate ordinary `move` tasks. A system that silently reorganises a warehouse overnight is a system nobody trusts.

### WCS integration boundary

```sql
automation_endpoint
  id, warehouse_id, system_type text,      -- conveyor, ASRS, AMR fleet, sorter
  protocol text, config jsonb, is_active

automation_task
  id, task_id FK, endpoint_id FK,
  external_reference text,
  status text CHECK (status IN ('sent','acknowledged','in_progress','completed','failed')),
  sent_at, acknowledged_at, completed_at timestamptz, error text
```

**The contract: the WMS decides *what*, the WCS decides *how*.** The WMS says "move LPN 00123 to zone B"; conveyor routing, sorter divert logic, and AMR path planning belong to the WCS. Every WMS project that has blurred this line has ended up writing a control system badly.

## 11.6 Go-live methodology

The largest gap in every previous version of these documents, and — as a product — something you will sell and repeat at every customer rather than do once.

### Stage 1 — Pre-cutover (weeks before)

| Activity | Exit condition |
|---|---|
| Location survey | Every bin physically labelled and matching `location`; pick sequence walked and verified |
| Master data load | Items, UoMs, barcodes imported and validated; **every barcode scan-tested**, not just imported |
| Device provisioning | Handhelds registered, PWA installed, coverage survey completed with dead spots mapped |
| User and role setup | Every operator has a badge and a role scope; supervisors identified |
| Configuration | Strategies chosen with the customer, not defaulted |
| Integration test | ERP round trip proven in a staging environment |
| Training | Operators trained on the actual devices, in the actual aisles |

The coverage survey deserves emphasis: knowing where wifi is bad **before** go-live turns a support crisis into an expectation.

### Stage 2 — Opening balance

The single highest-risk activity in any WMS implementation.

1. **Freeze window.** No stock movements between count and go-live. Usually a weekend; sometimes a shift.
2. **Full physical count**, location by location, ideally on the new devices — it doubles as final training.
3. **Import via `import_job` with `import_type = 'opening_balance'`**, validate-then-apply.
4. **Reconcile against the outgoing system** and explain every variance before proceeding. Unexplained variance at this point becomes permanent, and every future discrepancy investigation runs into it.
5. Opening movements are written with `movement_type = 'adjustment'` and a dedicated reason code, so the ledger's first entries are identifiable forever.

Where a full freeze is impossible, count by zone across several nights and freeze each zone as it is counted.

### Stage 3 — Pilot

One zone live on the WMS, the rest on the existing process, for a defined period — typically one to two weeks.

This is where real problems surface: barcodes that do not scan under actual lighting, pick sequences that do not match how people actually walk, screens that need a two-hand grip. Fixing these across one zone is a bad week. Fixing them across a building is a failed implementation.

### Stage 4 — Cutover

A sequenced, timed runbook with named owners, a communications plan, and a single decision-maker.

**Abort criteria, agreed in writing before the day.** Deciding whether to abort while the warehouse is stalling is not a decision anyone makes well:

| Trigger | Threshold |
|---|---|
| Reconciliation variance after 4 hours | > 0.5% of counted lines |
| Scan-to-confirm p95 | > 3× the pilot baseline |
| Open exceptions unresolved | > 50, or any ageing beyond 2 hours |
| Pick rate against baseline | < 60% after 6 hours |
| Any data-integrity error | Immediate |

Abort means reverting to the previous process with the paper fallback, not debugging in production with fifty people waiting.

### Stage 5 — Hypercare

Two weeks of elevated support: on-site or immediately reachable, exception queue monitored continuously, daily reconciliation reviewed with the customer, daily issue triage.

**Sign-off criteria** rather than a date: reconciliation variance at zero for five consecutive days, exception queue ageing under four hours, pick rate at or above the pre-cutover baseline, and no open severity-1 issues.

**Staffing (Open Item 12): two people for two weeks, one on site for the first three days.**

For the first customer specifically, **whoever built the system should be the one on site**. It is not sustainable and it does not scale, but it is the fastest learning available about what the product actually needs — and that learning is worth more at customer one than the engineering time it costs.

Two consequences that are commercial rather than technical:

- Hypercare cost belongs in the **implementation fee**, not absorbed into the licence. It is real, recurring per customer, and invisible if bundled.
- Hypercare capacity **caps concurrent go-lives**. With a small team that is one at a time, which is a sales constraint as much as a delivery one, and needs to be known before someone promises two in a month.

## 11.7 Phase 4 exit criteria

1. A new customer deployment is provisioned end to end by automation and passes its smoke test with no manual step.
2. A release rolls internal → canary → general, halting automatically on an injected error-rate regression.
3. A migration runs unattended across the fleet and is resumable after an induced mid-run failure.
4. A customer's configuration is exported, imported into a clean test deployment, and reproduces their setup.
5. Fleet dashboard shows version drift, exception ageing, and variance count across all deployments.
6. A full go-live rehearsal is completed against a synthetic customer, including opening balance import and a triggered abort.

---

# Part V — How the Non-Functional Picture Evolves

The NFRs in §5 are stated for Phase 1A/1B. They do not stay still.

## 12.1 Scaling pressure by phase

| Phase | New pressure | Response |
|---|---|---|
| 1A/1B | Baseline: 50 operators, scan latency, task dispatch | Partitioned ledger, `SKIP LOCKED`, reserved API pool |
| 2 | Wave allocation is bursty and CPU-heavy; media processing competes for I/O | Allocation already isolated in Worker; media renditions on a separate worker role with its own pool |
| 3 | Reporting load grows; count sheets add write volume; ERP sync adds sustained background traffic | Materialised views on the replica; integration staging table partitioned by month |
| 4 | N deployments, each with its own profile; no single system is larger, but the fleet is | Per-deployment sizing; control plane aggregates rather than centralises |

**The fleet does not make any single deployment bigger.** That is the main scaling advantage of single-tenant delivery, and it is why Docker Compose remains viable per deployment far longer than it would for a multi-tenant SaaS. The ceiling is operational, not technical, and it arrives at roughly ten to fifteen customers.

## 12.2 Security posture by phase

| Phase | Addition |
|---|---|
| 1A | Argon2id credentials, token rotation, security stamp, permission scoping, segregation of duties, full audit |
| 2 | Media access control — packing photos can reveal customer packaging; content-addressed URLs need either short-TTL presigning or an authorising endpoint |
| 3 | Carrier and ERP credentials in a secret store, referenced by `credentials_ref` and never stored in the database; integration payloads may contain customer PII, so retention applies to `integration_message` too |
| 4 | Control-plane authentication to each deployment; least-privilege heartbeat scope; no data-plane access from the control plane, ever |

The Phase 4 line is the one to hold. Once a control plane can read customer data "just for support", single-tenant isolation is decorative.

## 12.3 Error handling — unchanged governing principle

**Commands may fail. Facts may not.**

Every phase adds work types, and each one is classified on the same axis before anything is built:

| Phase | New facts (never refused) | New commands (refusable) |
|---|---|---|
| 2 | `replenish_confirmed`, `carton_packed`, `carton_weighed` | Wave release, wave cancel, consolidation confirm |
| 3 | `count_confirmed`, `return_received` | Variance approval, disposition, configuration change, label request |
| 4 | — | Provisioning, upgrade, configuration import |

Two notes on the boundaries:

- **Carton content scanning is a command, not a fact.** Rejecting a wrong item scan at packing is legitimate because nothing physical has happened yet — the item is in the packer's hand, not in the carton. The distinction is whether refusing asks someone to undo something they cannot undo.
- **Weight verification blocks rather than warns.** A divergence means a miss-pick that scan verification missed, and letting it through defeats the control.

## 12.4 What stays out, at every phase

Explicitly not built, and the trigger that would change that:

| Not built | Would be justified by |
|---|---|
| Message broker (Kafka, RabbitMQ) | Outbox throughput becoming a measured bottleneck |
| Distributed cache | Read latency the replica cannot absorb |
| Microservices | A module needing independent scaling or an independent team |
| Kubernetes | Roughly 10–15 deployments, or a genuine 24/7 no-downtime requirement |
| Sharding | A single customer exceeding what one well-specified instance handles |
| Event sourcing beyond the ledger | Nothing foreseeable — the ledger already provides it where it matters |

Each of these is a real option. None is justified by the requirements as they stand, and adopting any of them early costs a small team more than it returns.


## Appendix A — Where Each Capability Is Designed

| Capability | Phase | Section |
|---|---|---|
| Identity, roles, scope, device sessions | 1A | §2.1, §5.1, §6.1 |
| Admin management: users, roles (incl. custom role composition), warehouses, zones, devices, handling units | 1A | §5.11 |
| Admin dashboard and inbound/stock reporting | 1A | §5.12 |
| OpenAPI document, generated client, and Swagger UI for manual testing | 1A | §3.6 |
| Master data, barcodes, UoM hierarchy | 1A | §2.2, §5.11 |
| Ledger, balances, exceptions, reconciliation | 1A | §2.3, §4, §7.2 |
| Task engine and leasing | 1A | §2.4, §5.2, §6.10 |
| Receiving and putaway | 1A | §2.5, §6.2, §6.3 |
| Offline sync and the fact endpoint | 1A | §4, §6.3, §6.4 |
| Orders, allocation, picking | 1B | §2.6, §6.7, §6.8, §6.9 |
| Waves, zone and cluster picking | 2 | §9.1, §6.11 |
| Replenishment | 2 | §9.2, §6.12 |
| Allocation and putaway strategies | 2 | §9.3 |
| Packing, cartons, weight verification | 2 | §9.4, §6.13 |
| Media, renditions, retention | 2 | §9.5, §6.14 |
| Bulk import | 2 | §9.6 |
| Cycle counting | 3 | §10.1, §6.15 |
| Returns and disposition | 3 | §10.2, §6.16 |
| Serial tracking and genealogy | 3 | §10.3, §6.17 |
| Configuration as data | 3 | §10.4, §6.18 |
| Carrier integration and labels | 3 | §10.5 |
| ERP and EDI integration | 3, 4 | §10.6, §6.19, §11.5 |
| Reporting and materialised views | 3 | §10.7 |
| Fleet control plane | 4 | §11.1 |
| Provisioning and upgrade rings | 4 | §11.2, §11.3, §6.20, §6.21 |
| Labour management | 4 | §11.5 |
| Slotting analysis | 4 | §11.5 |
| WCS integration boundary | 4 | §11.5 |
| Go-live methodology | 4 | §11.6, §6.22 |

## Appendix B — Resolved Decisions

All twelve items closed. Each resolution is written into the section shown; this table is the index and the rationale summary.

### Decided on their merits

| # | Item | Resolution | Section |
|---|---|---|---|
| 1 | Base UoM policy | **Per-item**, not globally `EACH`. `base_uom` immutable once movements exist. `item_uom.is_discrete` rejects fractional quantities on countable units | §2.2 |
| 2 | Lease TTL and batch size | **10 hours, per-warehouse configurable**, batch 20. Second timer `lease_heartbeat_at` informs reclaim decisions | §2.4, §3.2 |
| 3 | Reason code taxonomy | **A `reason_code` table**, not an enum. ~25 seeded, customer-extensible. `is_shrinkage` separates loss from surplus | §2.7 |
| 4 | Document numbering | **Per-warehouse, per-type, per-day** via `document_sequence` with `UPDATE … RETURNING`. Gap-tolerant by default | §2.7 |
| 5 | Idle timeout | **Two timers: 3-minute suspend, 12-hour session end.** Suspend never terminates an open task | §3.2 |
| 6 | Report export in 1A | **CSV in 1A, Excel in Phase 3.** Both asynchronous via queued job | §3.9 |
| 11 | Control-plane hosting | **Separate region** from the deployments it manages | §8.1 |

### Revised from the original framing

| # | Item | Resolution | Section |
|---|---|---|---|
| 8 | Count during picking | Freeze retained, but **scheduled** via `count_plan.schedule_window` — fast-moving pick faces counted outside peak hours | §7.1 |
| 10 | Labour management | **Split into three tiers.** Activity capture and aggregate capacity reporting in the base product; individual performance scorecards a separate module, off by default | §8.5 |

### Decided under a stated assumption, with a revisit trigger

These lacked data. Deferring them costs more than deciding and revising, so each is decided with its assumption and trigger written down.

| # | Item | Resolution | Assumption | Revisit when |
|---|---|---|---|---|
| 7 | Pick method | **Discrete and zone in Phase 2.** Batch and cluster deferred. Default zone for multi-zone sites, discrete otherwise | Average order size is moderate (2–5 lines) | Real order profile is known. Below ~2 lines/order, batch and cluster become high-value |
| 9 | First carriers | **None in the base product.** Adapter interface plus manual label path in Phase 2; each carrier added for and funded by the deal requiring it | Carriers vary by customer and geography | First design partner names their carrier. First two adapters must use different transports |
| 12 | Hypercare staffing | **Two people, two weeks, one on site for the first three days.** Builder on site for customer one | Small team, single concurrent go-live | Third customer, or when a deal requires overlapping go-lives |

### Decisions carrying a commercial consequence

Four resolutions above are business decisions with a design consequence, and are flagged so they reach the right person:

- **Gapless document numbering** (4) has a throughput cost. Confirm with the first customer's auditor rather than assuming.
- **Individual performance monitoring** (10) as a separate module protects deals in accounts where it would never be enabled.
- **Carrier adapters** (9) are funded per deal. Each is two to four weeks and should be priced, not absorbed.
- **Hypercare** (12) belongs in the implementation fee and caps concurrent go-lives to one. That is a sales constraint before it is a delivery one.

### Newly opened

Resolving these surfaced three smaller questions, none blocking:

| # | Item | Needed by |
|---|---|---|
| 14 | Whether `schedule_window` needs per-zone granularity or per-plan suffices | Phase 3 build |
| 15 | Licence packaging boundary for the individual-performance module | Phase 4 scope |

---

## Appendix C — Reason Code Catalogue

Resolves Open Item 13. The seed migration is `0042__seed_reason_codes.sql`; this appendix covers the design decisions behind it.

**34 codes across seven categories**, more than the ~25 first estimated. The overrun is in receipt and pick discrepancies, where the distinctions turned out to be operationally real rather than cosmetic: "supplier short shipped" and "damaged in transit" lead to different claims against different parties, and collapsing them loses the ability to make either.

### The four flags

| Flag | Meaning | Design consequence |
|---|---|---|
| `requires_note` | Free text mandatory before the fact is accepted | Adds ~10 seconds. Used only where the code alone is genuinely ambiguous |
| `requires_photo` | Media capture mandatory | Reserved for evidence supporting a claim against a third party, or an internal write-off |
| `requires_approval` | Supervisor elevation (§6.5) | Anything that writes off value or corrects the ledger administratively |
| `is_shrinkage` | Counts as loss the business bears | Drives loss reporting; see below |

### Three decisions worth defending

**Receipt discrepancies are not shrinkage.** A supplier short shipment is a loss that sits with the supplier, recoverable through a claim. Marking it shrinkage inflates apparent internal loss and points investigations in the wrong direction. The photo requirements in this category exist because a claim without evidence is a write-off in practice.

**Count overage is not shrinkage, and must never be netted against shortage.** Netting them hides both. A location with +50 and −50 has an accuracy problem that a net of zero conceals entirely, and this is the single most common way inventory-accuracy reporting is rendered useless.

**`PICK_NOT_FOUND` is the most operationally valuable code in the catalogue.** It marks phantom inventory — the system believed stock was in a location and it was not — and its rate is the best available proxy for overall inventory accuracy. It is deliberately friction-free: no note, no photo, no approval. A picker at an empty bin needs to move on, and any friction here means they will pick a different code or skip the task, destroying the signal.

### The catch-all

`GEN_OTHER` is included deliberately, made expensive to use (note, photo, and approval all required), and monitored. A catalogue with no escape hatch does not eliminate unclassified events — it causes operators to choose a *wrong* code instead, which is worse, because wrong data looks like real data.

**Alert when `GEN_OTHER` exceeds 5% of coded events.** That threshold is not about operator behaviour; it means the catalogue is missing something the operation actually does.

### Presentation

Never show all 34 on a handheld. The device filters by `applies_to` against the movement type in progress, sorts by `sort_order`, and shows the three most-used codes for that context first. A pick discrepancy screen offers six options; a receipt screen offers eight.

### Validation

This list is a draft built from common warehouse situations, not from an observed operation. Expect roughly three to five additions and two to three deletions per customer. Reviewing it with a warehouse manager is a high-yield hour — reacting to a concrete list surfaces far more than asking what reason codes they need.

Customers extend the catalogue through configuration, never through migration.
