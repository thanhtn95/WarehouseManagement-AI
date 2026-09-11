---
name: system-design
description: Use when proposing or making a change to the WMS's architecture, schema, API contracts, invariants, or non-functional targets — a new capability, a data model change, revisiting a Phase 3/4 open item, or reconsidering a documented decision.
---

# System / architecture design changes

## The document hierarchy — know which one to edit

| Document | What it is | Edit it when |
|---|---|---|
| `wms-project-proposal.md` | Scope, resolved decisions (`D`-numbered), phases, risks | Almost never — scope-of-engagement, not implementation detail |
| `wms-architecture-review.md` | **Historical record** of two review passes — findings `C1–C7` (Critical), `H1–H10` (High), `M1–M5` (Medium) | Never. See "Superseding a finding" below |
| `wms-design-document.md` | **The living source of truth.** Schema, API reference, 22 data flows, NFRs, all phases | Any schema, API contract, flow, or NFR change — this is the one you edit |
| `docs/adr/` | One-off decisions made *during build*, going forward | A new structural or tooling call not fixing a documented gap |
| `docs/shortcuts.md` | Phase 1A compromises, each paired with its Phase 2 replacement | The moment a shortcut is taken — not later |
| `CLAUDE.md` | The always-in-context **summary** of the design doc's invariants and phase | Whenever the design doc's invariants, stack, or phase scope change — it must stay in sync, not just accurate on its own |

## Superseding a finding, without rewriting history

`wms-architecture-review.md` is a snapshot of what two review passes concluded
at the time. Don't edit it when a later decision changes the answer — editing
history hides *why* the system now works the way it does. Instead, add a note
at the point of use in the design document that says which finding is
superseded and why (see design doc §1.3/§4.3 for two worked examples: C6's
"only two exceptions" framing being generalized, and H7's `tenant_id`
requirement being superseded by the single-tenant decision).

## The propagation rule

**This is the mistake that actually happens:** an invariant or architecture
rule isn't stated once — it's echoed into every file that enforces or
teaches it, because that's what makes it enforceable instead of just
documented. A single change needs to be found and fixed in all of them, or
the copies silently contradict each other and whichever one an agent reads
first wins by accident. Check every one of these:

- `CLAUDE.md` — the invariant's summary form
- `docs/wms-design-document.md` — the authoritative statement, plus every
  place elsewhere in the doc that references it by finding ID or restates it
- `.claude/agents/*.md` — any reviewer agent whose enforcement rules quote
  the old wording (e.g. `concurrency-reviewer.md`)
- `.claude/skills/*/SKILL.md` — any skill whose reference tables restate it
  (e.g. `wms-domain`'s aggregate table)
- `.claude/hooks/*.sh` — mechanical checks that encode the rule as a pattern
  match, not just prose

`grep -rn "<the phrase you're changing>" docs/ CLAUDE.md .claude/` before
considering the change done. A change that touches the design doc but leaves
a stale copy in an agent or skill file is not finished — it's a new
inconsistency in a different place.

## Classifying the change

- **Touches one of CLAUDE.md's 12 numbered invariants?** Correctness-critical
  by definition — reason through it explicitly, and have the
  `concurrency-reviewer` or `schema-guardian` agent check the result before
  calling it done.
- **Changes schema or an API contract?** Design doc update and (for schema) a
  migration land in the *same* change — this is the project's own working
  agreement, not a suggestion.
- **A temporary compromise to hit the current phase's exit criteria?**
  `docs/shortcuts.md`, paired with the Phase 2+ item that replaces it, added
  when taken.
- **A new decision made during build that doesn't fix a documented gap?**
  `/adr`.
- **Uncertain whether it's in scope right now?** `/phase-check` — Phase 1A's
  boundaries in CLAUDE.md ("Current phase") are load-bearing; building
  allocation logic during 1A is a process bug, not just early delivery.

## After the change

Run the `doc-sync` agent to check for drift the edit may have introduced
elsewhere in the design doc or between the doc and any code that already
exists. Don't invent a new finding ID (`C`/`H`/`M`) for routine changes —
those series belong to the two historical review passes; most design changes
are just design doc prose, no ID needed.
