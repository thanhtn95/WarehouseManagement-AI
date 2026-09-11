# WMS — Working Context

Full detail lives in `docs/`. This file is the always-in-context summary —
keep it in sync with `docs/wms-design-document.md` whenever an invariant,
the stack, or the current phase changes (the system-design skill's
propagation rule).

This is a commercial, multi-customer WMS product, not a single-warehouse
internal tool — read `docs/wms-project-proposal.md` in full before assuming
anything about scope.

- `docs/wms-project-proposal.md` — objectives, resolved/open decisions
  (D1–D15), core domain model, phased roadmap (1A → 1B → 2 → 3 → 4), risks.
- `docs/wms-architecture-review.md` — historical findings (C1–C7, H1–H10,
  M1–M5, plus a second-pass addendum: C6–C7, H7–H10, M4–M5). Never edited
  after the fact — a later decision that changes the answer gets a
  superseding note at its point of use in the design document instead.
- `docs/wms-design-document.md` — the living source of truth: data models,
  API conventions and reference, the fact registry, 22 data flows, error
  catalogue, and NFRs, across all four phases. Edit this one when
  schema/API/flow/NFR content changes.
- `docs/shortcuts.md` — Phase 1A/1B proof-of-concept compromises, recorded
  the moment each is taken, paired with its replacement.

## Current phase: 1A — Foundation and inbound

Get stock accurately *into* the building, with the platform underneath it
built properly: auth (badge/PIN operator login, staff login, device
registration, device sessions, supervisor override), full user management
(create/list/edit/deactivate, role-scope assignment — never a hard
delete), the permission/role/scope model, master data (warehouses, zones,
items, UoM hierarchy, locations, handling units — each with real
maintenance screens on the admin site), blind receipt with
over/under/damage raising an exception (never an error), directed putaway,
the ledger and incremental reconciliation, the offline queue with
idempotent sync and the exception queue, an admin dashboard over inbound
and stock position, the scan-input abstraction, and the two-route-tree
frontend with the operator routes installable offline-capable.

**Explicitly excluded from 1A:** allocation, picking, packing, shipping,
media, lot/FEFO exposure, serials, waves, cycle counting, returns,
configuration UI, carrier labels, ERP integration. Picking/packing/shipping
are Phase 1B, not 1A — building them now is a process bug, not early
delivery. See `wms-project-proposal.md` §13 for full phase detail and exit
criteria.

## Stack

.NET (ASP.NET Core), modular monolith, two deployed processes (API +
Worker) · PostgreSQL primary + streaming replica · EF Core for aggregate
writes, Dapper for hot read paths · DbUp for migrations (raw versioned SQL,
never EF migrations) · PgBouncer, per-role connection pools · React +
TypeScript (Vite), one application, two route trees (`/admin/*`,
`/operator/*` — D15), operator routes an installable PWA with a service
worker + IndexedDB fact queue · Docker Compose for dev and per-customer
deploy · GitHub Actions CI/CD, images by SHA, migrator gates API restart ·
S3-compatible object storage (MinIO on-prem or cloud), never DB BLOBs.

## Invariants (summary — authoritative statement in design doc §1–§4, §8)

1. Stock quantities are ledger-derived: `stock_movement` (append-only,
   partitioned by `recorded_at`) is the source of truth; `stock_balance` is
   a derived projection maintained in the same transaction. **No**
   `CHECK (on_hand >= 0)` — a negative balance is true information and is
   never suppressed (C1, C3).
2. `stock_balance`'s grain is exactly (owner, item, location, lot,
   stock_status) — never coarser. No synchronously-maintained SKU-level or
   warehouse-level total exists anywhere in the schema (C1).
3. Allocation is serialized per warehouse by a single active consumer
   (`pg_advisory_lock`), never synchronous on the request path — the sole
   mechanism preventing overselling (C2).
4. Every endpoint is classified as a **command** or a **fact** before it's
   built. Facts (`/sync/facts`) are never refused except malformed/
   unauthenticated — divergence from expectation raises an
   `inventory_exception`, never an error. Commands may be refused (`409`)
   only when nothing physical has happened yet (C3).
5. Ledger ordering and reporting are authoritative from server-assigned
   `sequence` / `recorded_at` / `business_date` — device-reported time is
   forensic only, never trusted for ordering (C4, M4).
6. One aggregate is mutated per transaction, with exactly two structural
   exceptions: fact confirmation (the fact plus the work item it confirms
   against) and allocation (contained by the advisory lock) (C6).
7. Every fact/command that mutates state requires a client-generated
   `Idempotency-Key`, generated at the moment of user action, not at send
   time. A replay with a matching hash returns the stored response; a
   mismatched hash is a `409` (client bug, not a retry) (H2).
8. Authorization checks are always against permissions plus scope
   (warehouse/zone) — never role names. Enforced server-side on every
   endpoint regardless of what the UI hides.
9. The approver of a variance, adjustment, or count may never be its own
   actor — segregation of duties enforced in the domain layer, not the UI.
10. Workload isolation: separate database roles with separate connection
    pools and statement timeouts, so no single slow query (allocation,
    bulk import, report) can stall a scan confirmation (C7).
11. Media is never a DB BLOB — object storage only, content-addressed,
    deduplicated by hash; EXIF stripped and content type validated by
    magic bytes on ingest.
12. Schema is forward-compatible from the first migration — lot, expiry,
    serial (`serial_unit`), owner, and handling-unit structures exist even
    before the features using them ship, because retrofitting them later
    means migrating the two largest tables in the system.

## Identity and roles

Two populations, two auth models: **staff** (email + password, conventional
browser session) and **operators** (badge scan or PIN, device-bound shared
sessions on pooled handhelds). Authorization is permission-based with
warehouse/zone scope, not role names — seeded system roles (System
Administrator, Warehouse Manager, Supervisor, Inventory Controller,
Receiver, Putaway Operator, Picker, Packer, Returns Processor, Auditor,
Client User) are named bundles of permissions, composable into custom
site-specific roles from the full permission catalogue (Phase 1A — not
deferred). In-place supervisor override (badge scan, no session change)
authorizes exceptions without logging the operator out. Full design doc
§2.1, §5.1, §6.1, §6.5; proposal §10.

## Single-tenant, commercial product (D1/D1a/D1b)

Own-operator target, sold as a product to multiple customers, **one
isolated deployment per customer** — isolation at the process/database
boundary, not a schema column. No `tenant_id`, no row-level security for
tenancy. Fleet operations (provisioning, version rings, cross-deployment
observability) are Phase 4 — see proposal §12, design doc §11.

## Agent & Skill Delegation Rules

**Read this before assuming any of the below is self-executing.** Three
different mechanisms are at play here, with three different levels of
enforcement — conflating them is how a rule silently stops applying:

- **Skills genuinely auto-load by relevance.** Claude Code matches a
  skill's `description` against the current task and loads it into
  context on its own; this is a real, built-in mechanism, not a
  convention.
- **Hooks genuinely run automatically**, wired in `.claude/settings.json`,
  on every matching tool call, with no invocation needed. Four exist
  today (listed under File-Pattern Triggers below) and are the only part
  of this section mechanically *enforced* rather than *instructed*.
- **Agents and commands are not automatically triggered by a file
  pattern.** There is no hook that spawns a subagent — hooks can only
  block, warn, or inject context. Dispatching `schema-guardian` when a
  migration changes, or running `/review-tx` before calling a transaction
  change done, happens because whoever is driving the session (Claude,
  in any given turn) reads the tables below and follows them — the same
  way this file's other rules are followed. Treat every row below as a
  standing instruction to comply with, not a system that runs itself.

### 1. File-Pattern Triggers

| Path touched | Load skill(s) | Delegate to agent before calling it done | Automatic hook already covering part of this |
|---|---|---|---|
| `db/migrations/**/*.sql` | `sql-migrations`, `database-design` | `schema-guardian` | `guard-applied-migration.sh` blocks editing a migration that isn't the newest file (already applied) |
| `src/Modules/**/*.cs` (general backend) | `dotnet-modules` | `backend-reviewer` | `guard-fact-path.sh` flags a guarded fact-path decrement, a `CHECK (on_hand >= 0)`, or a role-name comparison; `format-dotnet.sh` runs `dotnet format` |
| A new or changed HTTP endpoint (`src/Wms.Api/**`, anything defining a route) | `wms-domain` (command/fact split) | `api-designer` | — |
| Anything touching allocation, pick confirmation, task leasing, the outbox, or opening a transaction, regardless of module | `wms-domain` | `concurrency-reviewer` | `guard-fact-path.sh` flags `FOR UPDATE` without `SKIP LOCKED` |
| `src/Modules/Identity/**`, or any permission check, credential handling, token/session code, or `auth_event` write anywhere | `wms-domain` | `security-reviewer` | `guard-fact-path.sh` flags a role-name comparison (a broken authorization check) |
| `tests/Wms.UnitTests/**` | `dotnet-testing` | — | — |
| `tests/Wms.IntegrationTests/**`, `tests/Wms.ArchitectureTests/**` | `dotnet-testing`, `testcontainers-concurrency` | `test-writer`, if the feature touches the ledger, leasing, allocation, or offline sync | — |
| `web/src/routes/**`, `web/src/shared/**` | `react-frontend` | `frontend-reviewer` | — |
| `web/**/*.test.tsx`, `web/**/*.spec.ts` | `frontend-testing` | — | — |
| `.github/workflows/**`, `Dockerfile*`, `docker-compose*.yml` | — | `cicd-reviewer` | — |
| `docs/wms-design-document.md`, `docs/wms-project-proposal.md`, `docs/wms-architecture-review.md`, `docs/adr/**`, this file | `system-design` | — | — |

### 2. Task-Pattern Triggers

Moment-based, not tied to one file — these are workflow checkpoints:

| Moment | Run |
|---|---|
| Creating a new HTTP endpoint | `/new-endpoint` first — it classifies command vs. fact before anything is designed, then hands off to `api-designer` |
| Adding a new fact type | `/new-fact-type` — refuses to proceed until the fact's `inventory_exception` divergence behaviour is declared |
| Writing a new migration | `/new-migration`, then `schema-guardian` before calling the file ready |
| Any transaction/lock/lease/allocation change, before calling it done | `/review-tx`, which delegates to `concurrency-reviewer` |
| Before a commit, or whenever unsure if current work is in scope | `/phase-check` — reports in-scope vs. out-of-scope work and any shortcut missing a `docs/shortcuts.md` entry |
| A structural or tooling decision not already covered by the design doc | `/adr`, checked against this file's 12 invariants first |
| After a feature lands, or before a phase review | Dispatch `doc-sync` to report drift between code and `wms-design-document.md` — it reports only, never fixes either side |
| **Before proposing a `git commit`** (already an `ask`-gated command in `settings.json`) | Dispatch `security-reviewer`, alongside `/phase-check`. The built-in Claude Code `security-review` skill is a reasonable general sweep too, but `security-reviewer` is scoped specifically to this design's own security posture (§8.1) and the H11–H13/M6 precedents — prefer it for anything touching auth, permissions, or a new admin/delegation capability. |
| A new admin or delegation capability (anything where one user grants, configures, or revokes access for another) | `security-reviewer` — privilege-escalation and revocation-actually-revokes are its specific ground (H11–H13) |
| Refactoring existing backend code | `backend-reviewer`, escalating to `schema-guardian`/`concurrency-reviewer`/`api-designer`/`security-reviewer` if the refactor touches their specific ground (schema, transactions, an endpoint contract, or auth/permissions) — same escalation rule the agents themselves state |

### 3. MCP Tool & Plugin Usage

| Tool | Installed? | When to invoke autonomously |
|---|---|---|
| `context7` (`query-docs`, `resolve-library-id`) | Yes | Whenever implementing against a specific library/framework whose exact current API shape matters (EF Core, ASP.NET Core, a React library) — don't rely on possibly-stale training knowledge for an exact signature. Also the right tool for verifying a library's current licence terms before adopting it (`dotnet-modules` skill's MediatR/AutoMapper/ImageSharp warning). |
| `claude.ai Figma` MCP | Available at session/account level; **not configured for this project** (no `.mcp.json`) | Only if the user actually shares a Figma file or URL for the admin or operator UI. Never invoke speculatively — there is nothing to fetch without one. |
| `playwright` MCP (interactive browser) | Yes | Interactively driving/inspecting a page by hand to verify a change, per the `frontend-testing` skill. A different thing from `@playwright/test` (the npm package running the automated E2E suite in CI) — the MCP tool is never a substitute for writing an actual `page.spec.ts`. Nothing to point it at until `web/` exists. |
| `microsoft-docs` (`microsoft_docs_search`, `microsoft_code_sample_search`, `microsoft_docs_fetch`) | Yes | **Prefer over `context7` for anything .NET, ASP.NET Core, or EF Core** — it is the authoritative source for that ecosystem. Use it before writing against an API whose exact signature matters, rather than trusting recall: hallucinated method names and deprecated patterns are precisely what it catches. |
| `csharp-lsp` | Yes | Language-server-backed navigation and diagnostics over `src/`. Reach for it on "where is this used / what does this resolve to" questions instead of grepping for a symbol name. |
| `superpowers` (plugin-provided skills, not an MCP server) | Yes | Its brainstorm → plan → implement → review workflow is worth reaching for on a *large* change (a whole module, a multi-step migration sequence). Overkill for a single endpoint or a doc edit — don't invoke it reflexively. |
| `frontend-design` (plugin-provided skill, not an MCP server) | Yes | Visual/aesthetic direction when building actual `/admin` or `/operator` screens. Nothing to apply it to until `web/` exists. |

Check `.claude/settings.json` → `enabledPlugins` before assuming any tool
above is actually available. All six are currently enabled.

### 4. Why each of these is installed

Nothing here is generic scaffolding — each piece exists to make one
specific decision or risk from `wms-project-proposal.md` /
`wms-architecture-review.md` survive contact with real code, instead of
being something a developer has to remember on every change.

**Skills — teach the vocabulary and conventions once, so every agent,
command, and ad-hoc change reasons from the same model:**

| Skill | Exists because |
|---|---|
| `system-design` | Four interlocking documents drift against each other. This encodes which one a given change belongs in, and the supersede-don't-rewrite rule for review findings. Not hypothetical — this repo's own D1–D10 status table had already gone stale against the proposal. |
| `wms-domain` | The aggregate table, the three-quantity model, and the command/fact split are assumed by every other agent and skill. Stated once here so nine other files don't each re-derive it slightly differently. |
| `database-design` | The decisions that come *before* a migration: grain, key strategy, sentinel-not-`NULL`. Risk table: a mutable-quantity inventory model is rated "severe — unrecoverable without rewrite." |
| `sql-migrations` | *How* to ship a schema change: DbUp raw SQL not EF migrations (§7.6), expand-then-contract (§11), partitioning mechanics, and the constraints that must never exist. |
| `dotnet-modules` | The module boundary rule (§7.4) that keeps a modular monolith from quietly becoming a regular one, plus the C# conventions `dotnet format` enforces. |
| `dotnet-testing` | Which of the three backend test projects a test belongs in — and that mocking `DbContext` tests the mock, not the system. |
| `react-frontend` | D15's two route trees, the offline fact queue, and the idempotency-key timing rule whose violation silently double-posts every queued fact. |
| `frontend-testing` | Vitest vs. Playwright (jsdom has no service worker), and §8.7's resize requirement — which a single fixed-viewport render cannot prove. |
| `testcontainers-concurrency` | The mandatory concurrency suite behind Phase 1A/1B's exit criteria. `SKIP LOCKED` and advisory locks don't exist in SQLite, so testing against a substitute tests nothing that matters. |

**Agents — catch what a pattern match can't. Model tier is chosen by how
severely the proposal's own risk table rates a mistake in that area:**

| Agent | Tier | Exists because |
|---|---|---|
| `schema-guardian` | opus | Risk: "Inventory model chosen as mutable quantities → severe, unrecoverable without rewrite." Reviews a migration *before* it's applied. |
| `concurrency-reviewer` | opus | Risk: "Concurrency defects in task dispatch → duplicate picks, inventory variance" — invisible in single-user testing, which is why a human reviewer misses them. |
| `api-designer` | opus | The command/fact split is the API's single governing rule. Misclassify once and an operator gets an error for something they physically already did. |
| `test-writer` | opus, and the only agent with write access | A concurrency test that looks right but doesn't actually make N operators race is worse than no test, because it reads as coverage. |
| `security-reviewer` | opus | Added *after* H11–H13 showed auth and privilege gaps slipping past the other four — a lost device that couldn't be de-authorised, a role edit that revoked nothing, a delegation path that could grant what the grantor lacked. |
| `backend-reviewer` / `frontend-reviewer` / `cicd-reviewer` | sonnet | General review at a cheaper tier, each explicitly deferring to the specialists above on their ground rather than giving a shallow opinion on it. |
| `doc-sync` | sonnet | Reports drift between code and the design document. Reports only — it fixes neither side, so it can't paper over a disagreement it should be surfacing. |

**Commands — make the correct shape the path of least resistance, rather
than catching the mistake afterward:**

| Command | Exists because |
|---|---|
| `/new-fact-type` | Refuses to proceed until the fact's divergence behaviour is declared. This guards the proposal's single most emphasised exit criterion: an over-receipt records 106 and raises an exception — it does *not* return an error to the device. |
| `/new-endpoint` | Forces command-vs-fact classification before anything is designed, when changing the answer is still free. |
| `/new-migration` | Produces an expand-then-contract-safe file with a duration class already stated, because these run unattended across a fleet. |
| `/review-tx` | The concurrency review reduced to one command, so it actually gets run. |
| `/phase-check` | Risk: "Proof of concept becomes the product by accretion." Reports scope creep and any shortcut missing its `docs/shortcuts.md` entry. |
| `/adr` | Decisions made *after* the proposal still need a record, checked against the 12 invariants first — an ADR that silently reverses one is worse than none. |

**Hooks — the only tooling that runs without being asked:**

| Hook | Exists because |
|---|---|
| `session-context` | Puts the open-shortcut count in front of every session, so accretion is visible without anyone remembering to look. |
| `guard-applied-migration` | An edited, already-applied migration is exactly how a fleet ends up on inconsistent schema with no way to tell which database has which version. |
| `guard-fact-path` | Mechanically encodes the three things most likely to be written wrong by instinct: a guard clause on a fact-path decrement, a `CHECK (on_hand >= 0)`, and a role-name comparison. Runs *after* the edit deliberately, so a pass can finish and self-correct. |
| `format-dotnet` | Style is enforcement, not etiquette — nobody should be relitigating brace placement in review. |

### Hooks (for reference — these run with no invocation needed)

`session-context` (SessionStart — injects branch/migration-count/
shortcut-count) · `guard-applied-migration` (PreToolUse) ·
`guard-fact-path` (PostToolUse) · `format-dotnet` (PostToolUse, async).
All four currently no-op safely: there is no `db/migrations/`, `.cs`, or
`.sql` file in this repository yet for any of them to act on.

## Before changing schema, an API contract, or an invariant

1. Update `docs/wms-design-document.md` first (and, for schema, land a
   migration in the same change once `db/migrations/` exists).
2. Propagate: `grep -rn "<phrase>" docs/ CLAUDE.md .claude/` — fix every
   stale copy, not just the one you meant to change.
3. Never edit a numbered finding (C/H/M) in `wms-architecture-review.md`
   — supersede it with a note at its point of use in the design document
   instead. The one exception: that file's "Consolidated Open Decisions"
   table is a live status tracker, not a finding narrative, and is kept
   current as each D-item actually resolves — see that table's own header
   note for why, and for the numbering mismatch against the proposal's
   final D-scheme.
