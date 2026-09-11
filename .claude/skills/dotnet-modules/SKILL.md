---
name: dotnet-modules
description: Module structure, boundaries, and .NET conventions for the WMS modular monolith. Use when adding a feature, deciding where code belongs, or wiring cross-module communication.
---

# .NET module conventions

## Structure

```
src/
  Wms.Api/                 HTTP, auth, transactional write path
  Wms.Worker/              allocation consumer, outbox drain, jobs
  Modules/
    Identity/  Catalog/  Inventory/  Tasks/
    Inbound/   Outbound/ Platform/
  Wms.SharedKernel/
```

Each module: `Domain/`, `Application/`, `Infrastructure/`, `Contracts/`.
Only `Contracts/` is visible to other modules.

## The boundary rule

A module may reference `Wms.SharedKernel` and other modules' `Contracts`.
Nothing else. No module reaches into another's tables, entities, or
repositories.

Enforced by an architecture test. This is what keeps a modular monolith from
becoming a regular one, and what makes a future extraction cheap.

Cross-module effects go through `outbox_message`, written in the same
transaction as the mutation. At-least-once delivery, so consumers must be
idempotent.

## Two processes, one solution

**API** runs on the `wms_api` database role: 5-second statement timeout,
reserved connection pool. Anything that could take longer belongs in the
Worker.

**Worker** runs on `wms_worker`: 300 seconds. Holds the allocation consumer
(one active per warehouse via advisory lock), the outbox drain, and scheduled
jobs.

Never allocate, generate renditions, or run reports from an API request.

## Data access

**EF Core** for aggregate writes — change tracking and unit of work suit
domain mutations. **Dapper** for hot reads: pick lists, barcode lookup,
dashboards. Both over the same Npgsql connection, same transaction.

Reporting uses a separate context bound to the replica with no write methods
and no grant on the primary.

## Authorisation

Check permissions, never role names. `if (user.Role == "Supervisor")` fails the
architecture test. Permissions resolve server-side from role and scope; scope
is warehouse plus zones and is evaluated on every check.

Segregation of duties lives in the domain layer, not the UI: the approver of a
variance, adjustment, or count may not be its actor.

## Libraries

Verify current licence terms before adopting MediatR, AutoMapper, or
ImageSharp — several popular .NET libraries moved to commercial or
revenue-threshold licensing. A hand-rolled mediator is about sixty lines and
removes the question.

## Style

Based on [Microsoft's C# Coding Conventions](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions)
and the [.NET runtime's own coding guidelines](https://github.com/dotnet/runtime/blob/main/docs/coding-guidelines/coding-style.md)
— the rules the BCL itself is written to, not house style invented for this
repo.

**Naming:** `PascalCase` for types, methods, properties, namespaces, and
public members. `camelCase` for locals and parameters. `_camelCase` for
private fields. `IPascalCase` for interfaces. No Hungarian notation.

**Types:** language keywords over BCL types — `int`, `string`, `bool`, never
`Int32`, `String`, `Boolean`. `var` only when the right-hand side already
makes the type obvious; an explicit type everywhere else, including every
method signature.

**Nullable reference types are `enable` in every project.** An unexpected
`?` is the compiler reporting a real gap, not noise to suppress with `!`.

**Modern idioms:** file-scoped namespaces, primary constructors for simple
DTOs and value objects, collection expressions (`[]` over `new List<T>()`),
pattern matching over `is`-then-cast, `required` members over defensive
constructor boilerplate.

- `sealed` by default, `record` for value objects.
- Constructor injection. No service locator.
- `CancellationToken` on every async method that crosses an I/O boundary.
- Braces on every `if`, even one-liners.
- Domain exceptions map to RFC 7807 problem types in one place, not per
  endpoint.

**Enforcement, not etiquette:** `.editorconfig` plus `dotnet format` — already
in Commands, and run async by the `format-dotnet.sh` hook after every edit —
is what applies this. Nobody should be relitigating brace placement in
review.
