# WMS

A commercial, multi-customer Warehouse Management System — sold as a product
and deployed **one isolated instance per customer** (own database, own
process pair), not a single-warehouse internal tool and not multi-tenant in
the shared-schema sense.

The system directs floor work rather than just recording it after the fact:
an operator scans a badge, is told which bin to go to, confirms by scan, and
every stock movement lands in an append-only ledger that a reconciliation
job continuously checks against the derived balances. See
[`docs/wms-project-proposal.md`](docs/wms-project-proposal.md) for the full
scope, objectives, and phased roadmap before assuming anything about what
this is or isn't.

## Status

**Phase 1A — Foundation and inbound.** The backend for auth, master data,
blind receipt with over/under/damage exceptions, directed putaway, the
ledger, offline-sync idempotency, and admin/reporting read models is built
and tested. Allocation, picking, packing, shipping, and everything after
receiving are explicitly out of scope for this phase — see
[`docs/wms-project-proposal.md`](docs/wms-project-proposal.md) §13 for exit
criteria.

**The frontend does not exist yet.** There is no `web/` directory — no
`/admin` or `/operator` UI, no service worker, no offline queue on the
client side. Everything below is backend-only.

Deliberate proof-of-concept compromises still open, each paired with what
replaces it, are tracked in [`docs/shortcuts.md`](docs/shortcuts.md) — read
it before assuming any given piece of behavior is production-ready.

## Stack

- **.NET / ASP.NET Core**, modular monolith, two deployed processes: API
  (`Wms.Api`) and background worker (`Wms.Worker`)
- **PostgreSQL**, EF Core for aggregate writes, Dapper-style raw SQL (Npgsql)
  for hot read paths
- **DbUp** for migrations — raw versioned SQL in `db/migrations/`, never EF
  migrations
- Argon2id credential hashing, bearer tokens with `security_stamp`-based
  revocation
- xUnit across three test projects: unit, integration (Testcontainers +
  real PostgreSQL), and architecture (module-boundary/convention tests)

See [`CLAUDE.md`](CLAUDE.md) for the full stack list including the pieces
not wired up yet (PgBouncer, object storage, the React frontend, CI/CD).

## Repository layout

```
src/
  Wms.Api/            HTTP endpoints (commands and facts)
  Wms.Worker/          Background jobs (reconciliation)
  Wms.Migrator/         One-shot DbUp migration runner
  Wms.SharedKernel/      Cross-module primitives (idempotency hashing, fact results)
  Modules/
    Identity/            Auth, users, roles, permissions
    Inbound/              Receiving
    Inventory/            Ledger, stock balance, reconciliation
    Tasks/                 Task leasing (putaway)
    Platform/              Idempotency store, outbox
    Catalog/                Master data (items, locations, UoM — in progress)
db/migrations/            Versioned DbUp SQL, applied in order, never edited once applied
tests/
  Wms.UnitTests/
  Wms.IntegrationTests/     Real PostgreSQL via Testcontainers
  Wms.ArchitectureTests/     Module-boundary and convention enforcement
docs/                      Design source of truth — see below
.claude/                   Agent/skill/hook configuration for this repo
```

## Documentation

Read [`CLAUDE.md`](CLAUDE.md) first — it's the always-current summary of
scope, stack, and the twelve invariants the schema and API are built around.
Beyond that:

| Document | What's in it |
|---|---|
| [`docs/wms-project-proposal.md`](docs/wms-project-proposal.md) | Objectives, resolved/open decisions, domain model, phased roadmap, risks |
| [`docs/wms-design-document.md`](docs/wms-design-document.md) | The living source of truth: data models, API reference, data flows, error catalogue, NFRs |
| [`docs/wms-architecture-review.md`](docs/wms-architecture-review.md) | Historical findings (never edited after the fact — superseded in the design doc instead) |
| [`docs/shortcuts.md`](docs/shortcuts.md) | Every proof-of-concept compromise still open, and what replaces it |
| [`docs/adr/`](docs/adr) | Architecture decision records |

## Getting started

### Prerequisites

- .NET 10 SDK
- PostgreSQL (a local instance, or point at any reachable one)
- Docker, running — required for the integration test suite
  (Testcontainers spins up real PostgreSQL per test class)

### Configure a connection string

Neither `appsettings.json` nor `appsettings.Development.json` carries a
database connection string — it's supplied at runtime, never committed.
The API and Worker both read `ConnectionStrings:Wms`
(environment variable form: `ConnectionStrings__Wms`); the Migrator takes
the connection string as its first CLI argument or via `WMS_DB_CONNECTION`.
See `.env.example` for the variable names this project expects.

### Run the migrations

```bash
dotnet run --project src/Wms.Migrator -- "<connection string>"
```

Applies every script in `db/migrations/` in order and exits. Safe to
re-run — every seed statement is `ON CONFLICT DO NOTHING`.

### Run the API

```bash
ConnectionStrings__Wms="<connection string>" dotnet run --project src/Wms.Api
```

In `Development`, `appsettings.Development.json` supplies a working
(placeholder, dev-only) JWT signing key and an opt-in header-based operator
principal for exercising the API before a frontend exists — see the comments
in that file and in `Program.cs`. Outside `Development`, a real
`Wms:Auth:SigningKey` must be configured or the process refuses to start.

### Run the worker

```bash
ConnectionStrings__Wms="<connection string>" dotnet run --project src/Wms.Worker
```

### Run the tests

```bash
dotnet test Wms.slnx
```

Unit and architecture tests need nothing beyond the SDK. Integration tests
need Docker running — each test class provisions its own PostgreSQL
container and applies every migration through the same `Wms.Migrator`
entry point production uses, so a schema drift fails the suite rather than
passing quietly.

### Format

```bash
dotnet format Wms.slnx
```

Style is enforced (`.editorconfig`), not a matter of review discussion.
