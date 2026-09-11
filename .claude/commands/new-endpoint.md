---
description: Design and scaffold a new API endpoint with the correct command/fact classification
argument-hint: "<METHOD /path — what it does>"
allowed-tools: Read, Write, Edit, Grep, Glob
---

Design the endpoint: **$ARGUMENTS**

Start by classifying it. **Would refusing ask someone to undo something they
cannot undo?**

- Yes → it is a fact. It does not get its own endpoint; add a fact type
  instead with `/new-fact-type`.
- No → it is a command. Continue.

For a command, produce:

1. Method, path, permission (`domain.action[.qualifier]`), and phase.
2. Request body with field types and which are optional.
3. Success response.
4. **Every error response that can actually occur**, using RFC 7807 with a
   machine-readable `type`. Reuse existing types from §7.1 where they fit.
5. Whether it needs `Idempotency-Key`, `If-Match`, or an elevation grant.
6. Pagination shape if it returns a collection — keyset, never `OFFSET`.

Then add it to §5 of `docs/wms-design-document.md` in the same change, in the
established format, and check it against the current phase scope in `CLAUDE.md`.

Delegate the design review to the **api-designer** subagent before implementing.
