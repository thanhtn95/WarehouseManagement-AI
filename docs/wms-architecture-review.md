# WMS Architecture Review and Revision

**Reviewer role:** Senior System Architect
**Reviewing:** WMS Project Proposal v0.1
**Status:** Findings and revised decisions — supersedes the named sections of v0.1
**Date:** September 2026

---

## Summary of Findings

The domain model, module boundaries, and technology selections in v0.1 hold up. The delivery and CI/CD plan holds up. The problems are concentrated in **concurrency, offline behaviour, and operational resilience** — the areas where a design reads fine on paper and fails on a warehouse floor at 09:00 with thirty operators.

Five findings are correctness issues rather than preferences. One of them is a direct internal contradiction in v0.1.

| ID | Finding | Severity |
|---|---|---|
| C1 | Synchronous balance maintenance reintroduces the exact hot-row contention the ledger was chosen to avoid | **Critical** |
| C2 | Allocation has no defined serialisation point, so concurrent orders can oversell | **Critical** |
| C3 | Offline operation is specified as "queue and sync" with no rule constraining what may be created offline | **Critical** |
| C4 | Ledger ordering relies on timestamps from devices whose clocks are not trustworthy | **Critical** |
| C5 | SignalR across two API replicas has no backplane and will silently deliver to the wrong replica | **Critical** |
| H1 | Reconciliation job as described is a full scan of the largest table in the system | High |
| H2 | Idempotency is asserted as a requirement but never designed | High |
| H3 | Media deduplication by content hash crosses tenant boundaries | High |
| H4 | No client version gate; a stale cached PWA will sync against a changed API | High |
| H5 | Single on-premise host with no standby and no stated recovery objective | High |
| H6 | "Separate read model" is named but not specified | High |
| M1 | Table partitioning specified from day one may be premature; the key design is the part that matters | Medium |
| M2 | Testcontainers per test class will make CI unpleasantly slow | Medium |
| M3 | Per-request permission resolution has no caching or invalidation story | Medium |

---

## Critical Findings and Revised Decisions

### C1 — The ledger and the balance table cancelled each other out

**The problem.** v0.1 justified the append-only ledger partly on the grounds that appending rows avoids hot-row contention. It then specified that `stock_balance` is updated incrementally *in the same transaction as the movement insert*. That update takes a row lock. Every concurrent transaction touching that balance row serialises behind it. The contention the ledger was supposed to eliminate is reintroduced one paragraph later.

This is the kind of error that survives review because both halves are individually defensible.

**Revised decision.** Keep the synchronous balance update, but constrain the grain rigidly and forbid aggregates.

- `stock_balance` is keyed on **(sku_id, location_id, lot_id, stock_status)** and nothing coarser.
- At that grain, contention is naturally sharded by physical location. Two operators transacting the same SKU in the same bin, in the same lot, at the same instant, is rare — they are physically standing in the same place. Contention is therefore low in practice, and the transactional consistency is worth having.
- **No synchronously-maintained SKU-level or warehouse-level total exists anywhere in the schema.** That row would be touched by every movement of that SKU across the entire building, and it is the actual hot row. Warehouse-wide availability is computed on demand by aggregating balance rows, or read from the replica (see H6).

If a future requirement demands a fast global availability figure, the correct answer is a periodically-refreshed cache with an explicit staleness contract — never a synchronously-updated counter.

### C2 — Allocation was undefined, and it is the real race

**The problem.** v0.1 named FEFO/FIFO/nearest-bin as allocation strategies and said nothing about how two orders arriving simultaneously for the last unit are prevented from both succeeding. Preventing overselling requires a serialisation point per SKU. There wasn't one.

**Revised decision. Allocation becomes a queued, serialised background process rather than a synchronous operation.**

```
Order arrives  →  status = pending_allocation, committed immediately
                       ↓
             allocation queue (Postgres table)
                       ↓
        allocation worker, one active consumer per warehouse
        (FOR UPDATE SKIP LOCKED, concurrency 1)
                       ↓
         status = allocated, hard reservations written
                       ↓
              eligible for wave release
```

The reasoning is that **allocation is not latency-critical**. No one picks an order 200 milliseconds after it arrives; it waits for the next wave regardless. Spending that latency budget to eliminate an entire class of race condition is an obvious trade. A single consumer per warehouse means allocation decisions are strictly ordered and no locking protocol is needed at all.

If throughput ever demands parallelism, shard by `hashtext(sku_code) % N` with `pg_advisory_xact_lock` per shard — but measure first. A single worker handles tens of thousands of lines per hour on modest hardware, which is well beyond the stated need.

This also makes allocation strategies far easier to test, because they become a pure function over a consistent snapshot rather than a concurrent operation.

### C3 — "Queue and sync" was hope, not a design

**The problem.** v0.1 said offline actions queue locally and sync later. It did not say *what* may be queued. If a handheld can create arbitrary movements offline, the server must handle confirmations that arrive twenty minutes late against stock that has since been adjusted, counted, or allocated elsewhere. There is no generally correct resolution for that conflict, and inventing one per case produces silent inventory drift.

**Revised decision. A hard rule, enforced in the API surface:**

> **A handheld may confirm work offline only if that work was assigned to it while online, against stock that is already hard-allocated to the task.**

The consequence is that offline confirmations **cannot fail on the server**. The reservation already exists; the confirmation merely resolves it. Late arrival changes nothing, and idempotent replay is safe.

Actions that *create* new facts about the world — ad-hoc moves, cycle counts, receipts, adjustments — are treated differently:

| Action type | Offline behaviour |
|---|---|
| Confirm assigned pick, putaway, replenishment | Permitted, always accepted on sync |
| Ad-hoc move, count, receipt, adjustment | Blocked offline, or queued to `pending_review` for supervisor clearance |

Blocking is the recommended default. The `pending_review` path exists for operations that genuinely cannot afford to stop, and it must be a visible queue with an ageing alert, not a silent buffer.

### C4 — Device clocks are not a source of truth

**The problem.** The ledger used `occurred_at` as a single timestamp. On an offline-capable handheld, that value comes from a device whose clock may be wrong by minutes or, after a battery swap, by years. Ordering the ledger by it produces movements that appear to happen before the receipt that created the stock.

**Revised decision.** Three fields, with clear authority:

```
sequence            bigint, generated always as identity  -- authoritative order
recorded_at_server  timestamptz, server clock             -- authoritative time
occurred_at_device  timestamptz, device clock             -- forensic only
```

All ordering, reconciliation, and balance derivation use `sequence`. Device time is retained because it answers "how long was this device offline", which matters when investigating variance. Device timestamps skewed beyond a threshold (say 15 minutes) are recorded but flagged, and the device is prompted to resync.

### C5 — SignalR across replicas, without a backplane

**The problem.** v0.1 put SignalR in the API container and specified two API replicas behind the proxy. Without a backplane, a message published on replica A never reaches a handheld connected to replica B. This fails intermittently and looks like a network problem, which makes it expensive to diagnose.

**Revised decision. Remove SignalR from Phase 1. Handhelds poll.**

A handheld polling for its next task every three to five seconds is:

- Correct across any number of replicas with no shared state
- Naturally graceful when the network drops — a missed poll is a non-event, where a dropped websocket needs reconnection logic
- Trivially debuggable with curl

The cost argument for push does not apply at this scale. A warehouse has tens to low hundreds of devices, not thousands. The polling load is negligible against a database that is already handling the transactional workload.

This also means **Redis is not required**, which was the alternative fix. I considered adding it and rejected it: with polling instead of push, single-worker allocation instead of distributed locking, and Postgres handling rate-limit counters, Redis would earn its place in the topology on caching alone — not enough to justify another stateful container on an on-premise box that someone has to back up and upgrade.

---

## High-Severity Findings

### H1 — Reconciliation must be incremental

Recomputing all balances from a ledger of hundreds of millions of rows is a full scan and a multi-hour job. As specified it would be disabled within a month of go-live, which removes the system's own correctness monitor.

**Revised:** watermark-based incremental reconciliation.

- Store `last_reconciled_sequence`.
- Each run recomputes only the `(sku, location, lot, status)` tuples with movements above the watermark. Typically thousands of rows, seconds of work. Run every 15 minutes.
- Separately, a **rolling full sweep** partitioned across a 30-day cycle — each nightly run verifies 1/30th of all balance rows. Every row is verified monthly without any single job being large.
- Any variance raises an alert and writes a `balance_discrepancy` record. It does not auto-correct: silent correction hides the bug that caused it.

### H2 — Idempotency, specified

Asserted in v0.1, never designed. It is load-bearing for C3.

```
idempotency_record
  key            text primary key      -- client-generated UUID
  endpoint       text
  request_hash   text                  -- guards against key reuse with different payload
  status_code    int
  response_body  jsonb
  created_at     timestamptz
```

Insert the key as the first statement of the transaction. A unique violation means this is a replay: return the stored response. A matching key with a *different* `request_hash` is a client bug and returns 422 rather than silently accepting either version. Retain 7 days.

The handheld generates the key when the operator taps confirm, not when the request is sent — otherwise a retry generates a new key and duplicates the movement.

### H3 — Content-hash deduplication leaks across tenants

Global `sha256` dedup means tenant A's asset row can point at bytes uploaded by tenant B. Authorisation at the link table prevents direct access, but the existence of a matching hash is itself an information leak, and refcounted deletion means tenant A's deletion request may not actually delete the bytes — which is a problem if a client asks for confirmation of data destruction.

**Revised:** scope the uniqueness constraint to the tenant.

```sql
CREATE UNIQUE INDEX ON media_asset (tenant_id, sha256);
```

Some storage is duplicated across tenants. Catalogue imagery is small, this costs little, and it removes an entire class of question. For a single-operator deployment (decision D1) the tenant column is constant and behaviour is unchanged.

### H4 — Client version gate

A PWA caches itself. A device offline for a shift, or one where the service worker did not update, will sync payloads against an API that has moved on. This surfaces as corrupt data rather than a clean error.

**Revised:**

- Every API response carries `X-Min-Client-Version`.
- Below that version, the client refuses to sync and displays a forced-update screen.
- The service worker applies updates only at a **safe point** — no open task, no queued unsynced actions. Never `skipWaiting` mid-pick.
- The API supports the previous client version for at least one release cycle, so a device that missed an update is degraded rather than bricked.

### H5 — Single host, undefined recovery

v0.1 required tested backups but never stated a recovery objective. "Restore from backup" on a warehouse box means hours of downtime during which no goods move. That needs to be a stated, accepted number or a mitigated one.

**Revised — and this also resolves H6.** Add a second PostgreSQL instance as a **streaming replica**:

- Reporting and analytics read from the replica, keeping long queries away from the transactional primary.
- The replica is a warm standby. Promotion is minutes, not hours.
- Backups are taken from the replica, removing backup I/O from the primary entirely.

One additional instance solves the read model, the recovery objective, and backup interference together. On-premise this can be a second physical box, which also covers host failure rather than only process failure.

Recovery objectives should then be stated explicitly in the proposal — for example, RPO measured in seconds via streaming replication, RTO in minutes via promotion — and confirmed with the business rather than assumed.

### H6 — Read model, specified

Resolved by H5. **Physical streaming replication rather than a separate projected read model.** The simpler option is correct here: the reporting workload is analytical queries over the same relational structure, not a differently-shaped view of the data. Building CQRS projections would add a synchronisation problem in exchange for nothing.

Route reads at the connection level: a `ReadOnlyDbContext` / separate Dapper connection string, with an architecture test asserting that write operations never use it.

---

## Medium-Severity Findings

### M1 — Partition later, but design the key now

Monthly partitioning of `stock_movement` from day one is likely premature. At moderate volume the table reaches only a few million rows a year, where partitioning adds operational complexity for no measurable gain.

But converting an unpartitioned table later is painful, because a partitioned table requires the partition key in every unique constraint.

**Revised:** design for it, defer the decision to D5 (volume).

- Primary key is `(id, occurred_at)` from the start, not `id` alone.
- No foreign keys point *at* `stock_movement` — references are by ID without a constraint, since FKs to partitioned tables are restrictive.
- Partition when annual volume justifies it, which the design then permits without a schema rewrite.

### M2 — Test container strategy

One container per test class is correct-looking and slow. Container startup dominates, and CI time is a real cost on developer behaviour: a 25-minute suite gets skipped.

**Revised:**

- **One container per test assembly**, with Respawn resetting data between tests. Fast, and sufficient for the large majority of integration tests.
- **A separate, small concurrency suite** that gets its own container and runs genuinely parallel connections. This is where `SKIP LOCKED` dispatch, allocation serialisation, and balance contention are verified. It is slow, it is worth it, and it is small.
- Both run on PRs. If total CI time exceeds roughly ten minutes, split the concurrency suite to run on merge to main only.

### M3 — Permission caching and revocation

Resolving permissions server-side per request means a database round trip per request unless cached, and caching means revocation is delayed.

**Revised:**

- In-memory cache per API replica, keyed by `(user_id, security_stamp)`, TTL 30 seconds.
- A role or credential change increments `security_stamp`, so the cache key changes and the new permission set is picked up on the next request — the stamp is validated against the session token, which is a single indexed lookup on a request that is already hitting the database.
- Termination sets user status to disabled, which is checked on the same lookup. Effect is immediate, not TTL-bounded.

Two replicas holding independent caches is acceptable because the key includes the stamp; there is no stale-cache-after-change window, only a 30-second window for changes that do not bump the stamp — and every change that matters bumps it.

---

## The Core Invariant, Written Out

The single transaction that the whole design exists to protect. Everything about the modular monolith decision comes back to this being one database transaction across four modules:

```
BEGIN;

  -- 1. idempotency guard (H2)
  INSERT INTO idempotency_record (key, endpoint, request_hash) VALUES (...);
  -- unique violation → replay, return stored response

  -- 2. verify the reservation exists and belongs to this task (C3)
  SELECT ... FROM allocation
   WHERE task_id = $1 AND status = 'reserved'
     FOR UPDATE;

  -- 3. append to the ledger — authoritative record (C4)
  INSERT INTO stock_movement (sequence, recorded_at_server, occurred_at_device, ...)

  -- 4. maintain balance at (sku, location, lot, status) grain only (C1)
  UPDATE stock_balance SET quantity = quantity - $n WHERE ...;

  -- 5. advance task state
  UPDATE task SET status = 'completed', completed_at = now() WHERE id = $1;

  -- 6. advance order line fulfilment
  UPDATE order_line SET picked_quantity = picked_quantity + $n WHERE ...;

  -- 7. outbox for ERP notification
  INSERT INTO outbox_message (...);

COMMIT;
```

Seven statements, four modules, one transaction, no distributed coordination. In a microservice architecture this is a saga with six compensating actions, and the compensations for physical inventory movements are not always possible — you cannot un-pick a carton that is already on a truck.

That is the argument for the modular monolith, and it survives review unchanged.

---

## Revised Container Topology

```
postgres-primary     transactional workload
postgres-replica     streaming replica: reporting reads, warm standby, backup source
minio                object storage (local; async replication to cloud if applicable)
api                  2 replicas, stateless, polling-based (no SignalR, no backplane)
worker-allocation    1 replica per warehouse, strictly single-consumer
worker-general       outbox drain, media renditions, scheduled jobs
web                  static builds, admin + handheld
caddy                TLS, routing, media cache
migrator             one-shot, gates API restart
```

The change from v0.1 is the replica and the split of the allocation worker from general background work. **The allocation worker must not be scaled horizontally** — that constraint belongs in a comment in the compose file, because it is the kind of thing someone increases during a busy period and quietly breaks correctness.

---

## What Did Not Change

Worth stating explicitly, since a review that rewrites everything is usually wrong:

- The append-only ledger as the source of truth — the flaw was in the balance maintenance around it, not the ledger itself
- Modular monolith over microservices, reinforced by the transaction above
- PostgreSQL, .NET, React, Docker Compose, GitHub Actions
- Two separate frontends *(later revised: retained as two route trees within one application — see D15 in the proposal. The screen-set distinction stands; the deployment split does not)*
- EF Core for writes, Dapper for reads, raw SQL migrations
- The task engine as shared infrastructure
- The identity and permission model, including in-place supervisor override
- Media pipeline design, apart from the tenancy scoping fix
- The phased roadmap and its sequencing

---

## Revised Immediate Actions

1. Resolve D1 and D2 as before — still gating
2. Add D9: **stated RPO and RTO**, confirmed with the business rather than assumed
3. Write the pick-confirm transaction above as the **first integration test**, before any feature code. It encodes most of the design's invariants, and if it is hard to write, something upstream is wrong
4. Write the allocation concurrency test second: two orders, one remaining unit, assert exactly one allocation
5. Then build the Phase 1 schema, with `(id, occurred_at)` keys and the sequence column present from the first migration

---
---

# Addendum — Second Review Pass

**Scope:** findings not covered by the first pass, plus one reconciliation where two findings interact.

The first pass concentrated on concurrency, offline behaviour, and clock authority — correctly, since those are where correctness fails. This pass covers **resource contention, blast radius, and operational continuity**: the areas where a system stays correct but stops being usable.

| ID | Finding | Severity |
|---|---|---|
| C6 | Consistency boundaries are never defined; modules are names, not architecture | **Critical** |
| C7 | No workload isolation at the connection level; one slow query stalls every picker | **Critical** |
| H7 | Tenancy treated as conditional scope on an unresolved decision | High |
| H8 | No degraded-mode operation when the system is down and the shift is not | High |
| H9 | Serialised stock forced through a quantity ledger | High |
| H10 | Two dependencies adopted without validation | High |
| M4 | Business date conflated with timestamp | Medium |
| M5 | Multi-row allocation has no lock ordering rule | Medium |

---

## C6 — Consistency boundaries are undefined

**The problem.** v0.1 lists modules — `Inventory`, `Tasks`, `Inbound`, `Outbound`, `Catalog`, `Media`, `Billing` — and enforces their boundaries with architecture tests. But it never states *which invariants are transactional*. Without that, a module boundary is a namespace convention. The first time someone hits a performance problem, they will open a transaction spanning four modules, the architecture test will pass because the dependency direction is legal, and the coupling will be invisible until it deadlocks.

The pick-confirm transaction written out at the end of the first pass is exactly right, and it is presented as an example. It needs to be presented as a **rule**.

**Revised decision — explicit aggregates:**

| Aggregate | Invariant held transactionally | Lock scope |
|---|---|---|
| **StockPosition** (sku × location × lot × status) | Quantity arithmetic; the only place stock quantities mutate | One row |
| **Task** | At most one active lease or assignment | One row |
| **Receipt** | Line quantities sum to header; valid status transitions | Root + lines |
| **Order** | Allocated quantity never exceeds ordered quantity | Root + lines |
| **HandlingUnit** | Contents consistent; no cyclic nesting | Root + contents |
| **User** | Role assignments valid within their scope | Root |

**Rule: one aggregate mutated per transaction.** Cross-aggregate effects propagate through the outbox.

Two deliberate exceptions, and they are the only two:

1. **Pick confirmation** — touches StockPosition, Task, and Order together. Justified because these must be atomic and the transaction is short, single-row per aggregate, and takes locks in a fixed order.
2. **Allocation** — touches Order and many StockPosition rows. Contained by the single-consumer worker (first pass, C2), which removes concurrency from the equation entirely.

Everything else goes through the outbox. Stating this as a rule is what makes the architecture tests meaningful, and it gives reviewers a concrete question to ask of any pull request: *which aggregate does this transaction mutate?*

---

## C7 — No workload isolation

**The problem.** The first pass added a read replica, which correctly moves reporting off the primary. But four workloads still share the primary: scan confirmations, allocation, outbox drain, and media metadata. Their profiles are incompatible.

| Workload | Duration | Tolerance for delay |
|---|---|---|
| Scan confirmation | Milliseconds | **None — an operator is standing still** |
| Allocation batch | Seconds to minutes | High |
| Outbox drain | Milliseconds, high frequency | High |
| Backfill / bulk import | Minutes | High |

Nothing in the design prevents a wave allocation, a bulk catalogue import, or a runaway query from consuming the connection pool. When that happens, every picker in the building waits — and the failure looks like a network problem, so it is diagnosed slowly.

This is the most common way warehouse systems fail under load, and it is invisible in testing because test waves are small.

**Revised decision — isolate at the database role level:**

```
wms_api      statement_timeout = 5s     reserved pool, never starved
wms_worker   statement_timeout = 300s   allocation, outbox, renditions
wms_report   statement_timeout = 600s   replica only, no primary access
wms_migrate  statement_timeout = 0      migrator container only
```

- PgBouncer in transaction mode with **per-role pool limits**, sized so the API pool cannot be exhausted by any other workload regardless of what it is doing.
- `wms_report` has no grant on the primary at all. The architecture test asserting read-only context usage (first pass, H6) is backed by an actual permission.
- The 5-second API timeout is deliberately aggressive. A scan confirmation taking five seconds is already a failure; failing fast surfaces it instead of queueing behind it.

The reserved-capacity point is the load-bearing one. Everything else is tuning.

---

## H7 — Tenancy is being deferred on an open decision

The first pass fixed media deduplication leaking across tenants (H3), which implies a `tenant_id`. But v0.1 still treats multi-tenancy as scope conditional on decision D1, which remains unresolved. If D1 resolves to 3PL after Phase 1, the retrofit touches every table, every query, and every index.

**Revised decision:** carry `tenant_id` on every business table from the first migration, defaulting to one seeded tenant. Enforce with **row-level security**, not application `WHERE` clauses.

```sql
ALTER TABLE stock_movement ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON stock_movement
  USING (tenant_id = current_setting('app.tenant_id')::uuid);
```

Set `app.tenant_id` once per request in middleware.

Cost today: one column, one session variable, one policy per table. Benefit: a forgotten `WHERE` clause returns nothing instead of leaking another client's inventory — which in a 3PL is a contract-terminating incident rather than a bug. If D1 resolves to single-operator, the column is constant and nothing is lost.

This is asymmetric risk. Take the cheap side.

---

## H8 — No degraded-mode operation

Neither v0.1 nor the first pass answers: *the system is down and forty people are on shift.* Trucks keep arriving. A warehouse cannot pause.

The first pass improved recovery time by adding a warm standby (H5), which addresses hardware and process failure. It does not address a bad deployment, a corrupted migration, or a multi-hour outage.

**Revised decision — three tiers, explicitly:**

1. **Device offline, server up.** Covered by the lease and reservation model.
2. **Server down, warehouse operating.** An hourly job writes open pick lists, putaway tasks, and current bin contents to PDF on a **separate host from the application stack**. Staff work from paper. Movements are keyed in afterwards through a bulk reconciliation screen, flagged as manually entered so they are visible in audit and excluded from productivity metrics.
3. **Data loss.** Restore, then replay the paper record.

Tier 2 is unglamorous and routinely omitted from designs. It is also the difference between a bad afternoon and a stopped operation — and the PDF export is perhaps a day of work.

The separate-host requirement matters: an export that lives on the box that just died is not a fallback.

---

## H9 — Serialised stock does not belong in a quantity ledger

Decision D4 (serial tracking) is open. If it resolves to yes, `stock_movement.quantity` is always 1 for those SKUs, and the ledger becomes an inefficient way to model individual object identity. Queries like "where is unit X now" become a scan of movement history rather than a lookup.

**Revised decision:** model serialised stock as a **separate aggregate** rather than forcing it through the quantity ledger.

```
serial_unit    id, tenant_id, sku_id, serial_number, lot_id,
               current_location_id, current_handling_unit_id,
               status, security_stamp
```

with its own movement history. Balances for serialised SKUs derive from `count(serial_unit)`; balances for everything else derive from the ledger. Two paths, each correct for its case, rather than one path awkward for both.

The item master's `is_serial_tracked` flag then selects the path, and this must be decided per SKU at creation and treated as immutable — switching a SKU's tracking mode with stock on hand is a migration, not a setting.

---

## H10 — Two dependencies adopted without validation

**MinIO.** Licensed under AGPL, with recent changes to editions and console features. For purely internal self-hosted use AGPL is generally unproblematic, but the current terms and edition boundaries should be verified directly rather than assumed from prior familiarity. Keep storage behind an S3-API abstraction so the choice stays reversible; alternatives include SeaweedFS, Garage, or cloud object storage.

**Handheld platform.** The PWA approach assumes the scanner presents as a keyboard wedge. On Zebra hardware this is configurable — DataWedge can emit keystrokes or intents — and any requirement for scanner control (aim, trigger, symbology switching) may force native access or Zebra's Enterprise Browser. Separately, aggressive Android battery management can terminate service workers, which is precisely the component the offline queue depends on.

**Action:** acquire one target handheld and prove the full path — scan capture, queue survival across app suspension *and device reboot*, sync after two hours offline — **before Phase 1 development begins.** Two days of work against the largest unvalidated assumption in the project.

---

## M4 — Business date is not a timestamp

The first pass fixed clock authority (C4) by establishing `sequence` as authoritative ordering. That is correct and separate from this.

Shifts cross midnight; sites may span time zones. "What did we ship on the 3rd" is ambiguous when derived from UTC timestamps, and it is the question every operational report asks.

**Revised:** store an explicit `business_date` alongside the timestamps, derived from the warehouse's configured day-boundary rule at write time.

```
sequence            bigint       -- authoritative order
recorded_at_server  timestamptz  -- authoritative time
occurred_at_device  timestamptz  -- forensic only
business_date       date         -- authoritative for reporting
```

Reporting groups on `business_date`. Audit and reconciliation use `sequence`. Deriving the business date in the report query instead means every report re-implements the rule, and they will not all do it the same way.

---

## M5 — Multi-row allocation needs a lock ordering rule

The single-consumer allocation worker (first pass, C2) eliminates allocation-versus-allocation races. It does not eliminate allocation-versus-pick-confirmation contention, since pick confirmations run concurrently on the API and touch the same balance rows.

An allocation touching balance rows in candidate-strategy order while a pick confirmation touches one of them is a deadlock waiting to happen — intermittent, load-dependent, and painful to reproduce.

**Revised:** all multi-row balance access acquires rows **sorted by primary key**, regardless of the order the business logic wants them in. One line of code, stated in a comment where allocation lives, eliminating an entire class of intermittent production failure.

---

## Reconciliation — C3 and the short pick

The first pass established (C3) that offline confirmations may only resolve pre-existing reservations, which guarantees they cannot fail on the server. That is the right primary control and it should stand.

But it does not cover every case, because **quantity discrepancies are physical facts, not authorisation failures.** A picker sent for twelve units may find eight. The reservation existed; the stock did not. The confirmation cannot be rejected — the eight units are already in the tote.

The same applies when a lease expires and a task is reclaimed while its original holder was out of coverage: two operators may both have picked, and both movements are real.

**Resolution — the two rules compose:**

1. **Prevent the conflict** (C3): only reservation-backed work may be confirmed offline. This removes the large majority of cases.
2. **Accept the residue as fact.** Where the physical world diverges from the reservation anyway, the movement is written to the ledger as it actually occurred and an `inventory_exception` record is raised for supervisor resolution.

The consequence, which must be explicit in the schema: **`stock_balance` may transiently go negative.** No check constraint forbidding it. A negative balance is true information — it means the system's belief was wrong — and suppressing it to preserve a database invariant destroys the audit trail the ledger exists to provide.

The exception queue is therefore a first-class operational surface with ownership, ageing alerts, and a resolution workflow. Not an error log.

---

## Revised Phase 0

> **Superseded.** A separate spike phase was subsequently dropped. Its content is absorbed into Phase 1 (proof of concept) in the proposal: device validation and latency measurement are sequenced to the start of that phase, and the concurrency harness backs its exit criteria. The reasoning below is retained as a record of why those items exist.

Insert two weeks before feature development, to test the three assumptions that are most expensive to discover as false in month six.

1. **Device spike (H10).** One target handheld. Scan capture, offline queue survival across suspension and reboot, sync after two hours offline.
2. **Concurrency harness.** The two tests named in the first pass — the pick-confirm transaction and the allocation race — plus a load generator running 30 simulated pickers against one zone, asserting no duplicate assignment, no lost update, no deadlock.
3. **Vertical slice.** One SKU, one location, one task type: lease → offline pick → sync → ledger → balance → exception path, with auth. Thin but complete.

If any of these fails, it fails while the design is still cheap to change. That is the entire purpose of the two weeks.

---

## Consolidated Open Decisions

Unlike the numbered findings above, this table is a **live status tracker**
for the ten open questions this pass identified, not a historical
narrative — so, deliberately unlike the rest of this file, it is kept in
sync as each one actually resolves rather than left to go stale. The
findings themselves (C1–C7, H1–H10, M1–M5, and this addendum's own H7–H10)
are never rewritten; this table just says where each question landed.

**A numbering note, since it causes real confusion otherwise:** the ten
IDs below are this review's own flat `D1`–`D10` sequence, drafted before
the proposal was finalized. The proposal's final numbering (`D1`–`D15`,
with letter suffixes like `D1a`/`D3a`/`D10a` added during that pass) does
**not** map one-to-one onto these — e.g. this table's `D3` ("regulated
goods") is the proposal's `D10a`, not its `D3` ("lot/batch tracking",
which this table never separately asked about). The "Resolved" column
below names the proposal's actual ID for exactly this reason.

| ID (this table) | Decision | Status |
|---|---|---|
| D1 | 3PL or single-operator | **Resolved** — Own-operator (proposal D1) |
| D2 | On-premise or cloud | **Resolved** — Cloud-hosted (proposal D2) |
| D3 | Regulated goods | **Resolved** — General merchandise, not regulated (proposal D10a) |
| D4 | Serial tracking | **Resolved** — Yes, modelled as a separate `serial_unit` aggregate (proposal D4) |
| D5 | Volume figures | **Resolved** — 50+ concurrent operators, ~60,000 order lines/day (proposal D5) |
| D6 | Shift pattern | **Resolved as an approach, not a single value** — configurable per customer (proposal §3) |
| D7 | Existing identity provider | **Resolved as an approach** — hybrid: staff federate where a corporate directory exists, operators always local (proposal D7) |
| D8 | Badge infrastructure | **Resolved as an approach** — per customer, but both badge and PIN are supported from the first release regardless (proposal D8) |
| D9 | Stated RPO and RTO | **Still open — genuinely blocking.** A contractual matter with customers (proposal D9); unresolved because there is no customer yet, not because it's been overlooked |
| D10 | Handheld model in use | **Resolved for now, with a stated revisit trigger** — mid-range Android default, revisit before the first customer pilot (proposal D10) |

---
---

# Addendum — Third Review Pass (Admin Management Extensions)

**Provenance, stated plainly.** Everything above this point — Pass 1, the
Second Review Pass, and the design document they resolved into — came from
a separate, more mature project and was brought into this repository
wholesale. This pass is different: it reviews the **admin management
endpoints added directly in this repository** (warehouse/zone/device/
handling-unit CRUD, full user management, and custom role composition) —
work this session did on top of the adopted design, not part of what was
adopted. Same rigor, same numbering sequence (continuing from C7/H10/M5),
but a genuinely new pass over genuinely new content.

| ID | Finding | Severity |
|---|---|---|
| H11 | Marking a device `lost` doesn't end its active session — the endpoint's own error response references a "force-end it first" step that doesn't exist | High |
| H12 | Editing a custom role's permissions doesn't propagate to `security_stamp` for users already holding it — revocation silently stops meaning "takes effect in seconds" | High |
| H13 | Custom role composition has no check that the granting admin holds the permissions they're bundling into a new role — a privilege-escalation path | High |
| M6 | Suspending a user with a nonzero offline queue on their device can permanently strand already-completed physical work | Medium |

## H11 — "Lost" needs to actually end the session

**The problem.** `PATCH /devices/{id} { "status": "lost" }` was designed
with a `409` for an active session, telling the caller to "force-end it
first" — but no endpoint does that. As written, a lost handheld with an
open session can never actually be marked lost while that session is live,
which is exactly the situation `lost` exists to handle: the device is
gone, nobody can log it out normally.

**Revised decision.** Split the two terminal states by urgency, not by a
shared precondition:

- **`retired`** (planned decommission) keeps requiring no active session —
  routine housekeeping, the precondition is reasonable.
- **`lost`** (emergency) needs none. Setting it **cascades**: the active
  `device_session` is ended (`end_reason = 'forced'`), and every
  `refresh_token` bound to that device is revoked. This is a single
  device's own session and tokens — a tight, device-scoped transaction,
  not a fan-out across other devices or users.

## H12 — Role-permission changes need to actually revoke

**The problem.** `security_stamp` exists specifically so a permission
change "takes effect in seconds rather than whenever the access token
happens to expire" (§8.1). That guarantee is stated for the user's own
role *assignment* changing. Nothing bumps it when a role's *definition*
changes — if `SENIOR_PICKER` loses `task.reassign` via `PATCH /roles/{id}`,
every user already holding that role keeps a token that still claims it,
for up to the token's remaining ~15-minute lifetime.

**Revised decision.** This is not a new exception to "one aggregate per
transaction" (C6) — it's the case the outbox already exists for. `PATCH
/roles/{id}` writes one `outbox_message` (`RolePermissionsChanged`,
payload `{ roleId }`) in the same transaction as the role update. A new
Worker responsibility — **role-permission-change propagation** — consumes
it and bumps `security_stamp` for every `app_user` reachable through
`user_role_scope` for that role, each in its own small transaction. Added
to §1.3's Worker responsibilities list alongside the existing scheduled
jobs.

## H13 — Composing a role should not let you grant what you don't hold

**The problem.** Nothing in `POST /roles` or `PATCH /roles/{id}` checks
that the requesting admin's *own* effective permissions are a superset of
what they're bundling into the role. A Warehouse Manager scoped to one
zone could compose a role combining `inventory.adjust.approve` and
`count.variance.approve` — permissions they may not hold themselves — and
grant it onward. This is a standard privilege-escalation-via-delegation
pattern, and it sits uncomfortably next to how emphatic this design
already is about segregation of duties elsewhere (invariant: an approver
may never be its own actor).

**Revised decision.** `POST /roles` and any `PATCH /roles/{id}` that adds
a permission validate `permissionCodes ⊆ (the requesting admin's own
current effective permission set)`. A request naming a permission outside
that set is rejected — `403 …/cannot-grant-permission-you-lack` — rather
than silently accepted. System Administrators (who hold the full catalogue
by design) are unaffected; this constrains delegation, not top-level
administration.

## M6 — Suspension can strand a device's unsynced queue

**The problem.** `unauthenticated` is already a documented, legitimate
reason `/sync/facts` may refuse a request (§3.3, §4.1) — so a suspended
user's stale-stamp token being rejected on `/sync/facts` is not a contract
violation. But it is an operational risk the design doesn't currently
surface: physical work already completed, still sitting in a device's
offline queue, becomes permanently unsyncable the moment the user who
performed it is suspended, because that device can never obtain a valid
token again for that identity.

**Revised decision.** Not a code fix — the information already exists.
`PATCH /users/{id}` with `status: suspended`/`ended` includes the current
`queueDepth` of any device the user has an active session on (from the
most recent heartbeat, §5.1) in its response, so the admin sees the risk
at the moment they act rather than discovering it later as an
unexplained reconciliation gap. Genuinely urgent suspensions (security
incidents) should still proceed regardless — this is a visibility fix,
not a blocker.

---

## Consolidated status after this pass

All four findings above are resolved directly in `wms-design-document.md`
in the same change that added this addendum — see §1.3, §2.1, and the
`PATCH /devices/{id}`, `POST /roles`, `PATCH /roles/{id}`, and `PATCH
/users/{id}` entries in §5.11.
