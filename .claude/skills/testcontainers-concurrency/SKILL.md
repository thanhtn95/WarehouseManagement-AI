---
name: testcontainers-concurrency
description: Patterns for integration and concurrency tests against real PostgreSQL using Testcontainers. Use when testing the ledger, task leasing, allocation, idempotency, or anything involving simultaneous operators.
---

# Testcontainers and concurrency testing

## Why real PostgreSQL, always

`SKIP LOCKED`, `pg_advisory_lock`, partition routing, and deadlock detection
do not exist in SQLite or the EF in-memory provider. Those mechanisms carry
the entire correctness burden of this system, so testing against a substitute
tests nothing that matters.

One container per test class. Run the real DbUp migrations against it. Seed
through application code paths, not raw inserts, so constraints and defaults
are exercised.

## Making a race actually race

Sequential calls prove nothing. Release N tasks simultaneously:

```csharp
var barrier = new Barrier(operatorCount);
var results = await Task.WhenAll(
    Enumerable.Range(0, operatorCount).Select(async i =>
    {
        barrier.SignalAndWait();
        return await LeaseTasksAsync(deviceIds[i], zoneId);
    }));
```

Then assert on **database state**, not on responses.

Repeat the race. A single passing run of a race condition is not evidence —
loop it 50 to 100 times, since interleavings are probabilistic.

## The mandatory suite

1. **No duplicate lease.** N devices lease the same zone at once; the union of
   returned task ids has no repeats.
1b. **Dispatch never blocks.** Hold a lock on the first N tasks in another
   transaction, then lease with a timeout: the call must return the *next* N
   immediately. **This, not test 1, is what `SKIP LOCKED` is for** — verified
   by mutation in this repo: removing `SKIP LOCKED` leaves test 1 green,
   because under READ COMMITTED a blocked `FOR UPDATE` re-checks
   `status = 'ready'` and drops the row that was leased while it waited.
   Disjointness comes from the row lock plus that predicate; `SKIP LOCKED`
   buys throughput, which at 50 operators is the difference between dispatch
   that scales and operators standing still.
2. **No oversell.** Two orders for the last unit; exactly one allocation
   succeeds and `on_hand - allocated >= 0`.
3. **No deadlock.** Multi-line allocation concurrent with pick confirmations,
   100 iterations, asserting no `PostgresException` with SQLSTATE `40P01`.
4. **Idempotent replay.** The same `clientFactId` submitted twice yields
   exactly one `stock_movement` row and a `duplicate` status on the second.
5. **Short pick.** Requested 12, confirmed 6: one movement of 6, one
   `short_pick` exception, HTTP `202` — never an error.
6. **Reconciliation.** Synthetic movement history recomputes to the stored
   balances with zero variance.

## Common mistakes

- Asserting on the HTTP response instead of the ledger. The response can be
  right while the balance is wrong.
- Forgetting that a fact endpoint returns `202` on divergence. A test
  expecting `409` from `/sync/facts` encodes the wrong contract.
- Sharing a container across tests that mutate the same rows.
- Testing allocation without the advisory lock held, which passes and proves
  nothing about production.
