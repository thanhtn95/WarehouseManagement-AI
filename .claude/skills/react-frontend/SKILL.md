---
name: react-frontend
description: React/TypeScript/Vite conventions for the WMS frontend — route trees, offline fact sync, the scan-input abstraction, and i18n. Use when adding a screen, a hook, or anything under web/.
---

# React frontend conventions

## Structure

```
web/
  src/routes/admin/       dense tables, dashboards, reports
  src/routes/operator/    single-task screens, service worker, IndexedDB queue
  src/shared/             generated API client, i18n catalogue, design tokens, auth context
```

One app, one Vite build, one deployment, served by Caddy from one origin.
Two route trees, not two apps — they share the build, the API client, and the
i18n catalogue (design doc §1.2).

## Admin vs operator

| | `/admin/*` | `/operator/*` |
|---|---|---|
| Shell | Sidebar, data grids, filters | Single-task, full-bleed, large targets |
| State | TanStack Query, server-authoritative | TanStack Query + IndexedDB fact queue |
| Offline | None — requires connectivity | Service worker, background sync |
| Delivery | Standard SPA | Installable PWA, `display: standalone` |

A screen under `/admin/*` calling `/sync/facts`, or one under `/operator/*`
expecting a request to fail cleanly when offline, is in the wrong tree.

## Layout and responsiveness

**REQUIRED BACKGROUND:** design doc §8.7 — this is a stated non-functional
requirement, not a nice-to-have. Neither tree may clip, overlap, or force
whole-page horizontal scroll anywhere in its realistic viewport range:
compact/medium/expanded per [Material Design 3's window size classes](https://m3.material.io/foundations/layout/applying-layout/window-size-classes).

- Relative units and flexbox/grid, always. No component assumes a fixed
  pixel viewport — `/operator/*` runs on handhelds that get rotated
  mid-task, `/admin/*` runs in a browser window that gets resized, not
  maximised-and-forgotten.
- A wide table or dense grid scrolls horizontally **inside its own
  container**. The page itself never scrolls sideways.
- `/admin/*`'s sidebar collapses to an icon rail or drawer before it
  overlaps content — build that behaviour in from the start, not as a
  later fix once someone resizes below your dev monitor's width.
- Don't hand-test at one fixed browser size and call a screen done. See
  frontend-testing for the resize check every primary screen needs.

## The command/fact split, on the client

**REQUIRED BACKGROUND:** the wms-domain skill's command/fact split. On the
client it maps directly to two request shapes:

- **Command** (`/work`, `/orders`, `/config`, …) — sent immediately, requires
  connectivity, the UI shows and acts on a refusal (`409`, `403`).
- **Fact** (`/sync/facts`) — enqueued to IndexedDB first, sent when connectivity
  allows, **never surfaced as a failure**. A `202` with an `exceptionIds` entry
  is a successful submission, not an error state — don't render it as one.

**Idempotency keys are generated at the moment of the user action** (the tap
that confirms a pick or a putaway), not when the queued item is finally
transmitted. Generating the key at send time defeats the point: a retry of
queued work must reuse the *original* key, or a device that queues offline
and later flushes a batch will double-post every fact in it. Generate once,
store the key alongside the queued fact, done.

## Offline fact queue

`/operator/*` only. A service worker intercepts `POST /api/v1/sync/facts`,
queues to IndexedDB on failure, and drains via Background Sync when
connectivity returns. Queue depth is reported on every heartbeat
(`POST /devices/{id}/heartbeat`, field `queueDepth`) — it is not just a UI
badge, it's a fleet-health signal.

A lease `410` (reclaimed) does **not** mean queued confirmations for that
task are discarded — they still submit and are still accepted (design doc
§6.4 case D). Don't wire lease expiry to clearing the queue.

## Scan-input abstraction

All barcode input goes through one `useScanner()` hook regardless of source:
keyboard wedge (burst-timing detection, inter-key interval under ~30ms),
camera (`BarcodeDetector` with ZXing-WASM fallback), or manual entry (always
available, always audited as manual). It emits `{ raw, symbology, source }`.
No screen branches on device type — that's what makes swapping hardware
later a non-event.

## State and data

TanStack Query everywhere; `/operator/*` additionally reads/writes IndexedDB
for the fact queue. Use the generated TypeScript API client
(`src/shared/`) rather than hand-written fetch calls — it's generated from
the API's contract, so a server-side change becomes a type error at build
time instead of a runtime bug.

## i18n

English and Japanese. `*_i18n` fields arrive from the API as
`{ "en": "...", "ja": "..." }` and are resolved client-side against the
user's `locale`, not `Accept-Language` renegotiated per request. Never
concatenate translated strings — pass full sentences to the catalogue.

## Style

Based on the [Google TypeScript Style Guide](https://google.github.io/styleguide/tsguide.html),
[React's own conventions](https://react.dev/learn), and the
[Airbnb React/JSX Style Guide](https://github.com/airbnb/javascript/tree/master/react)
— not house rules invented for this repo.

**Exports:** named exports only, no `default export` — Google's guide states
this outright. A renamed component can't silently keep a stale name at every
import site, and auto-import tooling never has to guess what to call it.

**Naming:** `PascalCase` components, one per file, filename matches the
component. `camelCase` everything else. Every custom hook starts with
`use` — not a style preference, `eslint-plugin-react-hooks` depends on the
prefix to know which functions the Rules of Hooks apply to.

**Types:** TypeScript strict mode. No `any`, ever — `unknown` plus a
narrowing check at the boundary instead. `interface` for object shapes
meant to be extended (component props, API DTOs); `type` for unions,
intersections, and mapped types. Explicit return types on every exported
function — inference is fine internally, but a public signature that
silently changes shape is a worse bug than the annotation is verbosity.

**Components:** function components and hooks only — no class components.
Props destructured in the signature, not accessed as `props.x` in the body.
Components take data, not query hooks, as props where reasonably possible
— keeps `/admin/*` grids testable without a network mock per test.

**Formatting is Prettier's job, not a discussion.** `npm run lint` includes
the format check; nobody hand-formats or debates brace style in review.
`npm run lint` and `npm test` (Vitest) before considering a change done —
see the Commands section of CLAUDE.md.
