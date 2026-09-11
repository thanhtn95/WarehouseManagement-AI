# 0002 — Incremental ledger readers bound scans by transaction visibility, not by `max(sequence)`

**Status:** accepted
**Date:** 2026-09-11

## Context

`stock_movement.sequence` is `bigint GENERATED ALWAYS AS IDENTITY`
(`0003__inventory_ledger.sql`). The ledger is the source of truth for every
stock quantity in the system (invariant 1), and two specified consumers read
it *incrementally*, each keeping a watermark of how far it has read:

- `GET /sync/status` returns `lastAcceptedSequence`, which a handheld uses to
  decide what it still needs (design doc §5.3).
- `balance_reconciliation_run` records `from_sequence` / `to_sequence` and
  recomputes `stock_balance` from the movements between them (§2.3, review
  finding H1).

An identity value is assigned when the row is **inserted**, but the row
becomes visible to other transactions only when that transaction
**commits**. Those are different moments, and under concurrency they are not
even in the same order: with fifty operators confirming facts, transaction A
can take sequence 500, transaction B take 501, and B commit first.

A reader that advances its watermark to `max(sequence)` at that instant
records 501 and will never see 500 — no later scan asks for anything below
its watermark. The movement is not delayed, it is **permanently skipped**.

For the sync endpoint that means a device that never learns about a
movement. For reconciliation it is materially worse: the job whose entire
purpose is to detect divergence between `stock_balance` and the ledger would
omit rows from its recomputation and report **zero variance** on a warehouse
that has drifted. A reconciliation that cannot be trusted is worse than no
reconciliation, because its clean bill of health is what stops anyone
looking further.

This was found while reviewing the first ledger writer. Neither consumer has
been built, which is exactly why it is cheap to decide now — and why it must
be decided in one place rather than twice, differently.

## Decision

**No incremental reader of `stock_movement` may use `max(sequence)` as its
watermark.** Every such reader bounds its scan by transaction visibility:

1. **Preferred — snapshot-bounded.** Take `pg_snapshot_xmin(pg_current_snapshot())`
   at the start of the run and treat as final only those rows whose `xmin` is
   below it. Everything still in flight is left for the next run. This is
   exact: it skips nothing and never double-counts.

2. **Acceptable — lag plus overlap.** Advance the watermark only over
   movements older than a fixed lag, and deliberately re-scan an overlap
   window on each run. This is simpler and adequate *only* for a consumer
   whose work is idempotent, which reconciliation's recompute-and-compare is
   and the sync cursor is not.

**Chosen for the first consumer: option 2.** `ReconciliationService` bounds
each scan by `recorded_at <= now() - VisibilityLag` (five minutes). It rests
on one stated assumption — *no transaction writing `stock_movement` stays open
longer than the lag* — which the API's five-second statement timeout (C7)
satisfies with two orders of magnitude to spare, and `recorded_at` being
transaction-*start* time means a movement is only ever considered once its
transaction cannot still be in flight. Option 1 is exact, but comparing a
row's `xmin` to a snapshot's `xid8` requires a text cast and carries a
wraparound edge; these files are read during incidents, and a bound that is
comprehensible and provably safe under a documented assumption beats a clever
one that is obviously neither. Should a future writer need a long transaction
against the ledger, the assumption breaks and this must move to option 1 —
which is why the assumption is written here rather than only in the code.

`sequence` remains the correct **ordering** key — it is server-assigned and
monotonic, which is what invariant 5 requires of it. This ADR constrains how
a reader decides it has seen *everything up to* a point, not how it orders
what it has seen.

## Consequences

- Reconciliation may lag real time slightly. That is the intended trade:
  it is a correctness monitor, not a live dashboard, and a late-but-correct
  variance report is worth far more than a prompt one that silently omits
  rows.
- Any future consumer of the ledger — an analytics export, a fleet-level
  feed, an event stream — inherits this rule, and the reviewer of that code
  should be pointed at this ADR rather than re-deriving the hazard.
- A test accompanies the first incremental reader:
  `AMovementInsideTheVisibilityLag_IsLeftForTheNextRun` — a movement too
  recent to have settled is left behind, and the following run starts exactly
  where the previous one stopped, so nothing is skipped and nothing is
  double-counted. Verified by mutation: removing the lag fails it.
- A second trap sits next to this one and is worth naming, because it is not
  obvious and no single-run test can see it. The *selection* of positions to
  check is incremental; the *recompute* of each selected position must not be.
  Summing only the window's movements and comparing that to a running total
  reports a variance on every row with prior history — the job then cries wolf
  and gets switched off, which costs the system its correctness monitor just
  as surely as skipping rows would. Covered by
  `APositionTouchedInALaterWindow_IsRecomputedFromItsWholeHistory`.
- This does **not** apply to the outbox drain, which claims rows by their own
  `processed_at IS NULL` predicate rather than by a monotonic cursor and is
  therefore not exposed to the hazard.

## Alternatives considered

**Assign `sequence` at commit time instead.** PostgreSQL offers no such
mechanism for an identity column, and emulating it with a commit-ordered
side table adds a second write and a second failure mode to the hottest
transaction in the system.

**Make the reader take an `ACCESS EXCLUSIVE`-style barrier so no writes are
in flight.** Correct and completely unacceptable: it stalls scan
confirmations, which is precisely the failure invariant 10 and finding C7
exist to prevent.
