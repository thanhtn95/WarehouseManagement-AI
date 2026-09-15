# Warehouse Management System — Project Proposal & Technical Plan

**Status:** Draft v0.1 — for review
**Date:** September 2026
**Author:** T

---

## 1. Executive Summary

This document proposes the design and phased delivery of a Warehouse Management System (WMS) intended to run real commercial warehouse operations — not a stock-tracking CRUD application.

The distinction matters. A stock tracker answers "how many do we have?" A WMS directs physical work: it tells a specific person to walk to a specific bin, pick a specific quantity, and confirm it by scan, then keeps a defensible audit trail of what happened. Everything in this plan follows from that difference.

The system will be built as a **modular monolith** on **.NET and PostgreSQL**, with **two separate React frontends** (a desktop admin console and a handheld scanner PWA), packaged with **Docker Compose** and delivered through **GitHub Actions**.

Three architectural commitments define the project and are made deliberately up front, because all three are extremely expensive to retrofit:

1. **Inventory is an append-only ledger**, not a mutable quantity column.
2. **A generic task engine** drives all directed work (pick, putaway, count, replenish, move).
3. **Lot/serial traceability and media attachment** are designed in from Phase 2, even where not yet exposed in the UI.

---

## 2. Objectives

| # | Objective | Success measure |
|---|---|---|
| O1 | Provide accurate, auditable, real-time inventory | Ledger-derived balances reconcile to zero variance against physical cycle counts |
| O2 | Direct floor work rather than record it after the fact | Operators receive assigned tasks; no free-text stock entry in normal operation |
| O3 | Eliminate wrong-item shipping errors | Scan-verified picking with product imagery at confirmation |
| O4 | Survive real warehouse conditions | Operation continues through wifi dead spots and short network outages |
| O5 | Support operational growth without re-platforming | Architecture accommodates additional sites and volume without redesign |

### Out of scope (initial release)

- Warehouse Control System (WCS) functions — conveyor, AS/RS, AMR fleet control. The WMS will expose an integration boundary for these but will not implement them.
- Transport Management (route planning, fleet dispatch, freight procurement).
- Full accounting/GL. The WMS posts inventory events; financial posting belongs to the ERP.
- Demand forecasting and purchasing.
- Automated slotting optimisation (deferred to a later phase; data will be captured to enable it).

---

## 3. Assumptions and Open Decisions

This plan is written against the assumptions below. Several are load-bearing and should be confirmed before Phase 2 development begins, because they change scope materially.

### Resolved decisions

| # | Decision | Resolution |
|---|---|---|
| D1 | Own-operator or 3PL | **Own-operator.** The product targets companies running their own warehouses. Billing engine, client portal, and per-client rule configuration are out of scope |
| D1a | Internal tool or product | **Commercial product.** Sold to multiple customers, which makes configurability and fleet operations first-class concerns (§12) |
| D1b | Delivery model | **Single-tenant.** One isolated deployment per customer. Isolation is at the process and database boundary; no `tenant_id` column and no row-level security |
| D2 | Hosting | **Cloud-hosted, connectivity required for server operation.** No on-site database, no edge appliance, no hybrid replication |

**D2 applies to the server, not to the devices.** In-building offline tolerance remains mandatory and unchanged: wifi dead spots in racking are unrelated to internet connectivity, and a picker in a poorly covered aisle must still complete the task in hand. Task leasing, the command/fact split, the offline queue, idempotency keys, and the exception queue all remain as specified.

Two consequences of D2 that are easy to miss:

- **Host in-region.** Every scan is now a network round trip. Routing warehouse traffic over international submarine cables — which have suffered repeated simultaneous faults — converts a working product into an unusable one during degradation periods, and the customer attributes that to the software.
- **The degraded-mode plan (H8 in the architecture review) becomes more important, not less.** Customers will lose access to a cloud-hosted system through their own ISP, through regional connectivity faults, and through your deployments. The paper fallback is what stops that being a lost operating day.

### Resolved — product and domain

| # | Decision | Resolution |
|---|---|---|
| D3 | Lot / batch tracking | **Yes.** `lot_id` is part of the stock position key from the first migration |
| D3a | Expiry dates and FEFO | **Yes.** Lots carry expiry; FEFO becomes a selectable allocation strategy in Phase 2 |
| D4 | Serial-number tracking | **Yes**, modelled as a separate `serial_unit` aggregate rather than through the quantity ledger. Exposed in Phase 3 |
| D4a | Stock ownership | **`owner_id` retained** in the position key, seeded to one owner per deployment. Supports consignment and vendor-managed inventory |
| D5 | Target scale | **50+ concurrent operators, multiple zones.** Roughly 60,000 order lines/day at the design ceiling |
| D5a | Unit-of-measure depth | **Full four levels:** each, inner, case, pallet |
| D10a | Target vertical | **General merchandise.** Not regulated goods; lot genealogy is supported but not compliance-driven |
| D11 | Interface languages | **English and Japanese** throughout, admin and operator alike — both are served by one application (D15). i18n infrastructure keeps adding a language a configuration change |
| D12 | Hosting region | **Domestic**, avoiding international submarine cable dependency for warehouse traffic |

### Consequences of D5 (scale)

The 50-operator target is not a tuning parameter. Three things move as a direct result:

- **Wave planning and zone picking move from Phase 3 to Phase 2.** Discrete order-at-a-time picking does not work at this operator count — it produces aisle congestion and repeated travel over the same ground.
- **Replenishment moves from Phase 3 to Phase 2.** At this throughput, pick faces empty within hours. Without automatic replenishment operators arrive at empty bins and the directed-work model collapses.
- **Partitioning is no longer deferred.** At roughly 40–50 million movements per year, `stock_movement` is range-partitioned by month from the first migration. The composite key `(id, occurred_at)` was already specified for exactly this.

### Deferred with a revisit trigger

| # | Decision | Position | Revisit when |
|---|---|---|---|
| D10 | Handheld hardware | **Mid-range Android for now**, using camera or Bluetooth scanner | Before the first customer pilot |
| D15 | Frontend split | **One application, two route trees** — admin and operator screens share a build and deployment | When operator screen count or offline complexity makes a separate build worthwhile |

Camera scanning costs roughly 1.5–3 seconds per scan against 0.2–0.5 for a dedicated imager. At the D5 volume that difference is on the order of tens of operator-hours per day, and it degrades further in dim aisles, on curved or shrink-wrapped labels, and at high rack levels. Camera capture is therefore suitable for development, demonstration, and low-volume tasks — not as the primary picking path at scale.

**This deferral is made safe by a scan-input abstraction, which is a Phase 1A requirement.** Keyboard-wedge input (Bluetooth scanners, Zebra DataWedge), camera capture, and manual keyboard entry all feed one pipeline. Device support then becomes configuration rather than a port, and upgrading the hardware position later costs a settings change instead of a rewrite. Since the product is sold, the device is each customer's choice anyway.

### Remaining open decisions

Configurable per customer rather than decided once, unless noted:

| # | Decision | Why it matters |
|---|---|---|
| D6 | **Shift pattern** | Per customer. Determines deployment windows |
| D7 | **Identity provider** | Per customer. Office users federate where a corporate directory exists; operators are always local |
| D8 | **Badge infrastructure** | Per customer, but the product supports both badge and PIN from the first release |
| D9 | **Stated RPO and RTO** | A contractual matter with customers rather than an internal assumption |
| D14 | **First design partner** | Open. No identified first customer; see Risks |

### Working assumptions

- Single site initially, with a second site plausible within 24 months.
- Barcode-driven operation using Zebra/Honeywell-class Android handhelds operating in keyboard-wedge mode.
- An existing ERP is the master for purchase orders, sales orders, and product master data; the WMS owns everything from goods receipt to dispatch.
- Team is small. The plan therefore favours a modular monolith over microservices and Docker Compose over Kubernetes.

---

## 4. Core Domain Model

Five concepts form the spine. Everything else is built on them.

### 4.1 Stock movement ledger

The central design decision. Stock levels are **derived** from immutable movement records; there is no `UPDATE inventory SET qty = qty - 5` anywhere in the system.

```
stock_movement
  id, occurred_at, sku_id, lot_id?, serial_id?,
  from_location_id?, to_location_id?, handling_unit_id?,
  quantity, uom, reason_code,
  reference_type, reference_id,      -- receipt, order, count, adjustment
  actor_id, device_id, created_at
```

Rationale:

- **Auditability.** Every unit's history is reconstructible. This is what makes recall, dispute resolution, and shrinkage investigation possible at all.
- **Concurrency.** Appending rows avoids hot-row contention when many operators transact against the same SKU simultaneously.
- **Correctness detection.** A materialised balance table can be independently recomputed from the ledger and diffed. Discrepancy means a bug, and you find it on a schedule rather than during a stock take.

A `stock_balance` table is maintained incrementally within the same transaction as the movement insert, and serves all read paths. Its grain is **owner × SKU × location × lot × status**, which is also the StockPosition aggregate boundary. Serialised SKUs are held instead in a separate `serial_unit` aggregate carrying current location and status, with balances derived by count.

`stock_movement` is range-partitioned by month from the first migration, with a composite key of `(id, occurred_at)`.

### 4.2 Location topology

`Warehouse → Zone → Aisle → Bay → Level → Bin`

Each bin carries: type (pick face, bulk reserve, staging, quarantine, returns, damage), volume and weight capacity, **pick sequence number** (the physical walk order — this is what makes pick-path optimisation possible), and constraints (temperature, hazmat, mixed-SKU permitted).

### 4.3 Handling units (LPN)

A pallet, cage, or tote receives its own barcode identity and *contains* stock. Moving a pallet is one scan, not forty line items. Nesting is supported (cases on a pallet).

This is routinely omitted from in-house builds and then bolted on painfully. It is included from the start.

### 4.4 Item master with unit-of-measure hierarchy

Each / inner / case / pallet, with conversion factors, dimensions, weight, and barcode per level. Plus: lot-tracked flag, serial-tracked flag, shelf-life rules, ABC classification, storage constraints.

UoM conversion errors are one of the most common defect classes in warehouse software; modelling the hierarchy explicitly rather than assuming "eaches" prevents most of them.

### 4.5 Three distinct quantities

**On hand** — physically present.
**Allocated** — committed to a released order.
**Available** — on hand minus allocated, minus quarantine.

Soft reservation occurs at order entry; hard allocation at wave release; the ledger decrements at pick confirmation. Collapsing these into a single number causes overselling.

---

## 5. Functional Scope

### 5.1 Inbound

- Advance Ship Notice (ASN) ingest from ERP or supplier
- Receipt against purchase order, including blind receipt
- Over/under/damage recording with **mandatory photo capture on discrepancy**
- Quality inspection and quarantine hold
- Directed putaway, driven by configurable strategy (nearest empty, consolidate with existing stock, zone-constrained, capacity-aware)
- Goods Received Note posting back to ERP

### 5.2 Storage and internal movement

- Replenishment: bulk reserve → pick face, triggered on min/max thresholds
- Cycle counting: ABC-driven schedule, blind counts, variance approval workflow, count-generated adjustments with reason codes
- Ad-hoc moves and bin-to-bin transfers
- Kitting, de-kitting, and repack
- Stock status transitions (available / quarantine / damaged / on hold)

### 5.3 Outbound

- Order intake from ERP or e-commerce platform
- Allocation by configurable strategy: FEFO, FIFO, LIFO, nearest-bin, whole-case-first
- Wave planning and release (with a waveless/continuous mode as an alternative)
- Pick task generation with route sequencing
- Pick methods: discrete, batch, zone, and cluster
- Scan-verified picking with product image displayed at confirmation
- Packing with carton selection, **packing evidence photo**, and content verification
- Carrier label generation and shipment manifest
- Dispatch confirmation and ASN out

### 5.4 Reverse logistics

- RMA receipt
- Inspection with condition photography
- Disposition: restock / repair / return to vendor / scrap

### 5.5 Task engine (cross-cutting)

All directed work — pick, putaway, count, replenish, move — shares one model: a work item with type, priority, state machine, assignment rules, and interleaving support (an operator walking past a due replenishment task can be offered it).

This is deliberately built as shared infrastructure rather than as separate per-process implementations. It is where most of the system's operational value concentrates, and building it once is the difference between adding a new work type in a day and adding one in a month.

### 5.6 Media and imagery

Cross-cutting capability, attachable to SKUs, receipt lines, shipments, returns, and locations.

Primary operational value is **not** catalogue browsing — it is error prevention and evidence:

- **Product image at pick confirmation** catches wrong-item errors before they reach packing
- **Damage photos at receipt** convert supplier disputes from argument into evidence
- **Packing evidence photos** are the standard defence against "the carton arrived empty" claims, and typically the highest-return media feature in the system
- **Return condition photos** drive and justify disposition decisions
- **Visual search** in the admin console, which is the originally requested benefit

### 5.7 Identity and access management

- Dual authentication: conventional login for office staff, badge or PIN for floor operators
- Shared-device session management on handhelds, with fast operator switching
- In-place supervisor override for exception authorisation
- Permission-based roles with warehouse and zone scoping
- Segregation-of-duties enforcement on approval workflows
- Role-driven UI: operators see only the functions they hold permissions for
- User lifecycle management including time-bounded accounts for temporary labour
- Authentication and authorisation audit log

Detailed design in §10.

### 5.8 Configuration (product requirement)

Because the system is sold rather than built for one operator, every operational rule must be **configurable as data through an interface**, by an implementation consultant who cannot deploy code:

- Allocation strategy per SKU class, zone, or order type
- Putaway strategy and capacity rules
- Replenishment thresholds
- Packing and carton-selection rules
- Pick method selection (discrete, batch, zone, cluster)
- Task priority and interleaving policy
- Business day boundary, units, locale, label templates

This is not a convenience feature. In a single-tenant product, the alternative to configuration is per-customer code branches — which is comfortable for the first two customers and fatal by the sixth. Configurability is therefore Phase 2 infrastructure for the strategies that exist then, extended as strategies are added. Phase 1 hardcodes a single strategy deliberately, and records that as a tracked shortcut.

### 5.9 Stock ownership

`owner_id` is carried on stock positions and seeded with a single owner per deployment.

The own-operator target does not mean one owner. Consignment and vendor-managed inventory — where a supplier retains ownership until stock is consumed — are common requirements, and ownership is operationally binding: stock belonging to one owner cannot be allocated to fill another owner's demand, even for the same SKU in the same bin.

Unlike tenancy, this is confined to the stock tables rather than the entire schema, so the cost of carrying it is small and the retrofit cost is high.

---

## 6. Non-Functional Requirements

| Area | Requirement | Rationale |
|---|---|---|
| Availability | Operation continues through short network interruptions | Wifi dead spots exist in every racking layout |
| Scan responsiveness | Task confirmation feels immediate to the operator | Latency here directly reduces picks per hour |
| Concurrency | No two operators are ever assigned the same unit of work | Correctness requirement, not a performance target |
| Idempotency | Every mutating endpoint accepts an idempotency key | Handhelds on unreliable wifi retry automatically |
| Auditability | Every stock change records actor, device, timestamp, reason | Regulatory and dispute requirement |
| Data retention | Operational photos have a defined lifecycle with legal hold | Unbounded growth otherwise; see §8.4 |
| Recoverability | Backups are automated and restores are tested on a schedule | An untested backup is a hypothesis |

Specific numeric SLOs should be set once volume figures (D5) are known. Setting them now would be inventing numbers.

---

## 7. Technology Stack and Reasoning

### 7.1 Summary

| Layer | Choice |
|---|---|
| Backend | .NET (ASP.NET Core), modular monolith |
| Database | PostgreSQL |
| Data access | EF Core for writes, Dapper for hot read paths |
| Schema migrations | Raw SQL via DbUp or Grate |
| Object storage | S3-compatible (MinIO on-prem, or cloud equivalent) |
| Admin frontend | React SPA + TanStack Query + TanStack Table |
| Handheld frontend | React PWA with service worker and IndexedDB queue |
| Runtime packaging | Docker + Docker Compose |
| CI/CD | GitHub Actions, images in GHCR |
| Background work | Hosted `BackgroundService` (outbox), Hangfire (scheduled jobs) |
| Integration testing | Testcontainers against real PostgreSQL |

### 7.2 Why PostgreSQL

The workload is transactional, highly relational, and correctness-critical — the case where a mature relational database is straightforwardly the right tool. Specific capabilities this design depends on:

- **`SELECT ... FOR UPDATE SKIP LOCKED`** — the correct primitive for task dispatch. Multiple operators poll the same queue; each gets a distinct task without lock contention or a separate queue broker.
- **Declarative partitioning** — `stock_movement` is the table that grows to hundreds of millions of rows. Monthly range partitioning keeps index depth and maintenance windows manageable.
- **Partial and expression indexes** — e.g. enforcing one primary image per SKU via `CREATE UNIQUE INDEX ... WHERE is_primary`, or indexing only open tasks.
- **Genuine transactional integrity** across ledger insert and balance update, which the entire correctness model rests on.
- Permissive licence, strong operational tooling, no per-core cost as the deployment grows.

### 7.3 Why .NET

- Strong typing and a mature concurrency model suit a domain where an arithmetic or race-condition error means physical inventory loss.
- Long-term support releases with predictable upgrade cadence, appropriate for a system expected to run for years with limited maintenance attention.
- Excellent PostgreSQL support via Npgsql.
- First-class background service hosting, so outbox processing and media rendering are in-process concerns rather than additional infrastructure.

### 7.4 Why a modular monolith, not microservices

Modules (`Inventory`, `Tasks`, `Inbound`, `Outbound`, `Catalog`, `Media`, `Billing`) live in one solution and one deployable, with enforced boundaries.

Rationale:

- The core invariants are **transactional across modules**. Allocating stock and creating a pick task must be atomic. In a distributed architecture this becomes a saga with compensating actions — significant complexity for no benefit at this scale.
- A small team operating a distributed system spends its time on infrastructure rather than warehouse features.
- Module boundaries are enforced in CI via architecture tests (NetArchTest). Modules communicate through public contracts and in-process messages, never by reaching into each other's tables. This discipline is what keeps a future extraction cheap if scale ever demands it.

Two processes are deployed from the solution: **API** and **Worker**. Media rendition and outbox draining must never compete with an operator's scan for thread-pool capacity.

### 7.5 Why EF Core *and* Dapper

Domain mutations benefit from change tracking, unit of work, and modelled relationships — EF Core. Pick lists, search, and reporting want hand-written SQL with exact index usage — Dapper. Both run over the same Npgsql connection and participate in the same transaction. Using one tool for both means either fighting the ORM on read performance or hand-rolling persistence for writes.

### 7.6 Why *not* EF Core migrations

The schema requires declarative partitioning, partial indexes, `SKIP LOCKED` queue semantics, and likely triggers — all of which EF Core migrations obstruct or cannot express. Plain versioned `.sql` files applied by DbUp or Grate keep the schema readable and reviewable. These files will be read during production incidents; clarity outweighs abstraction.

### 7.7 Two interfaces, one application

The admin console and the operator handheld share almost nothing in interaction terms:

| | Admin console | Operator handheld |
|---|---|---|
| Screen | Desktop, 1920px | ~4–6", one-handed, gloved |
| Input | Mouse and keyboard | Scanner, minimal typing |
| Interaction | Dense tables, filters, dashboards | One task, large targets, near-zero navigation |
| Network | Assumed reliable | Assumed intermittent |
| Offline | Not required | Required |

**Decision (D15): one application, two route trees** — not two separately built and deployed applications.

- Same repository, build, deployment, and origin
- Shared auth, generated API client, i18n catalogue, and design tokens
- `/admin/*` — desktop layouts
- `/operator/*` — task-at-a-time layouts, scanner-driven
- **Service worker, IndexedDB queue, and offline sync scoped to the operator routes only**

The distinction that matters is the *screen set*, not the deployment artefact. A single responsive layout serving both is the failure mode to avoid — it produces something poor at each. Separate route trees avoid that at the cost of a routing decision rather than a second build pipeline.

Operator routes are served as an **installable PWA in standalone display mode**. A raw browser tab on Android gives operators an address bar, pull-to-refresh, and a back gesture that exits mid-task; standalone mode removes all three for roughly an hour of work.

Scanner input arrives through the scan-input abstraction (keyboard-wedge, camera, or manual entry), so the hardware position under D10 can change without touching screen code.

**The TypeScript API client is generated from the OpenAPI document in CI.** Hand-maintained API types drift, and drift surfaces as a runtime crash on a handheld mid-shift.

### 7.8 Why Docker Compose rather than Kubernetes

The realistic deployment is a single well-specified host per site. Compose expresses that topology completely, is operable by one person, and can be understood from a single file. Kubernetes solves multi-node scheduling and rolling deployment problems this system does not yet have.

The known limitation is accepted: Compose has no true rolling deploy. With two API replicas behind the reverse proxy, restarting them sequentially gives near-zero downtime; anything more is scheduled between shifts. If the operation becomes genuinely 24/7 with no acceptable window, that is the trigger to reconsider (k3s), and not before.

### 7.9 Why GitHub Actions

Native to the source repository, no additional infrastructure, and Docker is available on hosted runners — which matters because the integration test strategy depends on Testcontainers spinning up real PostgreSQL instances per test class. The concurrency, environment-approval, and OIDC features cover the deployment control needed.

### 7.10 Licensing note

Several widely-used .NET libraries (including MediatR, AutoMapper, and ImageSharp) have moved to commercial or revenue-threshold licensing. **Current terms must be verified before each is adopted**, rather than assumed from prior familiarity. For mediator functionality specifically, a hand-written dispatcher is a small amount of code and removes the question entirely.

---

## 8. Architecture

### 8.1 Solution structure

```
src/
  Wms.Api/                    HTTP endpoints, SignalR, auth
  Wms.Worker/                 outbox, media renditions, scheduled jobs
  Modules/
    Inventory/                ledger, balances, allocation
    Tasks/                    work engine
    Inbound/                  receiving, putaway
    Outbound/                 waves, picking, packing, shipping
    Catalog/                  items, UoM, locations
    Media/                    assets, renditions, retention
    Billing/                  (conditional on D1)
  Wms.SharedKernel/
tests/
  Wms.UnitTests/
  Wms.IntegrationTests/       Testcontainers
  Wms.ArchitectureTests/      boundary enforcement
```

### 8.2 Data strategy

- `stock_movement` range-partitioned by month from day one
- `stock_balance` maintained incrementally in the same transaction as the movement insert
- A **scheduled reconciliation job** recomputes balances from the ledger and diffs them against `stock_balance`; any variance raises an alert. This is the system's own correctness monitor.
- Reporting and analytics read from a separate read model, never from operational tables
- Outbox pattern for all outbound integration events, ensuring ERP sync survives restarts

### 8.3 Media pipeline

Binaries live in object storage; PostgreSQL holds metadata only. Storing images in `bytea` inflates WAL, degrades backup times, and complicates replication.

```
media_asset      id, storage_key, content_type, byte_size,
                 width, height, sha256, blurhash, status, uploaded_by

sku_media        media_id, sku_id, role, sort_order, is_primary
receipt_line_media / shipment_media / return_media / location_media
```

Separate link tables per entity rather than a polymorphic table, so foreign keys remain real and the database can enforce integrity.

Flow:

1. Client compresses locally (a 12MP handheld photo at 4–6MB becomes ~250KB at 1600px/q80 — over warehouse wifi this decides whether the feature is usable at all)
2. Presigned PUT direct to object storage, bypassing the API
3. Confirm call registers the asset row with `status = processing`
4. Worker generates renditions — thumb (~96px), card (~400px), full (~1400px) — as WebP, plus a blurhash
5. Deduplication by `sha256` unique index; the same catalogue image uploaded repeatedly stores once

Serving uses content-addressed URLs (`/media/{sha256}/thumb.webp`), which are immutable and therefore indefinitely cacheable with no invalidation logic. Where images may carry client-confidential information (private-label packaging in a 3PL context), access is mediated by short-TTL presigned URLs or an authorising endpoint.

Search read models carry a denormalised `primary_thumb_key` and blurhash, so grid views never join to media at query time or issue one request per row.

### 8.4 Media retention

Catalogue imagery is negligible in volume. **Operational photography is not** — packing evidence alone accumulates on the order of a gigabyte per day at moderate throughput, indefinitely.

Therefore, defined from the start: a retention window (90–180 days typical for packing evidence), lifecycle rules moving older objects to cold storage, and a **legal-hold flag** exempting anything attached to an open claim from deletion. Introducing this after two years of accumulation is materially harder than building it now.

On ingest: EXIF stripped (GPS and size), content type validated by magic-byte sniffing rather than file extension, size capped at the presign step.

---

## 9. Integrations

| System | Direction | Content |
|---|---|---|
| ERP | Bidirectional | Item master, POs, sales orders in; receipts, adjustments, shipments out |
| E-commerce / OMS | Inbound | Orders, cancellations |
| Carriers | Outbound | Rating, label generation (ZPL direct to Zebra printers), tracking |
| EDI (if retail customers) | Bidirectional | 856 ASN, 940 warehouse shipping order, 945 shipping advice, 214 status |

**Carrier label generation is consistently underestimated** and should be planned as its own workstream, not as a task within packing.

---

## 10. Identity, Authentication, and Access Control

### 10.1 Two populations, two authentication models

| | Office / administrative | Floor operator |
|---|---|---|
| Credential | Email + password, MFA where available | Badge scan, or short PIN |
| Device | Personal workstation | Pooled handheld, shared across shifts |
| Session | Browser session, idle timeout | Device-bound shift session |
| Login frequency | Once a day | Several times per shift |
| Federation | Via corporate IdP if one exists (D7) | Always local to the WMS |

Operators wear gloves, work one-handed, and are measured on picks per hour. A password prompt on a 4-inch screen is a design failure — it will be worked around by sharing credentials, which destroys attribution and with it the entire audit trail. Badge scan is a single action using a tool already in the operator's hand.

### 10.2 Shared device sessions

Handhelds are **pooled equipment, not personal devices**. The same unit is used by a different person each shift. The model must reflect that:

- **Devices are first-class entities** with their own registration, label, home warehouse, and status. An unregistered device cannot transact.
- An operator **claims** a device by badge scan. The claim ends on explicit logout, shift end, or idle timeout.
- **Idle timeout must suspend, not terminate, when a task is open.** The screen locks; a badge re-scan resumes the same task in the same state. Terminating an in-progress pick leaves orphaned work and ambiguous inventory — and teaches operators to keep the screen awake artificially, defeating the control.
- Every stock movement records **both `actor_id` and `device_id`**. When variance is investigated, "which handheld" is as useful a question as "which person".

### 10.3 In-place supervisor override

Exceptions requiring authorisation are routine: short picks, over-receipts, adjustments above threshold, count variances outside tolerance. The operator does not hold the permission; a supervisor does.

The wrong pattern is to log the operator out, log the supervisor in, perform the action, and log back in. It is slow, it happens dozens of times a shift, and it attributes the action to the supervisor rather than recording both parties.

The correct pattern is **elevation without session change**: a modal requests a supervisor badge scan, the action proceeds, and the record captures the operator, the authorising supervisor, and the reason. The device session is untouched.

This single pattern probably contributes more to floor throughput than any other decision in this section.

### 10.4 Permissions, roles, and scope

Three distinct layers, deliberately not collapsed:

**1. Permissions** — atomic and specific:
`receipt.create`, `receipt.over_receive`, `putaway.execute`, `pick.execute`, `pick.short`, `inventory.adjust`, `inventory.adjust.approve`, `count.execute`, `count.variance.approve`, `order.release`, `media.delete`, `user.manage`, `role.manage`, `billing.view`

**2. Roles** — named bundles of permissions.

**3. Scope** — where a role applies: tenant (3PL only), warehouse, and zone set.

Authorisation checks are always against **permissions, never role names**. Code of the form `if (user.Role == "Supervisor")` becomes unmaintainable within a year, and every new role requires touching every check.

Scope is not optional detail. A picker assigned to ambient zones should not be offered cold-chain tasks. A supervisor at site A should not approve adjustments at site B. Scope is evaluated alongside permission on every check.

**Seeded system roles** (non-deletable): System Administrator, Warehouse Manager, Supervisor, Inventory Controller, Receiver, Putaway Operator, Picker, Packer, Returns Processor, Auditor (read-only), and Client User (3PL only).

Administrators may compose **custom roles** from the permission catalogue for site-specific arrangements.

### 10.5 Segregation of duties

Executing an action and approving it are separate permissions, and **the approver must not be the actor**. This applies to:

- Counting a location and approving its variance
- Recording an adjustment and approving it
- Receiving a discrepancy and authorising the write-off

Enforcement lives in the domain layer, not only in the UI.

This is a shrinkage control rather than a bureaucratic one. Warehouses lose stock to internal theft, and the most common mechanism is a single person who can both create and approve an inventory correction.

### 10.6 Roles as operational simplification

The security benefit is only half the value. The permission set also **drives what the interface presents**.

A picker's handheld home screen shows picking. Not a menu of eleven modules with nine disabled. New staff reach productivity faster, mis-taps drop, and training shortens because there is less to explain. The same applies to the admin console, where a Client User sees only their own inventory and a Receiver never sees the billing module.

Client-side hiding is user experience, never security. The server re-checks every request regardless of what the interface displayed.

### 10.7 Data model

```
app_user          id, user_type (staff|operator), display_name, employee_code,
                  status, security_stamp, valid_from, valid_until

credential        id, user_id, type (password|pin|badge),
                  secret_hash, last_used_at, failed_attempts, locked_until

role              id, name, description, is_system
permission        code, description, category
role_permission   role_id, permission_code

user_role_scope   user_id, role_id, tenant_id?, warehouse_id, zone_ids[]

device            id, label, warehouse_id, status, last_seen_at
device_session    id, device_id, user_id, started_at, ended_at, end_reason

auth_event        id, occurred_at, actor_id, event_type, target_user_id?,
                  device_id?, ip_address, outcome, detail
```

Two fields carry more weight than they appear to:

**`valid_until`** — warehouses run on agency and seasonal labour with high turnover. Accounts that expire on a date by default prevent the slow accumulation of live credentials belonging to people who left months ago. This is one of the most common findings in warehouse security reviews.

**`security_stamp`** — incremented on any change to credentials, roles, or status. Tokens carrying a stale stamp are rejected on the next request. This is how a termination or role revocation takes effect in seconds rather than whenever the access token happens to expire.

### 10.8 Tokens and session mechanics

- Short-lived access token (~15 minutes), refresh token bound to the device
- Refresh rotation with reuse detection
- **Permissions resolved server-side** from role and scope rather than embedded as a long claim list — keeps tokens small and makes revocation immediate
- PIN credentials are rate-limited and lockout-protected. A 4-digit PIN has ten thousand combinations, so **PIN alone is not sufficient authentication**: it should either act as a second factor to a badge scan, or be restricted to resuming an existing session on a device the user has already claimed. It should never grant approval-level permissions on its own.

### 10.9 Offline authentication

An honest constraint, stated plainly: **a fresh login requires connectivity; an established session continues offline.**

Operators log in at shift start at the charging dock, where coverage is reliable. The session then survives dead spots for the remainder of the shift. Queued offline actions carry the user and device captured at the moment of the action, so attribution is preserved when they sync.

Caching credential material on the device to permit fully offline login is technically possible but places secrets on shared hardware that is dropped, mislaid, and passed between staff. It is not recommended unless coverage at the login point is genuinely unreliable — in which case the wifi is the thing to fix.

### 10.10 Audit

The authentication audit log is **separate from the stock movement audit** and records: logins and failures, lockouts, session start and end with reason, overrides both granted and denied, role assignment changes, permission changes, credential resets, and device registration.

Role assignment changes matter disproportionately. "Who granted this person adjustment-approval rights, and when" is among the first questions asked in any shrinkage investigation, and it is unanswerable if role changes are applied without history.

### 10.11 External identity federation (decision D7)

Where a corporate directory exists, office users should authenticate through it via OIDC rather than holding separate WMS credentials — this puts offboarding in one place.

Floor operators remain local to the WMS. They typically have no corporate account, and badge authentication does not map cleanly onto OIDC flows. A hybrid arrangement is normal and should not be treated as a compromise.

### 10.12 Platform security

- Mandatory reason codes on all stock adjustments
- Tenant isolation enforced at the data layer if D1 resolves to 3PL
- Secrets held outside version control (SOPS-encrypted or injected at deploy)
- TLS terminated at the reverse proxy, including on internal warehouse networks

---

## 11. Delivery Pipeline and Environments

### Workflows

**`ci.yml`** (pull requests): build, unit tests, integration tests against Testcontainers PostgreSQL, architecture tests, frontend typecheck and build. Path-filtered so documentation changes skip the full suite. No image push — a PR workflow able to push images is one a fork can exploit.

**`release.yml`** (merge to main): build images tagged by git SHA, push to GHCR, deploy behind a GitHub Environment with required approval.

### Deployment

The one-shot **migrator container must run to successful completion before API containers restart**. Migration never runs on application startup — with multiple replicas that is a race, and failure leaves a half-started system rather than a clear stop.

```
compose pull
compose run --rm migrator     # non-zero exit fails the job, API untouched
compose up -d api worker web
```

This requires every migration to be backward-compatible with the currently running application version, since new schema is briefly live under old code. **Expand-then-contract**: add nullable column and backfill in release one, write to it in release two, drop the old column in release three. Tedious, and it is what permits deployment without a maintenance window.

Images are always deployed by SHA. `latest` is never deployed — it destroys the ability to state what is running or to roll back to a known state.

For an on-premise target, deployment is **pull-based** (self-hosted runner or reconciling agent on the host), avoiding inbound access from CI into the warehouse network. A self-hosted runner must only ever be attached to a **private** repository.

### Compose topology

```
postgres     tuned config, named volume, healthcheck, pinned major version
minio        object storage
api          2 replicas behind proxy
worker       renditions, outbox, scheduled jobs
web          static build served by proxy
caddy        TLS, routing, media caching
migrator     one-shot, runs to completion, exits
```

Base compose file, plus a dev override (bind mounts, hot reload) and a prod override (pinned tags, resource limits, no source mounts).

### On-premise prerequisites (if D2 resolves to on-prem)

Three items must be verified real before go-live:

1. **Automated backups with a restore that has actually been performed.** pgBackRest or WAL-G to S3-compatible storage.
2. **A major-version upgrade plan.** PostgreSQL major version pinned explicitly — never `postgres:latest` — so an image pull cannot silently break the data directory.
3. **UPS on the host.** Warehouse power quality is poor; sudden loss during a WAL write is how data corruption is learned about.

A scheduled workflow should run ledger reconciliation against a restored copy of the previous night's backup. This exercises the restore path continuously and detects inventory drift before the warehouse manager does.

---

## 12. Fleet Operations

A single-tenant product means operating **N isolated deployments**, not one system. This is the dominant operational concern of the delivery model and needs designing rather than discovering.

### Version management

Customers will not all upgrade at once. The product must tolerate version skew across the fleet:

- Every deployment records its running version, schema version, and configuration revision, reported to a central control plane
- Upgrades roll out in rings: internal environment, then a nominated canary customer, then the fleet
- The **expand-then-contract migration discipline** (§11) becomes doubly important — a migration may need to be compatible across two application versions *and* run unattended against dozens of databases with differing data volumes
- Migrations must be idempotent and resumable. A migration that fails halfway on customer 14 of 30 cannot require manual repair on each

### Provisioning

New-customer onboarding must be automated end to end: infrastructure, database, schema, seed configuration, first admin account, and device registration. Manual provisioning is the constraint that caps how many customers the business can carry, and it degrades as the number of deployments grows.

### Observability across deployments

Per-deployment metrics, logs, and alerting aggregated into one view. Without this, "customer X says it's slow" is unanswerable. Minimum: scan confirmation latency, task queue depth, exception queue depth and age, sync backlog per device, and failed outbox messages — all per deployment, with per-customer alert thresholds.

### The orchestration ceiling

Docker Compose is appropriate for the first several deployments. Somewhere in the region of **ten to fifteen customers**, fleet-wide migration, per-deployment observability, and provisioning automation become materially harder than an orchestrator would make them.

This is a planned transition, not a failure. It is recorded here so that it is anticipated at customer eight rather than encountered at customer twelve. The migration path is straightforward precisely because the application is a stateless modular monolith with an external database.

### Support and reproducibility

Diagnosing an issue requires knowing the customer's version, configuration revision, device fleet, and data shape. Configuration must therefore be exportable and importable as a versioned artefact, so a customer's setup can be reproduced in a test environment without access to their data.

---

## 13. Phased Roadmap

Sequencing is firm; durations are indicative and depend on team size, which is not yet fixed.

### Phase 1A — Foundation and inbound

**Keepable code.** Everything needed to get stock accurately *into* the building, with the platform underneath it built properly.

Because putaway is a directed, leased task, this phase exercises most of the architecture's hard machinery — the task engine, offline confirmation, idempotent sync, and the exception path — even though nothing ships yet.

**In scope:**

| Area | Included | Deliberately crude |
|---|---|---|
| Auth | Operator badge/PIN, office login (forced password change on first login), device registration, shared-device sessions, supervisor override, auth audit log | Federation (D7) deferred |
| Roles | Permission catalogue, seeded system roles, warehouse and zone scoping, role management UI, custom role composition | — |
| Master data | Item master with UoM hierarchy, location topology, handling units — with real maintenance screens | No bulk import; no ERP sync |
| Receiving | Blind receipt, over/under/damage with discrepancy raising an exception | No ASN, no PO matching, no QC hold |
| Putaway | System-directed, leased as a task, offline-capable | One hardcoded strategy: first empty bin by pick sequence |
| Ledger | Movements, balance maintenance, incremental reconciliation | — |
| Offline | Queue, idempotency keys, sync, exception queue with resolution workflow | — |
| Admin | Full dashboard and reports over inbound and stock position | Served from the primary; reporting replica deferred |
| Scan input | Abstraction over keyboard-wedge, camera, and manual entry | Camera used as the working default (D10) |
| Frontend | One app, `/admin` and `/operator` route trees; operator routes installable in standalone mode | Shared components not yet factored out |
| Offline | Service worker and IndexedDB queue on operator routes | — |
| i18n | Infrastructure across both route trees; English and Japanese | Additional languages are config |

**Excluded:** allocation, picking, packing, shipping, media, lots and FEFO, serials, waves, cycle counting, returns, configuration UI, carrier labels, ERP integration.

**Exit criteria:**

1. Receive 100 units and put them away. The ledger holds exactly the expected movements and the balance reads correctly in the destination bin.
2. Reconciliation recomputes balances from the ledger and matches to zero variance.
3. A putaway is confirmed with the device in airplane mode. The device is then rebooted. On reconnection the movement lands **exactly once**.
4. Two devices leasing from the same zone never receive the same putaway task, verified under a load harness simulating 30 concurrent operators.
5. **An over-receipt — expected 100, counted 106 — records 106 and raises an exception record.** It does not return an error to the device.
6. A user lacking `receipt.over_receive` is blocked; supervisor override by badge scan authorises the action without changing the session, and the record captures both identities.
7. Scan-to-confirm latency measured over the real network path and recorded as a baseline.

Criterion 5 is the one that matters most. It is the only test proving the command/fact split was actually implemented, and it is the design decision most likely to have been built as a rejection by mistake — because rejecting invalid input is what instinct says to do.

**Carry one test forward from 1B.** Write the allocation concurrency test now — two orders, one remaining unit, assert exactly one succeeds — even with no picking UI behind it. Allocation is the hardest correctness problem in the system, and this split otherwise defers all of it. A failing test sitting in the suite is cheap; discovering the problem in 1B is not.

**Note on reports.** Dashboards built against fifty bins and a few thousand movements will mislead you about query plans, because the plans that matter only change shape at volume. Seed a large synthetic movement history before trusting any report's performance.

*Indicative duration: 5–7 weeks for one experienced full-stack developer; less with two. Confirm against actual team size.*

**Status as of 2026-09-15 — a live progress snapshot, not part of the plan itself.** Unlike the rest of this section, the line below is checked and updated as the phase actually proceeds, the same kind of deliberate exception `wms-architecture-review.md`'s "Consolidated Open Decisions" table is to that document's own never-edit rule. Backend only; frontend status isn't tracked here.

| Exit criterion | Status |
|---|---|
| 1. Receive 100, exact ledger + balance | ✅ Met — `ConfirmReceiptFactTests`, `ConfirmPutawayFactTests` |
| 2. Reconciliation → zero variance | ✅ Met — `ReconciliationServiceTests` |
| 3. Airplane-mode reboot, exactly-once | ⚠️ Partial — idempotency-key dedup proven at the backend; the device-reboot scenario itself needs the operator PWA, which doesn't exist yet |
| 4. No duplicate lease, 30 concurrent operators | ⚠️ Partial — `TaskLeaseConcurrencyTests` proves no duplicate lease, but at 8 operators/50 iterations, not the specified 30-operator load harness |
| 5. Over-receipt records 106 + exception, no error | ✅ Met — proven and mutation-tested |
| 6. Missing `receipt.over_receive` blocked; supervisor override authorises | ❌ Not met — `POST /auth/elevate` doesn't exist; `receipt_confirmed` never conditionally checks `receipt.over_receive` |
| 7. Scan-to-confirm latency baseline over real network | ❌ Not met — needs a real deployment, which doesn't exist yet |
| Allocation concurrency test carried forward from 1B | ❌ Not started — no allocation test anywhere in the suite |

Also short of the scope table above, confirmed by reading the actual registered routes rather than the design document's intent: no admin API exists yet for warehouses, zones, locations, items, or handling units (master data is only reachable by direct SQL — every integration test seeds it that way); no device registration/management endpoints; `POST /roles`/`PATCH /roles/{id}` don't exist so no custom role can actually be composed; and the auth audit log has no read path. Each of these has its own entry in `docs/shortcuts.md`. The hard architectural machinery — the ledger, the command/fact split, idempotent sync, task leasing, reconciliation — is built and well-tested; what's short is a real slice of the admin/CRUD surface, the supervisor-override mechanism, and the carried-forward allocation test.

### Phase 1B — Fulfilment

**Keepable code.** Completes the lap: stock leaves the building under system direction.

**In scope:**

| Area | Included | Deliberately crude |
|---|---|---|
| Orders | Order and line model, manual entry | Seeded or hand-entered; no ERP or e-commerce intake |
| Allocation | Conditional `UPDATE` against balance, single-consumer worker per warehouse | One strategy; no FEFO, no whole-case-first |
| Balance | Full three-quantity model: on hand, allocated, available | — |
| Picking | Task generation, lease, scan-verified pick, short-pick exception | No waves, no batching, no route optimisation |
| Admin | Reports extended to outbound and order status | — |

**Exit criteria:**

1. Two orders submitted concurrently for the last remaining unit: exactly one allocates, the other is told there is no stock.
2. **A short pick — sent for 10, found 6 — records a movement of 6 and raises an exception.** It does not return an error to the device.
3. A full lap completes end to end: receive, putaway, allocate, pick. Reconciliation matches to zero variance across the whole sequence.
4. No deadlock under multi-line allocation running concurrently with pick confirmations, verified under load.
5. A picker scoped to one zone is never offered a task in another.

*Indicative duration: 4–5 weeks on the same basis.*

### Notes applying to both phases

**Managing the shortcuts.** The infrastructure built here — auth, ledger, task engine, sync, exception handling — is production code and is kept. The shortcuts are not, and every one must be recorded in a tracked list at the moment it is taken, with the Phase 2 item that replaces it. Without that list, "hardcoded for now" becomes permanent, and a proof of concept quietly becomes the product by accretion. This is the most common way a good PoC damages a project.

**Sequencing.** Validate the target handheld first, before building anything on top of it — scan capture, offline queue survival across app suspension and device reboot, sync after a long offline period. The offline design rests on a service worker that Android's battery management is entitled to kill, and that assumption is cheapest to test on day two rather than week five. Measure scan round-trip latency over the real network path to the chosen cloud region early, not from localhost at the end.

**Secondary purpose.** These are also the artefacts you show a prospective first customer. Build the handheld screens as real screens rather than debug UI — it costs little at this scale and it is the difference between a technical demo and a design-partner conversation. Note that 1A alone demonstrates a system that receives and stores but cannot ship, which is a harder story to tell; if a customer demo drives the timeline, 1B is the milestone that matters.

### Phase 2 — Operational core

Item master, UoM hierarchy, location topology, handling units, full receiving and putaway, allocation strategies including FEFO, pick/pack/ship, barcode scanning, handheld PWA, SKU imagery with pick-confirmation display, admin console, audit trail.

**Wave planning, zone picking, and replenishment are Phase 2, not Phase 3** — at the D5 scale of 50+ operators across multiple zones, discrete picking and manual replenishment do not work, so these are core rather than maturity features.

Identity and access: permission catalogue, seeded system roles, warehouse and zone scoping, dual authentication, shared-device sessions, supervisor override, and the authentication audit log.

Most of the identity model, including custom role composition, is delivered in Phase 1A; what remains here is federation and extending the permission catalogue to cover features added in this phase.

*Exit criterion: a real order can be received, stored, picked, packed, and shipped entirely within the system, with the ledger reconciling.*

### Phase 3 — Operational maturity

Cycle counting with variance approval, returns and disposition, serial-number tracking exposed, configurable strategies through the configuration UI, damage and packing photography, carrier integration and label printing, ERP integration hardening, reporting replica, batch and cluster picking.

### Phase 4 — Commercial readiness

Fleet provisioning automation, cross-deployment observability, upgrade rings, labour management and productivity metrics, EDI, slotting analysis, WCS integration boundary, and the go-live methodology (opening balance seeding, pilot zone, parallel running, abort criteria) as a repeatable implementation process.

---

## 14. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| Inventory model chosen as mutable quantities | Severe — unrecoverable without rewrite | Ledger committed in Phase 1; architecture tests prevent direct balance mutation |
| Handheld UX designed as a shrunken desktop app | Severe — system rejected by floor staff | Separate handheld application; operator testing from first working build |
| Carrier label integration underestimated | Schedule slip in Phase 2 | Planned as an independent workstream with its own estimate |
| Media storage growth unmanaged | Storage cost and backup degradation | Retention and lifecycle policy built in Phase 1 |
| Connectivity outage halts operations | Operational stoppage | Offline queue on handheld; on-premise deployment if D2 permits |
| Concurrency defects in task dispatch | Duplicate picks, inventory variance | `SKIP LOCKED` dispatch, verified by Testcontainers concurrency tests |
| Library licensing change mid-project | Commercial exposure | Licence review before each dependency is adopted |
| Migration breaks running deployment | Downtime during shift | Expand-then-contract discipline; migrator gates API restart |
| Operator login too slow, leading to shared credentials | Severe — destroys audit trail and accountability | Badge scan login; supervisor override without session change; measured on the floor before rollout |
| Flat role checks hard-coded through the codebase | Every new role requires touching every check | Permission-based authorisation from the first endpoint; architecture test forbids role-name comparisons |
| Departed staff retain active accounts | Shrinkage exposure | Time-bounded accounts by default; security stamp for immediate revocation; periodic access review |
| Approval controls bypassed by self-approval | Undetectable inventory manipulation | Segregation of duties enforced in the domain layer, not the UI |
| Per-customer code branches instead of configuration | Fatal to the product — parallel codebases cannot be shipped | Configuration as data from Phase 1; no customer-specific build permitted |
| Manual provisioning caps customer growth | Business model constraint, not a technical one | Automated onboarding built before the third customer |
| Fleet-wide migration fails partway | Customers stranded on inconsistent schema | Idempotent, resumable migrations; ring-based rollout with a canary customer |
| Cloud hosting routed over international links | Product unusable during regional connectivity faults | Host in-region; measure scan latency from a real customer site before launch |
| "Online-only" misread as removing device offline support | Product fails first site survey | In-building offline tolerance stated as a separate, non-negotiable requirement (§3) |
| Proof of concept becomes the product by accretion | Permanent hardcoding, unreplaceable shortcuts | Every shortcut recorded as a tracked item with its Phase 2 replacement, at the moment it is taken |
| Proof of concept scoped around easy features | Proves nothing; risk discovered later at full cost | Scope fixed on the hardest path: lease, offline confirm, short-pick exception, reconciliation |
| Allocation concurrency deferred to Phase 1B | Hardest correctness problem proven last | Allocation concurrency test written in Phase 1A ahead of the feature it covers |
| Camera scanning carried into production at 50+ operators | Tens of operator-hours lost daily; degrades in dim aisles and on curved labels | Scan-input abstraction from Phase 1A; hardware position revisited before first pilot (D10) |
| No design partner | Every scope decision made by guesswork | Secure floor access to observe a shift, ahead of Phase 2 scope lock |
| i18n retrofitted after the UI is built | Slow, error-prone rework across every screen | i18n infrastructure is a Phase 1A deliverable, not a later feature |
| Operator screens inherit desktop layouts from the shared app | Unusable on a handheld; rejected by floor staff | Separate `/operator` route tree with its own layouts; operator screens reviewed on-device, never in a desktop browser |
| Offline dropped because "it's just the web app" | Largest architectural risk goes unproven | Service worker and queue scoped to operator routes; Phase 1A exit criterion 3 is non-negotiable |

---

## 15. Immediate Next Steps

1. Resolve open decisions **D1** (3PL or own-operator) and **D2** (on-premise or cloud) — these gate schema and deployment design
2. Confirm D3 (regulated goods) and D4 (serial tracking), which determine Phase 1 traceability scope
3. Obtain volume figures (D5) for partitioning and sizing
4. Walk the physical warehouse: confirm racking layout, wifi coverage map, handheld models in use, and label printer models
5. Produce the Phase 1 schema as SQL migration files, with the ledger and task tables first
6. Stand up the Compose skeleton and CI pipeline before feature work, so every commit is tested and deployable from day one

---

## Appendix A — Glossary

| Term | Meaning |
|---|---|
| **ASN** | Advance Ship Notice — notification of an inbound shipment's contents |
| **LPN** | License Plate Number — barcode identity for a pallet, cage, or tote |
| **UoM** | Unit of Measure — each, inner, case, pallet |
| **FEFO / FIFO** | First Expired First Out / First In First Out — allocation strategies |
| **Pick face** | Accessible bin location from which picking occurs |
| **Bulk reserve** | Higher-density storage feeding pick faces via replenishment |
| **Wave** | A batch of orders released to the floor together |
| **Putaway** | Movement of received stock into storage locations |
| **Replenishment** | Movement from bulk reserve to pick face |
| **Cycle count** | Rolling partial stock count, avoiding full shutdown stock takes |
| **Disposition** | Decision on returned goods: restock, repair, return to vendor, scrap |
| **WCS** | Warehouse Control System — controls physical automation equipment |
| **GRN** | Goods Received Note |
| **Blurhash** | Compact string encoding a blurred image preview, used as a loading placeholder |
| **SoD** | Segregation of Duties — the person performing an action may not approve it |
| **Supervisor override** | In-place authorisation of an exception by a higher-privileged user, without changing the active session |
| **Security stamp** | Version marker on a user record; incrementing it invalidates all issued tokens immediately |
| **Scope** | The warehouses and zones within which a role assignment is valid |
