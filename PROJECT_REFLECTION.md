# Project Reflection — Technical Audit

**Audit date:** 2026-09-10, revised same day after the `.claude/` tooling
suite was ported in full. Scope: everything present in this repository and
this Claude Code session as of the revision. Every claim below is grounded
in an actual file, config value, or tool call from this session — where a
section's premise doesn't match reality (e.g. "sub-agents used"), that is
stated plainly rather than filled in to match the template. One factual
error from the first version of this document (an overcount of
`AskUserQuestion` calls) was caught and corrected while re-verifying for
this revision — see §4.

---

## 1. Project Scope & Overview

This repository is, as of this audit, a **documentation-and-design-only**
project for a Warehouse Management System (WMS) — a commercial,
single-tenant-per-customer product intended to direct real warehouse floor
work (receive → putaway → allocate → pick → pack → ship), not an internal
stock-tracking tool for one company. The distinction is load-bearing
throughout the design: a WMS tells a specific person to walk to a specific
bin and confirm the action by scan, keeping a defensible audit trail —
answering "what should happen next," not just "how many do we have."

**Verified current state of the repository** (fresh directory scan for
this revision):

| Fact | Status |
|---|---|
| Application code (`src/`, `web/`, `db/`, `.csproj`, `package.json`, `.sln`, `docker-compose*`) | **Still none exists.** Confirmed by a recursive file scan — zero matches. Everything below is documentation and Claude Code configuration only. |
| Git commits | **None.** `git log` still reports `fatal: your current branch 'master' does not have any commits yet`. |
| Git remote | **None configured.** |
| `docs/` | 5 files (`wms-project-proposal.md`, `wms-architecture-review.md`, `wms-design-document.md`, `shortcuts.md`, `claude-tooling-log.md`) plus `adr/0000-template.md`. |
| Root | `CLAUDE.md`, this file (`PROJECT_REFLECTION.md`). |
| `.claude/` | **Substantially larger than when this audit was first written.** 9 skills, 8 agents, 6 commands, 4 hooks (now wired into `settings.json`, previously empty) — see §2–§4 for what's actually been used versus merely installed. |

Primary objective, per `docs/wms-project-proposal.md` §2 (O1–O5): accurate
auditable real-time inventory; directing floor work rather than recording
it after the fact; eliminating wrong-item shipping errors via scan-verified
picking; surviving real warehouse network conditions (wifi dead spots,
short outages); and supporting operational growth (more sites, more
volume) without a re-platform. Delivery is phased 1A → 1B → 2 → 3 → 4, with
1A (the current phase, per `CLAUDE.md`) scoped to authentication, the
permission/role model, master data, receiving, and the inventory ledger —
explicitly **excluding** picking, packing, and shipping, which are Phase 1B.

---

## 2. Sub-Agents & Roles Used

**None were dispatched.** No call to the Agent/Task tool occurred at any
point in this session — every document in this repository, including this
one, was produced by direct, single-thread reasoning in the main
conversation, never by spawning a specialized persona to do it. This
conclusion hasn't changed since the first version of this audit, but the
premise underneath it has: `.claude/agents/` is **no longer empty**.

**Now installed, still never dispatched:** eight subagent definitions,
ported from the reference project after this audit was first written —

| Agent | Model | Tools | Job |
|---|---|---|---|
| `api-designer` | opus | Read, Grep, Glob | Classify and design an HTTP endpoint (command vs. fact) |
| `schema-guardian` | opus | Read, Grep, Glob | Review a migration before it's applied |
| `concurrency-reviewer` | opus | Read, Grep, Glob | Review a transaction/lock/lease change |
| `test-writer` | opus | Read, Write, Edit, Grep, Glob, Bash | Write the Testcontainers concurrency suite — the only one of the eight with write access |
| `backend-reviewer` | sonnet | Read, Grep, Glob | General `.NET` backend review, deferring to the three above for their specialist ground |
| `frontend-reviewer` | sonnet | Read, Grep, Glob | React/TypeScript review |
| `cicd-reviewer` | sonnet | Read, Grep, Glob | GitHub Actions / Docker deployment review |
| `doc-sync` | sonnet | Read, Grep, Glob | Reports drift between code and the design document; fixes neither |

All eight are now valid `subagent_type` values for the Agent tool. None has
been invoked — there is no backend, frontend, or migration code yet for
any of them to review.

One case is adjacent enough to name explicitly, unchanged from the first
version of this audit: at one point the user asked me to "act as a senior
system architect" and review the design. That was a **conversational
framing applied in the main thread** — it produced the two-pass
architecture review in `docs/wms-architecture-review.md` — not a
dispatched sub-agent with its own context or tool restrictions.

Also still true: the Claude Code session's own built-in available agent
types (`claude`, `claude-code-guide`, `Explore`, `general-purpose`, `Plan`,
`statusline-setup`) were listed as available by the harness throughout and
never invoked.

---

## 3. Skills & Domain Knowledge Applied

All nine skills the reference project defines are now present in
`.claude/skills/`. Exactly one has actually been invoked via the Skill
tool:

- **`system-design`** — invoked multiple times. Governs which of this
  project's document types a given change belongs in; its "supersede,
  never silently edit" rule for `wms-architecture-review.md`; and its
  propagation rule, mechanically enforced by running
  `grep -rn "<phrase>" docs/ CLAUDE.md .claude/` after every architecture
  change to catch stale copies.

**Installed, never invoked** (eight — added when the reference project's
`.claude/` tooling was ported in full, after this audit was first
written): `database-design` (naming/key/grain/normalization conventions),
`sql-migrations` (DbUp conventions, expand-then-contract, partitioning),
`dotnet-modules` (module boundaries, C# style), `dotnet-testing` (which of
the three backend test projects a test belongs in), `react-frontend`
(route-tree placement, the offline fact queue, i18n), `frontend-testing`
(Vitest vs. Playwright), `testcontainers-concurrency` (the mandatory
concurrency suite), and `wms-domain` (vocabulary, the aggregate table).

One thing worth correcting from the first version of this audit:
`database-design` was flagged there as declaring two **REQUIRED
BACKGROUND** dependencies — `sql-migrations` and `wms-domain` — that
didn't exist in this repository yet. Both now do, as of the full tooling
port. That gap is closed; the skill is complete as installed, just still
unused.

**Domain knowledge reflected in the design content itself** (not a
Skill-tool artifact — expertise embedded in the documents, carried in
wholesale from the reference design rather than independently derived
here): the append-only ledger with a derived balance projection; the
command/fact API classification rule for offline-tolerant systems; the
single-consumer, advisory-lock-serialized allocation worker; segregation
of duties enforced in the domain layer; per-role database workload
isolation; expand-then-contract migration discipline; and modular-monolith
module boundaries enforced by architecture tests.

---

## 4. Plugins, MCP Tools & Integrations

**Configured in `.claude/settings.json` → `enabledPlugins`** (installed via
`/plugin` earlier this session, confirmed by the session's own
install-confirmation output) — **unchanged since the first version of this
audit:**

| Plugin | Actually used in this repo? |
|---|---|
| `superpowers@claude-plugins-official` | **No.** No `superpowers:*` skill was invoked to produce any artifact here. |
| `context7@claude-plugins-official` | **No.** Its MCP tools (`query-docs`, `resolve-library-id`) were never called. |
| `frontend-design@claude-plugins-official` | **No.** No UI code exists yet to apply visual-design guidance to. |

Not installed here (deliberately — see §5): `csharp-lsp`,
`microsoft-docs`, `playwright`, which the reference project enables
instead for actual implementation work.

**`.claude/settings.json` → `hooks`: new since the first version of this
audit.** Previously `{}`; now wired to all four ported hook scripts:

| Hook | Event | Status |
|---|---|---|
| `session-context.sh` | SessionStart | Verified by manual smoke test (not yet by a real session start since being wired) to emit correct JSON context. |
| `guard-applied-migration.sh` | PreToolUse (Edit\|Write) | **Has actually fired** on every `Edit`/`Write` call made in this session since being wired (this document included) — its own path filter (`*db/migrations/*.sql`) correctly no-ops on every one of them, since no migration file exists yet. This is inferred from the deterministic shell logic and the file types actually touched, not from a hook execution log I can directly inspect. |
| `guard-fact-path.sh` | PostToolUse (Edit\|Write) | Same — fires on every edit, no-ops because no `.cs`/`.sql` file has been touched. |
| `format-dotnet.sh` | PostToolUse (Edit\|Write), async | Same — no-ops because no `.cs` file exists. |

Both interpreters the hooks depend on were confirmed present before wiring
them in: `python3` (3.10.11) and `dotnet` (10.0.303).

**Present in the broader session environment, not project-specific:** a
`claude.ai Figma` MCP server is available at the session/account level.
No `.mcp.json` or Figma reference exists anywhere in this repository — it
was never invoked here.

**Actually used, general-purpose Claude Code capabilities rather than
project-specific integrations:**

- **`WebSearch` / `WebFetch`** — used once each, to check the official
  Anthropic Claude Code plugin marketplace
  (`github.com/anthropics/claude-plugins-official`) for a database-design
  or schema-review plugin. No exact match exists; the closest entries
  (`cockroachdb`, `clickhouse-best-practices`) are tied to databases this
  project doesn't use.
- **`AskUserQuestion`** — used **four** times, each at a genuine fork:
  scoping the login/roles work; how far to take the reference-design
  work (the largest single decision in this repository's history);
  clarifying what "add database-design skill" actually meant; and how to
  proceed once marketplace research found no exact-fit plugin. *(Correction
  from the first version of this audit, which claimed six — a recount
  while revising this document found only four `AskUserQuestion` calls in
  the session transcript; the earlier figure was wrong and is fixed here
  rather than carried forward.)*
- **`Read` / `Write` / `Edit` / `Bash` / `Grep` / `Glob`** — the mechanism
  behind every document and configuration file in this repository,
  including all 25 files brought in during the tooling port. No
  `NotebookEdit`, browser automation, or scheduling/cron tool was used at
  any point.

**Worth noting from `.claude/settings.json`'s permission policy:**
`Bash(psql*)` and `Bash(rm -rf*)` are both explicitly **denied** —
consistent with the design's own stated philosophy that schema changes
travel through versioned SQL migration files, never ad hoc `psql`.

---

## 5. Technical Decisions & Rationale

Every stack/architecture decision below is stated in
`docs/wms-project-proposal.md` §7 or resolved/revised in
`docs/wms-architecture-review.md`; this section summarizes the *why*.

| Decision | Alternative considered | Why this, not that |
|---|---|---|
| **.NET/ASP.NET Core, modular monolith** | Microservices | Core invariants are transactional *across* modules (a pick confirmation touches inventory, the task, and the order atomically) — a distributed architecture turns that into a saga with compensating actions that aren't always physically possible. Module boundaries are enforced by architecture tests instead, keeping a future extraction cheap if ever justified. |
| **PostgreSQL** | A generic RDBMS choice | `SELECT ... FOR UPDATE SKIP LOCKED` for task dispatch, declarative partitioning for the movement ledger, partial/expression indexes, and genuine transactional integrity across the ledger-insert-plus-balance-update the whole correctness model rests on. |
| **Append-only ledger + derived balance** (grain: owner × item × location × lot × status) | A single mutable on-hand quantity column | Rated "severe — unrecoverable without rewrite" in the risk table. The grain was already revised once in this design's own review history after an earlier version reintroduced the hot-row contention the ledger existed to avoid. |
| **Single-consumer, advisory-locked allocation worker** | Synchronous inline allocation | Allocation isn't latency-critical — trading unused latency budget removes an entire class of overselling race condition, with no locking protocol needed at all. |
| **Command/fact API split** | A generic "queue and sync" offline model | A command may be refused because nothing physical has happened yet; a fact may never be refused, because the goods already moved and refusing would ask someone to undo something they cannot undo. |
| **EF Core (writes) + Dapper (hot reads)** | One ORM/query tool for everything | Domain mutations want change-tracking; pick lists and dashboards want hand-tuned SQL. Both share one connection/transaction. |
| **DbUp, raw versioned SQL migrations** | EF Core migrations | The schema needs declarative partitioning, partial indexes, and `SKIP LOCKED` semantics EF's migration DSL can't express, and these files get read during production incidents. |
| **One React/TypeScript app, two route trees** | Two separately built and deployed frontends | Reverses a decision made independently *within this repository* earlier in its history. A single layout serving both screen types is worse at each; a fully separate build/deploy pipeline isn't justified by what actually differs — the screen set, not the artifact. |
| **Badge/PIN operator auth, password staff auth, shared device sessions** | One uniform login method | A password prompt on a four-inch, gloved-hand screen gets worked around by credential sharing, destroying the audit trail. |
| **Docker Compose, not Kubernetes** | An orchestrator from day one | The realistic per-customer deployment is a single well-specified host, operable by one person. Deferred explicitly to "roughly ten to fifteen customers." |
| **GitHub Actions** | A different CI/CD platform | Native to the repository; Docker on hosted runners is required for the Testcontainers integration-test strategy. |

**The decision above the technology layer, specific to this repository:**
rather than continuing to build up this project's own
independently-designed single-warehouse MVP scope, the user pointed at a
separate, more mature design and, offered a choice between a narrow
cherry-pick and taking the whole thing, chose the full scope. Every
decision in the table above traces to that design's own reasoning, not to
independent architectural reasoning performed inside this repository.

**A second, separable decision, made later in the same session:** whether
to also bring in the `.claude/` tooling (skills, agents, commands, hooks)
that *enforces* that design during implementation, versus stopping at the
design documents themselves. These were kept explicitly separate — the
first pass adopted only the design and named the tooling as a deliberate
omission; a later, explicit request ("create all the skills, agents,
commands, hooks needed for the development of this project") completed
the port. Every file was read in full before installation, not
copied blindly — material for the four hooks in particular, since those
execute automatically on every future edit rather than waiting to be
invoked.

---

## 6. Build & Execution Timeline

There is no compile, test, or deploy step anywhere in this history — the
repository is not executable yet. "Build" below means the sequence in
which the design documents and tooling were actually constructed.

1. **Login and roles requested.** Scope was ambiguous (proposal-only, no
   design document existed). Resolved via `AskUserQuestion`; the user's
   own typed-in answer — "update the proposal doc" — set the scope, adding
   decision D10 to the original proposal.
2. **"Act as a senior system architect, check and redesign."** Produced
   this repository's first architecture review and its first full design
   document, plus the initial `CLAUDE.md`. Challenge solved mid-pass: a
   self-caught inconsistency in the inventory-ledger worked example was
   fixed before being presented as finished.
3. **Admin/Operator site split requested.** Added as decision D11 — two
   separately built frontends — resolved into the design document's §9.
4. **"Is the design as solid as it could be?"** A self-directed second
   review pass surfaced nine further gaps, including a defined `CANCELLED`
   order status with no flow that ever used it — resolved in the same turn.
5. **Standing instruction established:** keep `docs/claude-tooling-log.md`
   updated proactively — saved to persistent memory, followed from then on.
6. **Reference design discovered and brought in wholesale.** Read in full
   (proposal, both review passes, the 3,840-line design document — roughly
   5,200 lines) before acting. `AskUserQuestion` forced an explicit scope
   decision; the smaller MVP-scoped documents from steps 1–4 were archived
   and replaced, copied byte-for-byte to guarantee fidelity. Challenge
   solved: recognizing the reference's D15 ("one app, two route trees")
   directly reversed this repository's own D11 from step 3, and stating
   that supersession explicitly.
7. **`database-design` skill added** on its own, ahead of the rest of the
   tooling — then its two missing dependencies were flagged rather than
   silently ignored.
8. **Two rounds of clarification** established that "add database-design
   skill" actually meant an official Claude Code marketplace plugin, not a
   copied file. `WebSearch`/`WebFetch` confirmed no exact match exists;
   the user chose to hold off on both an alternative and a review, for now.
9. **Design-origin narrative removed from `CLAUDE.md`** on request. At the
   time, `docs/claude-tooling-log.md` was deliberately left untouched,
   since its purpose is tracking that kind of provenance — but see step 11.
10. **The rest of the `.claude/` tooling ported**, on explicit request:
    7 more skills, all 8 agents, all 6 commands, all 4 hooks — 25 files,
    each read in full before installation. `python3`/`dotnet` availability
    was checked first, since several hooks depend on them. The `hooks`
    block in `settings.json` (previously `{}`) was wired to match; a new
    `## Tooling` section was added to `CLAUDE.md`; `enabledPlugins` was
    deliberately left alone, since hand-editing it to list plugins that
    weren't actually installed via `/plugin` can't be verified to work.
11. **`docs/claude-tooling-log.md`'s own wording corrected**, on a
    follow-up request — the file whose purpose is tracking provenance was,
    on reflection, still using the framing the user had already asked
    removed from `CLAUDE.md`. Reworded throughout; direct quotes of the
    user's own past instructions were left verbatim rather than rewritten.
12. **This document, first written**, then **revised here** to reflect
    steps 10–11, correct the `AskUserQuestion` count (§4), and note the
    `database-design` dependency gap from step 7 had closed by step 10.

**One discrepancy, still unresolved:** `docs/archive-v1-mvp-scope/` —
created in step 6 to preserve the original MVP-scope documents — still
does not exist on disk, reconfirmed by a fresh listing for this revision.
`CLAUDE.md` still references it as if it does. Cause remains unknown; this
repository has no git history to consult. Still flagged rather than
silently reconstructed or silently left inaccurate.
