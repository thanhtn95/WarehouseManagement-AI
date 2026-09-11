# 0001 — One project per module, boundary enforced by architecture test

**Status:** accepted
**Date:** 2026-09-11

## Context

The design document (§1.3) and the `dotnet-modules` skill both specify the
module structure — `Domain/`, `Application/`, `Infrastructure/`,
`Contracts/` per module, with "only `Contracts/` visible to other modules,"
enforced by NetArchTest in CI.

What neither specifies is **project granularity**: whether a module is one
`.csproj` with those four as folders, or several `.csproj` files with
`Contracts` compiled separately so the *compiler* refuses a cross-module
reference to anything else.

This has to be decided before the first module gains code, because
changing it later means moving every file and rewriting every reference.

## Decision

**One project per module.** `src/Modules/<Name>/Wms.Modules.<Name>.csproj`,
with `Domain/`, `Application/`, `Infrastructure/`, `Contracts/` as folders
inside it. The boundary rule is enforced by NetArchTest in
`Wms.ArchitectureTests`, which fails the build — not the review — on a
module referencing another module's non-`Contracts` namespace.

Phase 1A creates six modules: `Identity`, `Catalog`, `Inventory`, `Tasks`,
`Inbound`, `Platform`. `Outbound` is deliberately absent — it is orders,
allocation and picking, all Phase 1B, and scaffolding it now would be the
same phase-boundary violation as building it.

## Consequences

**Easier:** twelve projects instead of roughly twenty; a faster build and
a project graph a newcomer can hold in their head. Moving a type between
`Application/` and `Infrastructure/` inside a module is a file move, not a
project change.

**Harder:** the boundary is *test*-enforced, not *compiler*-enforced. A
violating `using` compiles cleanly and is caught by a failing architecture
test rather than by the IDE as you type it. That is a slower feedback loop
than a compile error.

**Knowingly accepting:** that slower loop, because the architecture test is
the enforcement mechanism the design already specifies regardless of
granularity — a separate `Contracts` project would add compiler
enforcement *on top of* a test that still has to exist for the role-name
and migration-immutability rules. Doubling the project count before a
single line of domain code exists is not justified by that overlap.

## Alternatives considered

**A separate `Contracts` project per module** (≈20 projects). Rejected for
now: the compiler enforcement is real value, but it front-loads structural
complexity onto an empty codebase, and the architecture test is required
either way.

**One project for all modules, namespaces only.** Rejected outright:
nothing but convention would stop a cross-module reference, and the design
treats the module boundary as load-bearing for a future extraction.

## Revisit when

A module-boundary violation actually reaches `main` despite the
architecture test, or the module count grows beyond roughly ten — either
signals the test-only enforcement is no longer sufficient, and the
`Contracts`-project split earns its cost.
