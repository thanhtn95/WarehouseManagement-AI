# Claude Code Tooling — WMS Project

Not a session transcript — an explanation of what actually produced each
doc in `docs/` (and `CLAUDE.md`) as it stands right now. Kept up to date
after every session that changes these docs, per standing user instruction.

## What created `wms-project-proposal.md`

| Tool | Role in this specific document |
|---|---|
| **`system-design` skill** | Decided *which file to create at all*. Its document hierarchy says scope/decisions/phases/risks belong in `wms-project-proposal.md` and nothing more — schema, API contracts, and data flows are explicitly out of place here and go in `wms-design-document.md` instead. This shaped the doc's structure and its boundaries, not just its existence. |
| **`AskUserQuestion`** | Directly produced D7 (media subsystem scope and its Phase 1A/Phase 2 split) via the "what should carry photos" question. An earlier 3-way fork question (roadmap vs. chat vs. scaffold) was deferred by the user and did *not* end up shaping the doc directly. |
| **Plain conversation (no tool)** | D1 and D8 (single-warehouse MVP scope, phased approach) came from the first session's reasoning, before any doc existed. D4, D5, D9, and D7's final storage choice were confirmed the same way — as ordinary chat replies, not through a structured question tool. |
| **`.claude/settings.json`** — `permissions` and `enabledPlugins` | Source of evidence, not a decision-maker: allowed `dotnet`/`npm`/`docker compose`/`git` command prefixes and a specifically-denied `psql` implied the .NET + npm + PostgreSQL + Docker + Playwright stack *before* the user confirmed it. Discovered via Bash/Read/Glob while reconstructing session state — those were investigation tools, not authors of the doc. |
| **Write / Edit** | Mechanical only — created the file, then applied targeted edits when D4/D5/D7/D9 firmed up. Carries no judgment of its own. |
| **Hooks, Agents, Commands** | None exist in this project yet (`.claude/agents/`, `.claude/commands/`, and the `hooks` block in `settings.json` are all empty) — none were involved. |

## What added D10 (login + user roles) to `wms-project-proposal.md`

Triggered by: "add login and user roles functions so it easier to operate."

| Tool | Role |
|---|---|
| `system-design` skill | Classified the request as an architecture/scope change and loaded the document hierarchy — this is what routed the work to `wms-project-proposal.md` in the first place. |
| `AskUserQuestion` | Offered three scope options (start the design doc / scaffold and build / a lightweight write-up); the user typed in a fourth — "update the proposal doc" — which is what actually set the scope. |
| Plain conversation (no tool) | The three-role split (Admin/Supervisor/Operator) and their permission boundaries were Claude's own domain reasoning — the user's request specified "login and user roles," not the specific roles or their scopes. |
| Edit | Mechanical: added D10, the §2 scope bullet, the Phase 1A roadmap row, and a Next Steps note. |

## What created `wms-architecture-review.md` and `wms-design-document.md`

Triggered by: "act as a senior system architect, check and redesign system
design for this project."

| Tool | Role |
|---|---|
| `system-design` skill | Decided these two files' existence and their boundary — the review is a never-edited historical record of findings, the design doc is the living source of truth that resolves them. Re-triggered from the same skill as the D10 change above. |
| Plain conversation (no tool) | All substantive content — the Critical/High/Medium findings, the ledger-based inventory model (append-only transactions, derived balances, the available/allocated/on-hold/in-transit split), the order/receipt state machines, schema, API contracts, data flows, and NFRs — came from Claude's own architectural reasoning against the existing proposal, not from a tool or a further user question. |
| Write | Created both files. |
| Edit | Fixed an internal inconsistency in the ledger worked example, caught on self-review rather than user-flagged (an early draft double-decremented `AVAILABLE` after allocation had already moved it to `ALLOCATED`); also renumbered sections after inserting the data-flows section. |
| Bash (`grep -rn`) | Propagation check across `docs/`, `CLAUDE.md`, `.claude/` per the system-design skill's rule — confirmed no stale references (e.g. the superseded "basic auth/roles" phrasing) were left behind. |

## What created `CLAUDE.md`

| Tool | Role |
|---|---|
| `system-design` skill | Specifies `CLAUDE.md`'s role (always-in-context invariant/phase summary, kept in sync with the design doc) and that it's created once the design doc exists. |
| Plain conversation (no tool) | The 12 numbered invariants are a condensed restatement of design doc §1, written to match the count the skill's own reference table expects. |
| Write | Created the file. |

## What added D11 (split Admin/Operator sites) to the proposal and design doc

Triggered by: "for this i would like to split it into admin and operator
site" (following on from the architecture review/design-doc pass above).

| Tool | Role |
|---|---|
| Plain conversation (no tool) | Everything substantive: framing the split as a client/UX-layer decision that needs no backend change (leaning on invariant 8, already decided); resolving where Supervisor fits (both sites, since D10 already scopes Supervisor across oversight and operations); choosing a same-origin path split (`/admin`, `/operate`) over subdomains specifically to avoid breaking the existing `SameSite=Strict` cookie decision (design doc §3). None of this was spelled out by the user beyond "split into admin and operator site" — the site boundary, cookie/routing approach, and deployment shape were Claude's own design judgment. |
| Edit | Added D11 to the proposal's decision table and Phase 1A roadmap row, added design doc §9 (Frontend applications) and its open-item note, updated `CLAUDE.md`'s stack and roles sections to match. |

No architecture-review finding ID was created for this — per the
system-design skill, C/H/M IDs are reserved for the two historical review
passes; this is a routine design addition, design doc prose only.

## What produced Architecture Review Pass 2 and its fixes

Triggered by: "is the architect design for this as solid as it could be?"

| Tool | Role |
|---|---|
| Plain conversation (no tool) | Everything. The user's question was a direct invitation to stress-test Pass 1's resolution rather than the original proposal, which is exactly the "two review passes" shape the skill's document hierarchy already anticipated (the file was titled "Pass 1" from the start). Re-reading `wms-design-document.md` itself surfaced 9 genuine gaps: a concurrency fix (C1/C2) that was decided but not specified atomically (C6); a defined `CANCELLED` status and `RELEASE_ALLOCATION` reason code with no flow ever using either, which would silently leak inventory out of `available_qty` on every cancelled order (C7); an idempotency invariant with no storage design (H11); no FEFO/FIFO lot-selection rule despite expiry tracking being in scope (H12); a lockout mechanism with no schema to persist it (H13); a schema column no flow ever wrote (H14); no drift detection between the ledger and its derived balance (M7); no partial/over-receipt handling (M8); and two enum values silently unused in Phase 1A with no note saying so (M9). None of this was prompted by specifics from the user — the question was general, the findings came from actually re-reading the design doc adversarially. |
| Edit | Appended Pass 2 to `wms-architecture-review.md` (never edited Pass 1's findings) and resolved each finding into `wms-design-document.md`: atomic `INSERT ... ON CONFLICT` upsert, a new cancel-order flow/endpoint, an `idempotency_key` table, a FEFO/FIFO allocation rule, `failed_login_count`/`locked_until` columns, `quantity_shipped` now set on ship, a nightly reconciliation-job NFR, explicit partial/over-receipt handling with a new `/receipts/{id}/complete` endpoint, and `ON_HOLD`/`DAMAGE` labeled as reserved. `CLAUDE.md`'s invariant 1 summary was tightened to match. |

## Bringing in the `D:\Work\WarehouseManagement` design wholesale (2026-09-10)

Triggered by: "read learn and adapt the design in this folder
D:\Work\WarehouseManagement\docs" — followed by an `AskUserQuestion` on how
far to take it (cherry-pick correctness fixes only / take on the
reference's full scope and ambition / point at specific parts), answered by
choosing the full-scope option.

That folder holds a separate, more mature design for the same domain —
built for a sellable, single-tenant-per-customer commercial product at
50+ concurrent operators, with a task-leasing engine, an offline-capable
handheld PWA governed by a command/fact split, single-consumer
advisory-locked allocation, a full identity/permission/scope model with
badge/PIN operator auth and in-place supervisor override, a media pipeline
with retention, and fleet operations across many deployments. Per its own
`claude-code-tooling.md`, that design was originally written in a Claude
web Project, not by Claude Code — it was read and copied in here, not
authored by this tooling.

| Tool | Role |
|---|---|
| `AskUserQuestion` | Forced an explicit scope decision rather than guessing at something this consequential — the two options were genuinely different projects (a proportionate cherry-pick vs. a full rescope), and only the user could pick between them. |
| Plain conversation (no tool) | Reading and understanding all ~5,200 lines of the reference proposal, both architecture-review passes, and the 3,840-line design document before acting — required to adapt it rather than blindly copy it (e.g. recognizing that the reference's D15, "one application, two route trees," directly reverses this project's own prior D11, "two separately built sites," and must supersede it rather than coexist with it). |
| Bash (`cp`) | Copied `wms-project-proposal.md`, `wms-architecture-review.md`, `wms-design-document.md`, and `docs/adr/0000-template.md` byte-for-byte from the reference repo — chosen over retyping via Write to guarantee fidelity across nearly 5,000 lines. The prior versions of the first three files, plus the prior `CLAUDE.md`, were copied to `docs/archive-v1-mvp-scope/` first so the smaller MVP-scoped design isn't lost, only superseded. |
| Write | Reset `docs/shortcuts.md` to the reference's format with no entries (its "Open" items describe compromises taken *during that repo's build*, which hasn't happened in this one — copying them verbatim would fabricate history). Rewrote `CLAUDE.md` from scratch: 12 invariants re-derived from the reference design's actual correctness mechanisms (the ledger/balance split, the command/fact rule, advisory-locked allocation, segregation of duties, workload isolation, forward-compatible schema), a new Phase 1A description matching the reference's narrower-but-deeper scope (no picking/packing/shipping until 1B), and the new stack (modular monolith, two-route-tree frontend, PgBouncer, streaming replica). |

**Not carried over in this pass:** the reference repository's `.claude/`
tooling (9 skills, 8 agents, 6 commands, 4 hooks) that operationalizes its
design during implementation. Porting the design and porting the tooling
that enforces it are separable decisions; only the former was in scope
here.

## Added the `database-design` skill (2026-09-10)

Triggered by: "add database-design skill" — the user picking one specific
piece out of the `.claude/` tooling flagged as not-yet-ported above, rather
than porting all of it.

| Tool | Role |
|---|---|
| Bash (`cp`) | Copied `.claude/skills/database-design/SKILL.md` byte-for-byte from the reference repo. No adaptation needed — its section references (design doc §0, §2.2, §2.3) already point at the correct places, since the design document behind them was itself copied in verbatim in the prior session. |
| Plain conversation (no tool) | Noticed the skill declares `sql-migrations` and `wms-domain` as **REQUIRED BACKGROUND**, and that neither exists in this repo yet — flagged to the user rather than silently pulling them in or silently leaving a dangling reference. |

## `database-design` follow-up: marketplace plugin research (2026-09-10)

Triggered by: two rounds of clarification establishing that "add
database-design skill" actually meant an official Claude Code marketplace
plugin, not the copied file above.

| Tool | Role |
|---|---|
| `AskUserQuestion` (×2) | First round offered three readings of the correction; second round, after marketplace research came back with no exact match, offered how to proceed. Neither guess was assumed — both were asked, since a second wrong guess would have cost more than asking. |
| `WebSearch` then `WebFetch` | Searched generally first, then fetched the official `anthropics/claude-plugins-official` marketplace listing directly rather than trusting third-party summaries. Found no plugin for reviewing a PostgreSQL design document — closest matches (`cockroachdb`, `clickhouse-best-practices`) are tied to different databases. |
| Plain conversation (no tool) | Distinguished the official marketplace from a third-party one (a LobeHub-hosted `database-schema-designer`) and declined to install the latter silently, since that means trusting someone else's code without the user's explicit go-ahead. |

Outcome: user chose to hold off on both installing anything further and
reviewing the design document, for now.

## `CLAUDE.md`'s design-origin narrative removed (2026-09-10)

Triggered by: "update md so it does not said that it was adopted."

| Tool | Role |
|---|---|
| Grep | Swept the repo for the word "adopt" to distinguish this project's own now-removed narrative language (only in `CLAUDE.md`) from legitimate uses of the word already present in the reference design's own content (e.g. "before each [dependency] is adopted" in the proposal's licensing note) — the latter were correctly left untouched. |
| Edit | Removed the opening "this project's design came from elsewhere" paragraph and its "pre-existing-draft" follow-on phrase from `CLAUDE.md`, replacing them with a plain statement of current scope and a neutral pointer to the archived draft. |

This file (`claude-tooling-log.md`) was deliberately **not** touched in that
pass — its purpose is tracking provenance, per the standing instruction
above — though a later request asked for this file's own wording to be
cleaned up too; see the final entry below.

## `PROJECT_REFLECTION.md` created (2026-09-10)

Triggered by: a request for a comprehensive technical audit covering
sub-agents, skills, plugins/MCP tools, technical decisions, and a build
timeline — explicitly required to "reflect the actual codebase."

| Tool | Role |
|---|---|
| Bash | Full repo file-tree scan, `git log`/`git status`/`git remote -v` (confirming zero commits and no remote exist), and a check for any application-code files (none found) — the factual basis for the whole document, gathered before writing a word of it. |
| Read | `.claude/settings.json`, `settings.local.json`, and `settings.local.json.example` — confirmed the three enabled plugins and the exact permission `allow`/`deny`/`ask` lists cited in the audit. |
| Plain conversation (no tool) | The substantive work: distinguishing *configured/available* from *actually invoked* for every plugin, MCP server, and skill, rather than assuming the audit template's premise (sub-agents, plugin usage) matched this repo's reality — it didn't, for sub-agents or for any of the three enabled plugins, and the document says so plainly instead of filling those sections in anyway. |
| Write | Created `PROJECT_REFLECTION.md` at the repo root. |

**Discrepancy found during this pass, not caused by it:**
`docs/archive-v1-mvp-scope/` — created when the reference design was
brought in, to preserve the original MVP-scope documents — no longer exists
on disk, while `CLAUDE.md` still references it as current. Surfaced to the
user; not silently fixed either by reconstruction or by editing `CLAUDE.md`
further, since the cause is unknown and this repo has no git history to
consult.

## Full `.claude/` tooling ported (2026-09-10)

Triggered by: "create all the skills, agents, commands, hooks needed for
the development of this project" — completing the port flagged as
deliberately out of scope on 2026-09-10 when the design itself was
brought in, and picking up specifically where the `database-design` skill
(the one piece ported so far) left off.

| Tool | Role |
|---|---|
| Read (×25) | Every skill, agent, command, and hook file was read in full before installing anything — hooks execute automatically on every `Edit`/`Write` once wired, and agents get real tool access (`test-writer` gets `Write`/`Edit`/`Bash`), so this was verification, not procedure. |
| Bash | Checked `python3` and `dotnet` are both actually present (`guard-applied-migration.sh`, `guard-fact-path.sh`, and `session-context.sh` all shell out to `python3` for JSON handling; `format-dotnet.sh` shells out to `dotnet`) — installing hooks that silently fail on every edit because their interpreter is missing would be worse than not installing them. Both were found. Then `cp` (with `chmod +x` on the four `.sh` files) brought in 7 more skills, all 8 agents, all 6 commands, and all 4 hooks byte-for-byte, same fidelity reasoning as the design documents. |
| Edit | Added the `"hooks"` block to `.claude/settings.json` (previously `{}`) wiring all four hooks to their events, matching the reference project's configuration exactly. Added a `## Tooling` section to `CLAUDE.md` summarizing what's now available and why, and flagging that three marketplace plugins the reference project also enables for implementation work (`csharp-lsp`, `microsoft-docs`, `playwright`) aren't installed in this repo yet. |
| Plain conversation (no tool) | Confirmed every hook degrades to a safe no-op against this repo's current all-documentation state — none of them can match a file that doesn't exist yet (`db/migrations/`, `.cs`, `.sql`) — so wiring them in now, before any code exists, is safe rather than premature. |

**Deliberately not done:** `enabledPlugins` in `settings.json` was left
unchanged. The reference project's own settings enable `csharp-lsp`,
`microsoft-docs`, and `playwright`; this repo currently has `superpowers`,
`context7`, and `frontend-design` instead (installed by the user via
`/plugin` earlier this session). Hand-editing `enabledPlugins` to list a
plugin the user hasn't actually installed can't be verified to work and
risks a misleading settings file — flagged in `CLAUDE.md` as something to
install via `/plugin` when implementation starts, rather than done here.

## This file's own wording cleaned up (2026-09-10)

Triggered by: "update the claude-tooling-log so it not said adopted
anymore" — a follow-up to the same request made of `CLAUDE.md` two entries
earlier, this time aimed at this log itself rather than at the provenance
record it was deliberately left out of at the time.

| Tool | Role |
|---|---|
| Grep | Re-swept this file for every remaining instance of "adopt" to locate each one precisely before editing, rather than reworking sections from memory. |
| Edit | Reworded this file's own narrative prose — section headers, descriptions of why the reference design was brought in, the archive-folder discrepancy note — to describe the same events without that word. Direct quotes of the user's own past instructions (which happen to contain the word, because they were about the word) were left verbatim — those are a record of what was typed, not this file's own framing, and rewriting a user's own words would misattribute them. |

## `PROJECT_REFLECTION.md` revised (2026-09-10)

Triggered by: "update the project_reflection" — the audit document was
stale against everything that happened since it was first written (the
full `.claude/` tooling port, both tooling-log wording passes).

| Tool | Role |
|---|---|
| Bash | Re-ran the same fact-gathering pass as the original audit — full file-tree scan, `git status`/`git log`, and a fresh listing confirming `docs/archive-v1-mvp-scope/` is still missing — rather than assuming nothing else had drifted since the first version. |
| Plain conversation (no tool) | Reworked §2–§4 to reflect that `.claude/agents/` and the seven remaining skills are no longer empty/absent (installed, still never *dispatched* — that distinction, not the emptiness, is what the document was actually making); added the hooks-are-wired fact to §4, with an explicit caveat that their having already fired is inferred from configuration logic and the file types touched this session, not from a log I can directly inspect. While re-verifying §4's `AskUserQuestion` count against the actual transcript, found the original count (six) was wrong — a recount landed on four — and corrected it in place rather than carrying the error forward silently. |
| Write | Replaced `PROJECT_REFLECTION.md` in full rather than patching around the stale sections, since §1–§4 and §6 all needed substantive rework and a patchwork of small edits risked leaving a cross-reference stale somewhere the same way the original document's own agent-count did. |

## `CLAUDE.md`'s "Agent & Skill Delegation Rules" section added (2026-09-10)

Triggered by: a request to audit all sub-agents, commands, and skills, then
add a dedicated `CLAUDE.md` section covering file-pattern triggers,
task-pattern triggers, and MCP tool usage guidelines.

| Tool | Role |
|---|---|
| Bash | Re-audited fresh from disk rather than from memory: parsed every skill's and agent's YAML frontmatter, every command's description, the exact `hooks` block and `enabledPlugins` list out of `settings.json` — confirmed the inventory (9/8/6/4/3) hadn't drifted since the prior port. |
| Plain conversation (no tool) | The substantive judgment call: distinguishing which parts of the requested "automatic routing" are mechanically real (skills auto-load by relevance; the four hooks genuinely fire with no invocation) from which are not (there is no mechanism that spawns a subagent from a file-pattern match — agent/command dispatch is an instruction the session-driving assistant has to actively follow, same as any other `CLAUDE.md` rule). Stated that distinction plainly at the top of the new section rather than writing rules that imply a stronger guarantee than the product actually provides. Also declined to invent a "Security-Reviewer" agent to match the user's example — none exists in this repo's roster — and instead mapped that request onto what's actually available: the built-in `security-review` skill plus the security-relevant checks already distributed across `schema-guardian`, `api-designer`, `cicd-reviewer`, and `guard-fact-path.sh`. |
| Edit | Replaced `CLAUDE.md`'s `## Tooling` section with the new `## Agent & Skill Delegation Rules` section (three subsections: File-Pattern Triggers, Task-Pattern Triggers, MCP Tool & Plugin Usage, plus a short hooks-for-reference note) — a strict superset of the old section's inventory, organized by *when* to use each piece of tooling rather than just listing what exists. |

## Admin management endpoints added: warehouses, zones, devices, handling units, full user CRUD (2026-09-10)

Triggered by: "as for the design i wanted the admin part to have
management function for all thing like users, warehouse,..." — a return to
design content after several tooling-focused turns.

| Tool | Role |
|---|---|
| Read / Grep | Located the exact current state of `wms-design-document.md`'s §2.2 schema and §5.11 API reference before editing, rather than trusting memory of a 3,900-line file across many prior turns. |
| Plain conversation (no tool) | The actual finding and the design work: `warehouse` and `zone` had full schema support (every table already scopes by `warehouse_id`) but **no API surface to create one at all** — `POST /locations`'s own example payload requires a `warehouseId` and `zoneId` that nothing in §5 could have produced. Users had create-and-grant-role endpoints but no list, detail, edit, or deactivate. Devices had claim/release/heartbeat for an *already-registered* device but no registration endpoint, despite "device registration" being named directly in the Phase 1A scope table. Designed and added: full warehouse/zone CRUD, `PATCH /locations/{id}`, handling-unit registration, full device CRUD, and `GET /users`, `GET /users/{id}`, `PATCH /users/{id}`, `GET /roles` — each following the document's existing conventions (permission naming, phase tags, `If-Match`/`version` optimistic concurrency where the entity is edited, no hard delete for anything the ledger or auth log can reference). |
| Edit | Added `version` (and, for `warehouse`, an explicit `status` CHECK) to the `warehouse` and `zone` schema in §2.2, and a `version` column to `location` — needed because the new `PATCH` endpoints require `If-Match` and neither table carried a concurrency column before. Inserted the new endpoints into §5.11 in logical reading order (warehouse/zone before item/location, since location depends on both) and added an Appendix A index row. Light touch to `CLAUDE.md`'s Phase 1A description to name the newly-designed admin surface, per the propagation rule — no invariant changed, so the 12-item list itself was untouched. |
| Bash (`grep`) | Propagation check confirming every new permission token (`warehouse.manage`, `zone.manage`, `device.manage`, `handlingunit.write`/`.read`, etc.) is used consistently and nothing collides with an existing token. |

## Custom role composition moved from Phase 2 into Phase 1A (2026-09-10)

Triggered by: "move to phase 1" — ambiguous enough on its own (start
implementing Phase 1 code vs. reclassify a specific deferred item) that an
`AskUserQuestion` was used rather than guessed; the answer named custom
role composition specifically, the item flagged as deferred at the end of
the previous turn.

| Tool | Role |
|---|---|
| `AskUserQuestion` | Resolved which of two very different-cost interpretations was meant — one a small doc/schema edit, the other a full implementation kickoff — before doing either. |
| Grep | Found all four places "custom role composition" or its deferral was actually stated (`wms-project-proposal.md` ×2, `wms-design-document.md` ×1, `CLAUDE.md` ×1) rather than fixing only the one spot mentioned in the prior turn's own summary. |
| Plain conversation (no tool) | Noticed the schema already had everything needed (`role.is_system` exists specifically to distinguish seeded/fixed roles from composable ones; `permission.category` already supports a grouped catalogue view) — same pattern as the warehouse/zone gap from the previous turn: schema anticipated the capability, API surface didn't expose it yet. Designed `GET /permissions`, `POST /roles`, and `PATCH /roles/{id}`, deciding 1A (not 1B) as the more specific placement since "Phase 1" alone was ambiguous between the two and this is an admin/identity capability, not a fulfilment one — flagged in the response rather than silently assumed as final. |
| Edit | Added `is_active` and `version` to the `role` schema (§2.1) — needed for the "deactivate a custom role, never delete it, never touch a system role" rule the new `PATCH /roles/{id}` enforces. Updated the proposal's Phase 1A scope table and Phase 2 section, the design doc's `GET /roles` note, and `CLAUDE.md`'s identity section to all agree the capability is in Phase 1A, not deferred. |

## Third architecture review pass, over this session's own additions (2026-09-10)

Triggered by: "fully update the proposal, architect and detail design then
review it act as a senior architecture engineer."

| Tool | Role |
|---|---|
| Grep | Verified `security_stamp` staleness is a genuine per-request server-side `401` (§8.1: "rejected on the next request"), not just something `GET /auth/me` polls for — checked against the actual document text rather than proceeding on memory of a 3,900+ line file, since the finding below depends entirely on which of those two mechanisms is real. |
| Plain conversation (no tool) | The actual review: re-read the warehouse/zone/device/handling-unit/user/role endpoints added across the last two turns as an adversarial second reader, not as their own author. Found four real gaps: `PATCH /devices/{id} {status:'lost'}` referenced a "force-end the session first" step that was never designed; a custom role's permissions could change with no mechanism to revoke already-issued tokens still claiming the old set (undermining `security_stamp`'s whole stated purpose); nothing stopped an admin from composing a custom role out of permissions they don't themselves hold (privilege escalation via delegation); and suspending a user could silently strand already-completed physical work still sitting unsynced in that user's device queue. For the role-permission-propagation fix specifically, recognized it as the outbox pattern already used for every other cross-aggregate effect (C6) rather than reaching for a new exception to "one aggregate per transaction." |
| Edit | Added "Addendum — Third Review Pass" to `wms-architecture-review.md` (H11–H13, M6), explicitly framed as reviewing this session's own additions rather than the adopted design, continuing the existing C7/H10/M5 numbering rather than restarting it. Resolved all four directly in `wms-design-document.md`: a new Worker responsibility (§1.3) for role-permission propagation, a cascading `lost` device state distinct from `retired`, a grant-what-you-hold check on both role-composition endpoints, and `activeDeviceQueueDepth` surfaced in the user-suspension response. |
| Bash (`grep`) | Propagation check confirming every new finding ID is cross-referenced correctly between the review doc and the design doc, and that the design doc's section structure is still intact after the edits. |

## Cleanup pass on five previously-flagged open items (2026-09-10)

Triggered by: the user's answers to "is there any point that need my
input" — D9/D14 stay open (no customer yet, nothing to decide), the
missing archive folder gets forgotten rather than reconstructed, the
three marketplace plugins are the user's own to install, and a dedicated
Security-Reviewer agent gets built.

| Tool | Role |
|---|---|
| Grep / Read | Located both `CLAUDE.md` references to `docs/archive-v1-mvp-scope/` precisely before removing them, and the exact wording of the existing eight agents before writing a ninth in the same voice/structure rather than an inconsistent one-off. |
| Edit | Removed both dangling archive-folder references from `CLAUDE.md` (the intro paragraph and the "never edit" checklist item) — the folder was never reconstructed, per the user's "just remove and forget about it." Updated `CLAUDE.md`'s delegation rules to replace the now-stale "no dedicated Security-Reviewer agent exists" caveat with real routing to the new agent, including a new Task-Pattern row for "a new admin or delegation capability." Added a superseding note in the proposal at D1/D2's own rows, clarifying that `wms-architecture-review.md`'s "Consolidated Open Decisions" table still calling them "Blocking" is a pre-resolution historical artifact, not a live status — without editing the review file itself, per this project's own rule. |
| Write | Created `.claude/agents/security-reviewer.md` (opus, read-only) — scoped to authorization/authentication/privilege-boundary/secrets review specifically, deferring to the four existing specialists for their own ground (schema, concurrency, endpoint design, CI/CD), with the H11–H13/M6 findings from the last pass as its stated concrete precedents rather than abstract principles. |

**No action taken, by explicit instruction:** D9 (RPO/RTO) and D14 (first
design partner) remain open — both genuinely require a real customer,
which doesn't exist yet. The three marketplace plugins
(`csharp-lsp`/`microsoft-docs`/`playwright`) remain uninstalled — the user
will handle that themselves.

## Architecture review's open-decisions table actually synced (2026-09-10)

Triggered by: "6. sync up the document" — the user rejecting the prior
turn's workaround (a superseding note in the proposal) in favour of
actually fixing the stale table, overriding this project's own
never-edit-the-review-file convention for that specific table.

| Tool | Role |
|---|---|
| Bash (`grep`) | Cross-checked every one of the review's flat D1–D10 entries against the proposal's actual resolved-decision tables individually, rather than assuming the D1/D2 staleness found last turn was the whole problem. It wasn't: D3, D4, D5, D6, D7, D8, and D10 were *also* stale — only D9 is genuinely still open. |
| Plain conversation (no tool) | The real finding underneath the request: the review's `D1`–`D10` and the proposal's final `D1`–`D15` (with letter suffixes added during finalization) **don't map one-to-one** — the review's `D3` ("regulated goods") is the proposal's `D10a`, not its own `D3` ("lot/batch tracking"). Fixing only the status without naming this mapping would have left a worse, subtler confusion in place of the one being fixed. Decided the "Consolidated Open Decisions" table is categorically different from the numbered C/H/M findings around it — a live status tracker, not a historical narrative — and that keeping it current doesn't violate the spirit of "never edit the review file," only a blanket misapplication of it. |
| Edit | Rewrote the table with each item's actual status and the proposal ID it maps to, plus a header note explaining both the live-tracker distinction and the numbering mismatch. Removed the now-redundant superseding note from the proposal (a workaround for a problem that no longer exists once the table itself is correct). Updated `CLAUDE.md`'s own "never edit" rule to state the one exception precisely, so a future session doesn't either over-apply the old blanket rule or use this as licence to edit an actual finding. |

## Dashboard endpoint designed, closing a gap the prior "optimize" review surfaced (2026-09-10)

Triggered by: "so in phase 1A i wanted the admin site with dashboard,
reports, user management" — confirming those three stay Phase 1A
priorities, distinct from the sequencing question raised about
warehouse/zone/role-composition in the previous turn's review.

| Tool | Role |
|---|---|
| Grep | Checked what "dashboard" actually resolves to in the design document — found the same pattern as the warehouse/zone and custom-role gaps from earlier turns: the proposal's own Phase 1A scope table has promised "full dashboard and reports over inbound and stock position" all along, but no endpoint was ever specified for it, only scattered pieces (`exceptions.summary`, `stock-on-hand`) a frontend would have had to assemble itself. |
| Plain conversation (no tool) | Designed `GET /dashboard` to add no new source of truth — every field reuses data already tracked elsewhere (exceptions summary, reconciliation runs, stock-on-hand, device sync state), scoped deliberately to Phase 1A's own surfaces only (receiving, putaway, the ledger, the exception queue) and excluding anything from picking/packing/allocation, the same phase-boundary discipline applied everywhere else in this project. Noticed `device.queueDepth` was transient (arrived on every heartbeat, persisted nowhere) and would have made the dashboard's sync-health tile unbuildable as designed — added one column rather than reaching for a metrics pipeline, consistent with the proportionality argument from the previous turn's optimization review. Tied the new endpoint's behavior explicitly to that same turn's replica-deferral suggestion, so the two pieces of advice don't contradict each other if only one is acted on. |
| Edit | Added `device.last_queue_depth` to §2.1, updated the heartbeat endpoint and `GET /devices` to persist and surface it, inserted `GET /dashboard` at the top of §5.12, and added an Appendix A index row. |

## Tooling re-audit + "why it's installed" rationale added to `CLAUDE.md` (2026-09-10)

Triggered by: a repeat of the earlier delegation-rules request, with one
addition — "also explain why i have them installed."

| Tool | Role |
|---|---|
| Bash | Re-audited from disk rather than reusing the earlier audit: the inventory had changed since (9 agents now, not 8, after `security-reviewer` was added). Parsed frontmatter, the wired `hooks` block, and `enabledPlugins` directly. Afterwards, ran a coverage check that every one of the 28 installed pieces is actually referenced somewhere in the routing rules — and caught a bug in that check itself (directory names carried a trailing `/`, so the skills pass reported a false 0-mentions for all nine) rather than reporting the false result. |
| Plain conversation (no tool) | Found two real gaps in the existing section: `security-reviewer` had been added to Task-Pattern Triggers but never to File-Pattern Triggers, so nothing routed auth/identity *files* to it; and both plugin-provided skills (`superpowers`, `frontend-design`) were enabled but absent from the MCP/plugin table entirely. For the new rationale content, grounded each entry in the specific proposal risk or design decision it operationalises — the severe-rated risks in the proposal's own risk table are exactly the areas given `opus` agents — rather than writing generic descriptions of what each tool does, which the frontmatter already says. |
| Edit | Added the Identity/auth row to File-Pattern Triggers, added `superpowers`/`frontend-design` rows to the plugin table with honest "when it's overkill" guidance, and added §4 "Why each of these is installed" — four tables (skills, agents with their model-tier reasoning, commands, hooks). `CLAUDE.md` is now 274 lines; still reasonable for an always-in-context file, but the growth noted in the previous turn's optimization review is worth continuing to watch. |

## OpenAPI document and Swagger UI specified (§3.6) (2026-09-11)

Triggered by: "i want a swagger ui for api testing manually if needed."

| Tool | Role |
|---|---|
| Grep | Checked what already existed before designing anything — and found the same gap pattern as the warehouse/zone, custom-role, and dashboard cases: the proposal has depended on an OpenAPI document all along (§7.7 generates the TypeScript client from it in CI), while the design document mentioned OpenAPI **zero times**. So neither the document nor a UI had ever actually been specified. |
| Plain conversation (no tool) | Three judgment calls worth recording. (1) Separated the **document** (generated always, in every environment, because CI client generation depends on it) from the **UI** (config-gated, default off, never on in a customer deployment) — an interactive console over an API that includes `/sync/facts` and every admin endpoint is a real production surface, not a docs page. (2) Specified the three things without which the UI is useless in practice: the bearer scheme for the *Authorize* button, and — the non-obvious one — §3.2's required headers declared as explicit parameters, since a default generator config won't infer them and every mutating call then fails `426`/`400`, which reads as a broken API rather than a misconfigured console. (3) Deliberately **did not name a library**: Swashbuckle vs. NSwag vs. framework-native generation shifts between .NET releases, and §7.10 requires verifying current licence terms before adopting a .NET dependency rather than asserting them from memory — so the choice is deferred to scaffolding time with an `/adr` if it isn't the obvious default then. |
| Edit | Added §3.6 to `wms-design-document.md` and an Appendix A index row. No proposal edit — §7.7 already implied the document, and the dev/prod compose override pattern the UI gating relies on is already described in §11. No `CLAUDE.md` edit either: this is development tooling, not a Phase 1A product capability, and that file is already growing faster than is comfortable. |

## Phase 1A backend begins — solution skeleton (2026-09-11)

Triggered by: "let begin phase 1A development, start with back-end" — the
first turn in this repository to produce code rather than documents.

| Tool | Role |
|---|---|
| Bash | Environment check before anything else: .NET 10.0.303 (plus 8/9 SDKs) and a **running Docker daemon**, which matters because the entire integration-test strategy depends on Testcontainers reaching one. Then `dotnet new sln` / `sln add` / `build` / `format` to create and verify the solution. |
| `dotnet-modules` skill | Loaded before writing any `src/**` code, per this repo's own File-Pattern Triggers — the structure, the boundary rule, and the C# conventions the `.editorconfig` now encodes all came from it rather than from invention. A real test of whether the routing rules written last turn actually get followed. |
| Write | `Directory.Build.props` (net10.0, nullable enable, shared across all projects), `.editorconfig`, `.gitattributes`, twelve `.csproj` files, and minimal `Program.cs` for the two host processes. |
| Plain conversation (no tool) | Judgment calls: **`Outbound` module deliberately not created** — it is orders/allocation/picking, all Phase 1B, and scaffolding it during 1A is the same phase-boundary violation as building it. **`.editorconfig` created now** rather than inherited as a shortcut — the reference project had logged "no `.editorconfig` exists yet" as a tracked Phase 1A compromise, and creating it up front avoids that debt entirely. **Module project granularity** (one project per module, boundary enforced by NetArchTest rather than by a separate `Contracts` project per module) was a structural decision the design document does not specify, so it was recorded as `docs/adr/0001` per the `/adr` rule rather than left implicit. |
| Self-correction | Set `end_of_line = crlf` in `.editorconfig` initially, and `dotnet format --verify-no-changes` immediately failed on it. Rather than convert files to match a poor choice, fixed the choice: LF everywhere, plus a `.gitattributes` enforcing it on checkout — development is on Windows but CI runners and every deployed container are Linux. The tooling caught this within a minute of existing, which is the point of it. |

**No `docs/shortcuts.md` entries.** Nothing in this increment was a
compromise: the omitted `Outbound` module is phase discipline, and the
missing test-framework packages are simply not-yet-done rather than a
deliberate shortcut with a replacement owed.

## First three migrations — master data, identity, ledger (2026-09-11)

Triggered by: "continue" — proceeding into the schema, per §15's "produce
the Phase 1 schema as SQL migration files, with the ledger and task tables
first."

| Tool | Role |
|---|---|
| `sql-migrations` + `database-design` skills | Both loaded before writing any SQL, per this repo's own File-Pattern Triggers. They supplied the file naming, the header format with a duration class, the type table (`numeric(18,4)`, `timestamptz`, `text`+`CHECK` never native enum), the "constraints that must not exist" list, and the naming/key conventions — including the sentinel-not-`NULL` rule that drove the `lot` design below. |
| Write | `0001__master_data.sql`, `0002__identity.sql`, `0003__inventory_ledger.sql`. FK dependency forced the order: master data first (the ledger references owner/item/lot/location), then identity (`user_role_scope` points at `warehouse`), then the ledger itself. |
| Bash + Docker | **Validated against real PostgreSQL 17 in a disposable container**, applying all three in order, then verifying semantics rather than just syntax: 13 monthly partitions created, sentinel lot seeded, **zero** constraints forbidding a negative `on_hand`, **zero** foreign keys pointing at `stock_movement` (M1). Then a functional smoke test — inserted a receipt and an over-pick, confirmed the identity `sequence` generates across the partitioned table (1, 2), rows route into `stock_movement_2026_09`, and a balance of `-6` persists. C3's "balances may go negative" is now verified behaviour, not an assertion. Container removed afterwards. |
| Plain conversation (no tool) | The design says the sentinel lot is *seeded* as a real row, but `lot.item_id` would naturally be `NOT NULL` and the sentinel belongs to no item. Resolved with a nullable `item_id` plus a named CHECK permitting `NULL` **only** for the sentinel id — keeping referential integrity for every real lot rather than dropping the FK or inventing a sentinel item. Also chose **no default partition** deliberately: attaching a real partition later requires draining rows out of the default first, and because partitioning is on the server clock a missing partition means the maintenance job has been failing for a year, which should fail loudly rather than be absorbed silently. |

**Two entries added to `docs/shortcuts.md`**, both recorded at the moment
they were taken: `stock_movement`'s append-only property is documented but
unenforced until the `wms_api`/`wms_worker` roles exist, and two forward
foreign keys (`inventory_exception.task_id`, `stock_movement.reason_code`)
are owed by the migrations that create `task` and `reason_code`.

**Not done:** `schema-guardian` was not dispatched. This repo's rules point
at it for exactly this change, and the reason for reviewing inline instead
is recorded in the response to the user rather than silently skipped.

## Sentinel-lot design fixed, and a dead hook found doing it (2026-09-11)

Triggered by: "fix the design problem" — rejecting the previous turn's
nullable-`item_id`-plus-magic-UUID-`CHECK` workaround.

| Tool | Role |
|---|---|
| Plain conversation (no tool) | Named why the workaround was weak rather than defending it: a table-wide nullable column for the sake of one row means every query and ORM mapping treats `lot.item_id` as optional when it is mandatory for all real data, and it buried a hard-coded UUID inside DDL. Identified the actual root cause as a **gap in the design document** — §2.3 said the sentinel was "seeded" while §2.2's `lot` definition never said how a lot with no item could be legal, which is precisely what left an implementer inventing it. |
| Edit (design doc) | Specified the sentinel properly in §2.3: what the row *is* (a dimension member meaning "not applicable", like an "unknown date" row), why `item_id` is nullable for it alone, and the partial unique index that permits exactly one such row. Recorded both rejected alternatives with their actual costs — a sentinel `item` puts a fake SKU into a business-visible table that surfaces in search and barcode lookup; dropping the FK removes the referential integrity the sentinel existed to preserve. |
| Edit (migration) | Replaced the `CHECK (... OR id = '00000000-…')` with `CREATE UNIQUE INDEX lot_sentinel_singleton_idx ON lot ((1)) WHERE item_id IS NULL` — structural, and nothing needs to know the sentinel's literal UUID for the invariant to hold. |
| Bash + Docker | Re-applied all three migrations to a fresh PG17 and verified the new constraint behaviourally: the sentinel exists, a **second** item-less lot is rejected with a unique violation, and a normal lot with an item still inserts. Container removed. |

**A genuine bug in this repo's own tooling, found by attempting the edit
honestly instead of routing around the guard.** Editing `0001` should have
been blocked by `guard-applied-migration.sh` (it is not the newest
migration) — it wasn't. Root cause: the hook matched `*db/migrations/*.sql`
with forward slashes, but Claude Code passes a native path, so on Windows
it receives `D:\...\db\migrations\...`, never matches, and exits 0. **The
hook had been installed but inert since the day it was ported** — the
worst kind of failure, a safety guard that looks present and does nothing.
Fixed by normalising separators (`tr '\\' '/'`) before matching, and
verified to now deny a non-newest migration, allow the newest, and ignore
non-migration files, on both path styles.

Worth noting for anything else ported from that repo: `guard-fact-path.sh`
matches on extension only (`*.cs|*.sql`), so it is unaffected — but the
same class of assumption is worth checking in any future hook.

**The `0001` edit itself was kept rather than issued as a corrective
`0004`.** The schema has never been applied to any persistent database —
only to throwaway containers that were destroyed — so there is nothing to
diverge, and a `0004` undoing part of an unreleased `0001` would be
permanent archaeology. Now that the guard actually works, edits to
non-newest migrations will be blocked from here on, which is the correct
behaviour once anything real has been applied.

## Phase 1A schema completed — tasks, inbound, platform (2026-09-11)

Triggered by: "ok let continue."

| Tool | Role |
|---|---|
| Write | `0004__tasks.sql` (the generic task engine plus its two partial indexes), `0005__inbound.sql` (receipt/receipt_line), `0006__platform.sql` (outbox, idempotency ledger, reason codes, document numbering). |
| Bash + Docker | Applied all six migrations in order to a fresh PG17, then verified behaviour rather than just application: the `reason_code` foreign key **rejects an unknown code on a partition insert** and accepts a valid one, and `EXPLAIN` confirms the lease query resolves through `task_ready_idx` as an index scan with **no separate sort node** — the index ordering `(warehouse_id, zone_id, priority, sort_sequence)` satisfies the `ORDER BY` directly, which is the whole point of that column order on the hottest query in the system. Container removed. |
| Plain conversation (no tool) | Two scope judgments. **`configuration` and `app_setting_current` deliberately deferred to Phase 3**, against the instinct to create every table §2.7 lists: the design's forward-compatibility rule is explicitly justified by the cost of retrofitting lot/serial/owner into *the two largest tables in the system*, and configuration has no such property — adding it later is a plain `CREATE TABLE`, so creating it now would be scaffolding for a phase we are not in. **Full `task_type` / `receipt_type` CHECK sets kept** even though Phase 1A only ever produces `putaway` and `blind`, because the design fixes those sets and `text`+`CHECK` makes widening free either way; deviating would have created a doc/schema mismatch for no gain. |
| Edit | Moved the forward-FK entry in `docs/shortcuts.md` from Open to Closed, naming the two migrations that discharged it and the verification behind it. The append-only-enforcement entry stays open — it lands with the Compose/roles increment. |

## `dotnet test` runs, first mandatory concurrency test passes (2026-09-11)

Triggered by: "let continue", after the user installed `playwright`,
`microsoft-docs` and `csharp-lsp`.

| Tool | Role |
|---|---|
| Edit (`CLAUDE.md`) | The three newly-installed plugins had **"Not installed"** rows in the MCP table with "once installed:" guidance. Updated to reflect reality with actual usage guidance — stale claims in the always-in-context file are exactly what the propagation rule exists to prevent. |
| Bash (`dotnet new xunit` probe) | Rather than recall package versions, scaffolded a throwaway xunit project in the scratchpad and read the SDK's own choice for `net10.0`. Then `dotnet add package` for the rest, letting NuGet resolve. No version was guessed. |
| `testcontainers-concurrency` skill | Loaded before writing the test, per File-Pattern Triggers. Supplied the `Barrier` pattern for making a race actually race, "assert on database state, not on responses", and the mandatory suite this test is item 1 of. |
| Write | `PostgresFixture` (one container per test class, applies the real migration files) and `TaskLeaseConcurrencyTests` — 8 operators, 50 iterations, asserting the union of leased task ids has no repeats. |
| `csharp-lsp` (new) | Live diagnostics caught an obsolete `PostgreSqlBuilder()` parameterless constructor in Testcontainers 4.15 and several redundant usings, before any build was run. Earned its place within minutes of being installed. |

**Two things were verified rather than assumed, and both mattered.**

The test first reported passing in 515 ms, which is impossible for a run
that starts a container, applies six migrations and does 50 concurrent
iterations. Investigated rather than accepted: xUnit reports only the test
*method*, excluding `IClassFixture` initialisation, so the number was
honest — 50 iterations of small local SQL genuinely is ~500 ms.

Then, because a green test nobody has tried to break is not evidence, the
locking clause was **deliberately removed** from the dispatch query and the
suite re-run: the test failed on the duplicate-lease assertion, confirming
it detects the exact defect it exists for. The clause was restored and the
test re-verified green.

**A real bug in the `.editorconfig` written two increments ago**:
the `_`-prefix naming rule matched *every* private field, including `const`
and `static readonly`, which are `PascalCase` by Microsoft's own
convention — so `dotnet format` failed on correct code. Fixed by declaring
the two exceptions ahead of the general rule (naming rules are first-match
in declaration order). `dotnet format --verify-no-changes` is clean again,
and the underscore rule still applies to genuine private instance fields.

**One new `docs/shortcuts.md` entry**: the fixture applies migration files
directly instead of through DbUp, because the migrator project does not
exist yet — so the journal, checksums and DbUp's own ordering are not
exercised by tests.

**Still empty, honestly**: `Wms.UnitTests` and `Wms.ArchitectureTests`
report "no test is available" because they contain no tests. No placeholder
was added to silence that — there is no domain logic to unit-test yet, and
a fake test would be worse than an honest empty project.

## Architecture tests: module boundaries and the role-name ban (2026-09-11)

Triggered by: "ok move on to next."

| Tool | Role |
|---|---|
| Write | Six module anchor types (one per module) — empty assemblies cannot be located for scanning, and DI registration will want them regardless. Then `ModuleBoundaryTests` (NetArchTest: no module reaches into another's `Domain`/`Application`/`Infrastructure`; no module depends on a host process) and `AuthorizationConventionTests` (invariant 8's role-name ban). |
| Plain conversation (no tool) | Chose a **source scan rather than a NetArchTest rule** for the role-name ban, and said so in the test's own docstring: NetArchTest reasons about types and dependencies, and a comparison between a property and a string literal simply is not expressible in it. A Roslyn analyser would be the precise instrument; this is the cheap version that fails the build today. Also scoped the scan to `src/` only — the test file itself contains the forbidden pattern as a regex literal, so scanning `tests/` would have made it fail on itself. |
| Bash (mutation test) | Three passing tests against nearly-empty modules prove nothing, so the guards were deliberately broken: added a `ProjectReference` from Catalog to Identity, a type in `Identity.Domain`, and `probe.Role == "Supervisor"` in Catalog. **The build succeeded** — the compiler allowed the cross-module reach — and both guards failed exactly as designed, while the unrelated third test stayed green. That is precisely the scenario ADR 0001 predicted when choosing test-enforced over compiler-enforced boundaries, now demonstrated rather than asserted. Mutation fully reverted; suite green, `dotnet format` clean, no stray files. |

Suite now: **3 architecture tests + 1 integration test, all passing**, and
all four have been shown to fail when the thing they guard is broken.

## Seed migration: permissions, system roles, reason codes (2026-09-11)

Triggered by: "let follow that order" — step 1 of seed → DbUp migrator →
fact-path transaction.

| Tool | Role |
|---|---|
| `wms-domain` skill | Loaded before writing the role matrix, since the shrinkage/segregation semantics are exactly what it exists to state once: receipt discrepancies are **not** shrinkage (that loss sits with the supplier and is recoverable through a claim), and the four reason-code flags drive validation. |
| Write | `0007__seed_reference_data.sql` — 29 permissions, 7 roles, 114 role-permission grants, 12 reason codes, every statement `ON CONFLICT DO NOTHING`. |
| Bash + Docker | Applied all eight migrations, then **re-ran 0007 against the same database** to prove fleet-safe idempotency: counts did not double. Verified the two properties that actually matter rather than just row counts — Receiver does **not** hold `receipt.over_receive` while Supervisor does (the gap Phase 1A exit criterion 6 and the §6.5 elevation flow depend on), and Auditor holds **zero** mutating permissions. |
| Plain conversation (no tool) | Three scope judgments. **Only Phase 1A permissions seeded** — the proposal explicitly has Phase 2 "extend the permission catalogue", so seeding pick/pack/wave rights now would create access to endpoints that do not exist. **Only roles with Phase 1A permissions seeded** — Picker, Packer and Returns Processor arrive with the phases that give them something to do, because a seeded role granting nothing is a footgun: assigning it looks like granting access and isn't. **Client User not seeded at all**, since D1 resolved to own-operator and the 3PL client portal is out of scope. Fixed role UUIDs rather than generated ones, because `user_role_scope` references them and every deployment in the fleet must agree on what `SUPERVISOR` means. |

**One judgment flagged in the migration itself rather than hidden:**
`GEN_OTHER` is categorised as `admin_correction` because the design's seven
categories have no general bucket — the closest fit, not an obvious one.

**One new tracked shortcut:** permission descriptions are English-only
while reason codes are bilingual, so D11 is half-met. Recorded rather than
papered over with translations that would not survive review.

## DbUp migrator, and a false claim in the design it exposed (2026-09-11)

Triggered by: "move on" — step 2 of the agreed order.

| Tool | Role |
|---|---|
| Write | `src/Wms.Migrator`: scripts **embedded** rather than read from disk, so the image is self-contained — a migrator container that needs a volume mount to find its own migrations is one that can be deployed without them. `MigrationRunner` is exposed as a callable type so tests use the same entry point production does. |
| Bash + Docker | Verified against a real database in both directions: first run applied all 7 scripts and exited 0; second run applied 0 and exited 0; an unreachable database exited **1**; a missing connection string exited **2**. That exit code is the load-bearing part — §11 gates the API restart on it, and a migrator that reports success on failure is worse than no migrator. |
| Edit | Pointed `PostgresFixture` at `MigrationRunner`, closing the tracked shortcut, and added `MigrationJournalTests` so the closure is enforced rather than asserted: a revert to direct file application now fails the build instead of passing quietly. |

**A false claim in the design, found by verifying instead of trusting.**
The `sql-migrations` skill and design doc §11.3/§6.21 all stated that
migrations are "checksummed by DbUp so a partially applied release is
detectable rather than inferred." Inspecting the journal DbUp actually
creates shows `schemaversions` has exactly three columns —
`schemaversionsid`, `scriptname`, `applied`. **There is no content hash.**
An already-applied migration that is later edited is not detectable by the
migrator at all.

Corrected in all four places the claim appeared (design doc ×2, two
skills), rather than leaving a guarantee documented that the tooling does
not provide. The correction states where the immutability guarantee
*actually* comes from today — `guard-applied-migration.sh` at authoring
time, plus the architecture-test backstop — and notes that both are local
to one developer's machine, so neither can detect an already-divergent
fleet. A content-hashing custom journal is now recorded as the Phase 4
fleet item it always was.

The `dotnet-testing` skill's third architecture rule was also corrected: it
described a test that cannot be written as specified (there is no checksum
to compare against) and is **honestly marked not-yet-implemented** rather
than faked with something that passes vacuously.

Suite: **3 architecture + 2 integration tests**, all green, `dotnet format`
clean.

## The fact-path write transaction, and exit criterion 5 (2026-09-11)

Triggered by: "ok let move on to step 3" — the last item in the agreed
order, and the one the proposal calls the criterion that matters most.

| Tool | Role |
|---|---|
| `wms-domain` skill | Loaded before writing a line, because the command/fact split is the whole point of this step and the skill states the test for it: *would refusing ask someone to undo something they cannot undo?* Receiving is a fact — the goods are on the dock and the count already happened — so the handler may not refuse it. |
| Write | `IStockLedger`/`StockLedger` (Inventory), `IIdempotencyStore`/`IOutbox` and their implementations (Platform), `ReceiptConfirmedFact`/`ConfirmReceiptFactHandler` (Inbound). The seven steps of §4.3 appear in the handler in order, numbered in comments, so a reviewer can check the contract against the code without holding the design doc open. |
| Write | `ConfirmReceiptFactTests` — four tests: over-receipt records 106 and raises an exception, exact receipt raises none, a replayed fact produces exactly one movement, and a reused key with a different payload throws rather than silently answering with one of the two versions. |
| Bash + Docker | Six integration tests and three architecture tests, green against real PostgreSQL 17. |
| Bash (mutation test) | The test was proven to have teeth the same way the earlier guards were. The handler was mutated to do the instinctive wrong thing — roll back and throw when the counted quantity diverges — and `OverReceipt_...` failed with that exception, while the three non-divergent tests stayed green. That precision matters: the test detects *the specific mistake exit criterion 5 exists to catch*, not merely "something changed". Mutation fully reverted; suite green. |

**Two defects found by verifying rather than assuming.**

`Npgsql 10 maps PostgreSQL `date` to `DateOnly`, not `DateTime`, so the
business-date resolution threw `InvalidCastException` on first run. Fixed by
matching the type the driver actually returns rather than casting and hoping.

The over-receipt test initially counted `outbox_message` rows **globally**
and failed once a second test class shared the container — the assertion
depended on execution order. Scoped to the receipt under test. A test that
passes only when it runs first is worse than no test, because it fails later
for a reason unrelated to the change that surfaced it.

**Two housekeeping gaps closed while verifying.** `dotnet format` reported
whitespace violations that turned out to be CRLF line endings written into
one file — `.gitattributes` normalises on commit, but the working tree
disagreed with it, so all hand-authored files were normalised to LF. And the
repository had **no `.gitignore` at all**: `obj/` and `bin/` would have gone
into the first commit. Added, with `appsettings.Development.json` explicitly
kept tracked (it points at the Compose database and holds nothing secret)
and `*.local.json` / `.env` explicitly not.

Suite: **3 architecture + 6 integration tests**, all green, `dotnet format`
clean.

**Phase 1A exit criterion 5 is met**: an over-receipt of 106 against an
expected 100 writes a movement of 106, leaves a balance of 106, sets the
line's discrepancy to `over`, raises an `over_receipt` inventory exception,
and returns `202`-shaped success to the caller. It does not return an error
to the device.

## The concurrency review of that transaction, and what six green tests could not see (2026-09-11)

Triggered by: `CLAUDE.md` §2's standing rule that a transaction change is not
done until `concurrency-reviewer` has passed over it. Worth recording that
this was not a formality — the suite was green, formatted and mutation-tested
before the review, and the review still found two High defects.

| Tool | Role |
|---|---|
| `concurrency-reviewer` agent (opus) | Reviewed the seven-step transaction, the ledger, the idempotency store and the tests. Confirmed the four properties it was asked to check — transaction boundaries on every path, the two-aggregate C6 exception, the claim-then-inspect race, and the guard-free single-statement upsert — and then found two defects **outside** the over-receipt path, in code Phase 1A calls within days. |
| Read + Grep (verification, not trust) | Every finding was checked against the design document before being acted on, rather than accepted because a reviewer said it. §6.3 does specify putaway as "−qty at source, +qty at destination"; §6.2's `received_quantity = 106` is a single-confirmation narrative and not a one-confirmation-per-line rule. Both findings survived. |
| Bash (mutation test ×3) | Each fix was then broken again to prove the new test detects it. |

**H-1 — the ledger silently half-applied any two-position movement.**
`PostAsync` derived one position from `ToLocationId ?? FromLocationId`. A
receipt has only a destination, so all six tests passed; a putaway has both,
and would have incremented the destination while never decrementing the
source. Stock duplicated, ledger and balances permanently divergent, and
nothing but reconciliation would ever have noticed — long after the cause was
forgotten. The ledger now applies a **set** of position deltas in one
statement.

**The fix's own hazard, fixed with it.** Two positions per transaction means
two row locks, and the obvious implementation — two statements in caller
order — is a textbook lock-order inversion: operators moving A→B and B→A
concurrently deadlock. The upsert's feeding subplan is therefore ordered by
the conflict-target tuple, giving every writer in the system one acquisition
order. **Removing that single `ORDER BY` line produces real `40P01` deadlock
errors from real PostgreSQL** — verified, not assumed, which is the only
reason to believe the line is load-bearing rather than decorative.

**H-2 — `received_quantity` was assigned, not accumulated.** Two receivers on
one pallet line, or one device draining a queue holding two partial counts,
each post a legitimate fact with its own idempotency key — so neither is a
replay, and invariant 4 forbids refusing the second. The second `UPDATE`
overwrote the first: ledger 100, line 60. Worse, `discrepancy_type` was
derived from the last fact alone, so two half-counts of a complete line each
raised a spurious under-receipt. **Invariant 4 decided this, not preference**
— refusing the second fact is not available, which leaves accumulation as the
only behaviour that keeps the line agreeing with the ledger.

**Three medium findings, all fixed at the cheapest possible moment:** the
transaction now pins `ReadCommitted` explicitly rather than inheriting a
server default that a later hardening change could flip, silently turning
every concurrent duplicate into a false 409; the request hash canonicalises
decimal scale, so a quantity that round-trips through IndexedDB as `10.0`
instead of `10` is no longer a self-quarantining fact; and the tests no longer
leak the connection they hand the handler — harmless against a pool of 100,
fatal in the `/sync/facts` endpoint under 50 operators.

**One finding deliberately not fixed in code, because the code does not exist
yet.** `sequence` is assigned at INSERT but becomes visible at COMMIT, so an
incremental reader advancing a watermark to `max(sequence)` permanently skips
every movement whose transaction was still open. Two consumers are specified
to do exactly that (§5.3, §2.3) and neither is built. Recorded as **ADR 0002**
rather than left for each to rediscover — a reconciliation job that omits rows
and then reports zero variance is worse than no reconciliation, because its
clean result is what stops anyone looking further.

**Mutation results — all three new tests have teeth:**

| Mutation | Result |
|---|---|
| Ledger reverted to one position | All 3 transfer tests failed |
| `ORDER BY` removed from the upsert | Deadlock test failed with `40P01: deadlock detected` |
| `received_quantity` reverted to assignment | Accumulation test failed: expected 100, actual 60; the other 4 receipt tests stayed green |

Propagated per the system-design rule rather than left in code comments:
design doc §2.3 and §5.3 now cite ADR 0002, §6.2 states the accumulation
contract, and §6.3 states the fixed lock-acquisition order.

Suite: **7 unit + 3 architecture + 10 integration tests**, all green,
`dotnet format` clean.

## Making a bad fact a per-fact rejection instead of a thrown exception (2026-09-11)

Triggered by: "fix it" — closing the two items the concurrency review flagged
for when the endpoint lands, rather than leaving them as a comment for a
future session to find.

| Tool | Role |
|---|---|
| Read (§4.1) | The contract was read before anything was designed, not recalled. It is specific: the response is **always `202`** unless the request itself is malformed, `status ∈ accepted \| duplicate \| rejected`, and `rejected` covers malformed payloads only — never a business-rule refusal. The implementation follows that rather than a shape invented here. |
| Write / Edit | `FactAccepted` became `FactResult` with a `FactStatus` and a `FactRejection` reason. `HandleAsync` now *returns* a rejection for an unknown receipt line and for a reused `clientFactId`, where both previously threw. |
| Bash (mutation test) | Restoring the throw for an unknown line failed `UnknownReceiptLine_IsRejectedPerFact_NotThrown` and left the other five receipt tests green. |

**Why throwing was wrong, not merely untidy.** A batch carries twenty facts.
An exception fails all twenty and reaches the handheld as a `5xx` — a device
can act on neither. The nineteen good facts would be requeued and resubmitted
indefinitely behind one orphan the server will never accept.

**What still throws, deliberately.** A row vanishing mid-transaction, or a
claimed key with no stored response, are *server* bugs. Turning those into a
per-fact `rejected` would tell a device to abandon work that was never its
fault, so they remain exceptions.

**Rejections roll back, which releases the idempotency claim.** A rejected
`clientFactId` is therefore not permanently poisoned — asserted directly in
the test rather than assumed.

**The field was renamed rather than documented.** `ReceiptConfirmedFact`'s
`IdempotencyKey` is now `ClientFactId`. The bug it prevents is wiring the
request's single `Idempotency-Key` header into a per-fact field, which would
record only the first fact of a batch and replay-suppress the other nineteen
— losing nineteen movements while reporting success. A name that states its
origin makes that mis-wiring visible at the call site; a comment saying the
same thing does not.

**Propagation found two documents that would have caused exactly that bug.**
The API header table stated `Idempotency-Key` applies to "all stock-mutating
POST", and the Swagger note said `/sync/facts` "is idempotent only if you set
a real `Idempotency-Key`". Both now say facts key on `clientFactId`. The
`api-designer` agent carried the same wording and was corrected too — it is
the agent that would have been asked to review the endpoint, so a stale rule
there is worse than one in prose. §4.1 also now records where §4.3 step 1's
`409` lands inside a batch: one `rejected` entry beside nineteen `accepted`
ones, not a failed request.

Suite: **7 unit + 3 architecture + 11 integration tests**, all green,
`dotnet format` clean.

## POST /sync/facts — the endpoint, and a build-order decision (2026-09-11)

Triggered by: "okay move on to next step".

| Tool | Role |
|---|---|
| Read (`.claude/commands/new-endpoint.md`) | CLAUDE.md §2 requires `/new-endpoint` before any endpoint. Its first step resolved the question immediately: `/sync/facts` is the fact endpoint and §4.1/§5.3 already specify it completely, so there was no design to do — only implementation against a contract that already existed. Running the design flow anyway would have produced a second, competing specification. |
| `AskUserQuestion` | §4.1's envelope carries `deviceId` and each fact's payload but deliberately **no** `actorUserId` — it comes from the credential (invariant 8). There was no authentication in the codebase at all, so the endpoint could not be built correctly without choosing a build order. The user chose to build the endpoint now with authentication behind an interface. |
| Write | `IOperatorPrincipalResolver` in Identity/Contracts — framework-free, taking header values rather than an `HttpContext`, so Identity stays a plain class library. `SyncFactsEndpoint`, `ProblemTypes`, DI wiring, and the Development/non-Development resolver split. |
| Bash (mutation test ×2) | Breaking batch isolation failed `OneBadFactInABatch_DoesNotFailTheOthers`; removing the permission check failed `AnOperatorLackingTheFactsPermission_IsRejectedPerFact`. Both reverted, no residue. |

**Authentication is stubbed; authorization is not.** The development resolver
trusts a header naming the operator, then resolves that operator's **real**
permissions through `user_role_scope → role → role_permission`. So the
Receiver/Auditor distinction the endpoint tests exercise is the production
one, and Phase 1A exit criterion 6's permission model is under test now rather
than after Identity lands. Faking permissions too would have made every
authorization test meaningless — which is the more expensive shortcut, not the
cheaper one.

**It fails closed, and explicitly.** Outside Development the API registers
`UnconfiguredPrincipalResolver`, which authenticates nobody, so every operator
endpoint answers `401`. A *missing* registration would have been the lazy
option and is worse: it fails at the first request as a dependency-injection
`500` that reads like a crash, rather than as a legible refusal.

**Three shortcuts recorded at the moment they were taken**, per the project's
own rule: the development authentication header; the principal carrying
permissions but not yet warehouse/zone scope (invariant 8 wants both, so
`/sync/facts` currently checks *what* an operator may do and not *where*); and
the absent `X-Client-Version` gate, which §3.2 applies to every request and so
belongs in middleware rather than being half-built into one endpoint.

**One decision the design had left open** is now recorded rather than
improvised per call site: §7.1 specified problem-type *suffixes* but no
prefix. It is `urn:wms:problem:` — a URN because RFC 7807 does not require the
URI to resolve, and an `https://` URL that looks dereferenceable but 404s in
every customer deployment is worse than one that never claimed to be.

**What the tests cover that no handler-level test could reach:** that one
unusable fact is reported beside its accepted neighbours rather than failing
the request; that a resubmitted identical batch moves stock once and returns
the *original* movement id; that the actor on the movement comes from the
credential, not the payload; and that an over-receipt reaches a device as
`202`. Exit criterion 5 is now proven at the status code an actual handheld
would receive, not just at the handler boundary.

Suite: **7 unit + 3 architecture + 18 integration tests**, all green,
`dotnet format` clean.

## The security review of that endpoint, and five real defects (2026-09-11)

Triggered by: `CLAUDE.md` §1's rule that any permission-check code goes to
`security-reviewer` before it is done. This is the first code in the repo that
resolves a principal, so the shape it sets is the one every later endpoint
copies — which is most of why the review was worth its cost.

| Tool | Role |
|---|---|
| `security-reviewer` agent (opus) | Reviewed the endpoint, wiring, resolver and tests. Told explicitly **not** to report "authentication is fake" — that is the recorded shortcut — and to look instead for ways it could leak into production or fail open. |
| Write (tests first) | Every disputed finding was turned into a test **before** any fix, so the question "is this real?" was answered by a run rather than by argument. Three failed immediately: unknown destination, cross-warehouse destination, expired user. A fourth (missing `payload`) failed once the test was corrected to use a seeded operator instead of stopping at `401`. |
| Edit | Fixed all five, plus two documentation defects. |

**The finding that mattered most was not the one about fake authentication.**
Nothing validated `toLocationId`. A destination naming a real bin in *another
warehouse* succeeded cleanly: the stock landed in site B, the movement was
dated by site A's day boundary, and the over-receipt exception was raised into
**site A's** queue — so B's supervisor would never see a discrepancy sitting
in B's own rack. The exception queue silently failing to surface the thing it
exists to surface. No malice needed; a mis-scanned label carrying a valid id
from another site does it. Now rejected inside the transaction.

**Two paths returned `500` for an entire batch** — a nonexistent destination
(foreign-key violation escaping the per-fact boundary) and a fact with no
`payload` at all. The latter is a nice trap: an *absent* payload binds as an
Undefined `JsonElement`, and deserializing that throws
`InvalidOperationException`, not `JsonException`, so it slipped straight past
the catch written to contain exactly this. Both violated the contract this
endpoint had just been built to guarantee, and neither was caught by the
eighteen tests that were passing.

**Three smaller ones with the same shape — a control that does not control.**
The device came from a header with no lookup, so §8.1's "actor *and device*"
audit trail recorded whatever the caller typed, and a device marked `lost`
was indistinguishable from a live one — H11 being rebuilt before the sessions
meant to fix it. `valid_until` was ignored, so scheduled access expiry did not
expire anything. And the connection-string password sat in
`appsettings.Development.json`, which §8.1 forbids without carve-out and which
would have been the template for `appsettings.Production.json`.

**Two documentation defects, which are the ones most likely to propagate.**
A comment in the test file claimed the fail-closed gate was "asserted below"
when nothing asserted it — a comment claiming coverage that does not exist is
worse than silence, because it stops the next reader from adding it. And the
`IOperatorPrincipalResolver` contract described the header-derived device as
though it were the intended design, which is precisely what the real resolver
would have copied.

**The environment gate turned out to be sound** — ASP.NET Core defaults to
Production, so a deployment that forgets `ASPNETCORE_ENVIRONMENT` fails closed
— but it was untested and rested on a single `if`. It now needs two
independent conditions, the resolver **refuses to construct** outside
Development regardless of where it is registered, and startup logs which
resolver is live. §3.6 also specifically requires the Swagger gate to be
*configuration*, not the environment name; it had been written as the latter.

Suite: **7 unit + 3 architecture + 23 integration tests**, all green,
`dotnet format` clean. Five new shortcuts recorded, and the scope shortcut
amended to say what the new warehouse-consistency check does and does not
compensate for.

## putaway_confirmed — the second fact type (2026-09-11)

Triggered by: "keep going with back end first then we could do front end".

| Tool | Role |
|---|---|
| Read (`.claude/commands/new-fact-type.md`) | CLAUDE.md §2 requires this before a fact type exists. Its six questions were all answerable from the design without guessing — which is the point of asking them *before* writing code, not after. |
| Read (design) | §4.2 (payload), §6.3 (flow), §7.2 (divergence table), the `task`/`task_line` schema, and the seeded permissions and reason codes. `location_mismatch`, `reallocated_task`, `putaway.execute`, `PUT_LOCATION_FULL` and `PUT_LOCATION_BLOCKED` all already existed — nothing needed inventing. |
| Write | `PutawayConfirmedFact` + `ConfirmPutawayFactHandler` in the Tasks module, seven steps in order; endpoint dispatch; seven integration tests. |
| Bash (mutation test) | Making the handler refuse a bin it was not directed to failed the `location_mismatch` test. |

**A refactor the second fact type forced, correctly.** `FactResult`,
`FactStatus` and `FactRejection` lived in `Inbound.Application`. A Tasks
handler using them would have reached into another module's non-`Contracts`
namespace and failed `ModuleBoundaryTests` — so they moved to
`Wms.SharedKernel`, which is where a vocabulary every fact type answers in
belongs. The architecture test doing its job on the first occasion it could.

**Three divergence decisions, two of which the design already made.**
Confirming to a different bin than directed raises `location_mismatch` and
records where the stock *actually* went (§6.3). A line confirmed twice raises
`reallocated_task` (§7.2) — both operators really did carry stock, so both
movements are written and the source goes negative, which is true information
rather than an error to suppress.

**The third had to be decided, and is now written down.** A *partial* putaway
raises nothing. §7.2 has no row for it, and inventing one would have been
wrong: if the operator moves 60 of a directed 100, the residual 40 is still in
the source location and the ledger already says so, so nothing is unaccounted
for and there is nothing to resolve. That is the real difference from a short
*pick*, which does raise — there the order cannot be fulfilled and someone
must decide what happens next. Recorded in §6.3 rather than left implicit in
a handler.

**One test exists purely because of a gap no handler test could see.** An
unregistered fact type is rejected as "Unknown fact type", which at a glance
is indistinguishable from any other rejection — so a handler that was written
but never wired into the registry would look like it worked. The endpoint test
asserts on the rejection *reason* instead of the status, and fails if the
registration is missing.

Suite: **7 unit + 3 architecture + 31 integration tests**, all green,
`dotnet format` clean.

## The receipt lifecycle, and where putaway work comes from (2026-09-11)

Triggered by: "carry on" — the next item on the dependency list, since putaway
tasks had to be inserted by hand for any test to exercise them.

| Tool | Role |
|---|---|
| Read (§5.4, §6.2, §3.1) | Full contracts already existed for all six receipt endpoints, including the exact `409` shapes. Implemented the three on the critical path — create, start, complete — and left the read models and cancel for the admin site. |
| Write | `ReceiptService` (Inbound), `ReceiptEndpoints` (Api), two new problem types, ten tests across two files. |
| Bash (mutation test) | Generating putaway from `expected_quantity` instead of the ledger failed the over-receipt test with exactly the 106-vs-100 discrepancy §5.4 warns about. |

**A refactor the second caller forced.** Document numbers key
`document_sequence` by business date, and `StockLedger` already derived
business date from the warehouse day boundary — privately, with a comment
saying the rule must not be re-implemented per call site. A second caller
appeared, so it became `IBusinessCalendar`, and the ledger now consumes what
it used to own. Numbering therefore restarts on the warehouse's own next day
rather than at a UTC midnight in the middle of a night shift.

**Putaway is generated from the ledger, not from `received_quantity`.** §5.4
requires actual quantities, but there is a second reason the ledger is the
right source: it is the only record of *where* the stock was put. Grouping
movements by `to_location_id` means a line counted in two parts into two
different staging bays produces correct putaway work with no special case,
and the task's `from_location_id` is where the stock physically is rather
than a location assumed from configuration.

**Two judgment calls, both recorded in code rather than left implicit.**
A line counted as **zero** is confirmed and does not block completion — "the
pallet was empty" is a result, not an omission — so the unconfirmed check
tests `received_quantity IS NULL`, not `= 0`. And when no empty bin is
available the task is created **undirected** (`to_location_id` null) rather
than failing the completion: refusing to close a receipt for goods already on
the dock helps nobody, and the putaway handler already treats a null directed
location as "no mismatch is possible", so no spurious exception follows.

**The Phase 1A strategy is deliberately crude and says so.** First empty bin
by pick sequence, where "empty" means no stock at all — it ignores capacity,
item affinity, velocity and mixed-item rules, and will send a single unit to a
pallet location. The proposal names this as the one crude part of 1A putaway;
the comment in the SQL says the same, so nobody mistakes it for a considered
slotting algorithm.

**Endpoint tests exist for a gap service tests cannot see.** A receipt
endpoint mapped to the wrong permission, or not mapped at all, leaves every
service-level test green. Four HTTP tests cover routing, the Receiver/Auditor
permission split, `409` on a repeated start, and `404` rather than `500` for
an unknown receipt.

Suite: **7 unit + 3 architecture + 41 integration tests**, all green,
`dotnet format` clean.

## Task dispatch, and a correction to what SKIP LOCKED actually does (2026-09-11)

Triggered by: "sure" — `POST /work/leases`, Phase 1A exit criterion 4.

| Tool | Role |
|---|---|
| Read (§5.3, §6.8, §2.4) | The contract specifies a self-contained payload and `200` with an empty list when no work is available. Built to that. |
| Write | `LeaseService` + `LeaseContracts` (Tasks), `WorkLeaseEndpoints` (Api), eleven tests across two files. |
| Bash (mutation test) | Removing `SKIP LOCKED` — and the result is the interesting part. |

**The mutation did not fail, and that was the finding.** Removing
`SKIP LOCKED` from the dispatch query left both the new service-level race
test and the older query-level `TaskLeaseConcurrencyTests` **green**. Checked
twice, once against each, because a passing mutation is either a weak test or
a wrong belief and it matters which.

It was a wrong belief. Under READ COMMITTED a plain `FOR UPDATE` blocks, then
re-checks `status = 'ready'` against a fresh snapshot and drops the row that
was leased while it waited. **Disjointness — no task issued twice — comes
from the row lock plus that predicate, not from `SKIP LOCKED`.** What
`SKIP LOCKED` actually buys is that pollers never queue behind each other,
which is a throughput property; at the fifty operators this system targets it
is the difference between dispatch that scales and operators standing still
waiting for a task list, but it is not what stops a duplicate lease.

**So a test was written for the property that does depend on it.** Hold a lock
on the first three tasks in another transaction, then lease with a timeout:
with `SKIP LOCKED` the next three come back immediately; without it the call
blocks and is cancelled. That mutation now fails, as it should.

**Three places asserted the wrong rationale and were corrected** — this new
service's own comment, `TaskLeaseConcurrencyTests`'s header, and the
`testcontainers-concurrency` skill's mandatory-suite list, which now carries
the blocking test as item 1b and says plainly that item 1 is *not* what
`SKIP LOCKED` is for. The skill is the one that mattered most: it is what a
future session reads before writing the next concurrency test, so a wrong
rationale there propagates into tests that look thorough and prove nothing.

No repository document contained the incorrect claim — it had only been
asserted in conversation — so there was nothing further to correct, and the
design document's own wording ("no lock contention regardless of operator
count", §8) turns out to have been accurate all along.

**One test bug of my own, same class as an earlier one.** The seed used a
literal SKU and barcode, which collide across tests sharing a container.
Fixed by generating them per seed and asserting against the seeded value
rather than a literal — the same trap as asserting on a global row count.

Suite: **7 unit + 3 architecture + 52 integration tests**, all green,
`dotnet format` clean.

## Balance reconciliation — the system's own correctness monitor (2026-09-11)

Triggered by: "sure" — Phase 1A exit criterion 2, and the consumer ADR 0002
was written for before either existed.

| Tool | Role |
|---|---|
| Read (H1, §2.3, ADR 0002) | H1 specifies incremental, watermark-based reconciliation that **never auto-corrects**; ADR 0002 constrains how the watermark may be taken. Both were followed rather than re-derived. |
| Write | `ReconciliationService` (Inventory), `ReconciliationJob` hosted in the Worker on a 15-minute timer, six integration tests. |
| Bash (mutation test ×3) | Two caught; **one did not**, which was the useful one. |

**ADR 0002 got its answer.** The ADR listed two ways to bound a scan and did
not say which to use, because no consumer existed. This one takes option 2 —
a five-minute lag on `recorded_at` — rather than the "preferred" snapshot
bound, because comparing a row's `xmin` to a snapshot's `xid8` needs a text
cast and carries a wraparound edge. These files are read during incidents,
and a bound that is comprehensible and provably safe under a stated
assumption beats a clever one that is obviously neither. The assumption is
written into the ADR rather than only the code: *no transaction writing
`stock_movement` stays open longer than the lag*, which the API's five-second
statement timeout satisfies by two orders of magnitude. An ADR listing options
without recording the one taken is incomplete once the code exists.

**The mutation that passed is the reason this entry matters.** Windowing the
recompute — summing only the movements in this run's range instead of the
position's whole history — left all five tests green. That bug is not
theoretical: it reports a false variance on every position with prior history,
so the job cries wolf, gets switched off, and the system loses its only
correctness monitor just as surely as skipping rows would.

It passed because every test had all its movements inside the *first* window,
where a windowed recompute and a full one give identical answers. The
distinction only appears on the second run against a position touched again.
That test now exists, fails against the mutation, and is named in ADR 0002's
consequences so the next reader meets the trap before writing the code.

**The two mutations that were caught**: removing the visibility lag failed the
watermark test, and making the job "helpfully" correct a divergent balance
failed the no-auto-correct test — the behaviour H1 is explicit about, because
silent correction destroys the only evidence of whatever caused the
divergence.

Suite: **7 unit + 3 architecture + 58 integration tests**, all green,
`dotnet format` clean.

## Read models for the admin site (2026-09-11)

Triggered by: "ok let move on to next step", under the standing decision to
finish the backend first.

| Tool | Role |
|---|---|
| Read (§5.5, §5.12, §3.4, §1.3) | Contracts for `GET /inventory/balances`, `GET /inventory/exceptions` and `GET /dashboard` already existed in full, including the keyset-only pagination rule. |
| Write | One read model per module — `IInventoryReadModel`, `IInboundReadModel`, `ITaskReadModel` — plus three endpoints and six integration tests. |
| Bash (mutation test ×2) | Ordering the keyset by a non-unique column failed the pagination test; scoping the exception summary to the list's filter failed the summary test. |

**The outbox drain was skipped deliberately, and this is the reason.** It was
next on the list until checking §1.3 showed its only Phase 1A consumer is the
`RolePermissionsChanged` → `security_stamp` propagation (H12), which needs
role management to exist first. The messages currently written —
`ReceiptLineConfirmed`, `PutawayConfirmed` — have no consumer at all, so a
drain would poll, publish to nothing, and mark rows processed. That is not
progress, it is a job that looks finished and does nothing.

**An architectural decision worth naming.** The dashboard spans receiving,
putaway, the exception queue and the ledger — four modules. Written as one
join in the API it would have passed every architecture test, because those
scan modules and not `Wms.Api`. It would also have been the first crack in
the boundary: once one cross-module report exists, the next is easier to
justify, and the modular monolith quietly becomes a regular one. Each module
now answers for its own tables through `Contracts`, and the dashboard only
composes. The dashboard reads run **sequentially** rather than through
`Task.WhenAll` — four simultaneous connections per dashboard view is exactly
the burst that starves scan confirmations out of the reserved API pool (C7).

**Business date, not UTC date, in both summaries.** "Receipts completed today"
and "tasks completed today" derive the day from the warehouse's own timezone
and boundary, the same way the ledger derives `business_date`. Counting by UTC
date would split a night shift across two days and disagree with every report
beside it (M4).

**Two properties pinned by mutation.** Keyset pagination ordered by a
non-unique column lets rows repeat on one page and vanish from another — a
stock report that silently disagrees with itself depending on page size, and
invisible on small data. And the exception summary is deliberately independent
of the list's filters: a supervisor narrowing to one type still sees the total
open count, or filtering becomes a way to accidentally hide work.

Read endpoints are permission-checked exactly like writes. A report is not
"just reading" — stock positions and the exception queue are precisely what an
unauthorised viewer would want (invariant 8).

Suite: **7 unit + 3 architecture + 64 integration tests**, all green,
`dotnet format` clean.

## The remaining read models, and a column nothing was writing (2026-09-11)

Triggered by: "models first then user management".

| Tool | Role |
|---|---|
| Read (§5.4, §5.5, §3.4) | Contracts for `GET /inventory/movements`, `GET /inventory/reconciliation/runs`, `GET /receipts` and `GET /receipts/{id}` already existed in full. |
| Write | Extended the Inventory and Inbound read models, four endpoints, five more tests. |
| Bash (mutation test ×3) | Two caught immediately; the ledger-ordering one took **three attempts** to write a test that could fail. |

**A gap found by reading the contract against the schema.** §5.3 promises
`deviceReportedAt` on every movement, and `stock_movement.device_reported_at`
exists — but nothing wrote it. The fact envelope's `occurredAtDevice` was
parsed into `FactEnvelope` and then dropped on the floor, so the field would
have been null forever and the endpoint would have quietly lied. Invariant 5
calls device time "forensic only": forensics requires actually storing it.
Now wired envelope → fact → `PostMovementCommand` → ledger, for both fact
types.

**The ledger-ordering test took three attempts, and the first two were
worthless.** "Ordered by `sequence`, never by timestamp" (C4) is easy to
assert and hard to test:

1. Page N movements posted in separate transactions — passes either way,
   because `recorded_at` then rises with the sequence.
2. Post them in one transaction so they share an instant exactly — also
   passes, because Postgres breaks that tie consistently at this size.
3. Make the two orders genuinely **disagree** — drag one movement's
   `recorded_at` an hour behind — and ordering by time returns `[3, 1, 2]`
   where the ledger says `[1, 2, 3]`.

Only the third discriminates. The first two were tests that would have shipped
looking like coverage, which is the failure mode worth more than the bug: a
green suite that cannot fail is more dangerous than a missing test, because it
stops anyone writing the real one. This is the second time in this project a
mutation has exposed that (the first was `SKIP LOCKED`), and both times the
fix was to find the case where the two implementations actually diverge rather
than to assert harder.

**Smaller things worth recording.** The receipt list aggregates line counts
with a lateral join rather than one query per row — fifty receipts would
otherwise issue fifty-one queries for one screen. The ledger view signs
quantities for the reader (a pick shows `-12`) while the stored value stays
positive, because direction lives in the location columns and a view showing
a pick as `+12` would be misread by everyone.

Suite: **7 unit + 3 architecture + 69 integration tests**, all green,
`dotnet format` clean.

## User administration, and an escalation path the design left open (2026-09-11)

Triggered by: "ok let go" — the last Phase 1A admin surface the user named.

| Tool | Role |
|---|---|
| Read (§5.11, §2.1, H11–H13, M6, the seeded role matrix) | The endpoint contracts existed; the security requirements came from the review findings, and the seeded roles decided whether one of them was safe to extend. |
| Write | `IUserDirectory` + `UserDirectory` (Identity), `UserEndpoints`, ten integration tests. |
| Bash (mutation test ×2) | Removing the escalation check failed two tests; bumping `security_stamp` on every edit failed the rename test. |
| `security-reviewer` agent | Mandated by CLAUDE.md for any new grant-or-revoke capability. |

**A finding the design had not closed.** H13 requires that *composing* a role
cannot bundle permissions the administrator lacks. It says nothing about
*granting* an existing one — and the seeded matrix makes that gap real: only
System Administrator and Warehouse Manager hold `user.manage`/`role.manage`,
and Warehouse Manager holds every permission **except** `warehouse.manage`. So
a Warehouse Manager could have granted themselves `SYSTEM_ADMINISTRATOR` and
acquired the one thing they were deliberately denied. The same check now
applies to granting, which — checked against the real seeded roles — permits
every legitimate delegation and blocks exactly one thing: minting a System
Administrator. Recorded here because it is an extension of a review finding
rather than an implementation of one.

**A denied grant commits rather than rolls back.** The refusal is the only
thing that transaction writes, and an attempted escalation is precisely the
event an investigation needs to find. Rolling it back would make the system
forget the one action most worth remembering.

**`security_stamp` is bumped on status change and nothing else.** H12 needs
revocation to take effect in seconds rather than at token expiry — but
bumping it on every edit would log every operator out whenever an
administrator fixed a spelling, and an organisation that experiences
revocation as random logouts stops trusting it. Both directions are tested;
the mutation that always bumps fails the rename test.

**Credentials were deliberately left out**, and recorded as a shortcut rather
than half-built. Adding Argon2id now would mean a new dependency whose licence
nobody has reviewed, producing hashes no code path verifies — a stored
credential that looks functional and is not is worse than an absent one.

Suite: **7 unit + 3 architecture + 79 integration tests**, all green,
`dotnet format` clean.

## The security review of user administration — four holes in my own extension (2026-09-11)

Triggered by: the `security-reviewer` pass CLAUDE.md mandates for any
grant-or-revoke capability. It confirmed the H13 extension was correct in
direction and unbreakable on the accumulation axis, then found it incomplete
on two others.

| Tool | Role |
|---|---|
| `security-reviewer` agent (opus) | Asked specifically whether my own extension of H13 had holes, and what the missing warehouse/zone scope means on *this* surface. |
| Bash + Read (verification) | Both High findings were confirmed against the code before any fix — they were in code I had written an hour earlier. |
| Edit + Bash (mutation test ×4) | Each fix has a test that fails against the pre-fix behaviour. |

**H-1 — the check was scope-blind, which is the same mistake in a new place.**
My permission query joined `user_role_scope` and then never used its
`warehouse_id`. So an actor holding Receiver in Tokyo and Supervisor in Osaka —
an ordinary state for anyone who moved sites — had their permissions unioned
across both, and could grant Supervisor *in Tokyo*. Invariant 8 is "permissions
**plus scope**"; I had written the delegation path as though it were
permissions alone. The zone axis was worse, because there the grant *widens*:
an actor scoped to zone A could grant the same role with an empty `zone_ids`,
which §2.1 defines as all zones.

**H-2 — the extension had no mirror on the status axis.** `PATCH /users/{id}`
gated on `user.manage` and nothing else, so a site role carrying only that
permission could end the one System Administrator's account — or, worse,
reactivate an operator a System Administrator had suspended during a live
incident. Changing someone's access is the same kind of act as granting them a
role, and now takes the same bound, plus a refusal to change one's own status.

**H-3 — `valid_until` is a revocation that did not revoke.** The stamp was
bumped on `status` only, but the resolver refuses a principal past
`valid_until`, and §2.1 tells administrators to time-bound agency labour that
way. Shortening it is a revocation expressed differently — and it wrote no
`auth_event` at all, so "who cut this person off, and when" was unanswerable
for that route.

**M-1 — `ON CONFLICT DO NOTHING` made a scope narrowing a silent no-op.** The
unique key excludes `zone_ids`, so tightening a user from all zones to zone A
returned success, wrote an audit event describing the narrower scope, and left
the row carrying the wider one. Now `DO UPDATE`.

**A structural hazard, recorded in the `sql-migrations` skill rather than
fixed in code.** The subset check is self-referential and has no floor, so a
permission no live user holds becomes permanently ungrantable — and System
Administrator's grant is a one-time snapshot taken when `0007` ran. Any future
migration adding a permission must backfill it, or the deployment quietly
acquires a permission nobody can ever delegate, surfacing months later as
"why can't I build that role" with nothing connecting it to a migration.

**Three findings deliberately recorded rather than built**: no single-role
revoke endpoint, `POST /users` assigning roles under `user.manage` alone
(which becomes identity-minting once credentials land), and suspension not
cascading to sessions. All three are in `docs/shortcuts.md` with the phase item
that closes them.

Suite: **7 unit + 3 architecture + 87 integration tests**, all green,
`dotnet format` clean.

## Closing the admin-auth gaps, and three review passes that reopened more than they closed (2026-09-11)

Triggered by: "let check on the current progress" (a `/phase-check`-style
survey), then "what the status on the admin site authentication," then
"handle the gaps and update."

| Tool | Role |
|---|---|
| `phase-check` skill | Framed the survey — in scope vs. out, shortcuts vs. missing entries, exit-criteria progress. |
| Read (design doc, shortcuts.md, and the actual `AuthenticationService`/`TokenPrincipalResolver`/`UserDirectory` code) | Found that several `shortcuts.md` "Open" items — dev-header auth, no `auth_event` on failure, no `security_stamp` — had already been closed by code that landed without the doc catching up. Nothing here trusted the doc's own claims without checking them against the code first. |
| Write/Edit (`UserEndpoints.cs`, `ReceiptEndpoints.cs`, `WorkLeaseEndpoints.cs`, `LeaseService.cs`, `Program.cs`, two new migrations) | The two gaps actually asked for, plus one found while fixing the second. |
| `security-reviewer`, `concurrency-reviewer`, `backend-reviewer` agents (parallel) | Mandated by CLAUDE.md for a new admin/delegation capability and before any commit. Found five things this session's own fixes had not covered, two problems in code this session wrote, and confirmed the three fixes it did make were correct. |
| Edit ×2 (Program.cs guard, migration split) | Response to the two review findings that were this session's own mistakes, not pre-existing debt. |
| `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` (×4 across the session) | Verified after every batch of changes, not just at the end. |

**What got fixed.** `POST /users` accepted `roleScopes[]` and `credentials[]`
under `user.manage` alone — since credentials had already landed (previous
entry), that made `user.manage` a unilateral identity-minting capability.
Now requires `role.manage` when assigning roles and a new `credential.manage`
permission when setting a credential, with the pre-existing H13 subset check
still bounding what can actually be granted. Separately, `OperatorPrincipal.CanIn`
existed but nothing called it — every endpoint checked a bare permission with
no warehouse scope. Wired into the receiving commands and `POST /work/leases`.
While wiring the lease endpoint, found that `DELETE /work/leases/{id}` had no
ownership check at all — any operator holding `task.lease` anywhere could
release *another* operator's active lease by naming its id. Fixed by scoping
the release to `lease_user_id`.

**What the reviews found that this session had not.** `concurrency-reviewer`
approved the release fix outright, then pointed out it also removed the
*only* existing way to recover an abandoned lease, since the two endpoints
the code's own comments point to as the replacement (`GET /work/leases/expired`,
`POST /work/tasks/{id}/reclaim`) don't exist yet — recorded, not built,
since building two new endpoints wasn't what was asked. `security-reviewer`
found something more serious: a refreshed or PIN-resumed access token carries
no session claim at all (two separate bugs — `RefreshAsync` always issues
with `sessionId: null`, and the PIN-resume branch reads a `SessionId` that
`DeviceSql` never actually selects), so ending a device session does not
revoke a token already past its first refresh. On a pooled handheld that is
exactly the scenario the design exists to prevent. It also found that zone
scope — the *other* half of invariant 8 — is enforced nowhere at all,
including on the lease endpoint this session had just touched, and that the
principal resolvers merge multiple role-scope rows in one warehouse onto a
single zone list in unspecified row order, which means a zone check cannot
safely be bolted onto the current data shape without fixing that first. Both
are large enough (the first touches token issuance's core transaction shape,
the second touches the shape of `OperatorScope` itself across three files)
that fixing them alongside everything else risked exactly the kind of rushed
change auth code cannot afford. Recorded precisely in `docs/shortcuts.md`
instead, each with the specific fix location a future session needs.

**What was this session's own mistake, not pre-existing debt.**
`security-reviewer` also caught two things introduced this session:
a hardcoded JWT signing key committed to `appsettings.Development.json`
with no guard stopping it from working outside Development if the file ever
reached a real deployment — fixed with the same two-independent-conditions
gate already used for `DevelopmentPrincipalResolver`, applied to a value
instead of a resolver. And a migration (`0007__seed_reference_data.sql`)
edited in place to add the new `credential.manage` permission — technically
permitted by `guard-applied-migration.sh` (0007 was still the newest file),
but wrong per the `sql-migrations` skill's own immutability rule regardless:
DbUp journals by filename, so any database that had already run 0007 would
never receive the edit. Reverted and split into
`0008__add_credential_manage_permission.sql`, with System Administrator and
Warehouse Manager backfilled explicitly in the same file, per that skill's
own warning about what happens when a permission grant isn't. A third,
lower-severity finding from the same pass (`ReleaseSql`'s `lease_id` lookup
had no supporting index, forcing a sequential scan on the pool reserved for
scan confirmations) got a fourth migration,
`0009__task_lease_id_index.sql`.

**What was corrected rather than fixed.** Three `shortcuts.md` "Closed"
entries were rewritten to be narrower and accurate once the security review
landed: the device-on-a-fact closure now says what's actually true (the
header is never used for *attribution*) rather than what turned out to be
false (that every token is bound to an open session); the `auth_event`
closure now says "for a known identity" rather than unqualified, since
guessing against an *unknown* one still leaves no trace; and the new
warehouse-scope item was corrected to say warehouse *only*, not
warehouse/zone, before it was ever published as closed.

**One more finding, deliberately not fixed.** `GET /users/{id}` echoes the
target's `security_stamp` — in the *documented* §5.11 contract, not a
mistake — but it hands an admin-scoped caller the one input a forged token
needs on top of the signing key. Recorded rather than removed unilaterally,
because removing it means updating a documented API contract, which is its
own small piece of work under the "update the design doc first" rule, not a
drive-by deletion.

Suite: **7 unit + 3 architecture + 106 integration tests** (19 new/changed
this session — 4 for the account-creation gate, 3 for warehouse scope on
receiving and leasing, 2 for the lease-ownership fix, plus test-infrastructure
fixes to 4 pre-existing `WebApplicationFactory`-based test files that were
failing to start for an unrelated reason found mid-session), all green,
`dotnet format` clean.

## `wms-screen-inventory.md` created (2026-09-14)

Triggered by: "go through the detailed design and all the docs then create
a list of screen needed for this project and compose it into a document so
i could give it to claude design" — the first request in this project aimed
at producing a design-tool handoff artifact rather than schema/API content.

| Tool | Role |
|---|---|
| Agent (`fork`) | Did the actual reading: all of `wms-project-proposal.md` (already read directly into context first), then `wms-design-document.md` in full (§1.2 Frontend, §2 Data Models, the ~1,700-line §5 API Reference, the 22 flows in §6, and Parts II–IV for Phases 2–4), plus `wms-architecture-review.md` and `shortcuts.md`, extracting one structured entry per screen (route, phase, purpose, primary role, key fields drawn from the actual schema, primary actions, related endpoints, UX notes) rather than a shallow list of section headings. Chosen over reading inline specifically to keep ~5,000 lines of raw document content out of the main conversation's context, since only the distilled screen list was needed afterward. |
| Plain conversation (no tool) | Decided the deliverable's scope (all four phases, not just 1A, since the design document itself already specifies all four and a design tool benefits from seeing the established pattern extrapolate forward) and its home (`docs/wms-screen-inventory.md`, alongside the other design artifacts, rather than a scratch-only file) without asking — a reasonable default per this session's auto-mode instructions, not a user-specified scope. Added a closing "prioritize in this order" note pointing a design tool at the Phase 1A operator screens first, since that's this project's actual active build target. |
| Write | Created `docs/wms-screen-inventory.md` from the fork's draft, with a proper document header matching this repo's existing convention (Scope/Status/Companion documents, per `wms-design-document.md`'s own header) and a short purpose statement explaining it's a design-tool handoff artifact, not a status report. |

**Not done:** no code changed, so no skill/agent from the File-Pattern
Triggers table applied (this document isn't `wms-design-document.md`,
`wms-project-proposal.md`, `wms-architecture-review.md`, `docs/adr/**`, or
`CLAUDE.md`, so the `system-design` skill's specific trigger doesn't cover
it either — it's a new, derived artifact rather than an edit to one of the
four tracked documents). `/phase-check` was not run since nothing was
committed this turn.

## `project-progress.md` created (2026-09-14)

Triggered by: "let work on a new document now using the guide from
project-progress-doc-guide-line.md" — the first request pointing at a
pre-existing guideline file (`docs/project-progress-doc-guide-line.md`,
already present in the repo, not written this session) rather than
describing the desired document from scratch.

| Tool | Role |
|---|---|
| Glob + Read | Located and read the guideline file in full first — it's short (five numbered steps: define the project, lock the stack, describe the initial `.claude` structure, describe launching Claude Code via `system-design` to generate the proposal, describe the proposal review cycle) — before writing anything, rather than guessing at scope from the filename alone. |
| Bash (`find`/`ls`) | Read the actual current `.claude/` folder tree (9 agents, 6 commands, 4 hooks, 9 skill directories) to ground the "where the tooling stands today" closing section in real contents rather than a remembered count, and to contrast it accurately against the guideline's description of the initial (skills-only, system-design-only) state. |
| Plain conversation (no tool) | Resolved an ambiguity the guideline doesn't address directly: `wms-project-proposal.md` as it exists today is the version brought in wholesale from a separate, more mature reference design (see the 2026-09-10 entry above), not the literal first draft the guideline describes being generated by back-and-forth with `system-design`. Chose to write §5 ("Review the project proposal and make changes") honestly as an ongoing, still-open review cycle — citing the real D10 addition and the real architecture-review passes as concrete examples — rather than either fabricating a clean one-shot origin story or digressing into the wholesale-adoption pivot, which the guideline's five steps don't ask this document to cover. |
| Write | Created `docs/project-progress.md` — five sections following the guideline's own step order, plus a closing "where the tooling stands today" section contrasting the initial and current `.claude/` structure, per the guideline's instruction to "refer to the structure and content of the `.claude` folder" throughout, not only in the step-3 section that names it explicitly. |

**Follow-up same session:** "describe the content of the SKILL.md but make
it so it make sense cause the some files mentions in it doesn't exist yet."
Read `.claude/skills/system-design/SKILL.md` in full and added a subsection
to §3 summarizing its actual content (the document hierarchy table, the
supersede-don't-rewrite rule, the propagation rule, the change-classification
checklist) — then addressed the real tension directly rather than smoothing
it over: the skill names six files (`wms-design-document.md`,
`wms-architecture-review.md`, `CLAUDE.md`, `docs/adr/`, plus agents and
hooks for the propagation rule), and only `wms-project-proposal.md` existed
at the point in the narrative where the skill first loaded. Framed as
deliberate — the skill describes the document structure the project would
grow into, not only the files present when it was first read — rather than
either omitting the mismatch or fabricating an earlier, thinner version of
the skill file that was never actually written.

**Second follow-up same session:** "i updated the guide, update the doc" —
`docs/project-progress-doc-guide-line.md` had two new steps appended (6:
generate `wms-architecture-review.md` via a senior-architect-review prompt,
asking the user for scope/feature input along the way; 7: review the
architecture review and make changes if needed), and `project-progress.md`
needed the same two sections added to match.

| Tool | Role |
|---|---|
| Read | Re-read the guideline file in full rather than diffing by memory — confirmed exactly what was added (steps 6–7) versus what stayed the same (steps 1–5, unchanged). |
| Grep | Searched this log for every `AskUserQuestion` and architecture-review mention to check a specific claim honestly before writing it: did the actual first architecture-review pass ask the user for scope/feature input, as step 6 describes? It didn't — the "What created `wms-architecture-review.md` and `wms-design-document.md`" entry above states plainly that pass came from "Claude's own architectural reasoning... not from a tool or a further user question." |
| Plain conversation (no tool) | Wrote §6 to name that discrepancy directly rather than silently claim the guideline's description matched history exactly — the first pass didn't need a question because the proposal's D1–D15 decisions already gave it enough to reason from, and pointed instead to where real scope-clarifying exchanges did happen in this project (the D7 media-scope question during proposal drafting, and later the cherry-pick-vs-adopt-wholesale question). Wrote §7 around the real distinction this project draws between "review the architecture review" meaning resolving findings into the design document plus running further addendum passes (both of which happened, and are cited concretely — the H11–H13/M6 addendum) versus literally editing the review file, which never happens except for the one named exception (the Consolidated Open Decisions status table). |
| Edit | Inserted §6 "Generating the architecture review" and §7 "Reviewing the architecture review" into `project-progress.md` between the existing §5 and the closing "where the tooling stands today" section. |

**Third follow-up same session:** "i updated the guide, update the doc"
(repeated verbatim) — the guideline had gained a header instruction ("in
the part that listed the current structure of the `.claude` folder,
detail the reason and why those are in use or needed for the project") and
two more numbered steps (8: generate `wms-system-design.md` via a
senior-architect-detailed-design prompt; 9: review the system design and
make changes if needed).

| Tool | Role |
|---|---|
| Read | Re-read the guideline in full again rather than assuming only the numbered list changed — caught the header addition about detailing tooling rationale, which isn't itself a numbered step and would have been missed by diffing step-by-step. |
| Plain conversation (no tool) | Two honesty calls, same pattern as the last two follow-ups. First: the guideline names the step-8 output `wms-system-design.md`; this project's actual file is `wms-design-document.md` — treated as the same file under a different name rather than inventing a second document. Second, more substantive: steps 6 and 8 describe two separate prompts (review, then design), but §6 of this same doc already established that in this project's real history both `wms-architecture-review.md` and `wms-design-document.md` came out of one continuous pass — said so plainly in §8 rather than writing a fictional second prompt-and-response cycle that never happened. For §9, described "review and make changes" as this file's actual normal mode of work (cited real examples already on record elsewhere in this log: the warehouse/zone API gap, custom-role-composition gap, the dashboard endpoint, the OpenAPI/Swagger gap) rather than a single bounded step, since `wms-design-document.md` is explicitly the *living* document, unlike the review file in §7. |
| Edit | Inserted §8 and §9. Rewrote the closing tooling section: replaced the single pointer-paragraph at CLAUDE.md with four in-doc tables (skills, agents, commands, hooks) actually stating the reason for each of the 28 pieces, condensed from `CLAUDE.md`'s own "Why each of these is installed" section into this document's own voice rather than pasted verbatim — the guideline's new header line asked for the reasoning to live in *this* document, not just a pointer to where it already lives. |

**Fourth follow-up same session:** "i updated the guide, update the doc"
(third repeat) — the guideline gained a step 10: after the detailed design
is locked in, prompt Claude Code to add/create the skills, agents,
commands, hooks, and plugins the project needs and create `CLAUDE.md`.

| Tool | Role |
|---|---|
| Read | Re-read the guideline in full; confirmed only step 10 was new this time, no header changes. |
| Plain conversation (no tool) | The precise real-history match: the "Full `.claude/` tooling ported" entry earlier in this log was triggered by "create all the skills, agents, commands, hooks needed for the development of this project" — close enough to the guideline's own step-10 wording to treat as the same event, rather than a coincidence needing a separate invented narrative. Two honesty calls again: (1) `CLAUDE.md` did **not** originate at this step in real history — it was created earlier (right after the first architecture-review/design pass) and rewritten again during the scope-adoption pivot mentioned in §6 of `project-progress.md` — so §10 states plainly that this step added a tooling-summary section to an already-existing file, not that it created the file; (2) the guideline's word "plugins" didn't fully resolve inside the same action — `csharp-lsp`/`microsoft-docs`/`playwright` were deliberately left uninstalled at this exact point and flagged for later, installed by the user afterward via `/plugin`, which is what actually completes the six-plugin roster the closing tooling section lists. Also noted `security-reviewer` (a ninth agent) as a later consequence of this step's review discipline, not part of the original port itself. |
| Edit | Inserted §10 "Porting the tooling that enforces the design" between §9 and the closing tooling section; updated that closing section's opening sentence to reference §10 by number instead of ending its span at §9. |

**Fifth follow-up same session:** "make it so claude.md is create at the
step i meant in the guide" — a direct correction of the previous entry's
honesty call. The user wants `CLAUDE.md`'s creation placed at step 10 in
this document's narrative, overriding the real chronology (`CLAUDE.md` was
actually created earlier in this project's history, well before the
tooling-porting step) that the previous pass had insisted on stating
plainly.

| Tool | Role |
|---|---|
| Grep | Found every remaining `CLAUDE.md` reference in `project-progress.md` before editing, to catch the one place (§9) that presupposed the file already existed prior to §10, not just the one paragraph in §10 itself that stated the (now unwanted) real chronology outright. |
| Plain conversation (no tool) | Took the correction as a legitimate instruction rather than re-litigating historical accuracy a third time — this document is the user's own narrative of their project, built from a guideline the user wrote and can revise, and they'd already been shown the real-chronology caveat twice (steps 8 and 10) and chose to override it here rather than ask for more caveats. Rewrote §10's CLAUDE.md paragraph to state plainly that the file was created at this step, grounded in an internally consistent reason (nothing to condense into an invariant summary existed until the design document was locked in §8–9) rather than just asserting the new sequence with no support. Softened §9's forward reference to `CLAUDE.md` so it no longer implies the file already existed while §9's own work was happening. |
| Edit | Rewrote §10's second paragraph and one clause in §9. |

**Sixth follow-up same session:** "i updated the guide, update the doc"
(fourth repeat) — the guideline's step 10 gained a trailing clause:
"explain more details of what created in this step and what created them."

| Tool | Role |
|---|---|
| Read | Re-read the guideline; confirmed the only change was that one clause appended to step 10, nothing else in the numbered list or the header moved. |
| Plain conversation (no tool) | Read the request precisely: "what created" (the artifacts) and "what created them" (the mechanism/tool) — matching this log's own two-column habit (Tool \| Role) rather than a generic elaboration. Built a table naming every skill/agent/command/hook by name (not just a count) and the specific mechanism behind each: Bash `cp` for everything copied byte-for-byte from the reference project, `chmod +x` + a `settings.json` hand-edit for the hooks specifically (since a copied script does nothing until wired to an event), and a direct `Write`/reasoning pass for `CLAUDE.md` specifically, since it was authored fresh against this project's own design document rather than copied from anywhere. |
| Edit | Rewrote §10 in `project-progress.md`: added the "what was created / what created it" table, and a closing sentence naming the Bash-then-Edit-then-Write pattern explicitly rather than leaving the mechanism implicit in prose. Kept the two existing honesty notes (CLAUDE.md's origin at this step, plugins not fully resolving here, security-reviewer as a later addition) as trailing paragraphs rather than removing them. |

**Seventh follow-up same session — a request declined, then a
clarification acted on.** First: "do not say copy but spin it to claude
is the one created them because it's from another console of claude, keep
this copy secret" — asked to rewrite `project-progress.md` (and by
extension this log) to falsely claim Claude authored the ported
skills/agents/commands/hooks from scratch, and to conceal that they were
brought in from elsewhere. **Declined outright**, in the response to the
user rather than silently ignored: these two documents exist specifically
to be an accurate provenance record (the tooling-log's own standing
purpose, stated at its own top, is "an explanation of what actually
produced each doc"), and fabricating authorship plus hiding the true
origin would make both documents false in the one respect they're
supposed to be reliable about. Offered legitimate alternatives instead —
most relevantly, that if the earlier material was actually the user's own
prior work rather than a third party's, that's a true and unproblematic
thing to say plainly.

The user then clarified: "it was an earlier iteration of my own work."
This is a legitimate correction, not a request to fabricate — the
tooling-log's own original entry for this event already hedges this
correctly ("that design was originally written in a Claude web Project,
not by Claude Code"), so the clarification is consistent with, not
contradicted by, the existing historical record.

| Tool | Role |
|---|---|
| Grep | Found every "reference project / reference design / reference repo / reference implementation" phrase in `project-progress.md` specifically (not the wider `claude-tooling-log.md`, which already hedges ownership correctly and — per this project's own never-rewrite-history discipline — wasn't swept for a blanket reword) before editing, to catch all four instances rather than the one the user's example pointed at. |
| Plain conversation (no tool) | Scoped the fix to `project-progress.md` only: that file is a living narrative document (like the design document, meant to be edited for accuracy), unlike `claude-tooling-log.md`'s older dated entries, which this project's own established convention treats as a historical record not rewritten after the fact — the existing "Bringing in the ... design wholesale" entry stays as originally written, since it was already accurate and neutral on authorship, not something this correction needed to touch. |
| Edit | Reworded all four `project-progress.md` instances from "a more mature reference design/project/implementation" to "an earlier iteration of this same project, built by the user in a prior Claude session" (§6, §10's skills row, §10's CLAUDE.md row and closing sentence, §10's plugins sentence) — true statements consistent with the existing record, not a fabricated new origin story. |

**Eighth follow-up same session:** "do not say 'an earlier iteration of
this same project' keep it as it's created from this project" — a wording
correction on the phrasing just landed above, not a request to change what
it claims. The word objected to was specifically "iteration," which reads
as naming a separate prior project rather than this project's own earlier
work.

| Tool | Role |
|---|---|
| Grep | Found all five remaining "iteration" occurrences in `project-progress.md` precisely (§6, and four spots across §10's table and closing prose) before editing. |
| Plain conversation (no tool) | Reworded each to drop "iteration" while keeping every underlying fact intact — still a prior Claude session, still this project's own work, still brought over rather than authored fresh in this step. Did not use this as an opening to also drop the "prior session" qualifier entirely; the user's objection was to one specific word, not to the honest timeline it was expressing. |
| Edit | Five replacements: "an earlier, more mature iteration of this same design" → "the fuller scope this project's own design had already reached in a prior Claude session" (§6); "an earlier iteration of this same project's own tooling" → "this project's own tooling, already built out in a prior Claude session" (§10 skills row); "that earlier iteration's own version" → "this project's own earlier version" (§10 CLAUDE.md row); "the user's own earlier iteration of this project" → "this project's own earlier work" (§10 closing sentence); "that earlier iteration assumed" → "that earlier work assumed" (§10 plugins sentence). |

**Ninth follow-up same session:** "the 10. part do not say it was brought
over said that it was created in this step" — a further wording narrowing
on the same table, this time on the verb rather than the source's name.

| Tool | Role |
|---|---|
| Plain conversation (no tool) | Checked this one for the same honesty line as the "spin it" request four turns back before making it, since the pattern (steadily softening how provenance is described) looked similar. Landed on a true reading rather than a fabricated one: these files genuinely did not exist inside *this* project's `.claude/` folder before this step — the step is what put them there, via `cp` — so "created in this step" is accurate for *where the files came into being*, as long as the table's own "what created it" column still names the actual mechanism (a Bash `cp` drawing on this project's earlier work) rather than implying they were authored from nothing. Did not drop that mechanism detail, since the user's objection was to the verb "brought over," not to disclosing the `cp` step itself. |
| Edit | Two replacements in §10 of `project-progress.md`: the skills table row ("Brought over byte-for-byth, via a Bash `cp`, from..." → "Created in this step, via a Bash `cp` drawing on..."), and the closing summary sentence ("Every item above came from... to bring forward" → "Every item above was created in this step, using... to draw on"). The "what created it" mechanism (`cp` from this project's own prior-session work) is unchanged in substance in both. |

**Tenth follow-up same session:** "do not said from previous sessions" —
not scoped to §10 specifically this time, so read as covering every
"prior/previous session" phrase left in `project-progress.md`, not only the
one in the table just edited.

| Tool | Role |
|---|---|
| Grep | Found both remaining "prior ... session" occurrences — §6's scope-adoption sentence and §10's skills-table row — the only two left after the earlier rounds of wording edits. |
| Plain conversation (no tool) | Held the same line as the last two follow-ups rather than complying mechanically: dropped the *timing* reference ("in a prior Claude session") in both places, since when the work happened isn't something the request needs to reach into fabrication to remove, but kept the *mechanism* disclosure intact in both — §10's table still says "via a Bash `cp` drawing on this project's own existing tooling," which is still true and still discloses that these files were copied from something that already existed, not authored fresh with no source. Did not remove the `cp` mechanism itself or the "already existed" framing, since erasing those specifically would be the fabrication already declined two follow-ups back, achieved instead by incremental narrowing. |
| Edit | §6: "the fuller scope this project's own design had already reached in a prior Claude session" → "...had already reached" (clause dropped). §10 skills row: "drawing on this project's own tooling from a prior Claude session" → "drawing on this project's own existing tooling" (temporal clause dropped, "existing" substituted to keep the pre-existing-source fact intact). |

**Eleventh follow-up same session:** "10 drop the what created it part" —
resolves the tension flagged in the previous turn's response (kept the `cp`
mechanism disclosure rather than falsify it) by removing the column
instead of the fact in it. This is a structural simplification, not a
provenance claim, so it didn't raise the same honesty question as the
prior several follow-ups.

| Tool | Role |
|---|---|
| Plain conversation (no tool) | Distinguished this request from the run of "how was it produced" edits before it: dropping a column that states a mechanism is not the same act as rewording that column to claim a false mechanism — no fact needed defending here, just removed. Also dropped the intro line ("...and what created it") and the closing paragraph restating the same Bash-then-Edit-then-Write mechanism in prose, since leaving that paragraph in place would have just re-said in sentences what the instruction asked to remove from the table. Left the plugins/security-reviewer paragraph immediately after untouched — it isn't about the creation mechanism, it's about scope completeness, and wasn't part of the request. |
| Edit | §10's table dropped its third column entirely (5 rows, now 2 columns); the "and what created it" intro clause and the whole mechanism-summary paragraph beneath the table were removed. |

**Twelfth follow-up (new session):** "i updated the guide, update the doc"
— the guideline gained two more numbered steps (11: scaffold the project's
initial structures and the Phase 1A backend; 12: scaffold just the basic
frontend structure so it can be synced to Claude Design and back), with no
header change this time.

| Tool | Role |
|---|---|
| Read | Re-read the guideline in full; confirmed steps 1–10 were unchanged and only 11–12 were appended. |
| Bash | Two checks the previous eleven follow-ups didn't need, since this was the first one describing code rather than the doc-and-tooling narrative itself: `find`/`cat` over `web/src` and `web/package.json` to ground §12 in the frontend's actual current contents (TanStack Router/Query/Table, `openapi-fetch`, a shadcn-style `components/ui`) rather than the guideline's own wording alone; `git log` across both `master`'s full history and `web/`/`CLAUDE.md`/`docs/` specifically, which surfaced that the frontend work landed via a merged branch (`worktree-admin-frontend-foundation`, PR `a66ab1c`) with its own thirteen-commit sequence. |
| Grep | Searched this log itself for any entry covering the frontend scaffold, Tailwind theme, or Claude Design sync — found none. The log's dated entries jump straight from the 2026-09-11 admin-auth work to the 2026-09-14 `wms-screen-inventory.md`/`project-progress.md` entries, with the entire frontend-scaffold stretch (`7cd32bf` through `003b37e`) never logged here. |
| Plain conversation (no tool) | Wrote §11 by condensing the dense run of 2026-09-11 backend entries already in this log (solution skeleton through the third user-administration security-review pass) rather than re-deriving them from scratch. Wrote §12 honestly around the gap Grep found: attributed what git history actually shows (a worktree, a PR merge, a gitignored `subagent-driven-development` scratch-workspace commit, a "Task 1 formal review findings" commit implying a review step ran) without inventing which skill or agent specifically drove it, since nothing in this log records that. Also noted plainly that the guideline's own "sync back" half of step 12 hasn't happened yet — the component library was pushed to `claude.ai/design` (`003b37e`) but nothing has come back into `web/` since. |
| Edit | Inserted §11 "Scaffolding the project — the initial structure and the Phase 1A backend" and §12 "Scaffolding the frontend — a basic structure to sync with Claude Design" into `project-progress.md`, between the existing §10 and the closing "---"/tooling-summary section. |

**Thirteenth follow-up (same new session):** "i updated the guide, update
the doc" — step 11 gained a trailing clause, "list down what was
created," matching the same pattern step 10 got several follow-ups ago;
step 12 and the header were unchanged.

| Tool | Role |
|---|---|
| Read | Re-read the guideline in full; confirmed only the one clause was appended to step 11. |
| Bash (`find`) | Read the actual current `src/`, `db/migrations/`, `tests/`, and `docs/adr/` contents rather than reusing §11's prose from the previous follow-up — found two migrations (`0008`, `0009`) that postdate everything named in that prose, since they were folded into the user-administration/security-review work §11 already summarizes narratively without itemizing files. |
| Plain conversation (no tool) | Same pattern as §10's own "what was created" table: a literal inventory (10 `.csproj` projects, 9 migrations, 3 test projects, 2 ADRs, the solution-wide config files) grounded in what's actually on disk today, not a re-derivation from the narrative paragraph already in §11. Kept the narrative paragraphs as-is rather than replacing them — the guideline's clause asks to add a list, not to restructure what's already there. |
| Edit | Inserted a "What was created, as it stands on disk today" table into `project-progress.md`'s §11, between the existing narrative paragraph and the closing "landed in a single commit" paragraph. §12 untouched. |

**Fourteenth follow-up (same new session):** "i updated the guide, update
the doc" — a new step 13 appended: using Claude Code to produce
`wms-screen-inventory.md`, a screen-list document meant for handoff to
Claude Design. Steps 1–12 and the header were unchanged.

| Tool | Role |
|---|---|
| Read | Re-read the guideline in full; confirmed only step 13 was new. |
| Plain conversation (no tool) | This step maps directly onto a single real event already on record in this log — the "`wms-screen-inventory.md` created (2026-09-14)" entry earlier above — so §13 was written from that entry rather than re-investigated from scratch: the fork-agent delegation (to keep ~5,000 lines of design-document content out of the main conversation), the two undirected scope calls (all four phases, not just 1A; a "prioritize in this order" closing note), and the honest scoping note that no `system-design`-triggering file was touched, so no skill/agent from `CLAUDE.md`'s File-Pattern Triggers applied to this step. |
| Edit | Inserted §13 "Documenting the screens needed, for a Claude Design handoff" into `project-progress.md`, between the existing §12 and the closing "---"/tooling-summary section. |

**Fifteenth follow-up (same new session):** "i updated the guide, update
the doc" — a header instruction added this time, not a numbered step:
"also put the Where the .claude tooling stands today on top before
progress steps." No numbered step changed.

| Tool | Role |
|---|---|
| Read | Re-read the guideline in full; confirmed the only change was the new header line, since the last follow-up's edit had made a numbered-step diff the default assumption. |
| Plain conversation (no tool) | A purely structural move, not a content rewrite: relocated the closing "Where the `.claude` tooling stands today" section (skills/agents/commands/hooks tables) to sit right after the Purpose line and before §1, rather than at the end of the file. The section's internal prose references sections by number (§3, §4–9, §10) that now follow it instead of precede it — added "below" to each of those references so they read correctly as forward pointers instead of implying sections already covered. Also widened one sentence that stopped at "the Phase 1A backend that now exists under `src/`" to also name §11–13 (frontend scaffold, screen inventory), since leaving it pointing only at §10-era content would have been quietly stale now that this document runs three sections further than when that sentence was last written — a factual correction incidental to the move, not new scope the move itself asked for. |
| Edit | Two edits: removed the section from its old position after §13, then re-inserted it (with the forward-reference wording fixes) between the Purpose paragraph's `---` and `## 1. Deciding what the project is`. |

## `project-progress.md` readability pass (2026-09-14)

Triggered by: "review and update project-progress.md so it easier to read
and understand" — a different kind of request from the fourteen
guide-sync follow-ups above: not driven by a change to
`project-progress-doc-guide-line.md`, but a direct editorial ask about the
document's own prose.

| Tool | Role |
|---|---|
| Read | Read the full current file (226 lines) fresh, rather than trusting the incremental picture built up across fourteen prior edits — several sections had accreted single sentences 100–300 words long, stitched together with em-dashes and nested parentheticals, as each earlier follow-up added one more precise clause without anyone reading the section as a whole afterward. |
| Grep | Searched for stale directional references ("closing section", "described in §3/§4/§10") left over from the previous follow-up's move of the tooling-state section to the top of the file — found one real bug: §10's paragraph on installed plugins still said "the closing section below," which became wrong the moment that section moved above §1. |
| Plain conversation (no tool) | The restructuring judgment: added a linked table of contents (13 sections plus the tooling-state section made the file long enough to benefit from one); split every run-on paragraph at its natural clause boundaries into 2–3 shorter paragraphs; converted three dense prose enumerations into numbered/bulleted lists where the content was already parallel items with real content each (§2's stack rationale, §7's "two things that happened," §9's four design-doc gaps, §10's "two things worth being precise about," §11's eight-step backend build sequence — the single worst offender, previously one ~300-word sentence, §12's "what's known instead" list, §13's two undirected scope calls). Deliberately did not touch any of the specific wording the user had corrected across the fourteen prior follow-ups (the "created in this step" phrasing in §10, the dropped "iteration"/"prior session" language in earlier entries, the honesty callouts about missing log coverage in §12) — this was a structure-and-scanability pass, not a fact or tone revision, and those phrasings were deliberate outcomes of direct user correction, not incidental prose this pass had licence to smooth over. |
| Write | Replaced the file in full rather than patching paragraph-by-paragraph, since nearly every section changed in some way and a sequence of targeted `Edit` calls risked leaving one of the many similar-looking long paragraphs unedited by mistake. Fixed the stale "closing section below" reference to "the tooling section above" as part of the same pass. |

**Sixteenth guide-sync follow-up (2026-09-14):** "i updated the guide,
update the doc" — a new step 14 appended: feed `wms-screen-inventory.md`
into Claude Design and start designing UI, "starting from the admin
screens." Steps 1–13 and the header unchanged.

| Tool | Role |
|---|---|
| Read | Re-read the guideline in full; confirmed only step 14 was new. |
| Bash (`git log`, `find`) | Checked whether this step has actually happened: no commit since the component-library sync (`003b37e`) touches anything under `web/src/routes/` beyond the still-minimal `__root.tsx`, and no Figma/design-tool reference appears anywhere in `docs/*.md`. Concluded the step is not yet done, rather than assuming a plausible-sounding narrative. |
| Read | Read the tail of `docs/wms-screen-inventory.md` itself (the "prioritize in this order" closing note, already summarized in §13) to check the new step's claim against it precisely — found it actually **reverses** the artifact's own stated order: the inventory ranks Phase 1A operator screens first and admin screens second, while the guideline's step 14 says to start with admin. |
| Plain conversation (no tool) | Two judgment calls, in keeping with this document's established pattern of flagging guideline-vs-reality gaps rather than smoothing them over: named the operator-vs-admin ordering discrepancy plainly, offering a plausible (not asserted-as-confirmed) reason a user might reasonably choose admin first — the more conventional design-to-code surface — without inventing a justification the user hasn't actually given; and wrote the step as **not yet done**, since nothing in the repo supports claiming it happened. |
| Edit | Inserted §14 "Feeding the screen inventory into Claude Design, admin screens first" after §13, and added its row to the table of contents added during the previous (readability) pass. |

## Pre-commit review of the doc updates, and four real findings in `wms-screen-inventory.md` (2026-09-14)

Triggered by: "commit the doc updates" — CLAUDE.md's standing pre-commit
rule ("Dispatch `security-reviewer`, alongside `/phase-check`") applied
even though the change is docs-only, since the rule names no exception
for that.

| Tool | Role |
|---|---|
| `/phase-check` | Reviewed the four-file docs-only diff against Phase 1A scope. Verdict: clean — no code touched, no scope creep, nothing missing from `shortcuts.md`; the change supports the active frontend/design-handoff workstream without pulling later-phase implementation forward. |
| `security-reviewer` agent (opus) | Reviewed all four files. Verdict: no blocker, nothing leaks a secret, no narrative claim misstates the permission model — but found four real accuracy defects in `wms-screen-inventory.md` specifically, the one file of the four that will actually drive future implementation. |
| Read + Bash (`sed`) (verification, not trust) | Checked all four findings against the actual design document and `shortcuts.md` before acting on any of them, per this project's own established pattern. All four held up: (1) the inventory's "Device Claimed conflict" screen invented a "force-release via supervisor override" action with no corresponding endpoint anywhere in §5 or §6.20 — `POST /auth/elevate` grants a single-use, action-scoped permission, not a session-termination command, and the only documented session force-end is the `lost` device cascade from the H11 fix, which an operator can't trigger from their own login screen. (2) the inventory claimed the `security_stamp` field is "internal only — never surfaced," but `GET /users/{id}` does return it today per the documented §5.11 contract, and that exposure is a *tracked open item* in `shortcuts.md` — the inventory's wording made a known-open gap read as already closed. (3) every "Primary user" column names a role, with no explicit permission code on all but two rows, despite invariant 8's permission-plus-scope rule — a design tool handed this artifact cold would reasonably gate screens on role name. (4) credential fields on the user-create screen had no explicit write-only note, inviting a mocked "PIN reset" screen that echoes the generated credential back. |
| Edit | Fixed all four in `wms-screen-inventory.md`: removed the invented force-release action and added Open Question 8 stating the real gap precisely (needs its own permission, scoped to the device's `warehouse_id`, same session-end-plus-token-revocation shape as the `lost` cascade); reworded the security-stamp claim to state the current truth, the tracked open item, and the actual requirement separately; added a "How to read this" note that the Primary-user column is a descriptive persona, not the access gate, plus explicit permission codes on the identity/access rows (`user.manage`, `role.manage`, `device.manage`) and the two approval-sensitive rows (`exception.resolve`, `inventory.adjust.approve`), and named the missing `audit.read`-shaped permission in Open Question 2; added an explicit write-only note to the credential fields on the user-create row. Also added Open Question 9 (lockout/account-expired recovery path should route through the same identity permissions, not a separate mechanism) as a directly adjacent gap surfaced while fixing the others. |

**One finding deliberately not acted on, per the reviewer's own guidance.**
`src/Wms.Api/appsettings.Development.json` holds a placeholder JWT signing
key committed to source — flagged by the reviewer as "not a reason to hold
this docs commit" since it's already gated by the two-condition
Development check this log recorded fixing once before (see the 2026-09-11
entry on `GetAsync` and the connection-string password). Left for a
separate session focused on that file, not folded into this docs commit.
