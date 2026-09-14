# WMS — Project Progress

**Purpose:** a record of how this project was bootstrapped — the decisions made, in the order they were made, and the `.claude/` tooling that carried each one

## Contents

- [Where the `.claude` tooling stands today](#where-the-claude-tooling-stands-today)
- [1. Deciding what the project is](#1-deciding-what-the-project-is)
- [2. Locking in the core tech stack](#2-locking-in-the-core-tech-stack)
- [3. The initial `.claude` folder structure](#3-the-initial-claude-folder-structure)
- [4. Launching Claude Code and generating the proposal](#4-launching-claude-code-and-generating-the-proposal)
- [5. Reviewing the proposal and making changes](#5-reviewing-the-proposal-and-making-changes)
- [6. Generating the architecture review](#6-generating-the-architecture-review)
- [7. Reviewing the architecture review](#7-reviewing-the-architecture-review)
- [8. Generating the detailed system design](#8-generating-the-detailed-system-design)
- [9. Reviewing the system design](#9-reviewing-the-system-design)
- [10. Porting the tooling that enforces the design](#10-porting-the-tooling-that-enforces-the-design)
- [11. Scaffolding the project — the initial structure and the Phase 1A backend](#11-scaffolding-the-project--the-initial-structure-and-the-phase-1a-backend)
- [12. Scaffolding the frontend — a basic structure to sync with Claude Design](#12-scaffolding-the-frontend--a-basic-structure-to-sync-with-claude-design)
- [13. Documenting the screens needed, for a Claude Design handoff](#13-documenting-the-screens-needed-for-a-claude-design-handoff)
- [14. Feeding the screen inventory into Claude Design, admin screens first](#14-feeding-the-screen-inventory-into-claude-design-admin-screens-first)

---

## Where the `.claude` tooling stands today

For contrast with the thin initial state described in §3 below, the tooling has grown alongside the design as each piece became relevant to enforce:

```
.claude/
  settings.json
  agents/         9 files  — schema-guardian, concurrency-reviewer, api-designer,
                              test-writer, security-reviewer, backend-reviewer,
                              frontend-reviewer, cicd-reviewer, doc-sync
  commands/       6 files  — adr, new-endpoint, new-fact-type, new-migration,
                              phase-check, review-tx
  hooks/          4 files  — session-context, guard-applied-migration,
                              guard-fact-path, format-dotnet
  skills/         9 dirs   — system-design, wms-domain, database-design,
                              sql-migrations, dotnet-modules, dotnet-testing,
                              react-frontend, frontend-testing,
                              testcontainers-concurrency
```

None of it existed at the point described in §3 below. It grew out of the proposal-and-review cycle in §4–9, was brought in explicitly in §10, and was applied repeatedly as the design matured — into `wms-design-document.md`'s full four-phase specification, then into the Phase 1A backend and the frontend/screen-inventory work covered in §11–13.

Nothing below is generic scaffolding: each piece exists to make one specific decision or risk from the proposal or the review findings survive contact with real code, rather than being something a developer has to remember on every change. `CLAUDE.md`'s own "Agent & Skill Delegation Rules" section is the authoritative statement of this mapping; the tables below summarize it.

### Skills — teach the vocabulary once

| Skill | Why it's needed |
|---|---|
| `system-design` | The four interlocking documents (proposal, review, design document, `CLAUDE.md`) drift against each other unless something states which one a given change belongs in — this is that something, and the supersede-don't-rewrite rule from §7 is one of its rules. |
| `wms-domain` | The aggregate table, the three-quantity model (on hand / allocated / available), and the command/fact split are assumed by every other agent and skill in this list. Stated once here so nine other files don't each re-derive it slightly differently. |
| `database-design` | The decisions that come *before* a migration exists: grain, key strategy, sentinel-not-`NULL`. The proposal's own risk table rates a mutable-quantity inventory model "severe — unrecoverable without rewrite," which is exactly the class of mistake this skill heads off. |
| `sql-migrations` | *How* to ship a schema change once it's designed: DbUp raw SQL rather than EF migrations, expand-then-contract deployment, partitioning mechanics, and the constraints (like `CHECK (on_hand >= 0)`) that must never exist. |
| `dotnet-modules` | The module-boundary rule that keeps a modular monolith from quietly turning into a tangled regular one, plus the C# conventions `dotnet format` enforces mechanically. |
| `dotnet-testing` | Which of the three backend test projects (unit / integration / architecture) a given test belongs in, and why mocking `DbContext` tests the mock rather than the system. |
| `react-frontend` | The two-route-tree decision (D15), the offline fact queue, and the idempotency-key timing rule — generate the key at the moment of user action, not at send time, or a retry silently double-posts every queued fact. |
| `frontend-testing` | Vitest versus Playwright (jsdom has no service worker to test offline behaviour against), and the responsive-layout requirement a single fixed-viewport render can't prove. |
| `testcontainers-concurrency` | The mandatory concurrency test suite behind Phase 1A/1B's own exit criteria. `SKIP LOCKED` and advisory locks don't exist in SQLite, so testing against a substitute database would prove nothing that actually matters. |

### Agents — catch what a pattern match can't

Each agent's model tier tracks how severely the proposal's own risk table rates a mistake in that area — the severe risks get the most capable model.

| Agent | Tier | Why it's needed |
|---|---|---|
| `schema-guardian` | opus | Guards the single risk the proposal calls out as "severe — unrecoverable without rewrite": an inventory model built on mutable quantities instead of the ledger. Reviews a migration *before* it's applied, not after. |
| `concurrency-reviewer` | opus | Guards against "concurrency defects in task dispatch → duplicate picks, inventory variance" — a class of bug that's invisible in single-user testing, which is exactly why a human reviewer working alone tends to miss it. |
| `api-designer` | opus | The command/fact split is the API's single governing rule (§4). Misclassify one endpoint and an operator gets an error dialog for something that has already physically happened. |
| `test-writer` | opus, and the only agent with write access | A concurrency test that looks correct but doesn't actually force N operators to race is worse than having no test at all, because it reads as coverage that doesn't exist. |
| `security-reviewer` | opus | Added only after a review pass (the §7 addendum, `H11–H13`/`M6`) found real auth and privilege gaps slipping past the other four specialists: a lost device that couldn't be de-authorised, a role edit that revoked nothing, a delegation path that could grant more access than the grantor themselves held. |
| `backend-reviewer` / `frontend-reviewer` / `cicd-reviewer` | sonnet | General review at a cheaper tier, each one explicitly deferring to the opus specialists above on their own ground rather than giving a shallow opinion on schema, concurrency, or API contracts. |
| `doc-sync` | sonnet | Reports drift between the code and `wms-design-document.md` (§9). Reports only, fixes neither side — so it can't quietly paper over a disagreement it should instead be surfacing. |

### Commands — make the correct shape the path of least resistance

| Command | Why it's needed |
|---|---|
| `/new-fact-type` | Refuses to proceed until a new fact's divergence behaviour is declared. Guards the proposal's single most emphasised exit criterion: an over-receipt records the counted quantity and raises an exception — it does *not* return an error to the device. |
| `/new-endpoint` | Forces the command-vs-fact classification before anything is designed, at the point where changing the answer is still free. |
| `/new-migration` | Produces an expand-then-contract-safe migration file with its duration class already stated, because these run unattended across a fleet of customer deployments, not just this one database. |
| `/review-tx` | The concurrency review from §7 of `system-design/SKILL.md`, reduced to one command — so it actually gets run instead of being a paragraph someone has to remember. |
| `/phase-check` | Guards the proposal's own named risk: "proof of concept becomes the product by accretion." Reports scope creep and any shortcut missing its `docs/shortcuts.md` entry. |
| `/adr` | A decision made *during* build, after the proposal was locked, still needs a record — checked against `CLAUDE.md`'s 12 invariants first, since an ADR that silently reverses one is worse than not writing one at all. |

### Hooks — the only tooling that runs without being asked

| Hook | Why it's needed |
|---|---|
| `session-context` | Puts the open-shortcut count in front of every session automatically, so accretion (the same risk `/phase-check` guards) stays visible without anyone remembering to check `docs/shortcuts.md`. |
| `guard-applied-migration` | An edited, already-applied migration is exactly how a fleet of per-customer deployments ends up on inconsistent schema with no way to tell which database has which version. |
| `guard-fact-path` | Mechanically encodes the three things most likely to be written wrong by instinct on a fact-handling code path: a guard clause on a fact decrement (facts are never refused, per §4), a `CHECK (on_hand >= 0)` (balances may legitimately go negative), and a role-name comparison (authorization is permission-based, never role-based). |
| `format-dotnet` | Style is enforcement, not etiquette — nobody should be relitigating brace placement in review, so it's checked mechanically instead. |

---

## 1. Deciding what the project is

The project is a **Warehouse Management System (WMS)** intended to run real commercial warehouse operations — not a stock-tracking CRUD application.

That distinction is the one the whole design follows from. A stock tracker answers "how many do we have?" A WMS directs physical work: it tells a specific person to walk to a specific bin, pick a specific quantity, and confirm it by scan, then keeps a defensible audit trail of what happened.

It is also built as a **commercial, multi-customer product** — sold to warehouse operators as single-tenant, per-customer deployments, not a bespoke internal tool for one site. That framing is stated explicitly at the top of `docs/wms-project-proposal.md` and repeated at the top of `CLAUDE.md`: it changes what "done" means for nearly every later decision, since configurability, fleet operations, and per-deployment isolation are first-class concerns from day one rather than something retrofitted once a second customer shows up.

## 2. Locking in the core tech stack

Before any design work started, the technology choice was fixed so that everything downstream — schema conventions, module boundaries, testing strategy — could be decided against a concrete stack rather than left abstract:

| Layer | Choice |
|---|---|
| Backend | .NET (ASP.NET Core), modular monolith |
| Frontend | React + TypeScript |
| Database | PostgreSQL |

The reasoning behind each choice is recorded in full in `wms-project-proposal.md` §7:

- **.NET** — strong typing and a mature concurrency model, for a domain where a race condition means physical inventory loss.
- **PostgreSQL** — `SELECT ... FOR UPDATE SKIP LOCKED`, declarative partitioning, and partial indexes are load-bearing, not incidental.
- **React** — split into two route trees rather than two separate applications, per decision D15.

The short version, locked at this early stage, was simply: **.NET backend, React frontend, PostgreSQL database**. Everything else — EF Core vs. Dapper, DbUp vs. EF migrations, Docker Compose vs. Kubernetes — was decided later, against this fixed foundation.

## 3. The initial `.claude` folder structure

The `.claude/` tooling folder was scaffolded with its **full structure** up front — every directory a mature Claude Code project would need — but populated deliberately thin. At the point the project proposal was first generated, the tree looked like this:

```
.claude/
  settings.json
  agents/                  (empty)
  commands/                (empty)
  hooks/                   (empty)
  skills/
    system-design/         ← the only populated skill
      SKILL.md
```

**Only `system-design` had content.** No agents, no commands, no hooks, and no domain-specific skills (`wms-domain`, `database-design`, `sql-migrations`, `dotnet-modules`, and the rest) existed yet — those all came later, once there was an actual design document and codebase for them to have jurisdiction over. `system-design` alone was enough to start, because its entire job is deciding *which document a piece of content belongs in* and *how the documents stay in sync with each other* — exactly what's needed before a proposal exists at all, and nothing more.

The folder skeleton being present from the start (rather than created incrementally) meant every later addition — an agent, a command, a hook — had an obvious, already-decided home, instead of the project having to invent its own tooling layout mid-stream.

**What `system-design/SKILL.md` actually said, at a point when almost none of the files it talks about existed yet:**

- **A document hierarchy table**, naming six files and stating which one to edit for which kind of change: `wms-project-proposal.md` (scope and resolved decisions), `wms-architecture-review.md` (a historical, never-edited record of review findings), `wms-design-document.md` (the living source of truth for schema/API/flows), `docs/adr/` (one-off build-time decisions), `docs/shortcuts.md` (tracked Phase 1A compromises), and `CLAUDE.md` (the always-in-context invariant summary).
- **A rule for superseding a finding without rewriting history** — a later decision that changes an earlier review's answer gets a note at its point of use in the design document, never an edit to the review file itself.
- **A propagation rule** — an invariant is never stated in exactly one place; it's echoed into `CLAUDE.md`, the design document, any reviewer agent whose rules quote it, any skill whose reference tables restate it, and any hook that encodes it as a pattern match — so a change has to be grepped across all of them, not just made once.
- **A change-classification checklist** and a closing instruction to run a `doc-sync` agent after any change, to catch drift the edit may have introduced.

At the moment this skill first loaded, only `wms-project-proposal.md` existed — not `wms-design-document.md`, `wms-architecture-review.md`, `CLAUDE.md`, `docs/adr/`, or a single agent or hook for the propagation rule to check. That's not an inconsistency to explain away.

The skill's job was never only to route content into files that already exist — it was written to describe the **document structure the project would grow into**. So when each of those files did get created (the design document once the proposal's scope was detailed enough to need one, `CLAUDE.md` once there were invariants worth summarizing, agents and hooks once there was code for them to guard), they landed in the place already decided for them, rather than the project inventing its documentation layout piecemeal. The skill file itself never changed; only how much of what it described had actually been built did.

## 4. Launching Claude Code and generating the proposal

With the stack locked and only `system-design` loaded, the project was started the same way any Claude Code session starts: by describing the idea in plain language — a WMS for real commercial warehouse operations, on .NET/React/PostgreSQL — and letting the `system-design` skill take it from there.

`system-design` auto-loaded on relevance (skills match against the task, not against an explicit invocation) and did two things before any content was written:

1. **Decided which file the output belonged in.** Its document hierarchy routes scope, objectives, resolved/open decisions, the core domain model, and the phased roadmap to `wms-project-proposal.md` specifically — schema, API contracts, and data flows are explicitly out of place there and belong in a separate design document instead, once one exists.
2. **Drove the back-and-forth that filled it in.** Rather than a single one-shot prompt producing a finished proposal, the content was built through iterative exchange — open questions surfaced as structured questions (`AskUserQuestion`) where a decision was genuinely the user's to make (for example, what the media/photo-evidence subsystem should cover, and at which phase), and filled in through ordinary conversation where the reasoning was architectural rather than a business call.

The output of this stage was `docs/wms-project-proposal.md` — objectives, the resolved-and-open decision log (the D1–D15 table), the core domain model, and the phased roadmap (1A → 1B → 2 → 3 → 4).

## 5. Reviewing the proposal and making changes

A first draft is not a finished design, and the proposal was revised repeatedly rather than treated as final the moment it existed. Two kinds of review happened, both recorded in `docs/claude-tooling-log.md` as they occurred:

- **Direct requests for specific additions.** For example, adding login and user-role functionality (the origin of decision D10) came from an explicit ask — "add login and user roles functions so it's easier to operate" — which `system-design` routed to the proposal's decision table, scope section, and Phase 1A roadmap row, with the specific role/permission split worked out as ordinary design reasoning against that request.
- **Open-ended adversarial review.** A general "is this design as solid as it could be?" prompt triggered a re-read of the documents looking specifically for gaps rather than confirmation — this is what later produced `wms-architecture-review.md` and, eventually, the fully detailed `wms-design-document.md` once the proposal's scope had matured enough to need one. Findings from these passes are never edited back into the original proposal narrative; where a later decision changes an earlier answer, the design document carries a superseding note at the point of use instead, so the review history stays intact.

This review cycle is still open-ended, not a one-time gate — the same pattern (a specific request, or a general "check this over" prompt) is how the design documents continue to be corrected as the project proceeds into Phase 1A implementation.

## 6. Generating the architecture review

Once the proposal was locked in, the next stage deliberately switched roles: rather than continuing to *build* the design, the instruction was to *audit* it — "act as a senior system architect engineer, confirm and review the proposal architecture." `system-design` picked this up the same way it picks up any architecture-scoped request.

The output this time was different in kind from the proposal itself: a **new**, third document, `wms-architecture-review.md`, holding numbered findings by severity — `C1–C7` (Critical), `H1–H10` (High), `M1–M5` (Medium) — rather than restating or replacing anything already in the proposal.

This is also the point where the architecture matured enough to need `wms-design-document.md` — the living source of truth for schema, API contracts, and data flows — since a review that finds a gap is only useful if the fix has somewhere concrete to live. In this project's own history, both files came out of the same pass: the review named what was wrong (the ledger-based inventory model needed derived balances rather than a mutable quantity column, state machines were underspecified, a handful of NFRs were missing), and the design document was written immediately after to resolve each finding.

The instruction anticipates this step asking the user for scope and feature input along the way — and it's worth being honest about where that happened and where it didn't. The very first review pass didn't need a further round of questions: the proposal already carried enough resolved scope (the D1–D15 decisions) for the findings to be reasoned through directly against it.

The scope-clarifying exchanges this step describes showed up more prominently at adjacent points in the project's actual history instead — deciding what the media/photo-evidence subsystem should cover during proposal drafting (§4), and, later, an explicit choice between cherry-picking fixes versus adopting wholesale the fuller scope this project's own design had already reached, which is exactly the kind of scope call this step exists to force onto the user rather than let Claude guess at silently.

## 7. Reviewing the architecture review

`wms-architecture-review.md` is treated differently from every other document in this project once it's created: it is a **historical record, never edited after the fact**. A later decision that changes what an earlier finding concluded doesn't get rewritten into the review — it gets a superseding note at its point of use in `wms-design-document.md` instead, so the record of what two review passes actually found stays intact rather than quietly rewritten to look correct in hindsight.

"Make changes if needed" in this project therefore didn't mean editing the review file — it meant two other things, both of which happened in practice:

1. **Resolving findings into the design document.** Each `C`/`H`/`M` finding got a concrete fix landed in `wms-design-document.md` — an atomic upsert for a concurrency finding, a new cancel-order flow for a leak the review caught, an idempotency-key storage design, and several more.
2. **Running a further review pass over the project's own subsequent additions.** A second pass later re-read the design document adversarially again, checking work the project itself had added since the first pass, and appended its findings as a numbered addendum (`H11–H13`, `M6`) continuing the same numbering rather than restarting it — exactly as the never-edit rule intends.

One deliberate exception exists, and it's worth naming precisely because it looks like it breaks the rule above: the review file's "Consolidated Open Decisions" table is a **live status tracker**, not a finding narrative, and is kept current as each `D`-numbered decision actually resolves. That one table gets edited directly; the `C`/`H`/`M` findings around it never do. The distinction is stated at the top of that table specifically so a future session doesn't either over-apply the never-edit rule to a table that's supposed to stay current, or use that one exception as licence to edit an actual finding.

## 8. Generating the detailed system design

With the architecture review's findings resolved, the next prompt asked for the opposite of an audit again — depth this time, not critique: "act as a senior system architect engineer, create a detailed system design of the project."

One naming note first: the guideline calls the output `wms-system-design.md`; the file this project actually produced is `wms-design-document.md`. Same role, different name — the document that covers schema, the full API reference, data flows, the error catalogue, and NFRs across every phase is the one already named and cross-referenced everywhere else in this record (§3, §6, §7). This section treats the two names as referring to the same file rather than introducing a second one.

The more substantive thing worth being honest about: this project's actual history didn't produce the design document as a separate step *after* the architecture review was fully locked in. §6 already describes how both documents came out of the **same pass** — the review named the gaps, and the design document was written immediately afterward to resolve them, in one continuous piece of work rather than two prompts separated by a review-and-approve gate. Steps 6 and 8 in the guideline describe two distinct prompts; in this project they collapsed into one.

What step 8 does capture accurately is the document's actual scope once it existed: `wms-design-document.md`'s own reading notes state that Phases 1A, 1B, and 2 are specified to build depth — schema, endpoints, and flows intended to be implemented as written — while Phases 3 and 4 fix contracts and schema shape without pinning down every internal detail, since real customers haven't yet revealed what they need there.

## 9. Reviewing the system design

Unlike the architecture review, `wms-design-document.md` is the **living** source of truth — meant to be edited directly as the design changes, not preserved as a historical snapshot. "Review and make changes if needed" has therefore been the normal mode of work on this file since it was created, not a single bounded step. Every gap found afterward got the same treatment:

- Warehouse and zone management had schema but no API surface to create one.
- Custom role composition was promised in the proposal's own Phase 1A scope table but never exposed as an endpoint.
- A dashboard the proposal committed to was never specified.
- An OpenAPI document the design had depended on since §7.7 was never actually specified.

Each gap was found by re-reading the document against its own stated commitments (the proposal's scope table, an earlier decision, a cross-reference elsewhere in the same file), not invented fresh. Each was resolved by editing the design document directly, then propagating the change to every other file that echoes the same rule — including `CLAUDE.md`, once §10 below brings it into existence — per the `system-design` skill's own propagation rule from §3.

The `doc-sync` agent exists specifically to keep this step from silently stopping once real code exists: its whole job is comparing `wms-design-document.md` against `src/` and reporting drift — never fixing either side, so a disagreement between what the document specifies and what the code actually does gets surfaced rather than resolved by whichever one happened to be read first.

## 10. Porting the tooling that enforces the design

Once the detailed design existed, the last bootstrapping step turned it into the tooling that would actually enforce it going forward — almost the exact request this project received in practice: "create all the skills, agents, commands, hooks needed for the development of this project."

Before creating anything, the two interpreters every hook shells out to (`python3` for JSON handling, `dotnet` for formatting) were confirmed present on the machine — installing a hook that silently no-ops on every single edit because its interpreter is missing would have been worse than not installing it at all.

**What was created in this step:**

| What was created | What it is |
|---|---|
| 7 skills — `wms-domain`, `sql-migrations`, `dotnet-modules`, `dotnet-testing`, `react-frontend`, `frontend-testing`, `testcontainers-concurrency` (`system-design` and `database-design` already existed from earlier steps) | `SKILL.md` files teaching one area's vocabulary and conventions, loaded automatically by relevance rather than invoked by name |
| 8 agents — `schema-guardian`, `concurrency-reviewer`, `api-designer`, `test-writer`, `backend-reviewer`, `frontend-reviewer`, `cicd-reviewer`, `doc-sync` | `.claude/agents/*.md` files, each defining one specialist's review scope, model tier, and tool access |
| 6 commands — `adr`, `new-endpoint`, `new-fact-type`, `new-migration`, `phase-check`, `review-tx` | `.claude/commands/*.md` slash-command definitions |
| 4 hooks — `session-context.sh`, `guard-applied-migration.sh`, `guard-fact-path.sh`, `format-dotnet.sh` | Shell scripts that fire automatically on session start or after a matching file edit, no invocation needed |
| `CLAUDE.md` | The always-in-context summary: the 12 numbered invariants, current phase, stack, and (added in this same step) the tooling-delegation rules for everything above |

Two things about the guideline's own wording are worth being precise about.

**First, "plugins" didn't fully resolve inside this one action.** The three marketplace plugins earlier work assumed (`csharp-lsp`, `microsoft-docs`, `playwright`) were deliberately left uninstalled at this point — hand-editing `enabledPlugins` to name a plugin the user hadn't actually installed risks a settings file that lies about what's available — and were instead flagged in `CLAUDE.md` as something to install once implementation work started. The user installed all three shortly after, via `/plugin`, completing the plugin roster the tooling section above now lists in full (`superpowers`, `context7`, `frontend-design`, `csharp-lsp`, `microsoft-docs`, `playwright`).

**Second, a ninth agent, `security-reviewer`, was added still later.** Not part of this porting step itself, but a direct consequence of the review discipline this step put in place: a subsequent review pass found real authorization gaps the four then-existing reviewer agents weren't scoped to catch, and the fix was a fifth specialist agent rather than a patch to an existing one.

## 11. Scaffolding the project — the initial structure and the Phase 1A backend

With the tooling from §10 in place, the next prompt was the first in this repository's history to produce application code rather than documents: "let begin phase 1A development, start with back-end." This is where `docs/claude-tooling-log.md` picks up in far more granular detail than any section above — dozens of dated entries under 2026-09-11 alone, each one tool-attributed the same way as every step in this record.

The `dotnet-modules` skill loaded first, per `CLAUDE.md`'s own File-Pattern Triggers, and shaped the solution skeleton: `Directory.Build.props`, `.editorconfig`, `.gitattributes`, and one `.csproj` per module — with the `Outbound` module (orders, allocation, picking) deliberately left uncreated, since it's Phase 1B and scaffolding it now would be the same phase-boundary violation the roadmap exists to prevent. Module-project granularity was recorded as `docs/adr/0001` rather than left implicit, since the design document doesn't pin that choice down itself.

From there the backend built out in the order its own schema forces:

1. **Three initial migrations** — master data, identity, the ledger. This included a sentinel-lot design correction, and surfaced a genuine bug in `guard-applied-migration.sh`, which silently no-op'd on Windows paths and was fixed once caught.
2. **The remaining Phase 1A schema** — tasks, inbound, platform — followed by a seed migration (permissions, system roles, reason codes).
3. **The DbUp migrator**, which itself corrected a false claim: this project's own docs had asserted a checksummed-migration guarantee DbUp doesn't actually provide.
4. **Architecture tests** enforcing module boundaries and the role-name ban.
5. **The fact-path write transaction** — `ReceiptConfirmedFact` and its handler, proving exit criterion 5 (an over-receipt records the counted quantity and raises an exception, never an error) — followed by a `concurrency-reviewer` pass that found two real defects six green tests hadn't caught.
6. **`POST /sync/facts`**, with a `security-reviewer` pass on it, then the `putaway_confirmed` fact and the receipt-to-putaway task-generation flow.
7. **Task dispatch** (with a correction to what `SKIP LOCKED` actually guarantees), balance reconciliation, and the admin read models.
8. **Full user administration**, including three further security-review passes that kept finding real gaps in the previous pass's own fix.

**What was created, as it stands on disk today:**

| What was created | What it is |
|---|---|
| 10 `.csproj` projects | `Wms.SharedKernel` (cross-module contracts); 6 module projects — `Wms.Modules.Catalog`, `.Identity`, `.Inventory`, `.Tasks`, `.Inbound`, `.Platform` (`Outbound` deliberately absent, per the Phase 1B boundary above); `Wms.Api` (the API host, with endpoint groups for Administration, Authentication, Receiving, Reporting, Sync, and Work); `Wms.Worker` (the background host); `Wms.Migrator` |
| 9 SQL migrations, `0001`–`0009` | Master data, identity, the inventory ledger, tasks, inbound, platform, seed reference data — plus two later additions folded in during the user-administration/security-review work above: a `credential.manage` permission and a task-lease index |
| 3 test projects | `Wms.UnitTests`; `Wms.IntegrationTests`, with one folder per feature area (Administration, Authentication, Inbound, Inventory, Receiving, Reporting, Sync, Tasks, Work); `Wms.ArchitectureTests` (module-boundary and role-name-ban enforcement) |
| 2 ADRs | `0001` (module-project granularity, recorded because the design document left it unspecified) and `0002` (the ledger sequence-watermark gap the concurrency review surfaced but that had no code yet to fix) |
| Solution-wide config | `Directory.Build.props`, `.editorconfig`, `.gitattributes` — created up front rather than left as a tracked shortcut |

All of it landed in a single commit, `3b65439` ("Initial commit: Phase 1A backend, tooling, and docs") — not because the work was undifferentiated, but because this repository had no git history at all until this point in the project's timeline (the `PROJECT_REFLECTION.md` audit, back around §10, had confirmed zero commits and no remote existed yet). The dozens of tool-attributed turns tracked in `claude-tooling-log.md` are the real granularity; the single commit just marks when version control caught up to work already done.

## 12. Scaffolding the frontend — a basic structure to sync with Claude Design

The next step, per the guideline, was narrower on purpose: not the full operator/admin frontend, just enough structure — Vite, React, TypeScript, and the routing/data/component libraries the design document already commits to — for that structure to be pushed to Claude Design, styled there, and synced back. `web/` now holds a Vite + React 19 + TypeScript app with TanStack Router/Query/Table for routing and data-fetching, a generated client (`openapi-fetch`) against the backend's OpenAPI document (design doc §3.6), and a shadcn-style component set (`radix-ui`, `class-variance-authority`, `tailwind-merge`) under `web/src/components/ui`.

This stretch of work is worth flagging as different in kind from every section above: it has **no corresponding entries in `docs/claude-tooling-log.md`**. The log's own entries stop at the admin-auth security-review work covered in §11 (dated 2026-09-11) and jump straight to `wms-screen-inventory.md` (2026-09-14) — nothing recorded in between says which skill or tool drove the frontend scaffold.

What's known instead comes from the git history and the repository's current state, not a tool-attributed log:

- The work happened on a separate branch/worktree (`worktree-admin-frontend-foundation`) and was merged via pull request (`a66ab1c`), consistent with this project's `using-git-worktrees` and `subagent-driven-development` skills — one commit (`35429f3`) exists specifically to gitignore a `subagent-driven-development` scratch workspace.
- A commit named "address Task 1 formal review findings" implies a formal review step ran, though which agent ran it isn't recorded anywhere this document can cite.
- The rest of the sequence — scaffold, fix Tailwind/tooling issues, generate the API client and a session/refresh-retry module, fix a `baseUrl` mismatch against the real backend's path schema, add a test covering a refresh-dedup race a review flagged, then establish the Tailwind v4 theme and a real library build for the component set — reads as the same iterative-correction pattern as §11, just without the log entries documenting each step's own tool attribution.

The guideline's "sync to claude-design" half of this step is done: `003b37e` ("sync the WMS component library to claude.ai/design") is the literal event. The "sync back once it is done" half — pulling a Claude Design-authored UI back into this codebase — hasn't happened yet; nothing in `web/` beyond the default Vite template `README.md` and the scaffolded component library reflects a finished design pass having come back in.

## 13. Documenting the screens needed, for a Claude Design handoff

The next request pointed at a document meant for a different tool entirely, not at continuing to build against the codebase: "go through the detailed design and all the docs then create a list of screen needed for this project and compose it into a document so i could give it to claude design." Unlike every step above, the deliverable's audience is Claude Design, receiving a handoff artifact ahead of any UI actually being drawn — not Claude Code continuing to build against it.

Because the source material is large — `wms-design-document.md` alone runs to roughly 5,000 lines across all four phases, on top of `wms-project-proposal.md`, `wms-architecture-review.md`, and `shortcuts.md` — the reading was delegated to a forked agent rather than done inline: a fork inherits the same conversation context but keeps its own tool output out of the parent's, so none of that raw document content had to sit in context once only the distilled screen list was needed afterward. The fork extracted one structured entry per screen — route, phase, purpose, primary role, key fields drawn from the actual schema, primary actions, related endpoints, UX notes — rather than a shallow list of section headings.

Two scope calls were made without being asked for, as a reasonable default rather than a specified requirement:

1. The document covers **all four phases**, not just the active Phase 1A, since the design document itself already specifies all four and a design tool benefits from seeing the established pattern extrapolate forward.
2. It closes with a "prioritize in this order" note pointing a design tool at the Phase 1A operator screens first, since that's this project's actual active build target.

The output landed at `docs/wms-screen-inventory.md`, with a document header matching this repo's existing convention (Scope/Status/Companion documents) and a short purpose statement making clear it's a design-tool handoff artifact, not a status report.

This step produced no code and touched none of the four documents `CLAUDE.md`'s File-Pattern Triggers route to the `system-design` skill (`wms-design-document.md`, `wms-project-proposal.md`, `wms-architecture-review.md`, `docs/adr/**`) — so no skill or agent from that table applied. `wms-screen-inventory.md` is a new, derived artifact, not an edit to one of the four tracked documents.

## 14. Feeding the screen inventory into Claude Design, admin screens first

The guideline's next step names the natural continuation of §13: take `docs/wms-screen-inventory.md` and use it to start designing UI in Claude Design, beginning with the admin screens.

One discrepancy is worth flagging before anything else. The screen inventory's own "prioritize in this order" closing note (see §13) ranks **Phase 1A operator screens first** — 8 screens, the smallest set but the highest architectural risk (offline, scan-verified, exception-raising), and the ones Phase 1A's own exit criteria are built around — with Phase 1A admin screens second (26 screens, the active build target per `CLAUDE.md`). This step's "starting from the admin screens" instruction reverses that order. That's a legitimate call for the user to make — the admin surface is the more conventional design-to-code path, while the operator side's offline/scan-verified PWA is a harder problem to hand a design tool cold — but it's a deviation from the artifact's own stated recommendation, not a continuation of it, so it's recorded here rather than silently smoothed over.

As of this update, this step **hasn't happened yet** in the repository. No commit since the component-library sync (`003b37e`, §12) touches an admin screen beyond the still-minimal root route (`web/src/routes/__root.tsx`), and nothing in `docs/claude-tooling-log.md` or the rest of the repository records `wms-screen-inventory.md` having actually been sent to Claude Design.
