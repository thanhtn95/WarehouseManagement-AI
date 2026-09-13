# design-sync notes

## Known render warns

- **Toaster**: left as the floor card, deliberately, after two attempts to
  author a real preview. `Toaster` renders nothing itself — it's a
  container that toast() calls populate — so a static preview needs a
  `toast()` call queued before/during mount to show anything real. Tried:
  (1) `useEffect` + `toast.success(...)`, (2) `useLayoutEffect` (synchronous,
  pre-paint) + `duration: Infinity`. Both produced a genuinely blank
  screenshot (`maxHeight: 0`, `allHollow: true`) — the toast list container
  is `position: fixed`, and unlike Dialog/Select's Radix portals (which
  `cardMode: "single"` measures correctly), a fixed-position toast list
  contributes no layout height to the region the capture measures, and/or
  its enter-transition genuinely hasn't settled by the time a headless
  screenshot fires. This is the "states that can't render statically" case
  the design-sync skill itself calls out — not a corner cut, an honest
  floor card for a fundamentally transient/animated component. Re-authoring
  is a reasonable thing for a later sync to attempt (e.g. driving the
  animation via Playwright interaction rather than a static screenshot), not
  something this sync could resolve within the "static mockups" fidelity
  the user chose.

## Re-sync risks

- `web/`'s Tailwind theme (`web/src/index.css`) is a source input to
  `dist-lib/styles.css` via the library build (`vite.lib.config.ts`) — any
  future theme/token change needs `npm run build:lib` re-run before the
  next design-sync, same as the app's own `npm run build`. The `buildCmd`
  in config.json already does this automatically on re-sync.
- The 10 authored previews assume the current shadcn component APIs
  (`Card`'s `size` prop, `Dialog`'s `showCloseButton`, `Select`'s
  `defaultOpen`/`defaultValue`, etc.). If any of these 10 components' props
  change, the previews should be re-checked against the new `.d.ts` before
  trusting them to still compile/render correctly.
- `src/components/ui/index.ts` (the library entry) is currently the only
  consumer of `web/src/index.css` as an actual import — nothing in the real
  app imports it through this barrel (the app's own screens, once built,
  import individual `@/components/ui/*` files directly and get the theme
  from the app's own root import, not this barrel). If a future refactor
  changes how the app wires its theme, double-check this barrel's CSS
  import still produces a real, themed `dist-lib/styles.css`.
