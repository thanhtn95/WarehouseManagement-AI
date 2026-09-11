---
name: database-design
description: Naming, key strategy, grain, and normalization conventions for designing a new table or column in the WMS schema. Use when deciding what a table should look like, before writing its migration.
---

# Database design conventions

**REQUIRED BACKGROUND:** the sql-migrations skill covers *how to ship* a
schema change safely (file format, expand-then-contract, partitioning
mechanics). This skill covers the decisions that come *before* that — what
the table should actually look like.

Grounded in [PostgreSQL's own documented identifier behaviour](https://www.postgresql.org/docs/current/sql-syntax-lexical.html#SQL-SYNTAX-IDENTIFIERS)
and [GitLab's public Database Migration Style Guide](https://docs.gitlab.com/development/database/) —
the closest thing to a widely-cited "big tech" reference for running
PostgreSQL at scale, which is the same expand-then-contract, no-maintenance-
window discipline this project already follows.

## Naming

- **Lowercase `snake_case`, always.** Postgres folds every unquoted
  identifier to lowercase; anything else means quoting that identifier
  forever, everywhere, including every hand-written migration and every ORM
  mapping.
- **Singular table names** (`item`, not `items`) — matches this project's
  existing convention (design doc §0). A table holds a set of rows of one
  kind; the name should say what one row *is*, and it sidesteps irregular
  pluralization ambiguity (`inventory` vs `inventories`?) entirely.
- **Foreign key columns:** `<referenced_entity>_id`, using the entity's
  semantic name rather than its literal table name where they differ —
  `app_user` rows are referenced as `user_id`, not `app_user_id`, because
  `app_` exists only to dodge `user` being a near-reserved word in Postgres,
  not to become part of every reference to it. A role adds a prefix instead
  of the bare pattern where the role is the point: `actor_user_id` and
  `target_user_id` on `auth_event` distinguish who did something from who it
  was done to; `parent_hu_id` on `handling_unit` beats `handling_unit_id`
  for a self-reference.
- **Audit "who touched this" columns come in two deliberately different
  shapes — pick by what the column is for, not by habit.** A plain
  administrative stamp on a business/config row is bare: `created_by`,
  `updated_by`, `changed_by` (used this way on `receipt`, `sales_order`,
  `wave`, `import_job`, `configuration`, `config_change_log`). Identity
  captured as first-class data on a security or ledger **event** — where
  distinguishing *whose role* matters to the event's meaning, not just who
  gets blamed — gets the fuller `<role>_by_user_id` form:
  `authorized_by_user_id` (the supervisor who overrode, distinct from the
  `actor_user_id` who performed the movement), `raised_by_user_id` /
  `resolved_by_user_id` on `inventory_exception`. Don't "fix" one shape to
  match the other — that's collapsing a real semantic distinction, not
  cleaning up an inconsistency.
- **Booleans:** `is_`/`has_`/`allows_`/`requires_` prefix, never a bare
  adjective — `is_lot_tracked`, `allows_mixed_lot`, `requires_photo`. The
  prefix alone tells a reader the column is a boolean without checking the
  DDL.
- **Timestamps vs dates:** `_at` suffix for an instant (`created_at`,
  `resolved_at`, always `timestamptz`), `_date` suffix for a calendar date
  with no time component (`expiry_date`, `business_date`, always `date`).
  Never store a date-only value in a `timestamptz` column to "keep it
  consistent" — it invites exactly the kind of day-boundary bug `business_date`
  exists to prevent (design doc §2.2, M4). **Exception:** a range boundary
  named `_from`/`_until`/`_since` doesn't need the type suffix on top —
  `valid_from`/`valid_until` (date) and `locked_until` (timestamptz) are
  self-documenting as boundaries regardless of the underlying type; adding
  `_date` or `_at` to those specifically reads worse, not clearer.
- **Discriminator columns** (`status`, `movement_type`, `exception_type`):
  bare noun, `text` + `CHECK`, never a native Postgres `enum` — see
  sql-migrations for why.
- **Hand-written indexes** (partial or expression, since Postgres
  auto-names simple ones): `<table>_<purpose>_idx` — `task_ready_idx`,
  `outbox_pending_idx`, `item_media_primary_idx`. Let Postgres auto-name a
  plain `CHECK`/`UNIQUE`/FK constraint unless it needs a stable name for a
  later `DROP CONSTRAINT`.

## Keys

- **`uuid` (v7) primary key** for every entity table — time-ordered, so it
  doesn't fragment the primary key's B-tree the way a random `uuid` v4 does,
  while still being safe to generate client-side or across services without
  a central sequence.
  **Exception:** a small, stable, code-referenced lookup table uses its
  natural code as PK instead — `permission.code`, `reason_code.code`. These
  codes are already the stable public identity everything else refers to
  (`role_permission.permission_code`, application code, config); a UUID
  surrogate would just be indirection between a value and itself.
- **Composite primary key** where the natural grain already *is* the
  identity — don't bolt a surrogate `id` onto something a tuple already
  identifies uniquely. `stock_balance`'s PK is
  `(owner_id, item_id, location_id, lot_id, stock_status)`;
  `document_sequence`'s is `(warehouse_id, document_type, business_date)`.
  A surrogate key there would just be a second, redundant way to name the
  same row.
- **Sentinel values, not `NULL`, for an "optional" dimension that must
  participate in a key.** `NULL` can't sit inside a `UNIQUE` or composite
  primary key the way you'd want (`NULL <> NULL`), and it turns every join
  or comparison on that column into a `NULL`-handling trap. Seed a real row
  meaning "not applicable" instead — `stock_balance.lot_id` uses
  `00000000-0000-0000-0000-000000000000` for "no lot" rather than allowing
  `NULL` (design doc §2.3).

## Columns

- `numeric(18,4)` for every money or quantity value — never `float`,
  `double precision`, or `money`. See sql-migrations for the full type
  table; don't restate it here.
- `jsonb` only for genuinely variable or sparse data — `*_i18n` label maps,
  `configuration.config_value`. If a `jsonb` column ends up filtered or
  joined on the same key regularly, that key wants to be a real column with
  a real index, not a `jsonb` operator query pretending to be one.
- Every **mutable** entity table carries `created_at`, and `updated_at` when
  no single transition dominates its mutation — but **prefer a
  domain-specific timestamp over the generic one whenever the table has one
  clear, meaningful transition.** `granted_at` (`user_role_scope`),
  `last_seen_at` (`device`), `processed_at` (`outbox_message`),
  `resolved_at` (`inventory_exception`) each say more than `updated_at`
  would, and this schema uses them consistently in preference to it. Reach
  for generic `updated_at` only when a row is genuinely touched across many
  unrelated fields with no one distinguishing moment. An **append-only**
  table (`stock_movement`, `auth_event`) gets neither — a row in it is never
  updated, and the column's presence would be a standing invitation to
  violate that.
- No soft deletes, ever — status column plus a history table instead (design
  doc §0). A `deleted_at` column is a query filter everyone has to
  remember, forever, in every query against that table.

## Grain and normalization

- A table's grain should be traceable to an aggregate or an event, not
  chosen for a report's convenience. `stock_balance`'s grain is exactly the
  `StockPosition` aggregate boundary (design doc §2.3) — not "whatever
  columns the inventory report happens to need."
  **REQUIRED BACKGROUND:** the wms-domain skill's aggregate table, for what
  the aggregates actually are.
- Denormalize only where a hot path is proven to need it, and say why in
  the migration header. `on_hand` and `allocated` sharing one
  `stock_balance` row — instead of two tables joined at read time — exists
  specifically so allocation and picking take one lock instead of a
  read-modify-write across two; that's a documented trade-off, not the
  default move.

## Foreign keys and indexing

- Every FK references a real primary key or unique constraint — Postgres
  won't silently allow the orphaned rows some ORMs' "logical," unenforced
  foreign keys do.
- Don't index a FK reflexively. Add the partial or composite index the
  actual query pattern needs (`task_ready_idx` supports the lease query, not
  "every FK gets an index because a linter said so") — see sql-migrations
  §Indexes.

## Before you write the migration

Changing schema means the design document changes in the same commit — see
the system-design skill for which document, and its propagation-rule
section for tracking down every place a changed convention gets echoed.
