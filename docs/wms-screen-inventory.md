# Warehouse Management System — Screen Inventory

**Scope:** All phases — 1A, 1B, 2, 3, 4
**Status:** Draft v0.1 — for design handoff
**Companion documents:** `wms-project-proposal.md`, `wms-design-document.md`, `wms-architecture-review.md`, `shortcuts.md`

**Purpose of this document.** This is a build target for a design tool (Claude Design or equivalent), not a status report. It lists every screen the *design* calls for across Phases 1A→4, derived from the API reference (§5), data flows (§6), and data models (§2) in `wms-design-document.md` — even though only Phase 1A is currently implemented in code. Phase tags tell you what needs a real mockup now vs. what's being designed ahead of schedule so later phases don't require re-deriving screen shape from scratch. **Start design work on the Phase 1A screens** — that's the active build (see `CLAUDE.md`).

---

## How to read this

One React SPA, one build, two route trees sharing auth, i18n (English/Japanese), design tokens, and a generated API client:

- **`/admin/*`** — desktop console. Sidebar + dense data grids + filters + dashboards. Mouse/keyboard. Assumes reliable network, no offline support. Resizable desktop window (Material 3 "medium"/"expanded" breakpoints), never full mobile.
- **`/operator/*`** — handheld PWA, installed in `display: standalone`. One task per screen, full-bleed, large touch targets, one-handed/gloved operation. Scanner-driven via a single `useScanner()` hook (keyboard-wedge / camera / manual entry — the screen never knows which). Service worker + IndexedDB fact queue: everything here must work through a dead wifi zone. Runs in either portrait or landscape (Material 3 "compact").

Every screen that mutates stock through operator action does so by queuing a **fact** (`POST /sync/facts`), which the server **never refuses** except for malformed/unauthenticated input — a discrepancy always raises an `inventory_exception` and lets the operator continue. Screens on `/admin/*` issue **commands**, which the server *may* refuse with a `409`. This split is the single most important thing a designer needs to internalize before mocking up any operator screen: **there is no error dialog on a wrong count** — there's an exception raised silently behind the scenes and the operator moves on.

**Supervisor override** is not a screen, it's a modal pattern that appears *on top of* whatever operator screen triggered it (a `403` with `elevation.possible`): badge-scan a supervisor, action proceeds, device session never changes. It recurs across nearly every operator flow with `requires_approval` reason codes, so design it once as a shared component.

**The "Primary user" column is a descriptive persona, not the access gate.** Per invariant 8, authorization is always permission-plus-warehouse/zone-scope, never a role name — a role like "Supervisor" is a seeded bundle of permissions, and a site can compose a custom role that holds the same permission under a different name. Treat the column as "who typically has this" for design context; the actual server-side gate is named explicitly on the identity/access and approval-sensitive rows below (`user.manage`, `role.manage`, etc.) and should be assumed present even where not spelled out.

---

# Admin Console (`/admin/*`)

## Phase 1A

### Auth & session

| Screen | Route | Purpose | Primary user | Key fields | Primary actions | Endpoints |
|---|---|---|---|---|---|---|
| Staff Login | `/admin/login` | Email/password sign-in for office staff | All staff roles | email, password | Sign in | `POST /auth/staff/login` |
| Forced credential change | `/admin/login/force-password-change` | Every staff account's very first login on a password credential — not provisioning-specific, and not shown again once the credential has been changed once | Any staff role, on their own account's first login | new password, confirm | Submit new password, then log in again normally | `POST /auth/credential-change` |

### Admin Dashboard

**`/admin/dashboard`** · Phase 1A · `GET /dashboard`

The admin site's home screen — every field is assembled from data the design already tracks elsewhere, i.e. this is a single aggregated read, not a new data source.

- **Purpose:** one-screen operational status for a single warehouse: receiving, putaway, exceptions, device sync health, reconciliation, stock summary.
- **Primary users:** Warehouse Manager, Supervisor, System Administrator.
- **Key data:** `receiving.openReceipts`, `receiptsCompletedToday`, `openDiscrepancies`; `putaway.tasksReady/tasksLeased/tasksCompletedToday`; `exceptions.open`, `oldestAgeHours`, `byType` breakdown; `sync.devicesActive`, `devicesWithBacklog`, `maxQueueDepth`; `reconciliation.lastRunAt`, `varianceCount`; `stockOnHand.skuCount`, `onHand`.
- **Primary actions:** warehouse selector; drill-through from each tile to its detail screen (exceptions tile → Exception Queue, sync tile → Devices).
- **UX notes:** deliberately excludes picking/packing/shipping/allocation tiles in 1A — those tiles don't exist until the underlying features ship in 1B+; a dashboard is not where scope creep should be introduced. `reconciliation.varianceCount` should always read `0` — treat any nonzero value as an alert-styled state, not a normal data point. Needs to read sensibly at both empty (new deployment) and thousands-of-movements scale — mock up against a large synthetic history, since a 50-bin demo will mislead on table layout at growth scale.

### Master data

| Screen | Route | Purpose | Primary user | Key fields | Primary actions | Endpoints |
|---|---|---|---|---|---|---|
| Warehouses — list | `/admin/warehouses` | Manage warehouse sites | System Administrator | code, name, timezone, status | Create, edit, view | `GET/POST /warehouses` |
| Warehouse — edit | `/admin/warehouses/:id` | Edit one warehouse | System Administrator | code, name, timezone, `dayBoundaryTime`, `defaultLocale`, status, version | Save (optimistic concurrency via `If-Match`) | `PATCH /warehouses/{id}` |
| Zones — list | `/admin/warehouses/:id/zones` | Manage zones within a warehouse | Warehouse Manager | code, name (en/ja), `zoneType` (receiving/bulk/pick/staging/quarantine/returns/damage), temperature class, status | Create, edit, retire | `GET/POST /zones`, `PATCH /zones/{id}` |
| Locations (bins) — list | `/admin/locations` | Manage bin/dock/staging locations, filterable by zone | Warehouse Manager | code (e.g. `A-12-03-2`), zone, type, aisle/bay/level, pick sequence, max weight/volume, mixed-item/mixed-lot flags, status | Create, edit, block/retire | `GET/POST /locations`, `PATCH /locations/{id}` |
| Location — edit | `/admin/locations/:id` | Single-location detail/edit | Warehouse Manager | as above + version | Save, block (excludes from putaway/allocation without touching balances) | `PATCH /locations/{id}` |
| Items (SKU master) — list | `/admin/items` | Browse/search the item catalogue | Inventory Controller, Warehouse Manager | SKU code, name (en/ja), base UoM, lot/expiry/serial-tracked flags, ABC class, status | Create, search, filter, open detail | `GET/POST /items` |
| Item — detail/edit | `/admin/items/:id` | Full item record: UoM hierarchy, barcodes, tracking flags | Inventory Controller | `item_uom` rows (EACH/INNER/CASE/PALLET, qty-in-base, dimensions, weight, discrete flag, receiving/picking defaults), `item_barcode` rows (barcode, type, per-UoM) | Add/edit UoM levels, add/edit barcodes, edit shelf life, save | `PATCH /items/{id}` |
| Handling Units (LPN) — list | `/admin/handling-units` | Register/track pallet, cage, tote barcode identities | Receiver, Warehouse Manager | LPN, HU type, parent HU (nesting), current location, status | Register new LPN, view current location, retire | `GET/POST /handling-units` |
| Reason Codes — list | `/admin/reason-codes` | Browse the seeded reason-code catalogue (~25 codes) | System Administrator | code, category, label (en/ja), `requiresNote`/`requiresPhoto`/`requiresApproval`/`isShrinkage` flags, `appliesTo` movement types | View (1A: read-only browse — write path not yet specified, see Open Questions) | `GET /reason-codes` |

### Identity & access management

| Screen | Route | Purpose | Primary user | Key fields | Primary actions | Endpoints |
|---|---|---|---|---|---|---|
| Users — list | `/admin/users` | Search/manage all staff + operator accounts | System Administrator, Warehouse Manager | display name, employee code, user type, status, `validUntil`, role scopes | Search (by name/code), filter by warehouse/type/status, create, open detail | `GET /users` |
| User — create | `/admin/users/new` | Onboard a new staff or operator account | System Administrator (`user.manage`) | display name, employee code, locale, `validFrom`/`validUntil`, credentials (password/PIN/badge — **write-only: submitted once, never returned by any read endpoint or re-displayed after save**), initial role scope(s) | Create account | `POST /users` |
| User — detail/edit | `/admin/users/:id` | Edit one user; suspend/reactivate; manage role scopes | System Administrator (`user.manage`) | display name, `validUntil`, status, role-scope list (role × warehouse × zones) — **not** the security stamp: `GET /users/{id}` does currently return `security_stamp` per the documented §5.11 contract, but this is a tracked open item (`shortcuts.md`) precisely because the UI must never render it, not because it's already hidden | Edit profile, suspend (shows `activeDeviceQueueDepth` risk warning), reactivate, add/remove role-scope | `PATCH /users/{id}`, `POST /users/{id}/role-scopes` |
| Roles — list | `/admin/roles` | Browse seeded system roles + site-composed custom roles | System Administrator (`role.manage`) | code, name (en/ja), `isSystem`, `isActive`, permission list | View, create custom role, edit custom role, deactivate custom role | `GET /roles` |
| Role — create/edit (custom role builder) | `/admin/roles/new`, `/admin/roles/:id` | Compose a custom role from the permission catalogue | System Administrator (`role.manage`) | code, name (en/ja), permission checklist grouped by category | Toggle permissions, save. System roles render read-only — no edit/delete controls at all | `POST /roles`, `PATCH /roles/{id}`, `GET /permissions` |
| Devices — list | `/admin/devices` | Register/monitor handheld scanners | System Administrator, Warehouse Manager (`device.manage`) | label ("HH-014"), warehouse, status, platform, app version, last seen, queue depth, `claimedBy` (live session) | Register new device, mark retired, mark **lost** (cascades: force-ends session, revokes refresh tokens) | `GET/POST /devices`, `PATCH /devices/{id}` |
| Auth Audit Log | `/admin/audit/auth-events` | Investigate logins, lockouts, overrides, role/credential changes | System Administrator, Auditor (an `audit.read`-shaped permission — not yet in the catalogue, see Open Questions) | event type, actor, target user, device, IP, outcome, detail, timestamp | Filter by user/date/device/event type | — (`auth_event` table exists; no documented `GET` yet — see Open Questions) |

### Inbound

| Screen | Route | Purpose | Primary user | Key fields | Primary actions | Endpoints |
|---|---|---|---|---|---|---|
| Receipts — list | `/admin/receipts` | Track all inbound receipts | Receiver, Warehouse Manager | receipt number, status, supplier reference, lines total/confirmed, discrepancy count, expected/started dates | Filter (status, date range, supplier ref), create blind receipt, open detail | `GET/POST /receipts` |
| Receipt — detail | `/admin/receipts/:id` | Full receipt: lines, discrepancies, lifecycle control | Receiver, Warehouse Manager | line-level: item, expected/received qty, UoM, lot code, expiry, discrepancy type, status, linked exception | Start (generates operator putaway task), add line (blind receipt placeholder), complete (generates putaway for *actual* received qty), cancel | `GET /receipts/{id}`, `POST /receipts/{id}/start`, `POST /receipts/{id}/lines`, `POST /receipts/{id}/complete`, `POST /receipts/{id}/cancel` |

### Inventory & the ledger

**Exception Queue — `/admin/exceptions`** · Phase 1A · `GET /inventory/exceptions`, `POST /inventory/exceptions/{id}/resolve`

Design this as "a first-class operational surface, not an error log" — it should read like a work queue (a support-ticket inbox), not a log viewer. This is the direct payoff of the command/fact split — design it as seriously as the pick screen.

- **Purpose:** surface every divergence between what was expected and what physically happened — over/under-receipts, short picks, negative balances, location mismatches, reallocated tasks, damage — and drive them to resolution.
- **Primary users:** Supervisor, Inventory Controller, Warehouse Manager (`exception.resolve`).
- **Key data:** summary strip (`open` count, `oldestAgeHours`, counts `byType`) above a filterable list; per-row: exception type, severity, age, item, location, expected vs. actual quantity and difference, linked movement/task, who raised it and on what device, status.
- **Primary actions:** filter by status/type/warehouse/min-age; open a row to resolve — choose `adjust_to_counted` / `accept` / `reallocate` / `write_off` / `escalate`, with a required note (and reason code / photo where the resolution implies a movement).
- **UX notes:** the resolver **cannot be the person who raised the exception** (segregation of duties — server returns `403 same-actor`, but the UI should pre-filter/disable this so it's never attempted). Age-based visual urgency (this is what the dashboard's `oldestAgeHours` metric feeds).

| Screen | Route | Purpose | Primary user | Key fields | Primary actions | Endpoints |
|---|---|---|---|---|---|---|
| Stock Balances | `/admin/inventory/balances` | Query current on-hand/allocated/available by any dimension | Inventory Controller, Auditor | owner, item, location, lot, stock status, on-hand (**may be negative** — real signal, not a bug), allocated, available (computed), updated-at | Filter (item/location/zone/owner/lot/status), toggle "include zero", export | `GET /inventory/balances` |
| Stock Movements (ledger) | `/admin/inventory/movements` | Browse the append-only movement ledger — the audit trail | Auditor, Inventory Controller | sequence, recorded-at (authoritative) vs. device-reported-at (forensic only), business date, movement type, item, lot, from/to location, quantities, reason code, reference, actor, device, `authorizedBy` | Filter (item/location/type/reference/date), keyset-paginate (never offset — this table can be huge) | `GET /inventory/movements` |
| Manual Adjustment — create | `/admin/inventory/adjustments/new` | Record an administrative stock correction | Inventory Controller | owner, item, location, lot, stock status, quantity delta, UoM, reason code (drives `requiresNote`/`requiresPhoto`/`requiresApproval`), note, photo upload | Submit (goes to `pending_approval` if the reason code requires it, else auto-approved) | `POST /inventory/adjustments` |
| Adjustment Approvals queue | `/admin/inventory/adjustments?status=pending_approval` | Approve/reject pending adjustments | Warehouse Manager (`inventory.adjust.approve`, someone other than the requester) | as above + requester identity | Approve (blocked if approver == requester), view photo evidence | `POST /inventory/adjustments/{id}/approve` |
| Reconciliation Runs | `/admin/inventory/reconciliation` | Monitor the scheduled ledger-vs-balance reconciliation job | System Administrator, Inventory Controller | run id, warehouse, started/completed, sequence range covered, rows checked, variance count, status | View run history; a nonzero `varianceCount` should read as a loud alert state, not a routine row | `GET /inventory/reconciliation/runs` |
| Barcode Lookup (utility) | `/admin/lookup` (or embedded search widget) | Resolve a scanned/typed barcode to item+UoM for troubleshooting | any admin role | barcode, resolved item (SKU, name, base UoM, tracking flags), resolved UoM (code, qty-in-base, discrete) | Type/scan a code, see resolution or "unknown barcode" | `GET /lookup/barcode/{code}` |

### Task / work monitoring

| Screen | Route | Purpose | Primary user | Key fields | Primary actions | Endpoints |
|---|---|---|---|---|---|---|
| Expired Leases queue | `/admin/tasks/expired-leases` | Recover work stuck on an unresponsive device | Supervisor | task id/type, zone, holding user, holding device, lease expiry, last heartbeat, "minutes since contact", lines confirmed/total | Review, then **manually** reclaim a task (never automatic — auto-reclaim is a duplicate-pick risk) | `GET /work/leases/expired`, `POST /work/tasks/{id}/reclaim` |

### Degraded-mode / paper fallback

| Screen | Route | Purpose | Primary user | Key fields | Primary actions | Endpoints |
|---|---|---|---|---|---|---|
| Bulk Reconciliation (paper fallback re-entry) | `/admin/inventory/paper-reconciliation` | Key in movements that happened on paper during a full system outage | Supervisor, Inventory Controller | task/receipt reference from the printed PDF, item, location, quantity, reason | Bulk entry form; every row it creates is flagged **manually entered** and excluded from productivity metrics | — (mechanism described in prose, no distinct endpoint documented — see Open Questions) |

### Reporting

| Screen | Route | Purpose | Primary user | Key fields | Primary actions | Endpoints |
|---|---|---|---|---|---|---|
| Stock on Hand report | `/admin/reports/stock-on-hand` | Point-in-time stock position, grouped | Warehouse Manager, Inventory Controller | grouped rows (by zone/item/owner): SKU count, on-hand, allocated, available; totals | Group-by selector, filter by zone/ABC class/as-of-date, export | `GET /reports/stock-on-hand` |
| Report Exports queue | `/admin/reports/exports` | Track async CSV/Excel export jobs (all exports are async — a sync export would blow the 5s API timeout) | any role with `report.export` | job id, status (queued/ready), row count, download link, expiry | Kick off an export from any report screen, watch status, download when ready | `POST /reports/{name}/export`, `GET /reports/exports/{jobId}` |

## Phase 1B

| Screen | Route | Purpose | Primary user | Key fields | Primary actions | Endpoints |
|---|---|---|---|---|---|---|
| Orders — list | `/admin/orders` | Manage sales orders (manually entered in 1B — no ERP intake yet) | Warehouse Manager | order number, status (draft/released/allocating/allocated/partially_allocated/picking/picked/cancelled), priority, required-by | Create order, filter by status, open detail | `GET/POST /orders` |
| Order — detail | `/admin/orders/:id` | Order lines, allocation results, lifecycle | Warehouse Manager | line-level: item, ordered/allocated/picked qty, status, shortfall reason; allocation metadata (requested/processed timestamps, attempts, last error); version | Release (async — returns `202`, poll for result), cancel (in-progress tasks finish, not recalled) | `GET /orders/{id}`, `POST /orders/{id}/release`, `POST /orders/{id}/cancel` |
| Order — allocations detail | `/admin/orders/:id/allocations` | "Where is this order's stock coming from" — the first question asked when a pick goes wrong | Supervisor, Warehouse Manager | per-allocation: location, pick sequence, lot, expiry, quantity, status, linked task line | Read-only investigation view | `GET /orders/{id}/allocations` |

## Phase 2

| Screen | Route | Purpose | Primary user | Key fields | Primary actions | Endpoints |
|---|---|---|---|---|---|---|
| Waves — list | `/admin/waves` | Plan and track pick waves | Warehouse Manager (wave planner) | wave number, status, order count, estimated lines/zones | Create wave (select orders by carrier cutoff/priority/required-by/zone affinity), filter | `POST /waves` |
| Wave — detail/progress | `/admin/waves/:id` | Live progress of a released wave | Warehouse Manager, Supervisor | order counts by state (allocated/partially/picked/packed); task counts (ready/leased/completed); per-zone breakdown with lines-short; consolidation counts (open/complete/short); exception counts | Release wave (async), monitor live progress, cancel (releases held allocations, cancels unstarted tasks) | `POST /waves/{id}/release`, `GET /waves/{id}/progress` |
| Replenishment Rules — list | `/admin/replenishment/rules` | Configure min/max thresholds per pick-face location | Inventory Controller | location, item, owner, min/max quantity, replen UoM, source zone, priority | Create/edit rule | `POST /replenishment/rules` |
| Replenishment Requests queue | `/admin/replenishment/requests` | Monitor threshold- and demand-driven replenishment | Supervisor, Warehouse Manager | trigger type (threshold/demand/manual), location, item, required quantity, status, linked task, **blocked order ids** (demand path — names what's waiting on this refill) | Filter by status/trigger type; trigger a manual run | `GET /replenishment/requests`, `POST /replenishment/run` |
| Carton Types — list | `/admin/carton-types` | Maintain the carton catalogue used by the pack-suggestion strategy | Warehouse Manager | code, name, internal dimensions, tare/max weight, cost | Create/edit | — (schema exists, no documented CRUD endpoint — see Open Questions) |
| Packing Stations — list | `/admin/packing-stations` | Register physical pack benches (printer + scale bindings) | System Administrator | code, location, label printer id, scale device id, active flag | Create/edit | — (schema exists, no documented endpoint) |
| Shipments — list | `/admin/shipments` | Track outbound shipments through packing → dispatch | Warehouse Manager | shipment number, order, carrier/service, status (pending→packing→packed→labelled→manifested→dispatched), total weight, tracking number | Filter by status, open detail | — (endpoints live under `/shipments/{id}/...`, §5.8) |
| Shipment — detail | `/admin/shipments/:id` | Cartons, weights, dispatch | Warehouse Manager | per-carton: type, weight (expected vs. actual), status, contents | View cartons/contents, force-dispatch (admin path parallel to the operator pack flow), view/reprint label | `POST /shipments/{id}/dispatch`, `POST /shipments/{id}/rate`, `POST /cartons/{id}/label` |
| Media Library / search | `/admin/media` | Visual search over item, receipt, carton, return, location photography — "the originally requested benefit" per the proposal | Any staff role with media-read access | thumbnail grid (denormalised `primary_thumb_key`/blurhash — never a per-row media join), role (packing evidence / damage / condition / catalogue), legal hold flag | Search/filter by entity type and date, view full image, set/clear legal hold, delete (blocked if legal hold set) | `GET /media/{sha256}/{variant}`, `DELETE /media/{id}`, `POST /media/{id}/legal-hold` |
| Import Jobs — list | `/admin/imports` | Bulk master-data and opening-balance imports | System Administrator | import type (item/location/barcode/replenishment_rule/opening_balance), status (validating/validated/failed/applying/applied), row/error counts | Upload file, view job status | `POST /imports` |
| Import — detail (errors) | `/admin/imports/:id` | Row-level validation errors before anything is written (validate-then-apply — nothing writes until the whole file passes) | System Administrator | row number, column, message (i18n), raw row | Review errors, fix and re-upload, or apply once clean | `GET /imports/{id}`, apply via `applyUrl` |
| Shrinkage report | `/admin/reports/shrinkage` | Loss/overage by reason code, **never netted** | Warehouse Manager, Inventory Controller | reason code, label, event count, units lost; totals split shrinkage vs. overage | Filter by date range, group by reason code | `GET /reports/shrinkage` |

## Phase 3

| Screen | Route | Purpose | Primary user | Key fields | Primary actions | Endpoints |
|---|---|---|---|---|---|---|
| Count Plans — list | `/admin/count/plans` | Configure cycle-count schedules | Inventory Controller | name, plan type (ABC/location sweep/item/ad hoc/triggered), ABC classes, zones, cron schedule, allowed counting hours (protects peak-picking pick faces from being frozen), tolerance %, requires-recount flag | Create/edit plan, trigger manual generation | `POST /count/plans`, `POST /count/plans/{id}/generate` |
| Count Sheets — list | `/admin/count/sheets` | Track generated count sheets | Inventory Controller | sheet number, status, zone, due date, progress (counted/variance out of total) | Filter by status/zone, open detail | — (list implied; detail is `GET /count/sheets/{id}`) |
| Count Sheet — detail/approve | `/admin/count/sheets/:id` | Review variance lines and approve/reject | Warehouse Manager (not one of the counters — segregation of duties) | per-line: location, item, lot, UoM, status — **`systemQuantity` never appears in this response even to the admin API**, blind counting is enforced by the contract | Per-line decision: `accept_count` / `keep_system`, with reason code + note; approve posts adjustments and unfreezes the location | `GET /count/sheets/{id}`, `POST /count/sheets/{id}/approve`, `POST /count/lines/{id}/recount` |
| Returns (RMA) — list | `/admin/returns` | Track return authorizations | Returns Processor | RMA number, status (expected/receiving/inspecting/dispositioned/closed), customer reference, original order | Create RMA, filter, open detail | `POST /rma` |
| RMA — detail / disposition | `/admin/returns/:id` | Inspect received returns and decide outcome | Returns Processor | per-line: item, expected/received qty, condition (sellable/damaged/defective/missing_parts/wrong_item), disposition (restock/repair/return_to_vendor/scrap/hold), condition photos | Set disposition per line (photo required for any non-sellable condition) | `POST /rma/{id}/lines/{lineId}/disposition` |
| Serial Genealogy lookup | `/admin/serials/:serialNumber` | Full chain-of-custody for one serialized unit — "the feature customers actually buy serial tracking for" | Auditor, Inventory Controller | serial number, item, current status/location, ordered chain of every movement with actor, device, timestamp, reference | Scan/type a serial, view chain (must render fast — this is a single indexed query, not a report) | `GET /serials/{serialNumber}/genealogy` |
| Configuration — schema-driven settings | `/admin/config` | Every operational rule, exposed as generated UI from `config_schema`/`json_schema` — explicitly **not hand-built per key**, because the system will have "several hundred" | System Administrator | category-grouped key list; per key: label/description (i18n), current value at each applicable scope (deployment/warehouse/zone), value type, validation schema, `requiresRestart` flag | Browse by category, edit a value at a chosen scope, see effective-from and previous value, view `config_change_log` | `GET /config/schema`, `PUT /config/{key}` |
| Configuration — export/import | `/admin/config/export-import` | Move a customer's whole configuration as one versioned artefact (support tooling) | System Administrator | export metadata (schema version, deployment id, checksum), entry list (key, scope, value, revision) | Export current config, dry-run import (shows would-apply/would-skip/conflicts), apply import | `GET /config/export`, `POST /config/import` |
| Carriers — list | `/admin/carriers` | Manage carrier adapters and services | System Administrator | code, name, adapter type, active flag; per-carrier services (code, name, transit days) | Add/edit carrier, add/edit service | — (schema exists, no documented endpoint) |
| Inventory Accuracy report | `/admin/reports/inventory-accuracy` | Continuous accuracy proxy between full counts | Warehouse Manager | period, locations counted/accurate, accuracy %, phantom rate (derived from `PICK_NOT_FOUND` events) | Filter by period | `GET /reports/inventory-accuracy` |
| ERP Integration Monitor | `/admin/integrations` | Watch inbound/outbound integration message health | System Administrator | endpoint (system code, direction, transport), message log (type, external id, status: received/processing/processed/failed/skipped, raw payload, error) | Filter by status/endpoint/date, inspect a failed message's raw payload (persisted first, specifically so malformed ERP data is diagnosable) | — (schema exists, no documented REST surface — see Open Questions) |

## Phase 4

| Screen | Route | Purpose | Primary user | Key fields | Primary actions | Endpoints |
|---|---|---|---|---|---|---|
| Fleet Dashboard | `/fleet/dashboard` (separate control-plane console — see Open Questions on whether this shares the product's app shell) | Cross-deployment health at a glance | Internal operations (vendor, not customer) | per-deployment: customer code, ring, app/schema version, config revision, uptime, queue depths (outbox/allocation/exceptions), reconciliation status, DB connection usage, replica lag | Sort/filter by ring/region/status, drill into one deployment | `GET /_fleet/health` (per deployment, aggregated) |
| Deployment — detail | `/fleet/deployments/:id` | One customer's full status and history | Internal operations | deployment metadata, `deployment_event` history (version changes), rolled-up `deployment_metric` (scan p95, exceptions, variance, orders shipped) over time | View history, trigger export-for-support | — |
| Release Rollout management | `/fleet/releases` | Manage ring-based version rollout | Internal operations | version, ring (internal/canary/general), soak hours, halt-on thresholds (error rate, scan p95, any balance variance), rollout status | Start rollout to a ring, monitor, halt (manual or automatic) | `POST /fleet/releases/{version}/rollout`, `POST /fleet/rollouts/{id}/halt` |
| Productivity / Capacity report | `/admin/reports/productivity` | Aggregate throughput by zone/shift/task type (base product — **not** individual scorecards, which are a separately-licensed, off-by-default module) | Warehouse Manager | tasks completed, units, active seconds, standard seconds, performance % — aggregated, not per-operator unless the licensed module is on | Filter by zone/shift/task type/date range | — (schema exists; individual-level view is a distinct, gated screen — see Open Questions) |
| Labour Standards — list | `/admin/labour-standards` | Configure time standards feeding productivity calculations | Warehouse Manager | task type, activity, fixed seconds, per-unit seconds, per-metre seconds, effective-from date | Create/edit standard | — (schema exists, no documented endpoint) |
| EDI Monitor | `/admin/edi` | Track EDI document exchange with trading partners | System Administrator | partner (ISA qualifier/id, transport), document log (type: 856/940/945/214/850/810, direction, control number, status, raw payload retained for dispute resolution) | Filter by partner/type/status, inspect raw payload | — (schema exists, no documented endpoint) |
| Slotting Analysis | `/admin/slotting` | Review and accept/reject slotting recommendations (**recommendations only — never automatic execution**) | Warehouse Manager, Inventory Controller | analysis run metadata, per-recommendation: item, from/to location, reason, projected time saving, status (proposed/accepted/rejected/applied) | Trigger analysis run, review recommendations, accept (generates an ordinary `move` task) or reject | — |
| WCS/Automation Endpoints | `/admin/automation-endpoints` | Register conveyor/AS-RS/AMR/sorter integration endpoints | System Administrator | warehouse, system type, protocol, config, active flag; linked `automation_task` status (sent/acknowledged/in_progress/completed/failed) | Register endpoint, monitor task dispatch health | — |

---

# Operator Handheld (`/operator/*`)

All screens here are installable-PWA, single-task, offline-capable **unless otherwise noted**, and drive every mutation through `POST /sync/facts`. None of them show a generic "error" for a physical discrepancy — they raise an exception behind the scenes and let the operator continue immediately.

## Phase 1A

### Sign-on and home

| Screen | Route | Purpose | Primary user | Key fields | Primary actions | Endpoints |
|---|---|---|---|---|---|---|
| Operator Login (badge/PIN claim) | `/operator/login` | Claim a pooled handheld for a shift | Any operator role | badge scan, or employee code + PIN | Scan badge / enter PIN to claim device | `POST /auth/operator/login` |
| Device Claimed conflict | (state of the login screen, not a separate route) | Handle "this handheld is already claimed by someone else" | Any operator | claimed-by name, claimed-since timestamp | Prompt to contact the current holder | `409 device-claimed` response |
| Home / task menu | `/operator/home` | Role-driven landing screen — shows only functions the operator holds permission for (a Picker's home shows picking, not "eleven modules with nine disabled") | Any operator role | permission-gated tile list (Receive / Putaway / Pick / Pack / Count / Replenish / Move, per phase and per role) | Start next available task in a category, or lease manually | `GET /auth/me` (permissions/scopes) |
| Idle-lock overlay | (overlay state, not a route) | Screen lock on idle timeout **without ending an in-progress task** — badge re-scan resumes exactly where it was | Any operator role | — | Re-scan badge to resume | — |
| Sync/offline status indicator | (persistent header component) | Show queue depth and connectivity so an operator knows whether their last confirm actually landed yet | Any operator role | queue depth, last synced time, connectivity state | Manual "retry sync" affordance | `GET /sync/status`, device heartbeat |
| Forced update block screen | (full-screen block state) | Stop a client below `minClientVersion` from continuing | Any operator role | current vs. minimum version | Update app | `426 client-too-old` on any request |

### Receiving

**Receiving task screen — `/operator/receive/:taskId`** · Phase 1A · blind receipt against a supervisor-created receipt

- **Purpose:** scan-count items against a receipt line, including over/under/damage — the flow that proves the command/fact split (Phase 1A exit criterion 5: "expected 100, counted 106 → records 106 and raises an exception, never an error").
- **Primary user:** Receiver.
- **Key data:** item (name in operator's locale, barcode, thumbnail), expected quantity (if not blind), UoM, lot code/expiry entry (if lot-tracked), destination (receiving location), applicable reason codes for this fact type.
- **Primary actions:** scan item barcode (resolved from a locally cached barcode map — no server round trip needed to scan), enter/confirm counted quantity, select reason code if the count diverges (e.g. `RCV_SUPPLIER_OVER`), enter lot/expiry if tracked, confirm — queues a `receipt_confirmed` fact and the UI **advances immediately**, no waiting on the server.
- **UX notes:** large numeric entry optimized for one-handed/gloved use. A quantity divergence never blocks — it queues normally and raises an exception behind the scenes. `RCV_SUPPLIER_OVER`-class codes carry `requires_approval = true`, so this screen must be able to trigger the supervisor-override modal inline. A line can be confirmed more than once (two receivers working one pallet, or a device resuming a drained queue) — this is not a re-scan bug, the accumulated total is what the server tracks.

### Putaway

**Putaway task screen — `/operator/putaway/:taskId`** · Phase 1A · fully offline-capable, the flow the whole offline architecture is proven against

- **Purpose:** direct an operator from a receiving/staging location to a system-directed bin, one leased task at a time, confirmed entirely offline.
- **Primary user:** Putaway Operator.
- **Key data:** source location, destination location + pick sequence, item (name, barcodes, thumbnail), lot/expiry if tracked, requested quantity, UoM, is-discrete flag.
- **Primary actions:** scan source location → scan item barcode → scan destination location → confirm quantity → confirm. Each queues locally with a `clientFactId` generated at the moment of the action (not at send time — this is what makes offline replay after a reboot safe).
- **UX notes:** batch of up to 20 tasks leased and cached to IndexedDB at once, worked entirely offline, synced when connectivity returns. A **partial** putaway (60 of 100 moved) is not a divergence — it closes the line as `short` silently, nothing to resolve. Putting stock in a *different* bin than directed (because the directed bin was full) is recorded as-is and raises a `location_mismatch` exception, never rejected — the UI must let the operator scan whatever bin they actually used, not force them back to the directed one. **This is the screen to design/prototype first and test hardest on real hardware** — it's the one Phase 1A exit criterion built specifically around airplane-mode-then-reboot survival.

### Supervisor override

**Supervisor override modal** · shared pattern, appears over any operator screen · Phase 1A

- **Purpose:** authorize an action the current operator lacks permission for, without changing the device session.
- **Trigger:** any `403` carrying `elevation: { possible: true }`.
- **Flow:** modal requests a supervisor badge scan → `POST /auth/elevate` → single-use grant token (2-minute expiry, bound to this specific action+context+operator) → original action resubmits with the grant → movement records both `actor_user_id` (operator) and `authorized_by_user_id` (supervisor).
- **UX notes:** requires connectivity (elevation cannot happen offline — `503` if unreachable, and the UI needs a clear "needs signal" state for this specific case since it's otherwise indistinguishable from a normal offline queue). Server rejects if the supervisor is the same person as the operator — design the modal so that failure reads clearly rather than as a generic error. Probably contributes more to floor throughput than any other single design decision — worth real design attention, not a bolted-on dialog.

## Phase 1B

**Pick task screen — `/operator/pick/:taskId`** · scan-verified, offline-capable

- **Purpose:** direct a picker to a bin, verify the correct item by scan, confirm quantity — including short picks.
- **Primary user:** Picker.
- **Key data:** location + pick sequence, item (name, barcodes, **product thumbnail — this is the wrong-item error-prevention feature**), lot/expiry if tracked, requested quantity, UoM.
- **Primary actions:** scan bin → scan item → confirm quantity (defaults to requested, editable down for a short pick) → confirm.
- **UX notes:** a short pick (`quantity < requested`) is **deliberately friction-free** — no note, no photo, no approval required for the reason codes used here (`PICK_SHORT`/`PICK_NOT_FOUND`), because friction at an empty bin causes operators to pick a wrong code or skip the task, destroying the accuracy signal the whole system depends on. Do not design an "are you sure" dialog into this path.

## Phase 2

| Screen | Route | Purpose | Primary user | Key fields | Primary actions | Endpoints | UX notes |
|---|---|---|---|---|---|---|---|
| Replenishment task screen | `/operator/replenish/:taskId` | Move stock from bulk reserve to a pick face | Putaway Operator / Picker (interleaved with other work) | source (bulk) location, destination (pick face), item, requested quantity, UoM | Scan source → item → destination → confirm | ordinary `replen_confirmed` fact, same task engine as putaway/pick | The payoff for the generic task engine: a new work type costs a strategy and a screen, not a subsystem. |
| Zone-pick / container staging screen | variant of `/operator/pick/:taskId` | Stage a completed zone tote at a consolidation point (multi-picker orders) | Picker | container code, staging location, consolidation id | Scan tote → scan staging location → confirm | `container_staged` fact | Order can't proceed to packing until all zones' totes arrive; a short-picked zone marks the consolidation `short` rather than blocking indefinitely. |
| Packing screen | `/operator/pack/:shipmentId` | Scan tote → select/confirm carton → scan contents → weigh → resolve | Packer | resolved shipment (via `task.pick_container_id`), suggested carton type + reason, per-line remaining-to-pack, expected vs. actual weight | Scan carton contents (**a wrong-item scan is legitimately rejected here** — nothing physical has happened yet, unlike every other operator screen), weigh carton, request label | `POST /cartons/{id}/contents`, `POST /cartons/{id}/weigh`, `POST /cartons/{id}/label` | The one operator command-path screen that *can* show a real rejection dialog (wrong item, quantity exceeds line) — design it differently from the fact-based screens around it. Weight mismatch **blocks** rather than warns (catches double-picks scan verification can't). |
| Photo capture (shared component) | embedded in receiving/adjustment/pack/return flows | Capture packing evidence, damage, condition photos | Any operator role whose flow requires it | — | Take photo → client-side compress to ~1600px/q80 **before** upload (mandatory, not optional — a 12MP raw photo over warehouse wifi is the difference between the feature working and not) | `POST /media/presign`, `POST /media/confirm` | Design as a reusable full-screen camera capture, not a small inline widget — it needs to work one-handed and show compression/upload progress clearly since uploads can lag behind the fact queue. |
| Move / bin-to-bin transfer | `/operator/move` | Ad-hoc stock move outside a directed task | Putaway Operator, Supervisor | source, destination, item, quantity, reason code | Scan source → item → destination → confirm | `move_confirmed` fact | Never raises an exception — an ad-hoc move has no "expected" to diverge from. |

## Phase 3

| Screen | Route | Purpose | Primary user | Key fields | Primary actions | Endpoints | UX notes |
|---|---|---|---|---|---|---|---|
| Cycle count screen | `/operator/count/:sheetId` | Count a frozen location, blind | Auditor / any assigned operator | location, item, lot, UoM — **counted quantity is the only number this screen ever shows; the system's expected quantity is never sent to the device**, enforced by the response schema itself | Scan location → scan item → enter counted quantity → confirm | `count_confirmed` fact | Design this to *not* show any "expected" field even as a placeholder — the blind-count guarantee is a product promise, not an incidental omission. |
| Recount screen | variant of count screen | Recount a line that came back outside tolerance | A **different** operator than the original counter (server assigns preferentially) | same as count screen | Same flow | `count_confirmed` fact against a `recount` line | — |
| Return receipt screen | `/operator/returns/:rmaId` | Receive goods against an RMA | Returns Processor | item, expected quantity, condition selector (sellable/damaged/defective/missing_parts/wrong_item), photo requirement gated on condition | Scan/count → select condition → photo if non-sellable → confirm | `return_received` fact | — |
| Disposition screen | `/operator/returns/:rmaId/disposition` | Inspector decides outcome per returned line | Returns Processor / Inspector | disposition options (restock/repair/return_to_vendor/scrap/hold), target location, reason code, photo | Select disposition → confirm | `POST /rma/{id}/lines/{lineId}/disposition` (command, not a fact) | This is a command path, can legitimately show a validation error (e.g. missing required photo). |
| Serial scan capture (embedded) | embedded in receive/putaway/pick/pack/dispatch/return screens | Extra scan step for serialized items — roughly doubles scan effort, so it should be visually distinct from a normal barcode scan | Any operator role touching a serialized item | serial number, context (which flow it's captured in) | Scan serial in addition to the item barcode | `serial_scanned` fact | An unknown serial or one in an incompatible state is **still recorded**, with an exception raised — same never-block philosophy as everything else on the fact path. |

---

# Open Questions / Design Decisions Not Fully Specified

These are places the documentation states a capability or a data model but doesn't pin down an exact screen shape — a designer will need to make a call, or the team should resolve it before design starts rather than during it.

1. **Reason code maintenance.** `GET /reason-codes` is documented (read path for operator screens), and the schema (§2.7) says "customers extend them through configuration," but no `POST/PATCH /reason-codes` endpoint appears in §5. Is reason-code editing part of the generic Phase 3 configuration UI (§5.11/§10.4), or does it need its own dedicated maintenance screen in 1A? Listed above as read-only in 1A pending that decision.
2. **Auth audit log read endpoint.** `auth_event` is a fully specified table (§2.1, §8.1, §10.10) described as essential to shrinkage investigations ("who granted this person adjustment-approval rights, and when"), but §5 never documents a `GET` for it. The admin screen is listed above on the strength of the data model and its stated importance, not a confirmed endpoint. Whatever endpoint is designed needs its own permission (an `audit.read`-shaped addition to the catalogue, most naturally held by the Auditor/System Administrator role bundles) rather than being gated by an existing permission that happens to be held by the right people today — a table of login/lockout/override/credential-change events is itself a privilege boundary, not an incidental read.
3. **Bulk paper-fallback re-entry.** §8.3 and the architecture review both describe the *process* (hourly PDF export during an outage, staff work from paper, movements keyed in afterwards through a bulk reconciliation screen, flagged as manually entered and excluded from productivity metrics) in enough detail to be clearly a required screen, but never names its route or endpoint. Treat the mechanism as settled, the screen's exact shape as open.
4. **Several Phase 2–4 CRUD surfaces exist only as schema, not as documented endpoints**: `carton_type`, `packing_station`, `carrier`/`carrier_service`, `labour_standard`, `edi_partner`, `integration_endpoint`, `automation_endpoint`. All are straightforward admin CRUD grids by the pattern established elsewhere (items, locations, zones), so they're listed above with that assumption, but none has a documented request/response shape to design against literally.
5. **Individual productivity scorecards.** §11.5 is explicit these are a separately-licensed, off-by-default module ("where the monitoring obligations attach... visible to the operators they describe"), distinct from the base-product aggregate report. Worth designing as a genuinely separate screen/entry point (with its own visible-to-the-operator-it-describes view), not a toggle on the aggregate report — this is a commercial/legal boundary, not a UI preference.
6. **`inventory_exception` resolution actions vs. UI form.** The `resolve` endpoint accepts five actions (`adjust_to_counted`/`accept`/`reallocate`/`write_off`/`escalate`) with somewhat different required fields per action (a movement is created for two of the five, not the other three) — the exact form/validation behavior per action isn't spelled out beyond one example in §5.5, so the resolution modal's field-by-action logic is a design decision.
7. **Fleet console access model.** Part IV frames the fleet control plane as "a separate service... operated by you, not deployed per customer," which suggests its screens (`/fleet/*` above) may not even live in the same route-tree/app as `/admin` and `/operator` — possibly a wholly separate internal tool. Confirm before designing it as if it shares the product's design system 1:1.
8. **No documented way to resolve a "device already claimed" conflict.** §6.20's login flow and §5's `409 …/device-claimed` response (which the Device Claimed conflict screen above is built from) only describe the block, not a way through it — there is no endpoint that ends another operator's session on that device. `POST /auth/elevate` doesn't fit either: it issues a single-use grant scoped to one `(action, contextId)`, not a session-termination command. The only documented way a device session force-ends today is the `lost` cascade on `PATCH /devices/{id}` (an admin action, not something an operator triggers from the login screen). Until this is designed as its own capability — with its own permission, scoped to the device's `warehouse_id`, and the same session-end-plus-refresh-token-revocation behaviour as the `lost` cascade — the conflict screen should offer nothing beyond "contact the current holder."
9. **`account-expired` and lockout screens need a stated audience, not an assumption.** `POST /auth/operator/login`'s `403 …/account-expired` and the credential-lockout responses aren't yet listed above with an explicit "who can clear this" note; whoever designs the recovery path should confirm it routes through the same `user.manage`/`credential.manage`-scoped admin flow as everything else in the identity/access rows, rather than inventing a separate unlock mechanism.

---

# Summary

| Route tree | 1A | 1B | 2 | 3 | 4 | Total |
|---|---|---|---|---|---|---|
| `/admin/*` | 26 | 3 | 11 | 11 | 7 | 58 |
| `/operator/*` | 8 (incl. 2 shared/overlay) | 1 | 5 | 5 | 0 | 19 |
| **Combined** | **34** | **4** | **16** | **16** | **7** | **77** |

Counts include shared components (supervisor-override modal, photo capture, idle-lock overlay, sync indicator) counted once at the phase they're introduced, not once per screen they appear on. Phase 1A dominates the operator count because most later operator work (replenish, count, return) reuses the same task-screen pattern established there rather than inventing new ones.

**For a design tool handoff, prioritize in this order:**

1. **Phase 1A operator screens** (8) — smallest set, highest architectural risk (offline, scan-verified, exception-raising), and the ones Phase 1A's own exit criteria are built around. Putaway is the single screen to get right first.
2. **Phase 1A admin screens** (26) — the active build target per `CLAUDE.md`. Dashboard and Exception Queue are the highest-value screens to nail visually; the rest are largely CRUD-grid variations on one pattern (list + detail/edit with `If-Match` optimistic concurrency).
3. **Phase 1B–4 screens** — design later, but the route/field/endpoint groundwork here means a design tool can extrapolate the established patterns (list/detail admin grids, single-task offline operator screens) rather than starting cold each phase.
