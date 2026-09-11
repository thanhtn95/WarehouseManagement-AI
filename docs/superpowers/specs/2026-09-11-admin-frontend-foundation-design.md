# Admin frontend foundation — design spec

**Date:** 2026-09-11
**Status:** Approved (brainstorming), pending implementation plan
**Phase:** 1A

## Purpose

Phase 1A calls for a real, installable frontend across two route trees
(`/admin/*`, `/operator/*` — design doc §1.2, D15), but `web/` does not exist
at all yet. Building the entire frontend in one pass is too large a unit of
work to spec and implement safely. This is the **first** of several
sub-projects: the application shell plus the `/admin/*` route tree only,
covering exactly the backend surface that already exists and is tested —
staff login, user and role-scope administration, and the reporting/dashboard
read models.

**Not in this sub-project**, each deferred to its own future spec:

- `/operator/*` — the offline PWA route tree (service worker, IndexedDB fact
  queue, `useScanner()`). A materially different, higher-risk piece of work.
- **Master-data admin screens** (warehouses, zones, items, locations,
  handling units). The design doc documents these endpoints (§5.11), but
  **no backend exists for them yet** — the Catalog module is an empty stub.
  Building these screens means building their API first, which is
  schema-guardian/api-designer territory, not a frontend concern, and is
  explicitly out of scope here.
- **Custom role composition** (`POST /roles`, `PATCH /roles/{id}`). Verified
  against the actual route table in `UserEndpoints.cs`: only `GET /roles`
  and `GET /permissions` exist. The design doc documents the write endpoints,
  but they are not implemented. The role-scope-assignment screen in this
  spec can only assign a user one of the *seeded* system roles — it cannot
  create a new custom role.
- **Role-scope revocation.** There is no `DELETE /users/{id}/role-scopes/{id}`
  (tracked in `docs/shortcuts.md`). The assignment screen has no way to
  revoke a grant, and must not pretend otherwise.
- Deployment wiring — Docker, Caddy, CI. This sub-project is `npm run dev`
  against a locally-running `Wms.Api`, nothing more.
- Japanese-language content (the i18n *infrastructure* is built per below,
  but translated strings are not authored in this pass — English only).

## Architecture

```
web/
  index.html
  vite.config.ts
  tsconfig.json
  src/
    main.tsx                    Router + QueryClient + AuthProvider bootstrap
    routes/
      admin/
        __root.tsx               Auth-protected shell: sidebar, top bar, warehouse picker
        login.tsx                 /admin/login — NOT under the protected layout
        index.tsx                  /admin — redirects to /admin/dashboard
        dashboard.tsx                /admin/dashboard
        users/
          index.tsx                   /admin/users — list
          new.tsx                      /admin/users/new — create
          $userId.tsx                   /admin/users/$userId — detail, edit, role-scope assignment
    shared/
      api/                       Generated client (openapi-typescript + openapi-fetch) + a thin typed wrapper
      auth/                      AuthContext, token storage, the /auth/me hydration hook
      i18n/                      Catalogue + the *_i18n resolver (English content only this pass)
      components/                Shared, cross-screen pieces (permission-gated action, page shell, data table wrapper)
  tests/                       Vitest + Testing Library specs, colocated per the frontend-testing skill's convention
```

- **Build:** Vite, React 18, TypeScript strict mode, npm.
- **Routing:** TanStack Router, file-based, under `src/routes/admin/`. No
  `src/routes/operator/` directory yet — added when that sub-project starts.
- **Styling:** Tailwind + shadcn/ui (Radix primitives). `npx shadcn init`
  brings in only the primitives this sub-project actually uses (table,
  dialog, form, select, badge, toast) — not the whole catalogue up front.
- **Data:** TanStack Table for the users list and every reporting grid.
  react-hook-form + zod for the create-user and role-scope-assignment forms.
- **API client generation:** `npm run generate-api` runs
  `openapi-typescript` against the running API's `/openapi/v1.json`
  (`Wms:Swagger:Enabled=true`) and writes `src/shared/api/schema.d.ts`.
  Checked in, regenerated on demand — not part of the normal build, so CI
  never needs a live API.

## Screens

All routes below except `/admin/login` sit under the `__root.tsx` layout,
which redirects to `/admin/login` if `AuthContext` has no valid session.

### `/admin/login`

Email + password form → `POST /auth/staff/login`
(`{ email, password }` → `{ accessToken, refreshToken, expiresIn, sessionId,
user: { id, displayName, userType, locale }, permissions, scopes, device }`
on `200`). On success: store the token pair, seed `AuthContext` from the
response body directly (no extra `/auth/me` round trip needed for the
first paint), redirect to `/admin`. On `401`
(`urn:wms:problem:unauthenticated`) or `423`
(`urn:wms:problem:account-locked`, carries `retryAfter`): render inline,
matching the design's deliberately-identical wording for unknown-email vs.
wrong-password.

### `__root.tsx` (shell)

Sidebar: Dashboard, Users. Top bar: display name, a warehouse selector
(`GET /dashboard` requires a `warehouseId` — there is no "all warehouses"
view; the picker's options come from `AuthContext.scopes`, i.e. only
warehouses the signed-in user actually holds a grant in), logout
(`POST /auth/logout`, then clear local state regardless of response — a
failed logout call must not strand the user in a logged-in-looking UI).
Calls `GET /auth/me` on mount and on window focus, to catch a revoked
`securityStamp` promptly (design doc §8.1) — a `401` here forces logout.

### `/admin/dashboard`

`GET /dashboard?warehouseId=…` in one call — receiving summary, putaway
summary, open-exception count, latest reconciliation run, stock-on-hand
summary. Each summary tile links through to the relevant filtered list
(exceptions tile → `/admin/dashboard` exceptions panel backed by
`GET /inventory/exceptions`, receiving tile → a receipts panel backed by
`GET /receipts`). Reconciliation's `varianceCount` renders as a hard
warning state when non-zero — the design doc is explicit that it should
always be zero.

### `/admin/users`

`GET /users?warehouseId=&userType=&status=&q=&limit=&afterId=` → keyset
pagination (`nextCursor.afterId`, `hasMore`). Table columns: display name,
employee code, user type, status, valid-until, role scopes (warehouse
codes). Filters: warehouse (from `AuthContext.scopes`), user type, status,
free-text search. "New user" button, gated on `Can('user.manage')` client-side
(server-enforced regardless).

### `/admin/users/new`

Form: user type (staff/operator), display name, employee code, email,
locale, valid-from/valid-until. **No credentials field and no role-scope
field on this form** — deliberately split from creation, matching the
backend's own permission gate added this session: `POST /users` requires
`role.manage` in addition to `user.manage` when `roleScopes` is non-empty,
and `credential.manage` in addition when `credentials` is non-empty. A
single combined form would either silently need three permissions to ever
succeed, or fail confusingly for an actor who only holds `user.manage`.
Role-scope assignment happens on the detail screen instead, as its own
action against its own endpoint, matching the backend's own separation.
Handles `403 cannot-grant-permission-you-lack` — not applicable here since
this form grants nothing, but `403 insufficient-permission` if the actor
lacks even `user.manage`.

### `/admin/users/$userId`

- **Detail/edit** — `GET /users/{id}`, `PATCH /users/{id}` for display
  name, locale, `validUntil`, `status`. Changing `status` to `suspended`
  surfaces the response's `activeDeviceQueueDepth` as a warning ("N unsynced
  device queue entries will be stranded") — informational, never blocking
  (design doc M6).
- **Role-scope assignment** — a form (role from `GET /roles`, scoped to
  what the *signed-in actor* can actually grant is a server-side H13 check,
  not something the client can compute cheaper than just submitting;
  warehouse from `AuthContext.scopes`; zones optional, empty = all zones
  per §2.1) → `POST /users/{id}/role-scopes`. On `403
  cannot-grant-permission-you-lack`, render the returned
  `missingPermissions` list directly — the backend returns it precisely so
  the UI doesn't have to guess why. Existing grants render as a read-only
  list (no revoke action — see Not in this sub-project, above).
- Deliberately **not** rendering `securityStamp` from the response body
  anywhere, even though the field is present — `docs/shortcuts.md` flags it
  as a value that shouldn't be exposed at all and may be removed from the
  response in a future pass.

## Auth & session

Access token held in memory (`AuthContext`), refresh token in
`localStorage` — there is no BFF to set a real `httpOnly` cookie, so this is
the realistic option for now, not the secure ideal. A query-client-level
response interceptor watches for `401`: on the *first* one, it calls
`POST /auth/refresh` once and retries the original request with the new
access token; a `401` on the refresh call itself (or a second `401` after a
successful refresh) clears `AuthContext` and redirects to `/admin/login` —
no further retry. `GET /auth/me`'s periodic `securityStamp` check (in
`__root.tsx`, on mount and window focus) is what catches a revoked session
promptly rather than waiting for whichever request happens to 401 next.

## Error handling

- **Auth failures**: handled by the refresh-and-retry flow above. Only a
  failure *of that flow* (§ Auth & session) reaches the UI as a logout —
  an individual request's first `401` is never user-visible.
- **Command refusals** (`403`, `409`, `400` from a `POST`/`PATCH`): render
  inline against the form or action that caused them, using the
  `type`/`detail` from the RFC 7807 body — never a generic "something went
  wrong."
- **Reads**: TanStack Query's error boundary per route, with a retry
  action — these are permission-gated the same as writes (`inventory.read`,
  `exception.read`, `receipt.read`, `report.read`), so a `403` here is a
  real, expected state (an actor whose scope changed since login), not a
  bug.
- **`423` account-locked** on login: show `retryAfter` as a countdown, not
  a raw seconds value.

## Testing

Per the `frontend-testing` skill: Vitest + Testing Library component tests
for the login flow, the users list (filters, pagination), create-user, and
role-scope-assignment flows — each against a mocked `src/shared/api`
client, not a live server. One Playwright smoke test: login →
`/admin/users` → open a user → assert the page renders, run against a real
locally-running API. Every screen listed above gets the compact/medium/
expanded resize check the skill requires — the sidebar-collapse behavior in
`__root.tsx` is required behavior, not a later polish pass.

## Open risks

- The exact shape of `ReceiptDetail`, `BalancePage.Totals`, and the various
  `*Summary` types consumed by the dashboard were not fully traced during
  this spec (they live in `Wms.Modules.Inventory.Contracts` /
  `Wms.Modules.Inbound.Contracts` / `Wms.Modules.Tasks.Contracts`) — the
  generated OpenAPI client is the source of truth for these at
  implementation time, not this document.
- `AuthContext.scopes` drives every warehouse picker in this design. If a
  user holds zero scopes (a newly-created account with no role grant yet),
  every screen needs a defined empty state rather than an empty dropdown
  silently breaking the dashboard's required `warehouseId`.
