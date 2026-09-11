---
name: wms-domain
description: Warehouse domain vocabulary, the aggregate model, and the invariants that make the WMS correct. Use when reasoning about inventory, tasks, allocation, receiving, picking, or any warehouse terminology.
---

# WMS domain

## Vocabulary

| Term | Meaning |
|---|---|
| **LPN / handling unit** | A pallet or tote with its own barcode, containing stock. Moving it is one scan, not forty line items |
| **Pick face** | The accessible bin picked from. Refilled from bulk reserve by replenishment |
| **Bulk reserve** | High-density storage that feeds pick faces |
| **Pick sequence** | The physical walk order of a location. What makes route optimisation possible at all |
| **UoM hierarchy** | each / inner / case / pallet, with conversion factors and a barcode per level |
| **FEFO / FIFO** | First Expired First Out, First In First Out — allocation strategies |
| **Wave** | A batch of orders released to the floor together |
| **Putaway** | Moving received stock into storage |
| **Replenishment** | Bulk reserve → pick face |
| **Cycle count** | Rolling partial count, avoiding a full shutdown stocktake |
| **Disposition** | The decision on returned goods: restock, repair, return to vendor, scrap |
| **Phantom inventory** | System believes stock is in a location and it is not. Measured by `PICK_NOT_FOUND` rate |
| **Shrinkage** | Loss the business bears. Distinct from overage, and never netted against it |
| **Consolidation** | Reassembling one order picked in parallel across zones |
| **WCS** | Warehouse Control System — drives physical automation. The WMS says *what*, the WCS decides *how* |

## The aggregates

One aggregate mutated per transaction, except fact confirmation and
allocation.

| Aggregate | Invariant | Lock scope |
|---|---|---|
| **StockPosition** | Quantity arithmetic. The only place stock quantities change | One `stock_balance` row |
| **Task** | At most one active lease | One row |
| **Receipt** | Line quantities sum to header; valid status transitions | Root + lines |
| **Order** | Allocated never exceeds ordered | Root + lines |
| **HandlingUnit** | Contents consistent, no cyclic nesting | Root + contents |
| **User** | Role assignments valid within scope | Root |

The permitted exceptions: **fact confirmation** — any fact type in the
registry (§4.2) mutates StockPosition together with the single work-item
aggregate it confirms against (Task for `putaway_confirmed`/`pick_confirmed`,
Receipt for `receipt_confirmed`, a count line for `count_confirmed`), each
short and single-row per aggregate with a fixed lock order — and
**allocation** (Order + many StockPosition, contained by the single-consumer
worker instead of per-transaction lock ordering). See design doc §4.3.

## Three quantities, never two

**On hand** is physically present. **Allocated** is committed to a released
order. **Available** is on hand minus allocated minus quarantine, and is
computed, never stored. Collapsing these causes overselling.

## The command / fact split

A **command** is a request that may be refused: assign a task, reserve stock,
release a wave, approve a variance. Requires connectivity.

A **fact** is a report of something that physically happened: picked 6 units
from bin A-12-03-2. It works offline and is **never** refused. When it
contradicts server state, the movement is still written and an
`inventory_exception` is raised.

The test: *would refusing ask someone to undo something they cannot undo?*

## Why the ledger

Balances derive from an append-only `stock_movement` table. This gives
auditability, concurrency that is bounded by physical reality rather than
locking, and a reconciliation job that recomputes balances from the ledger and
alerts on any variance. That job is the system's own correctness monitor and
is the main reason the design is worth its cost.

`stock_balance` may go negative. A negative balance is true information — the
system's belief was wrong — and a constraint forbidding it would hide exactly
what the ledger exists to reveal.

## Reason codes

Every stock-affecting action carries a reason code from the `reason_code`
table. Four flags drive validation: `requires_note`, `requires_photo`,
`requires_approval`, and `is_shrinkage`.

`is_shrinkage` separates loss from surplus. Receipt discrepancies are **not**
shrinkage — the loss sits with the supplier. Count overage is **not**
shrinkage and is never netted against shortage; a location running +50 and −50
has an accuracy problem a net of zero conceals.

`PICK_NOT_FOUND` is deliberately friction-free: no note, no photo, no
approval. A picker at an empty bin must be able to move on, and any friction
makes them choose a different code, destroying the accuracy signal.
