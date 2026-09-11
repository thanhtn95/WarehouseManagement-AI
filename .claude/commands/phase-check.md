---
description: Check whether current work is inside the active phase scope
allowed-tools: Read, Grep, Glob, Bash(git status*), Bash(git diff*)
---

Check the current work against the active phase in `CLAUDE.md`.

Changed files: !`git status --short 2>/dev/null | head -30`

Report:

1. **In scope** — belongs to the current phase.
2. **Out of scope** — belongs to a later phase. Name which, and say whether it
   is a genuine dependency or scope creep. Building Phase 2 features during
   Phase 1A is how proof-of-concept work becomes unshippable.
3. **Shortcuts taken** — anything hardcoded, stubbed, or seeded that will need
   replacing. Every one must have an entry in `docs/shortcuts.md` paired with
   the phase item that replaces it. List any that are missing; those get added
   now, not later.
4. **Exit criteria progress** — which of the current phase's criteria this
   work advances, and which remain unproven.

Be direct about scope creep. It is easier to cut now than after it has tests.
