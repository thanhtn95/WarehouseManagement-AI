---
description: Write an architecture decision record
argument-hint: "<the decision being made>"
allowed-tools: Read, Write, Glob, Bash(ls docs/adr/*)
---

Write an ADR for: **$ARGUMENTS**

Existing ADRs: !`ls docs/adr/ 2>/dev/null | tail -5`

Number it one above the highest, as `NNNN-kebab-case-title.md`:

```markdown
# NNNN — <Title>

**Status:** proposed | accepted | superseded by NNNN
**Date:** YYYY-MM-DD

## Context
What forces this decision. Include the constraint that makes it non-obvious.

## Decision
What we are doing, stated plainly.

## Consequences
What becomes easier. What becomes harder. What we are accepting.

## Alternatives considered
Each with the reason it was not chosen. An ADR listing no rejected
alternative is a decision nobody actually made.

## Revisit when
The concrete trigger that would reopen this.
```

Check first whether this contradicts an invariant in `CLAUDE.md` or a decision
already recorded in `docs/wms-design-document.md`. If it does, say so
explicitly — an ADR that silently reverses an invariant is worse than none.
