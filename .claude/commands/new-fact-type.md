---
description: Add a new fact type to the sync endpoint, with its divergence behaviour
argument-hint: "<fact_type_name>"
allowed-tools: Read, Write, Edit, Grep, Glob
---

Add the fact type: **$ARGUMENTS**

A fact is something that physically happened. It cannot be refused.

**Before writing any code, answer these. If any is unanswered, stop and ask
me rather than guessing.**

1. What physical action does this record?
2. Which `inventory_exception` type does it raise when reality diverges from
   expectation? A fact type with no declared divergence path is a silent
   discrepancy — this question is not optional. See §7.2.
3. Which balance rows does it mutate, and in which direction?
4. Which work state does it advance (`task_line`, `receipt_line`, …)?
5. Which permission gates it?
6. Which reason codes apply, and do any require a note, photo, or approval?

Then:

- Add the payload to the fact registry table in §4.2 of the design document.
- Add the divergence row to §7.2.
- Implement the handler following the seven-step transaction contract in §4.3.
  **The balance decrement carries no guard clause.**
- Write the idempotency replay test and the divergence test.

Never return a business-rule error from this path. `202` always.
