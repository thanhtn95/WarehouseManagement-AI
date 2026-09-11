---
name: frontend-testing
description: Vitest, Testing Library, and Playwright conventions for testing the WMS React frontend. Use when writing or running a test under web/, or verifying a UI change actually works.
---

# Frontend testing conventions

**REQUIRED BACKGROUND:** the react-frontend skill — this assumes the
`web/` structure, the admin/operator split, and the offline fact queue it
describes.

## Two layers, not one

| | Component/unit | End-to-end |
|---|---|---|
| Tool | Vitest + `@testing-library/react` | Playwright |
| Runs in | jsdom, in-process | a real browser |
| Covers | a component, a hook, a mapper | a full flow across both route trees |
| Speed | milliseconds | seconds |

Vitest because it shares Vite's config and transform pipeline — no second
bundler to keep in sync. Playwright because jsdom doesn't have a service
worker, `BarcodeDetector`, or IndexedDB in any form worth trusting; a flow
that depends on those needs a real browser, same reasoning as
testcontainers-concurrency's "why real PostgreSQL, always."

## What goes where

- A component that renders a pick line, a hook that formats a quantity, a
  mapper from API response to view model → Vitest + Testing Library. Query by
  role/text, not test IDs, so the test breaks when a user-visible behaviour
  breaks.
- Leasing a task, confirming a pick offline, going back online and watching
  the queue drain → Playwright, against a real running dev server.
- `useScanner()` → Vitest for the three source paths (keyboard-wedge burst
  timing, a `BarcodeDetector` stub, manual entry) — no need for a browser to
  prove `{ raw, symbology, source }` comes out right for each.

## Mocking policy

Mock the generated API client at the network boundary (`msw` intercepting
`fetch`) for component tests — never hand-roll a fake client, it drifts from
the real contract silently.

**Don't mock IndexedDB with an in-memory stand-in for anything testing the
fact queue.** Use `fake-indexeddb` for component-level queue tests (it's the
real IndexedDB algorithm, not a reimplementation) and Playwright for
anything that also involves the service worker or Background Sync — a
in-memory-array stand-in proves nothing about a real device queue surviving
a tab close.

## Tests that must exist

A test asserting the fact's idempotency key is generated once, at the
moment of the user action, and is **still the same key** after a simulated
offline period and retry — not regenerated when the queued item is finally
transmitted. This is invariant 11; a regression here silently starts
double-posting every fact queued while offline, and nothing else catches it.

A Playwright test per primary screen in each route tree that resizes the
viewport across Material Design 3's window size classes — compact, medium,
expanded (react-frontend skill, design doc §8.7) — mid-test, and asserts no
element overflow or clipping and no whole-page horizontal scrollbar.
Rendering once at one fixed viewport and calling a screen "responsive"
proves nothing; the requirement is specifically about surviving a resize or
rotation, so the test has to actually do one.

## Running

```bash
npm test                    # Vitest — unit/component, no browser install needed
npx playwright install      # once, before the first E2E run — downloads browser binaries
npx playwright test         # E2E, against a running dev server
```

The `playwright` MCP plugin (browser automation from inside Claude Code) is
for interactively driving and inspecting a page — useful for verifying a
change by hand. It is a different thing from `@playwright/test`, the npm
package that runs an automated E2E suite in CI; both matter, but a
`page.spec.ts` file is what actually executes on every push.

## i18n

Assert both `en` and `ja` resolve for a string carrying `*_i18n` data — a
component test that only ever renders the default locale will not catch a
missing Japanese translation until an operator does.
