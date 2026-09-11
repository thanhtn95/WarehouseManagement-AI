---
name: sql-migrations
description: Conventions for writing DbUp SQL migrations in this project — naming, partitioning, indexes, expand-then-contract, and fleet safety. Use whenever creating or altering database schema.
---

# SQL migrations

Raw SQL applied by DbUp from `db/migrations/`. **Never EF Core migrations** —
the schema needs declarative partitioning, partial indexes, and `SKIP LOCKED`
semantics that EF migrations obstruct, and these files are read during
production incidents, where clarity beats abstraction.

## Naming and immutability

`NNNN__snake_case_description.sql`, zero-padded to four digits.

**A migration that has been applied anywhere is immutable.** Editing one means
some databases have the old version and some the new, with no way to tell
which. Write a new migration instead.

## Header

```sql
-- =====================================================================
--  0043__add_replenishment_rules.sql
--  Duration: instant | seconds | background
--  Backward-compatible with: v2.4.x  (expand phase of expand/contract)
-- =====================================================================
```

Duration matters because these run unattended across a fleet. A migration
taking 200ms against a dev database can take 40 minutes against the largest
customer's `stock_movement`.

## Expand-then-contract

The migrator runs to completion **before** API containers restart, so new
schema is briefly live under the previous application version. Every migration
must be compatible with both.

1. Add the nullable column and backfill.
2. Start writing to it.
3. Drop the old column.

Three releases. Tedious, and it is what permits deploying without a
maintenance window.

## Types

| Use | Not |
|---|---|
| `numeric(18,4)` | `float`, `double precision`, `money` |
| `timestamptz` | `timestamp` |
| `uuid` (v7) | `serial`, `bigserial` for entity keys |
| `text` + `CHECK` | native `enum` — altering one rewrites the table |
| `jsonb` `*_i18n` | `text` for user-facing labels |

## Partitioning

`stock_movement` is range-partitioned monthly on `recorded_at` with PK
`(id, recorded_at)`. Partitions are created twelve months ahead by a scheduled
job.

Partition on the **server** clock. A device with a wrong clock must never be
able to write into a missing partition.

## Indexes

Partial indexes for the hot paths:

```sql
CREATE INDEX task_ready_idx ON task (warehouse_id, zone_id, priority, sort_sequence)
  WHERE status = 'ready';
CREATE INDEX outbox_pending_idx ON outbox_message (created_at)
  WHERE processed_at IS NULL;
CREATE UNIQUE INDEX item_media_primary_idx ON item_media (item_id)
  WHERE is_primary;
```

Use `CREATE INDEX CONCURRENTLY` on populated tables — but note it cannot run
inside a transaction, so it needs its own migration file.

## Constraints that must not exist

- `CHECK (on_hand >= 0)` on `stock_balance`. Offline facts drive balances
  negative and that is correct behaviour.
- Anything preventing `INSERT` into `stock_movement`, or permitting `UPDATE`
  or `DELETE` on it.

## A migration that adds a permission must backfill System Administrator

`0007__seed_reference_data.sql` grants System Administrator `SELECT code FROM
permission` — a **one-time snapshot** taken when that migration ran, not a
standing rule. Nothing re-runs it.

This matters because role granting is guarded by a subset check: an
administrator may only grant permissions they themselves hold. The check is
self-referential and has no floor, so **a permission no live user holds
becomes permanently ungrantable**. Add `pick.short` in migration 0043 without
backfilling, and no one in any deployment can ever delegate it — the failure
appears months later as "why can't I build that role", with nothing in the
logs connecting it to a migration.

So every migration that inserts into `permission` must, in the same file:

```sql
INSERT INTO role_permission (role_id, permission_code)
SELECT '10000000-0000-0000-0000-000000000001', code
  FROM permission WHERE code IN ( ...the codes this migration added... )
ON CONFLICT DO NOTHING;
```

The same reasoning is why suspending the last holder of a permission needs a
break-glass answer rather than being discovered during an incident.

## Fleet safety

Idempotent where possible (`IF NOT EXISTS`). Resumable — a migration failing
halfway on customer 14 of 30 must not need hand repair on each.

**DbUp does not checksum scripts.** Its journal (`schemaversions`) records
the script name and the time it ran, nothing about content — verified
against a real database, and corrected here after the design claimed
otherwise. An already-applied migration that is later edited will not be
detected by the migrator. Immutability is enforced *before* that point, by
`guard-applied-migration.sh` at authoring time and the architecture-test
backstop; a content-hashing journal is a Phase 4 fleet item (design doc
§11.3).
