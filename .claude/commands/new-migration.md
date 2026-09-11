---
description: Scaffold a new DbUp SQL migration with the project's conventions
argument-hint: "<short description of the change>"
allowed-tools: Read, Write, Glob, Bash(ls db/migrations/*)
---

Create a new migration for: **$ARGUMENTS**

Existing migrations: !`ls db/migrations/ 2>/dev/null | tail -5`

Steps:

1. Number it one above the highest existing file. Name it
   `NNNN__snake_case_description.sql`.
2. Write plain SQL. **Never** EF Core migrations — this project uses DbUp.
3. Apply the conventions in `CLAUDE.md`: `numeric(18,4)` for quantities,
   `timestamptz` for time, `text` + `CHECK` for enumerations, `jsonb` `*_i18n`
   for user-facing labels, no soft deletes.
4. Make it **expand-then-contract safe**. The migrator runs to completion
   before API containers restart, so the new schema is briefly live under the
   previous application version. If this change is not backward-compatible,
   split it across releases and say so.
5. Make it idempotent where possible (`IF NOT EXISTS`), because it will run
   unattended across a fleet.
6. State the expected duration class in a header comment: instant, seconds, or
   background. Assume `stock_movement` holds tens of millions of rows.
7. Update `docs/wms-design-document.md` §2 in the same change.

Then hand the file to the **schema-guardian** subagent for review before
telling me it is ready.
