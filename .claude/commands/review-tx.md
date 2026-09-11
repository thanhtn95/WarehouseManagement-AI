---
description: Review transaction boundaries and concurrency in the current change
allowed-tools: Read, Grep, Glob, Bash(git diff*), Bash(git status*)
---

Review the concurrency correctness of the current change.

Current diff: !`git diff --stat HEAD 2>/dev/null | tail -20`

Delegate to the **concurrency-reviewer** subagent, then summarise its findings
for me with your own judgement on which matter most.

Pay particular attention to anything that:

- opens a transaction spanning more than one aggregate
- reads a balance before updating it, rather than using a conditional `UPDATE`
- adds a guard clause to a fact-path decrement
- accesses multiple balance rows without ordering by primary key
- omits `SKIP LOCKED` from a queue or lease query
- performs allocation on a request thread
- calls into another module directly instead of via the outbox

If the change touches the write path and adds no concurrency test, say so.
