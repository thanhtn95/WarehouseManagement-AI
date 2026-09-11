---
name: dotnet-testing
description: xUnit conventions and the three backend test projects (unit, integration, architecture) for the WMS. Use when writing or running any .NET test, or deciding which test project a new test belongs in.
---

# .NET testing conventions

## Three projects, three jobs

```
tests/
  Wms.UnitTests/          pure logic, no I/O, milliseconds, no Docker
  Wms.IntegrationTests/   Testcontainers — real PostgreSQL, always
  Wms.ArchitectureTests/  NetArchTest — module boundaries, no role-name comparisons
```

**The test for which project a test belongs in:** does it touch a database,
the outbox, or a lock? If yes, `Wms.IntegrationTests` — **REQUIRED
BACKGROUND:** the testcontainers-concurrency skill and the test-writer agent
own that ground, including the mandatory concurrency suite (no duplicate
lease, no oversell, no deadlock, idempotent replay, short-pick exception,
reconciliation). Don't duplicate that content here.

If it's pure logic with no persistence — UoM conversion math, permission
evaluation against a scope, reason-code validation (`requires_note` /
`requires_photo` / `requires_approval`), RFC 7807 problem mapping — it's a
`Wms.UnitTests` test: fast, no container, no mocking framework needed beyond
a simple fake at the actual system boundary.

`Wms.ArchitectureTests` runs NetArchTest rules and fails the build, not the
review, on:
- A module reaching into another module's internals instead of its
  `Contracts` project.
- `if (user.Role == "...")` anywhere — invariant 8. Permission checks only.
- A migration file edited after it has been applied. Note DbUp records no
  content hash to compare against (see sql-migrations), so this backstop
  has to establish "already applied" some other way — a committed manifest
  of script hashes is the workable version. **Not yet implemented**; today
  the only enforcement is `guard-applied-migration.sh` at authoring time,
  which is local to one machine.

Two of the three rules above exist as tests today
(`ModuleBoundaryTests`, `AuthorizationConventionTests`). The third is
honestly absent rather than faked.

## What NOT to mock

Never mock `DbContext`, a repository, or Npgsql for anything that touches
persistence — that tests the mock, not the system. Use
`Wms.IntegrationTests` with Testcontainers instead. Mocking is for the actual
external boundary only (a payment gateway, an email provider) — this system
has none of those in Phase 1A.

## Style

xUnit, one behaviour per test, named `Method_Scenario_ExpectedOutcome`.
Arrange-act-assert separated by blank lines. `[Theory]` with
`[InlineData]` for input variations, not copy-pasted `[Fact]`s.

## Running

```bash
dotnet test                              # Unit + Architecture — no Docker needed
dotnet test tests/Wms.IntegrationTests   # Testcontainers — needs a running Docker daemon
docker info                              # confirms the daemon is actually up, not just installed
```

`dotnet test` alone silently skips nothing — if `Wms.IntegrationTests` is
included in the solution filter, a stopped Docker daemon fails those tests
with a connection error, not a skip. Check `docker info` first if a run that
was passing suddenly isn't.
