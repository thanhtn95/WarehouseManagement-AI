# Admin Frontend Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up the WMS admin frontend from nothing — a Vite/React/TypeScript app with a typed API client generated from the live backend, staff login, a permission-aware shell, and the user/role-management and dashboard/reporting screens the backend already supports.

**Architecture:** One Vite build under `web/`. TanStack Router (file-based) for routing, TanStack Query for server state, a hand-rolled session module (no React) for token/refresh state that the typed `openapi-fetch` client and the React `AuthContext` both sit on top of. shadcn/ui (Radix) + Tailwind for components. Every screen talks to a real, already-tested backend endpoint — nothing here is mocked at the integration level except in component tests.

**Tech Stack:** React 18, TypeScript (strict), Vite, TanStack Router, TanStack Query, TanStack Table, openapi-typescript + openapi-fetch, Tailwind CSS v4, shadcn/ui, react-hook-form + zod, Vitest + Testing Library, Playwright.

**Spec:** `docs/superpowers/specs/2026-09-11-admin-frontend-foundation-design.md`

## Global Constraints

- TypeScript strict mode everywhere. No `any` — `unknown` plus a narrowing check at the boundary (react-frontend skill).
- Named exports only, no `default export` (react-frontend skill).
- `PascalCase` components (one per file, filename matches), `camelCase` everything else, every hook starts with `use` (react-frontend skill).
- `interface` for object shapes meant to be extended (props, DTOs); `type` for unions/intersections/mapped types (react-frontend skill).
- Function components and hooks only. Props destructured in the signature.
- Every primary screen must not clip, overlap, or force page-level horizontal scroll across compact/medium/expanded viewport widths (design doc §8.7) — verified per screen, not assumed.
- This plan builds `/admin/*` only. No `web/src/routes/operator/` directory is created.
- No credentials/role-scope fields on the create-user form — the backend requires `role.manage`/`credential.manage` in addition to `user.manage` for those, and combining them would make the simple case (an actor who only holds `user.manage`) fail confusingly.
- `POST /roles` does not exist — the role-scope-assignment screen may only assign a *seeded* role, never compose a new one.
- There is no `DELETE /users/{id}/role-scopes/{id}` — existing grants render read-only, no revoke action.
- `security_stamp` is present in the `GET /users/{id}` response but must never be rendered anywhere in the UI (`docs/shortcuts.md`).
- All commands run from the repository root unless a step says otherwise; `web/` is created by Task 1 and every later `cd`/`npm` command in this plan implicitly runs inside it.

---

## Task 1: Project scaffold

**Files:**
- Create: `web/package.json`, `web/tsconfig.json`, `web/tsconfig.node.json`, `web/vite.config.ts`, `web/index.html`, `web/src/main.tsx`, `web/src/index.css`, `web/.eslintrc.cjs`, `web/.prettierrc.json`, `web/src/vite-env.d.ts`
- Create: `web/src/App.test.tsx` (proof the test harness works — deleted in Task 5 once real routing exists)
- Create: `web/components.json` (shadcn config, written by its CLI)

**Interfaces:**
- Produces: a working `npm run dev`, `npm run build`, `npm run lint`, `npm test` in `web/`. Every later task depends on this.

- [ ] **Step 1: Scaffold the Vite React-TS project**

```bash
cd D:/Work/AI/WarehouseManagementSystem
npm create vite@latest web -- --template react-ts
cd web
```

- [ ] **Step 2: Install runtime dependencies**

```bash
npm install @tanstack/react-router @tanstack/react-query @tanstack/react-table \
  openapi-fetch react-hook-form zod @hookform/resolvers
```

- [ ] **Step 3: Install dev dependencies**

```bash
npm install -D @tanstack/router-plugin openapi-typescript \
  tailwindcss @tailwindcss/vite \
  vitest @testing-library/react @testing-library/jest-dom @testing-library/user-event jsdom \
  @playwright/test \
  eslint eslint-plugin-react-hooks eslint-plugin-react-refresh @typescript-eslint/eslint-plugin @typescript-eslint/parser \
  prettier
```

- [ ] **Step 4: Configure Vite — router plugin, Tailwind plugin, Vitest, path alias**

`web/vite.config.ts`:

```ts
/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { tanstackRouter } from '@tanstack/router-plugin/vite';
import tailwindcss from '@tailwindcss/vite';
import path from 'node:path';

export default defineConfig({
  // tanstackRouter MUST precede react() — it generates routeTree.gen.ts
  // that react()'s fast-refresh pipeline then needs to see.
  plugins: [
    tanstackRouter({ target: 'react', autoCodeSplitting: true }),
    react(),
    tailwindcss(),
  ],
  resolve: {
    alias: { '@': path.resolve(__dirname, './src') },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test-setup.ts'],
  },
});
```

`web/src/test-setup.ts`:

```ts
import '@testing-library/jest-dom/vitest';
```

`web/src/index.css`:

```css
@import "tailwindcss";
```

- [ ] **Step 5: Configure TypeScript strict mode and the `@/` path alias**

Edit `web/tsconfig.json`'s `compilerOptions` to include:

```json
{
  "compilerOptions": {
    "strict": true,
    "noUncheckedIndexedAccess": true,
    "baseUrl": ".",
    "paths": { "@/*": ["./src/*"] }
  }
}
```

- [ ] **Step 6: Add `npm run` scripts**

Edit `web/package.json`'s `scripts`:

```json
{
  "scripts": {
    "dev": "vite",
    "build": "tsc -b && vite build",
    "lint": "eslint . && prettier --check .",
    "format": "prettier --write .",
    "test": "vitest run",
    "test:watch": "vitest",
    "e2e": "playwright test",
    "generate-api": "openapi-typescript http://localhost:5000/openapi/v1.json -o src/shared/api/schema.d.ts"
  }
}
```

(The port in `generate-api` is a placeholder for Task 2, which corrects it to whatever `dotnet run` actually prints — there is no `launchSettings.json` pinning it yet.)

- [ ] **Step 7: Initialize shadcn/ui**

```bash
npx shadcn@latest init -y -b radix -t vite
npx shadcn@latest add button input label table dialog form select badge toast card
```

- [ ] **Step 8: Write a trivial test to prove the harness works**

`web/src/App.test.tsx`:

```tsx
import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';

function Hello() {
  return <p>WMS admin</p>;
}

describe('test harness', () => {
  it('renders', () => {
    render(<Hello />);
    expect(screen.getByText('WMS admin')).toBeInTheDocument();
  });
});
```

- [ ] **Step 9: Run the test to verify it passes**

Run: `npm test` (from `web/`)
Expected: PASS — 1 test.

- [ ] **Step 10: Verify build and lint are clean**

Run: `npm run build && npm run lint`
Expected: both exit 0.

- [ ] **Step 11: Commit**

```bash
git add web/
git commit -m "chore: scaffold admin frontend (Vite, React, TS, TanStack, shadcn)"
```

---

## Task 2: Generated API client and session module

**Files:**
- Create: `web/src/shared/api/schema.d.ts` (generated, not hand-written)
- Create: `web/src/shared/auth/session.ts`
- Create: `web/src/shared/auth/session.test.ts`
- Create: `web/src/shared/api/client.ts`
- Create: `web/src/shared/api/withAuthRetry.ts`
- Create: `web/src/shared/api/withAuthRetry.test.ts`

**Interfaces:**
- Consumes: nothing from earlier tasks besides the scaffold.
- Produces:
  - `paths` type from `@/shared/api/schema` (openapi-typescript output).
  - `SessionUser { id: string; displayName: string; userType: string; locale: string }`, `SessionScope { warehouseId: string; zoneIds: string[] }`, `SessionSnapshot { status: 'anonymous' | 'authenticated'; user: SessionUser | null; permissions: Set<string>; scopes: SessionScope[] }` from `@/shared/auth/session`.
  - `session.getSnapshot(): SessionSnapshot`, `session.subscribe(listener: () => void): () => void`, `session.setSession(data: { accessToken: string; refreshToken: string; user: SessionUser; permissions: string[]; scopes: SessionScope[] }): void`, `session.clearSession(): void`, `session.getAccessToken(): string | null`, `session.refreshAccessToken(): Promise<boolean>` from `@/shared/auth/session`.
  - `api` (the `openapi-fetch` client instance) from `@/shared/api/client`.
  - `withAuthRetry<T>(call: () => Promise<{ data?: T; error?: unknown; response: Response }>): Promise<{ data?: T; error?: unknown; response: Response }>` from `@/shared/api/withAuthRetry`.

### Step group A — generate the typed schema

- [ ] **Step 1: Start the API locally and note the actual URL**

```bash
dotnet run --project src/Wms.Migrator -- "<local Postgres connection string>"
dotnet run --project src/Wms.Api
```

Note the `http://localhost:PORT` Kestrel prints on startup (there is no `launchSettings.json`, so this is not a fixed port — read it from the console).

- [ ] **Step 2: Fix the `generate-api` script to the real URL, then run it**

Edit `web/package.json`'s `generate-api` script to use the port from Step 1, then:

```bash
npm run generate-api
```

Expected: `web/src/shared/api/schema.d.ts` is created and non-empty.

- [ ] **Step 3: Verify the generated schema actually contains the endpoints this plan depends on**

Run: `grep -c "/api/v1/auth/staff/login\|/api/v1/users\|/api/v1/dashboard" web/src/shared/api/schema.d.ts`
Expected: a count greater than 0. If zero, `Wms:Swagger:Enabled` is not `true` on the running instance — check `appsettings.Development.json`.

### Step group B — session module (plain TypeScript, no React)

- [ ] **Step 4: Write the failing test for session state transitions**

`web/src/shared/auth/session.test.ts`:

```ts
import { afterEach, describe, expect, it, vi } from 'vitest';
import { clearSession, getAccessToken, getSnapshot, setSession, subscribe } from './session';

describe('session', () => {
  afterEach(() => {
    clearSession();
  });

  it('starts anonymous with no access token', () => {
    expect(getSnapshot().status).toBe('anonymous');
    expect(getAccessToken()).toBeNull();
  });

  it('becomes authenticated after setSession, and notifies subscribers', () => {
    const listener = vi.fn();
    const unsubscribe = subscribe(listener);

    setSession({
      accessToken: 'access-1',
      refreshToken: 'refresh-1',
      user: { id: 'u1', displayName: 'Ada', userType: 'staff', locale: 'en' },
      permissions: ['user.manage'],
      scopes: [{ warehouseId: 'w1', zoneIds: [] }],
    });

    expect(listener).toHaveBeenCalledTimes(1);
    const snapshot = getSnapshot();
    expect(snapshot.status).toBe('authenticated');
    expect(snapshot.user?.displayName).toBe('Ada');
    expect(snapshot.permissions.has('user.manage')).toBe(true);
    expect(getAccessToken()).toBe('access-1');

    unsubscribe();
  });

  it('returns to anonymous and clears the access token on clearSession', () => {
    setSession({
      accessToken: 'access-1',
      refreshToken: 'refresh-1',
      user: { id: 'u1', displayName: 'Ada', userType: 'staff', locale: 'en' },
      permissions: [],
      scopes: [],
    });

    clearSession();

    expect(getSnapshot().status).toBe('anonymous');
    expect(getAccessToken()).toBeNull();
  });
});
```

- [ ] **Step 5: Run the test to verify it fails**

Run: `npm test -- session.test.ts` (from `web/`)
Expected: FAIL — `./session` has no exports yet.

- [ ] **Step 6: Implement the session module**

`web/src/shared/auth/session.ts`:

```ts
export interface SessionUser {
  id: string;
  displayName: string;
  userType: string;
  locale: string;
}

export interface SessionScope {
  warehouseId: string;
  zoneIds: string[];
}

export interface SessionSnapshot {
  status: 'anonymous' | 'authenticated';
  user: SessionUser | null;
  permissions: Set<string>;
  scopes: SessionScope[];
}

interface SetSessionInput {
  accessToken: string;
  refreshToken: string;
  user: SessionUser;
  permissions: string[];
  scopes: SessionScope[];
}

const REFRESH_TOKEN_KEY = 'wms.refreshToken';

let accessToken: string | null = null;
let snapshot: SessionSnapshot = { status: 'anonymous', user: null, permissions: new Set(), scopes: [] };
const listeners = new Set<() => void>();

function notify(): void {
  for (const listener of listeners) {
    listener();
  }
}

export function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function getSnapshot(): SessionSnapshot {
  return snapshot;
}

export function getAccessToken(): string | null {
  return accessToken;
}

export function setSession(input: SetSessionInput): void {
  accessToken = input.accessToken;
  localStorage.setItem(REFRESH_TOKEN_KEY, input.refreshToken);
  snapshot = {
    status: 'authenticated',
    user: input.user,
    permissions: new Set(input.permissions),
    scopes: input.scopes,
  };
  notify();
}

export function clearSession(): void {
  accessToken = null;
  localStorage.removeItem(REFRESH_TOKEN_KEY);
  snapshot = { status: 'anonymous', user: null, permissions: new Set(), scopes: [] };
  notify();
}

// Deduplicated: concurrent 401s must trigger exactly one refresh call, not
// one per failed request.
let refreshInFlight: Promise<boolean> | null = null;

export async function refreshAccessToken(): Promise<boolean> {
  if (refreshInFlight) {
    return refreshInFlight;
  }

  refreshInFlight = (async () => {
    const refreshToken = localStorage.getItem(REFRESH_TOKEN_KEY);
    if (!refreshToken) {
      clearSession();
      return false;
    }

    // A raw fetch, deliberately not the openapi-fetch client: the client's
    // own auth middleware calls this function, so routing the refresh call
    // back through the client would recurse.
    const response = await fetch('/api/v1/auth/refresh', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ refreshToken }),
    });

    if (!response.ok) {
      clearSession();
      return false;
    }

    const body = (await response.json()) as { accessToken: string; refreshToken: string };
    accessToken = body.accessToken;
    localStorage.setItem(REFRESH_TOKEN_KEY, body.refreshToken);
    return true;
  })();

  try {
    return await refreshInFlight;
  } finally {
    refreshInFlight = null;
  }
}
```

- [ ] **Step 7: Run the test to verify it passes**

Run: `npm test -- session.test.ts`
Expected: PASS — 3 tests.

### Step group C — typed client and refresh-retry wrapper

- [ ] **Step 8: Implement the typed client with the auth-header middleware**

`web/src/shared/api/client.ts`:

```ts
import createClient, { type Middleware } from 'openapi-fetch';
import type { paths } from './schema';
import { getAccessToken } from '@/shared/auth/session';

const authMiddleware: Middleware = {
  async onRequest({ request }) {
    const token = getAccessToken();
    if (token) {
      request.headers.set('Authorization', `Bearer ${token}`);
    }
    return request;
  },
};

export const api = createClient<paths>({ baseUrl: '/api/v1' });
api.use(authMiddleware);
```

- [ ] **Step 9: Write the failing test for the refresh-and-retry-once behavior**

`web/src/shared/api/withAuthRetry.test.ts`:

```ts
import { afterEach, describe, expect, it, vi } from 'vitest';
import { withAuthRetry } from './withAuthRetry';
import * as session from '@/shared/auth/session';

describe('withAuthRetry', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('returns the result unchanged when the call succeeds', async () => {
    const call = vi.fn().mockResolvedValue({ data: { ok: true }, response: new Response(null, { status: 200 }) });

    const result = await withAuthRetry(call);

    expect(result.data).toEqual({ ok: true });
    expect(call).toHaveBeenCalledTimes(1);
  });

  it('refreshes once and retries after a 401, returning the retry result', async () => {
    const refreshSpy = vi.spyOn(session, 'refreshAccessToken').mockResolvedValue(true);
    const call = vi
      .fn()
      .mockResolvedValueOnce({ response: new Response(null, { status: 401 }) })
      .mockResolvedValueOnce({ data: { ok: true }, response: new Response(null, { status: 200 }) });

    const result = await withAuthRetry(call);

    expect(refreshSpy).toHaveBeenCalledTimes(1);
    expect(call).toHaveBeenCalledTimes(2);
    expect(result.data).toEqual({ ok: true });
  });

  it('does not retry a second time if the retried call also 401s', async () => {
    vi.spyOn(session, 'refreshAccessToken').mockResolvedValue(true);
    const call = vi.fn().mockResolvedValue({ response: new Response(null, { status: 401 }) });

    const result = await withAuthRetry(call);

    expect(call).toHaveBeenCalledTimes(2);
    expect(result.response.status).toBe(401);
  });

  it('does not retry when refreshAccessToken itself fails', async () => {
    vi.spyOn(session, 'refreshAccessToken').mockResolvedValue(false);
    const call = vi.fn().mockResolvedValue({ response: new Response(null, { status: 401 }) });

    const result = await withAuthRetry(call);

    expect(call).toHaveBeenCalledTimes(1);
    expect(result.response.status).toBe(401);
  });
});
```

- [ ] **Step 10: Run the test to verify it fails**

Run: `npm test -- withAuthRetry.test.ts`
Expected: FAIL — `./withAuthRetry` has no exports yet.

- [ ] **Step 11: Implement withAuthRetry**

`web/src/shared/api/withAuthRetry.ts`:

```ts
import { refreshAccessToken } from '@/shared/auth/session';

interface ApiResult<T> {
  data?: T;
  error?: unknown;
  response: Response;
}

export async function withAuthRetry<T>(
  call: () => Promise<ApiResult<T>>,
): Promise<ApiResult<T>> {
  const first = await call();
  if (first.response.status !== 401) {
    return first;
  }

  const refreshed = await refreshAccessToken();
  if (!refreshed) {
    return first;
  }

  return call();
}
```

- [ ] **Step 12: Run the test to verify it passes**

Run: `npm test -- withAuthRetry.test.ts`
Expected: PASS — 4 tests.

- [ ] **Step 13: Commit**

```bash
git add web/src/shared/api web/src/shared/auth web/package.json
git commit -m "feat: generated API client, session module, and refresh-retry wrapper"
```

---

## Task 3: AuthContext (React auth layer)

**Files:**
- Create: `web/src/shared/auth/AuthContext.tsx`
- Create: `web/src/shared/auth/AuthContext.test.tsx`

**Interfaces:**
- Consumes: `session.*` from `@/shared/auth/session` (Task 2), `api` from `@/shared/api/client` (Task 2), `withAuthRetry` from `@/shared/api/withAuthRetry` (Task 2).
- Produces: `AuthProvider` component, `useAuth(): { status: 'anonymous' | 'authenticated'; user: SessionUser | null; permissions: Set<string>; scopes: SessionScope[]; can(permission: string): boolean; login(email: string, password: string): Promise<{ ok: true } | { ok: false; kind: 'invalid' | 'locked'; retryAfterSeconds?: number }>; logout(): Promise<void>; validateSession(): Promise<void> }` from `@/shared/auth/AuthContext`.

- [ ] **Step 1: Write the failing test for `login` populating auth state**

`web/src/shared/auth/AuthContext.test.tsx`:

```tsx
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AuthProvider, useAuth } from './AuthContext';
import { api } from '@/shared/api/client';
import { clearSession } from './session';

function Probe() {
  const auth = useAuth();
  return (
    <div>
      <span data-testid="status">{auth.status}</span>
      <button
        onClick={() => {
          void auth.login('ada@example.com', 'correct horse battery staple');
        }}
      >
        log in
      </button>
    </div>
  );
}

describe('AuthContext', () => {
  afterEach(() => {
    clearSession();
    vi.restoreAllMocks();
  });

  it('moves from anonymous to authenticated after a successful login', async () => {
    vi.spyOn(api, 'POST').mockResolvedValue({
      data: {
        accessToken: 'a',
        refreshToken: 'r',
        expiresIn: 900,
        sessionId: null,
        user: { id: 'u1', displayName: 'Ada', userType: 'staff', locale: 'en' },
        permissions: ['user.manage'],
        scopes: [],
        device: null,
      },
      response: new Response(null, { status: 200 }),
    } as never);

    render(
      <AuthProvider>
        <Probe />
      </AuthProvider>,
    );

    expect(screen.getByTestId('status')).toHaveTextContent('anonymous');
    await userEvent.click(screen.getByText('log in'));

    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('authenticated'));
  });
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `npm test -- AuthContext.test.tsx`
Expected: FAIL — `./AuthContext` has no exports yet.

- [ ] **Step 3: Implement AuthContext**

`web/src/shared/auth/AuthContext.tsx`:

```tsx
import { createContext, useCallback, useContext, useSyncExternalStore, type ReactNode } from 'react';
import { api } from '@/shared/api/client';
import { clearSession, getSnapshot, setSession, subscribe, type SessionScope, type SessionUser } from './session';

type LoginOutcome =
  | { ok: true }
  | { ok: false; kind: 'invalid' | 'locked'; retryAfterSeconds?: number };

interface AuthContextValue {
  status: 'anonymous' | 'authenticated';
  user: SessionUser | null;
  permissions: Set<string>;
  scopes: SessionScope[];
  can(permission: string): boolean;
  login(email: string, password: string): Promise<LoginOutcome>;
  logout(): Promise<void>;
}

const AuthReactContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const snapshot = useSyncExternalStore(subscribe, getSnapshot);

  const login = useCallback(async (email: string, password: string): Promise<LoginOutcome> => {
    const { data, response } = await api.POST('/auth/staff/login', {
      body: { email, password },
    });

    if (response.status === 200 && data) {
      setSession({
        accessToken: data.accessToken,
        refreshToken: data.refreshToken,
        user: data.user,
        permissions: data.permissions,
        scopes: data.scopes,
      });
      return { ok: true };
    }

    if (response.status === 423) {
      const body = (await response.clone().json()) as { retryAfter?: number };
      return { ok: false, kind: 'locked', retryAfterSeconds: body.retryAfter };
    }

    return { ok: false, kind: 'invalid' };
  }, []);

  const logout = useCallback(async () => {
    try {
      await api.POST('/auth/logout', { body: {} });
    } finally {
      // Clearing local state must happen regardless of whether the network
      // call succeeded — a failed logout must not strand the user in a
      // logged-in-looking UI.
      clearSession();
    }
  }, []);

  const can = useCallback((permission: string) => snapshot.permissions.has(permission), [snapshot.permissions]);

  const value: AuthContextValue = {
    status: snapshot.status,
    user: snapshot.user,
    permissions: snapshot.permissions,
    scopes: snapshot.scopes,
    can,
    login,
    logout,
  };

  return <AuthReactContext.Provider value={value}>{children}</AuthReactContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const value = useContext(AuthReactContext);
  if (!value) {
    throw new Error('useAuth must be used within an AuthProvider');
  }
  return value;
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `npm test -- AuthContext.test.tsx`
Expected: PASS — 1 test.

- [ ] **Step 5: Write the failing test for `validateSession` logging out on a failed check**

Append to `web/src/shared/auth/AuthContext.test.tsx`:

```tsx
it('logs out when validateSession finds the session no longer resolves', async () => {
  vi.spyOn(api, 'POST').mockResolvedValue({
    data: {
      accessToken: 'a',
      refreshToken: 'r',
      expiresIn: 900,
      sessionId: null,
      user: { id: 'u1', displayName: 'Ada', userType: 'staff', locale: 'en' },
      permissions: [],
      scopes: [],
      device: null,
    },
    response: new Response(null, { status: 200 }),
  } as never);
  vi.spyOn(api, 'GET').mockResolvedValue({
    response: new Response(null, { status: 401 }),
  } as never);

  function Probe() {
    const auth = useAuth();
    return (
      <div>
        <span data-testid="status">{auth.status}</span>
        <button onClick={() => void auth.login('a@b.com', 'x')}>log in</button>
        <button onClick={() => void auth.validateSession()}>validate</button>
      </div>
    );
  }

  render(
    <AuthProvider>
      <Probe />
    </AuthProvider>,
  );

  await userEvent.click(screen.getByText('log in'));
  await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('authenticated'));

  await userEvent.click(screen.getByText('validate'));
  await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('anonymous'));
});
```

- [ ] **Step 6: Run the test to verify it fails**

Run: `npm test -- AuthContext.test.tsx`
Expected: FAIL — `validateSession` does not exist on the context value yet.

- [ ] **Step 7: Implement `validateSession`**

In `web/src/shared/auth/AuthContext.tsx`, add the import and the method, and include it in the returned `value`:

```ts
import { withAuthRetry } from '@/shared/api/withAuthRetry';
```

```ts
  const validateSession = useCallback(async () => {
    // The backend's TokenPrincipalResolver already compares security_stamp
    // on every request and resolves to no principal on a mismatch — a 401
    // here (even after withAuthRetry's one refresh attempt) means the
    // session is genuinely gone, not a transient network blip.
    const { response } = await withAuthRetry(() => api.GET('/auth/me', {}));
    if (response.status !== 200) {
      clearSession();
    }
  }, []);
```

Add `validateSession` to the `value` object alongside `login`/`logout`.

- [ ] **Step 8: Run the test to verify it passes**

Run: `npm test -- AuthContext.test.tsx`
Expected: PASS — 2 tests.

- [ ] **Step 9: Commit**

```bash
git add web/src/shared/auth
git commit -m "feat: AuthContext wrapping the session module, with focus-triggered session validation"
```

---

## Task 4: i18n catalogue

**Files:**
- Create: `web/src/shared/i18n/resolveI18n.ts`
- Create: `web/src/shared/i18n/resolveI18n.test.ts`
- Create: `web/src/shared/i18n/strings.ts`

**Interfaces:**
- Produces: `resolveI18n(field: Record<string, string> | null | undefined, locale: string): string` from `@/shared/i18n/resolveI18n`; a `strings` object of UI-chrome English text from `@/shared/i18n/strings` (e.g. `strings.nav.dashboard`, `strings.nav.users`, `strings.actions.logout`).

- [ ] **Step 1: Write the failing test**

`web/src/shared/i18n/resolveI18n.test.ts`:

```ts
import { describe, expect, it } from 'vitest';
import { resolveI18n } from './resolveI18n';

describe('resolveI18n', () => {
  it('returns the value for the requested locale', () => {
    expect(resolveI18n({ en: 'Picker', ja: 'ピッカー' }, 'ja')).toBe('ピッカー');
  });

  it('falls back to English when the locale is missing', () => {
    expect(resolveI18n({ en: 'Picker' }, 'ja')).toBe('Picker');
  });

  it('returns an empty string for a null or undefined field rather than throwing', () => {
    expect(resolveI18n(null, 'en')).toBe('');
    expect(resolveI18n(undefined, 'en')).toBe('');
  });
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `npm test -- resolveI18n.test.ts`
Expected: FAIL — `./resolveI18n` has no exports yet.

- [ ] **Step 3: Implement resolveI18n**

`web/src/shared/i18n/resolveI18n.ts`:

```ts
// Mirrors the backend's own fallback, e.g.
// `COALESCE(name_i18n->>@locale, name_i18n->>'en')` — never silently blank
// when a translation is missing.
export function resolveI18n(
  field: Record<string, string> | null | undefined,
  locale: string,
): string {
  if (!field) {
    return '';
  }
  return field[locale] ?? field.en ?? '';
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `npm test -- resolveI18n.test.ts`
Expected: PASS — 3 tests.

- [ ] **Step 5: Write the English UI-chrome string catalogue**

`web/src/shared/i18n/strings.ts`:

```ts
export const strings = {
  nav: { dashboard: 'Dashboard', users: 'Users' },
  actions: {
    logout: 'Log out',
    newUser: 'New user',
    save: 'Save',
    cancel: 'Cancel',
    assignRole: 'Assign role',
  },
  login: {
    title: 'Sign in',
    email: 'Email',
    password: 'Password',
    submit: 'Sign in',
    invalidCredentials: 'Incorrect email or password.',
    locked: (retryAfterSeconds: number) =>
      `Too many attempts. Try again in ${retryAfterSeconds} seconds.`,
  },
  users: {
    title: 'Users',
    searchPlaceholder: 'Search by name or employee code',
    noRevokeNotice: 'Role scopes can be granted here, but not yet revoked from this screen.',
  },
} as const;
```

- [ ] **Step 6: Commit**

```bash
git add web/src/shared/i18n
git commit -m "feat: i18n resolver and English UI string catalogue"
```

---

## Task 5: Router and Query bootstrap

**Files:**
- Create: `web/src/routes/__root.tsx`
- Create: `web/src/main.tsx` (replaces the scaffold's version)
- Delete: `web/src/App.tsx`, `web/src/App.css`, `web/src/App.test.tsx` (scaffold defaults, superseded)

**Interfaces:**
- Consumes: `AuthProvider`, `useAuth` (Task 3).
- Produces: the running router shell — every later route task adds files under `web/src/routes/admin/` and the generator (`tanstackRouter` Vite plugin, configured in Task 1) picks them up automatically into `web/src/routeTree.gen.ts`.

- [ ] **Step 1: Remove the scaffold's placeholder app**

```bash
rm web/src/App.tsx web/src/App.css web/src/App.test.tsx
```

- [ ] **Step 2: Create the root route**

`web/src/routes/__root.tsx`:

```tsx
import { createRootRouteWithContext, Outlet } from '@tanstack/react-router';
import type { QueryClient } from '@tanstack/react-query';

interface RouterContext {
  queryClient: QueryClient;
  auth: {
    status: 'anonymous' | 'authenticated';
    can(permission: string): boolean;
  };
}

export const Route = createRootRouteWithContext<RouterContext>()({
  component: () => <Outlet />,
});
```

- [ ] **Step 3: Wire main.tsx — QueryClient, router creation, live auth context injection**

`web/src/main.tsx`:

```tsx
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider, createRouter } from '@tanstack/react-router';
import { routeTree } from './routeTree.gen';
import { AuthProvider, useAuth } from '@/shared/auth/AuthContext';
import './index.css';

const queryClient = new QueryClient();

// Created once, outside any component, so authentication changes never
// recreate the router — the live auth object is injected per the
// `context` prop on RouterProvider below instead.
const router = createRouter({
  routeTree,
  context: {
    queryClient,
    auth: undefined!, // populated by InnerApp on every render
  },
});

declare module '@tanstack/react-router' {
  interface Register {
    router: typeof router;
  }
}

function InnerApp() {
  const auth = useAuth();
  return <RouterProvider router={router} context={{ queryClient, auth }} />;
}

const rootElement = document.getElementById('root');
if (!rootElement) {
  throw new Error('#root element not found');
}

createRoot(rootElement).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <AuthProvider>
        <InnerApp />
      </AuthProvider>
    </QueryClientProvider>
  </StrictMode>,
);
```

- [ ] **Step 4: Verify the dev server starts with no routes yet**

Run: `npm run dev` (from `web/`), open the printed URL.
Expected: a blank page with no console errors (there are no routes under `src/routes/admin/` yet — that starts in Task 6). Stop the dev server after confirming.

- [ ] **Step 5: Verify the build still succeeds**

Run: `npm run build`
Expected: exits 0.

- [ ] **Step 6: Commit**

```bash
git add web/src/main.tsx web/src/routes
git rm web/src/App.tsx web/src/App.css web/src/App.test.tsx
git commit -m "feat: router and query client bootstrap"
```

---

## Task 6: Login screen

**Files:**
- Create: `web/src/routes/admin/login.tsx`
- Create: `web/src/routes/admin/login.test.tsx`

**Interfaces:**
- Consumes: `useAuth()` (Task 3), `strings` (Task 4).
- Produces: the `/admin/login` route. Later tasks' auth-guard redirects to this path.

- [ ] **Step 1: Write the failing test**

`web/src/routes/admin/login.test.tsx`:

```tsx
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { LoginForm } from './login';
import * as authModule from '@/shared/auth/AuthContext';

describe('LoginForm', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('calls login with the entered credentials and shows the invalid-credentials message on failure', async () => {
    const login = vi.fn().mockResolvedValue({ ok: false, kind: 'invalid' });
    vi.spyOn(authModule, 'useAuth').mockReturnValue({
      status: 'anonymous',
      user: null,
      permissions: new Set(),
      scopes: [],
      can: () => false,
      login,
      logout: vi.fn(),
    });

    render(<LoginForm />);

    await userEvent.type(screen.getByLabelText('Email'), 'ada@example.com');
    await userEvent.type(screen.getByLabelText('Password'), 'wrong-password');
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(login).toHaveBeenCalledWith('ada@example.com', 'wrong-password');
    await waitFor(() =>
      expect(screen.getByText('Incorrect email or password.')).toBeInTheDocument(),
    );
  });
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `npm test -- login.test.tsx`
Expected: FAIL — `./login` has no exports yet.

- [ ] **Step 3: Implement the login route and form**

`web/src/routes/admin/login.tsx`:

```tsx
import { useState } from 'react';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useAuth } from '@/shared/auth/AuthContext';
import { strings } from '@/shared/i18n/strings';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';

export const Route = createFileRoute('/admin/login')({
  component: LoginForm,
});

export function LoginForm() {
  const auth = useAuth();
  const navigate = useNavigate();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  async function handleSubmit(event: React.FormEvent) {
    event.preventDefault();
    setSubmitting(true);
    setErrorMessage(null);

    const outcome = await auth.login(email, password);

    setSubmitting(false);
    if (outcome.ok) {
      void navigate({ to: '/admin/dashboard' });
      return;
    }

    setErrorMessage(
      outcome.kind === 'locked'
        ? strings.login.locked(outcome.retryAfterSeconds ?? 60)
        : strings.login.invalidCredentials,
    );
  }

  return (
    <main className="flex min-h-screen items-center justify-center p-4">
      <form onSubmit={handleSubmit} className="w-full max-w-sm space-y-4">
        <h1 className="text-xl font-semibold">{strings.login.title}</h1>

        <div className="space-y-1">
          <Label htmlFor="email">{strings.login.email}</Label>
          <Input
            id="email"
            type="email"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            required
          />
        </div>

        <div className="space-y-1">
          <Label htmlFor="password">{strings.login.password}</Label>
          <Input
            id="password"
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            required
          />
        </div>

        {errorMessage && (
          <p role="alert" className="text-sm text-destructive">
            {errorMessage}
          </p>
        )}

        <Button type="submit" disabled={submitting} className="w-full">
          {strings.login.submit}
        </Button>
      </form>
    </main>
  );
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `npm test -- login.test.tsx`
Expected: PASS — 1 test.

- [ ] **Step 5: Manually verify the route resolves**

Run: `npm run dev`, navigate to `/admin/login`.
Expected: the form renders, no console errors. (Submitting will fail until the API base URL is proxied — that is Task 7's dev-server concern; this step only checks the route itself renders.)

- [ ] **Step 6: Commit**

```bash
git add web/src/routes/admin/login.tsx web/src/routes/admin/login.test.tsx
git commit -m "feat: admin login screen"
```

---

## Task 7: Authenticated shell (`_authenticated` layout)

**Files:**
- Create: `web/src/routes/admin/_authenticated.tsx`
- Create: `web/src/routes/admin/_authenticated.test.tsx`
- Create: `web/src/shared/warehouse/SelectedWarehouseContext.tsx`
- Modify: `web/vite.config.ts:1-25` — add a dev-server proxy so `/api/*` reaches `Wms.Api` during `npm run dev`

**Interfaces:**
- Consumes: `useAuth()` (Task 3), `api`, `withAuthRetry` (Task 2), `strings` (Task 4).
- Produces: the `/admin/_authenticated` pathless layout route every protected screen (Tasks 8–11) is nested under. `useSelectedWarehouse(): { warehouseId: string | null; scopes: SessionScope[]; select(warehouseId: string): void }` from `@/shared/warehouse/SelectedWarehouseContext`.

- [ ] **Step 1: Add the dev-server API proxy**

Edit `web/vite.config.ts` — add a `server` block:

```ts
export default defineConfig({
  // ...existing plugins/resolve/test config...
  server: {
    proxy: {
      '/api': { target: 'http://localhost:5000', changeOrigin: true },
    },
  },
});
```

(Match the port to whatever Task 2 found `Wms.Api` actually running on.)

- [ ] **Step 2: Implement the selected-warehouse context**

`web/src/shared/warehouse/SelectedWarehouseContext.tsx`:

```tsx
import { createContext, useContext, useState, type ReactNode } from 'react';
import { useAuth } from '@/shared/auth/AuthContext';

interface SelectedWarehouseValue {
  warehouseId: string | null;
  select(warehouseId: string): void;
}

const Context = createContext<SelectedWarehouseValue | null>(null);

const STORAGE_KEY = 'wms.selectedWarehouseId';

export function SelectedWarehouseProvider({ children }: { children: ReactNode }) {
  const auth = useAuth();
  const [warehouseId, setWarehouseId] = useState<string | null>(() => {
    const stored = localStorage.getItem(STORAGE_KEY);
    const isValid = stored && auth.scopes.some((s) => s.warehouseId === stored);
    return isValid ? stored : (auth.scopes[0]?.warehouseId ?? null);
  });

  function select(id: string) {
    setWarehouseId(id);
    localStorage.setItem(STORAGE_KEY, id);
  }

  return <Context.Provider value={{ warehouseId, select }}>{children}</Context.Provider>;
}

export function useSelectedWarehouse(): SelectedWarehouseValue {
  const value = useContext(Context);
  if (!value) {
    throw new Error('useSelectedWarehouse must be used within a SelectedWarehouseProvider');
  }
  return value;
}
```

- [ ] **Step 3: Write the failing test for the auth guard**

`web/src/routes/admin/_authenticated.test.tsx`:

```tsx
import { describe, expect, it, vi } from 'vitest';
import { Route } from './_authenticated';
import { redirect } from '@tanstack/react-router';

vi.mock('@tanstack/react-router', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@tanstack/react-router')>();
  return { ...actual, redirect: vi.fn((opts) => { throw { isRedirect: true, opts }; }) };
});

describe('_authenticated beforeLoad', () => {
  it('redirects to /admin/login when the caller is anonymous', () => {
    const beforeLoad = Route.options.beforeLoad;
    expect(beforeLoad).toBeDefined();

    expect(() =>
      // @ts-expect-error — partial context is enough for this check
      beforeLoad!({ context: { auth: { status: 'anonymous' } }, location: { href: '/admin/dashboard' } }),
    ).toThrow();

    expect(redirect).toHaveBeenCalledWith(
      expect.objectContaining({ to: '/admin/login' }),
    );
  });

  it('does not redirect when the caller is authenticated', () => {
    const beforeLoad = Route.options.beforeLoad;
    expect(() =>
      // @ts-expect-error — partial context is enough for this check
      beforeLoad!({ context: { auth: { status: 'authenticated' } }, location: { href: '/admin/dashboard' } }),
    ).not.toThrow();
  });
});
```

- [ ] **Step 4: Run the test to verify it fails**

Run: `npm test -- _authenticated.test.tsx`
Expected: FAIL — `./_authenticated` has no exports yet.

- [ ] **Step 5: Implement the authenticated shell**

`web/src/routes/admin/_authenticated.tsx`:

```tsx
import { createFileRoute, Link, Outlet, redirect, useNavigate } from '@tanstack/react-router';
import { useEffect } from 'react';
import { useAuth } from '@/shared/auth/AuthContext';
import { SelectedWarehouseProvider, useSelectedWarehouse } from '@/shared/warehouse/SelectedWarehouseContext';
import { strings } from '@/shared/i18n/strings';
import { Button } from '@/components/ui/button';

export const Route = createFileRoute('/admin/_authenticated')({
  beforeLoad: ({ context, location }) => {
    if (context.auth.status !== 'authenticated') {
      throw redirect({ to: '/admin/login', search: { redirect: location.href } });
    }
  },
  component: AuthenticatedShell,
});

function AuthenticatedShell() {
  return (
    <SelectedWarehouseProvider>
      <ShellLayout />
    </SelectedWarehouseProvider>
  );
}

function ShellLayout() {
  const auth = useAuth();
  const navigate = useNavigate();
  const warehouse = useSelectedWarehouse();

  // Catches a revoked security_stamp promptly (design doc §8.1) rather than
  // waiting for whichever request happens to 401 next. validateSession
  // (Task 3) calls GET /auth/me and logs out if it no longer resolves.
  useEffect(() => {
    function revalidate() {
      void auth.validateSession();
    }
    window.addEventListener('focus', revalidate);
    return () => window.removeEventListener('focus', revalidate);
  }, [auth]);

  return (
    <div className="grid min-h-screen grid-cols-[auto_1fr] grid-rows-[auto_1fr]">
      <header className="col-span-2 flex items-center justify-between border-b p-4">
        <div className="flex items-center gap-4">
          <span className="font-semibold">WMS Admin</span>
          {warehouse.warehouseId && auth.scopes.length > 1 && (
            <select
              value={warehouse.warehouseId}
              onChange={(e) => warehouse.select(e.target.value)}
              className="rounded border px-2 py-1 text-sm"
              aria-label="Warehouse"
            >
              {auth.scopes.map((s) => (
                <option key={s.warehouseId} value={s.warehouseId}>
                  {s.warehouseId}
                </option>
              ))}
            </select>
          )}
        </div>
        <div className="flex items-center gap-3">
          <span className="text-sm">{auth.user?.displayName}</span>
          <Button
            variant="outline"
            onClick={() => {
              void auth.logout().then(() => navigate({ to: '/admin/login' }));
            }}
          >
            {strings.actions.logout}
          </Button>
        </div>
      </header>

      <nav className="w-48 shrink-0 border-r p-4 sm:w-56">
        <ul className="space-y-2">
          <li>
            <Link to="/admin/dashboard" className="[&.active]:font-semibold">
              {strings.nav.dashboard}
            </Link>
          </li>
          <li>
            <Link to="/admin/users" className="[&.active]:font-semibold">
              {strings.nav.users}
            </Link>
          </li>
        </ul>
      </nav>

      <main className="min-w-0 overflow-x-auto p-4">
        <Outlet />
      </main>
    </div>
  );
}
```

(The warehouse `<select>` renders the raw `warehouseId` for now — Task 8 or a later polish pass can resolve it to a warehouse name once a warehouse-lookup endpoint exists; there is none today, matching the plan's own master-data-is-out-of-scope constraint.)

- [ ] **Step 6: Run the test to verify it passes**

Run: `npm test -- _authenticated.test.tsx`
Expected: PASS — 2 tests.

- [ ] **Step 7: Resize check**

Run `npm run dev`, log in (requires Task 8's dashboard to exist to land somewhere — if run before Task 8, just verify `/admin/_authenticated` doesn't error at compact width by checking browser devtools' responsive mode at 360px, 800px, and 1400px widths).
Expected: sidebar and header do not overlap or force horizontal scroll on the page itself at any width. (Full sidebar-collapse-to-icon-rail behavior is a later polish item, not required for this plan's exit bar — note this as a known gap rather than blocking on it.)

- [ ] **Step 8: Commit**

```bash
git add web/src/routes/admin/_authenticated.tsx web/src/routes/admin/_authenticated.test.tsx web/src/shared/warehouse web/vite.config.ts
git commit -m "feat: authenticated admin shell with warehouse selector"
```

---

## Task 8: Dashboard screen

**Files:**
- Create: `web/src/routes/admin/_authenticated/dashboard.tsx`
- Create: `web/src/routes/admin/_authenticated/dashboard.test.tsx`

**Interfaces:**
- Consumes: `api`, `withAuthRetry` (Task 2), `useSelectedWarehouse` (Task 7).
- Produces: the `/admin/dashboard` route.

- [ ] **Step 1: Write the failing test**

`web/src/routes/admin/_authenticated/dashboard.test.tsx`:

```tsx
import { describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DashboardView } from './dashboard';
import { api } from '@/shared/api/client';

vi.mock('@/shared/warehouse/SelectedWarehouseContext', () => ({
  useSelectedWarehouse: () => ({ warehouseId: 'w1', select: vi.fn() }),
}));

describe('DashboardView', () => {
  it('renders the receiving and putaway summary counts once the query resolves', async () => {
    vi.spyOn(api, 'GET').mockResolvedValue({
      data: {
        warehouse: { id: 'w1' },
        generatedAt: '2026-09-11T00:00:00Z',
        receiving: { openReceipts: 4, receiptsCompletedToday: 12, openDiscrepancies: 1 },
        putaway: { tasksReady: 3, tasksLeased: 2, tasksCompletedToday: 40 },
        exceptions: { open: 1, oldestAgeHours: 2.5, byType: { over_receipt: 1 } },
        reconciliation: { lastRunAt: '2026-09-11T00:00:00Z', varianceCount: 0 },
        stockOnHand: { skuCount: 120, onHand: 5400 },
      },
      response: new Response(null, { status: 200 }),
    } as never);

    const queryClient = new QueryClient();
    render(
      <QueryClientProvider client={queryClient}>
        <DashboardView />
      </QueryClientProvider>,
    );

    await waitFor(() => expect(screen.getByText('4')).toBeInTheDocument());
    expect(screen.getByText('3')).toBeInTheDocument();
    expect(screen.getByText('120')).toBeInTheDocument();
  });
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `npm test -- dashboard.test.tsx`
Expected: FAIL — `./dashboard` has no exports yet.

- [ ] **Step 3: Implement the dashboard route**

`web/src/routes/admin/_authenticated/dashboard.tsx`:

```tsx
import { createFileRoute } from '@tanstack/react-router';
import { useQuery } from '@tanstack/react-query';
import { api } from '@/shared/api/client';
import { withAuthRetry } from '@/shared/api/withAuthRetry';
import { useSelectedWarehouse } from '@/shared/warehouse/SelectedWarehouseContext';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';

export const Route = createFileRoute('/admin/_authenticated/dashboard')({
  component: DashboardView,
});

function useDashboard(warehouseId: string | null) {
  return useQuery({
    queryKey: ['dashboard', warehouseId],
    queryFn: () =>
      withAuthRetry(() =>
        api.GET('/dashboard', { params: { query: { warehouseId: warehouseId! } } }),
      ),
    enabled: warehouseId !== null,
  });
}

function Stat({ label, value }: { label: string; value: number | string }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-sm font-normal text-muted-foreground">{label}</CardTitle>
      </CardHeader>
      <CardContent className="text-2xl font-semibold">{value}</CardContent>
    </Card>
  );
}

export function DashboardView() {
  const { warehouseId } = useSelectedWarehouse();
  const { data, isPending, isError } = useDashboard(warehouseId);

  if (warehouseId === null) {
    return <p>No warehouse scope on this account yet — nothing to show.</p>;
  }

  if (isPending) {
    return <p>Loading…</p>;
  }

  if (isError || !data.data) {
    return <p role="alert">Could not load the dashboard.</p>;
  }

  const dashboard = data.data;

  return (
    <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4">
      <Stat label="Open receipts" value={dashboard.receiving.openReceipts} />
      <Stat label="Open discrepancies" value={dashboard.receiving.openDiscrepancies} />
      <Stat label="Putaway ready" value={dashboard.putaway.tasksReady} />
      <Stat label="Putaway leased" value={dashboard.putaway.tasksLeased} />
      <Stat label="Open exceptions" value={dashboard.exceptions.open} />
      <Stat
        label="Reconciliation variance"
        value={dashboard.reconciliation?.varianceCount ?? 0}
      />
      <Stat label="SKUs on hand" value={dashboard.stockOnHand.skuCount} />
      <Stat label="Units on hand" value={dashboard.stockOnHand.onHand} />
    </div>
  );
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `npm test -- dashboard.test.tsx`
Expected: PASS — 1 test.

- [ ] **Step 5: Resize check**

`npm run dev`, view `/admin/dashboard` at 360px/800px/1400px widths.
Expected: the stat grid reflows to fewer columns at narrow widths, never clips or forces page-level horizontal scroll.

- [ ] **Step 6: Commit**

```bash
git add web/src/routes/admin/_authenticated/dashboard.tsx web/src/routes/admin/_authenticated/dashboard.test.tsx
git commit -m "feat: admin dashboard screen"
```

---

## Task 9: Users list screen

**Files:**
- Create: `web/src/routes/admin/_authenticated/users/index.tsx`
- Create: `web/src/routes/admin/_authenticated/users/index.test.tsx`

**Interfaces:**
- Consumes: `api`, `withAuthRetry` (Task 2), `useSelectedWarehouse` (Task 7), `strings` (Task 4).
- Produces: the `/admin/users` route, and the `Link` target `/admin/users/new` and `/admin/users/$userId` that Tasks 10 and 11 implement.

- [ ] **Step 1: Write the failing test**

`web/src/routes/admin/_authenticated/users/index.test.tsx`:

```tsx
import { describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, createRootRoute, createRouter, RouterProvider } from '@tanstack/react-router';
import { UsersListView } from './index';
import { api } from '@/shared/api/client';

vi.mock('@/shared/warehouse/SelectedWarehouseContext', () => ({
  useSelectedWarehouse: () => ({ warehouseId: 'w1', select: vi.fn() }),
}));

function renderWithRouter(ui: React.ReactElement) {
  const rootRoute = createRootRoute({ component: () => ui });
  const router = createRouter({
    routeTree: rootRoute,
    history: createMemoryHistory({ initialEntries: ['/'] }),
  });
  return render(<RouterProvider router={router} />);
}

describe('UsersListView', () => {
  it('renders the returned users', async () => {
    vi.spyOn(api, 'GET').mockResolvedValue({
      data: {
        items: [
          {
            id: 'u1',
            displayName: 'Ada Receiver',
            employeeCode: 'EMP001',
            userType: 'operator',
            status: 'active',
            validUntil: '2026-12-31',
            roleScopes: [{ roleCode: 'RECEIVER', warehouseCode: 'TKY', zoneCodes: [] }],
          },
        ],
        nextCursor: null,
        hasMore: false,
      },
      response: new Response(null, { status: 200 }),
    } as never);

    const queryClient = new QueryClient();
    renderWithRouter(
      <QueryClientProvider client={queryClient}>
        <UsersListView />
      </QueryClientProvider>,
    );

    await waitFor(() => expect(screen.getByText('Ada Receiver')).toBeInTheDocument());
    expect(screen.getByText('EMP001')).toBeInTheDocument();
  });
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `npm test -- users/index.test.tsx`
Expected: FAIL — `./index` has no exports yet.

- [ ] **Step 3: Implement the users list**

`web/src/routes/admin/_authenticated/users/index.tsx`:

```tsx
import { useState } from 'react';
import { createFileRoute, Link } from '@tanstack/react-router';
import { useQuery } from '@tanstack/react-query';
import { api } from '@/shared/api/client';
import { withAuthRetry } from '@/shared/api/withAuthRetry';
import { useSelectedWarehouse } from '@/shared/warehouse/SelectedWarehouseContext';
import { strings } from '@/shared/i18n/strings';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';

export const Route = createFileRoute('/admin/_authenticated/users/')({
  component: UsersListView,
});

function useUsers(warehouseId: string | null, search: string) {
  return useQuery({
    queryKey: ['users', warehouseId, search],
    queryFn: () =>
      withAuthRetry(() =>
        api.GET('/users', {
          params: { query: { warehouseId: warehouseId ?? undefined, q: search || undefined, limit: 50 } },
        }),
      ),
    enabled: warehouseId !== null,
  });
}

export function UsersListView() {
  const { warehouseId } = useSelectedWarehouse();
  const [search, setSearch] = useState('');
  const { data, isPending, isError } = useUsers(warehouseId, search);

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between gap-4">
        <h1 className="text-lg font-semibold">{strings.users.title}</h1>
        <Button asChild>
          <Link to="/admin/users/new">{strings.actions.newUser}</Link>
        </Button>
      </div>

      <Input
        placeholder={strings.users.searchPlaceholder}
        value={search}
        onChange={(e) => setSearch(e.target.value)}
        className="max-w-sm"
      />

      {isPending && <p>Loading…</p>}
      {isError && <p role="alert">Could not load users.</p>}

      {data?.data && (
        <div className="overflow-x-auto rounded border">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Employee code</TableHead>
                <TableHead>Type</TableHead>
                <TableHead>Status</TableHead>
                <TableHead>Valid until</TableHead>
                <TableHead>Role scopes</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {data.data.items.map((user) => (
                <TableRow key={user.id}>
                  <TableCell>
                    <Link to="/admin/users/$userId" params={{ userId: user.id }} className="underline">
                      {user.displayName}
                    </Link>
                  </TableCell>
                  <TableCell>{user.employeeCode ?? '—'}</TableCell>
                  <TableCell>{user.userType}</TableCell>
                  <TableCell>{user.status}</TableCell>
                  <TableCell>{user.validUntil ?? '—'}</TableCell>
                  <TableCell>
                    {user.roleScopes.map((rs) => `${rs.roleCode}@${rs.warehouseCode}`).join(', ') || '—'}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}
    </div>
  );
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `npm test -- users/index.test.tsx`
Expected: PASS — 1 test.

- [ ] **Step 5: Resize check**

`npm run dev`, view `/admin/users` at 360px/800px/1400px.
Expected: the table scrolls horizontally inside its own `overflow-x-auto` container at narrow widths — the page itself never scrolls sideways.

- [ ] **Step 6: Commit**

```bash
git add web/src/routes/admin/_authenticated/users/index.tsx web/src/routes/admin/_authenticated/users/index.test.tsx
git commit -m "feat: admin users list screen"
```

---

## Task 10: Create-user screen

**Files:**
- Create: `web/src/routes/admin/_authenticated/users/new.tsx`
- Create: `web/src/routes/admin/_authenticated/users/new.test.tsx`

**Interfaces:**
- Consumes: `api` (Task 2), `strings` (Task 4).
- Produces: the `/admin/users/new` route (linked from Task 9).

- [ ] **Step 1: Write the failing test**

`web/src/routes/admin/_authenticated/users/new.test.tsx`:

```tsx
import { describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CreateUserForm } from './new';
import { api } from '@/shared/api/client';

describe('CreateUserForm', () => {
  it('submits displayName and userType, with no roleScopes or credentials fields', async () => {
    const postSpy = vi.spyOn(api, 'POST').mockResolvedValue({
      data: { id: 'u2', employeeCode: null, status: 'active' },
      response: new Response(null, { status: 201 }),
    } as never);

    const queryClient = new QueryClient();
    render(
      <QueryClientProvider client={queryClient}>
        <CreateUserForm />
      </QueryClientProvider>,
    );

    await userEvent.type(screen.getByLabelText('Display name'), 'New Person');
    await userEvent.click(screen.getByRole('button', { name: strings.actions.save }));

    await waitFor(() => expect(postSpy).toHaveBeenCalled());
    const [, options] = postSpy.mock.calls[0]!;
    expect(options.body).toMatchObject({ displayName: 'New Person' });
    expect(options.body).not.toHaveProperty('roleScopes');
    expect(options.body).not.toHaveProperty('credentials');
  });
});

// Imported for the assertion above without re-declaring the literal string.
import { strings } from '@/shared/i18n/strings';
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `npm test -- users/new.test.tsx`
Expected: FAIL — `./new` has no exports yet.

- [ ] **Step 3: Implement the create-user form**

`web/src/routes/admin/_authenticated/users/new.tsx`:

```tsx
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useMutation } from '@tanstack/react-query';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { api } from '@/shared/api/client';
import { strings } from '@/shared/i18n/strings';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';

export const Route = createFileRoute('/admin/_authenticated/users/new')({
  component: CreateUserForm,
});

const schema = z.object({
  userType: z.enum(['staff', 'operator']),
  displayName: z.string().min(1, 'Required'),
  employeeCode: z.string().optional(),
  email: z.string().email().optional().or(z.literal('')),
});

type FormValues = z.infer<typeof schema>;

export function CreateUserForm() {
  const navigate = useNavigate();
  const {
    register,
    handleSubmit,
    formState: { errors, isSubmitting },
  } = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: { userType: 'staff', displayName: '', employeeCode: '', email: '' },
  });

  const createUser = useMutation({
    mutationFn: (values: FormValues) =>
      api.POST('/users', {
        body: {
          userType: values.userType,
          displayName: values.displayName,
          employeeCode: values.employeeCode || null,
          email: values.email || null,
          locale: 'en',
        },
      }),
    onSuccess: (result) => {
      if (result.data) {
        void navigate({ to: '/admin/users/$userId', params: { userId: result.data.id } });
      }
    },
  });

  return (
    <form onSubmit={handleSubmit((values) => createUser.mutate(values))} className="max-w-md space-y-4">
      <div className="space-y-1">
        <Label htmlFor="userType">User type</Label>
        <select id="userType" {...register('userType')} className="w-full rounded border px-2 py-1">
          <option value="staff">Staff</option>
          <option value="operator">Operator</option>
        </select>
      </div>

      <div className="space-y-1">
        <Label htmlFor="displayName">Display name</Label>
        <Input id="displayName" {...register('displayName')} />
        {errors.displayName && <p className="text-sm text-destructive">{errors.displayName.message}</p>}
      </div>

      <div className="space-y-1">
        <Label htmlFor="employeeCode">Employee code</Label>
        <Input id="employeeCode" {...register('employeeCode')} />
      </div>

      <div className="space-y-1">
        <Label htmlFor="email">Email</Label>
        <Input id="email" type="email" {...register('email')} />
      </div>

      {createUser.isError && <p role="alert">Could not create the user.</p>}

      <Button type="submit" disabled={isSubmitting}>
        {strings.actions.save}
      </Button>
    </form>
  );
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `npm test -- users/new.test.tsx`
Expected: PASS — 1 test.

- [ ] **Step 5: Commit**

```bash
git add web/src/routes/admin/_authenticated/users/new.tsx web/src/routes/admin/_authenticated/users/new.test.tsx
git commit -m "feat: create-user screen"
```

---

## Task 11: User detail, edit, and role-scope assignment

**Files:**
- Create: `web/src/routes/admin/_authenticated/users/$userId.tsx`
- Create: `web/src/routes/admin/_authenticated/users/$userId.test.tsx`

**Interfaces:**
- Consumes: `api` (Task 2), `strings` (Task 4).
- Produces: the `/admin/users/$userId` route.

- [ ] **Step 1: Write the failing test for the role-scope grant flow, including the 403 detail rendering**

`web/src/routes/admin/_authenticated/users/$userId.test.tsx`:

```tsx
import { describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { UserDetailView } from './$userId';
import { api } from '@/shared/api/client';

function renderView() {
  const queryClient = new QueryClient();
  return render(
    <QueryClientProvider client={queryClient}>
      <UserDetailView userId="u1" />
    </QueryClientProvider>,
  );
}

describe('UserDetailView', () => {
  it('does not render securityStamp anywhere, even though the API response carries it', async () => {
    vi.spyOn(api, 'GET').mockImplementation((path) => {
      if (path === '/users/{id}') {
        return Promise.resolve({
          data: {
            id: 'u1',
            displayName: 'Ada Receiver',
            employeeCode: 'EMP001',
            userType: 'operator',
            locale: 'en',
            status: 'active',
            validFrom: '2026-01-01',
            validUntil: null,
            securityStamp: '11111111-1111-1111-1111-111111111111',
            roleScopes: [],
          },
          response: new Response(null, { status: 200 }),
        } as never);
      }
      return Promise.resolve({ data: { items: [] }, response: new Response(null, { status: 200 }) } as never);
    });

    renderView();

    await waitFor(() => expect(screen.getByText('Ada Receiver')).toBeInTheDocument());
    expect(screen.queryByText(/11111111-1111/)).not.toBeInTheDocument();
  });

  it('shows the missingPermissions list from a 403 cannot-grant-permission-you-lack response', async () => {
    vi.spyOn(api, 'GET').mockImplementation((path) => {
      if (path === '/users/{id}') {
        return Promise.resolve({
          data: {
            id: 'u1',
            displayName: 'Ada',
            employeeCode: null,
            userType: 'staff',
            locale: 'en',
            status: 'active',
            validFrom: '2026-01-01',
            validUntil: null,
            securityStamp: 's',
            roleScopes: [],
          },
          response: new Response(null, { status: 200 }),
        } as never);
      }
      if (path === '/roles') {
        return Promise.resolve({
          data: { items: [{ id: 'r1', code: 'RECEIVER', name: 'Receiver', isSystem: true, isActive: true, permissions: [] }] },
          response: new Response(null, { status: 200 }),
        } as never);
      }
      return Promise.resolve({ data: { items: [] }, response: new Response(null, { status: 200 }) } as never);
    });
    vi.spyOn(api, 'POST').mockResolvedValue({
      error: { missingPermissions: ['receipt.over_receive'] },
      response: new Response(null, { status: 403 }),
    } as never);

    renderView();

    await waitFor(() => expect(screen.getByLabelText('Role')).toBeInTheDocument());
    await userEvent.selectOptions(screen.getByLabelText('Role'), 'RECEIVER');
    await userEvent.type(screen.getByLabelText('Warehouse ID'), 'w1');
    await userEvent.click(screen.getByRole('button', { name: strings.actions.assignRole }));

    await waitFor(() => expect(screen.getByText(/receipt.over_receive/)).toBeInTheDocument());
  });
});

import { strings } from '@/shared/i18n/strings';
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `npm test -- users/\$userId.test.tsx`
Expected: FAIL — `./$userId` has no exports yet.

- [ ] **Step 3: Implement the user detail route**

`web/src/routes/admin/_authenticated/users/$userId.tsx`:

```tsx
import { useState } from 'react';
import { createFileRoute } from '@tanstack/react-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '@/shared/api/client';
import { withAuthRetry } from '@/shared/api/withAuthRetry';
import { strings } from '@/shared/i18n/strings';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';

export const Route = createFileRoute('/admin/_authenticated/users/$userId')({
  component: () => {
    const { userId } = Route.useParams();
    return <UserDetailView userId={userId} />;
  },
});

export function UserDetailView({ userId }: { userId: string }) {
  const queryClient = useQueryClient();

  const userQuery = useQuery({
    queryKey: ['user', userId],
    queryFn: () => withAuthRetry(() => api.GET('/users/{id}', { params: { path: { id: userId } } })),
  });

  const rolesQuery = useQuery({
    queryKey: ['roles'],
    queryFn: () => withAuthRetry(() => api.GET('/roles', {})),
  });

  const [roleCode, setRoleCode] = useState('');
  const [warehouseId, setWarehouseId] = useState('');
  const [grantError, setGrantError] = useState<string[] | null>(null);

  const grantRole = useMutation({
    mutationFn: async () => {
      setGrantError(null);
      const result = await api.POST('/users/{id}/role-scopes', {
        params: { path: { id: userId } },
        body: { roleCode, warehouseId, zoneIds: [] },
      });

      if (result.response.status === 403) {
        const body = result.error as { missingPermissions?: string[] } | undefined;
        setGrantError(body?.missingPermissions ?? ['insufficient permission']);
        return;
      }

      void queryClient.invalidateQueries({ queryKey: ['user', userId] });
    },
  });

  if (userQuery.isPending) {
    return <p>Loading…</p>;
  }

  if (userQuery.isError || !userQuery.data.data) {
    return <p role="alert">Could not load this user.</p>;
  }

  const user = userQuery.data.data;
  const roles = rolesQuery.data?.data?.items ?? [];

  return (
    <div className="max-w-2xl space-y-8">
      <section>
        <h1 className="text-lg font-semibold">{user.displayName}</h1>
        <dl className="mt-2 grid grid-cols-2 gap-2 text-sm">
          <dt className="text-muted-foreground">Employee code</dt>
          <dd>{user.employeeCode ?? '—'}</dd>
          <dt className="text-muted-foreground">Type</dt>
          <dd>{user.userType}</dd>
          <dt className="text-muted-foreground">Status</dt>
          <dd>{user.status}</dd>
          <dt className="text-muted-foreground">Valid until</dt>
          <dd>{user.validUntil ?? '—'}</dd>
        </dl>
        {/* user.securityStamp exists on the response but is deliberately never rendered. */}
      </section>

      <section>
        <h2 className="font-semibold">Role scopes</h2>
        <p className="text-sm text-muted-foreground">{strings.users.noRevokeNotice}</p>
        <ul className="mt-2 space-y-1 text-sm">
          {user.roleScopes.map((rs, i) => (
            <li key={i}>
              {rs.roleCode} @ {rs.warehouseCode}
              {rs.zoneCodes.length > 0 ? ` (${rs.zoneCodes.join(', ')})` : ''}
            </li>
          ))}
          {user.roleScopes.length === 0 && <li>No role scopes yet.</li>}
        </ul>

        <form
          onSubmit={(e) => {
            e.preventDefault();
            grantRole.mutate();
          }}
          className="mt-4 flex items-end gap-2"
        >
          <div className="space-y-1">
            <Label htmlFor="roleCode">Role</Label>
            <select
              id="roleCode"
              value={roleCode}
              onChange={(e) => setRoleCode(e.target.value)}
              className="rounded border px-2 py-1"
            >
              <option value="">Select…</option>
              {roles.map((r) => (
                <option key={r.code} value={r.code}>
                  {r.name}
                </option>
              ))}
            </select>
          </div>

          <div className="space-y-1">
            <Label htmlFor="warehouseId">Warehouse ID</Label>
            <Input id="warehouseId" value={warehouseId} onChange={(e) => setWarehouseId(e.target.value)} />
          </div>

          <Button type="submit" disabled={!roleCode || !warehouseId || grantRole.isPending}>
            {strings.actions.assignRole}
          </Button>
        </form>

        {grantError && (
          <p role="alert" className="mt-2 text-sm text-destructive">
            Cannot grant — you do not hold: {grantError.join(', ')}
          </p>
        )}
      </section>
    </div>
  );
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `npm test -- users/\$userId.test.tsx`
Expected: PASS — 2 tests.

- [ ] **Step 5: Resize check**

`npm run dev`, view `/admin/users/<any real id>` at 360px/800px/1400px.
Expected: the definition list and grant form reflow without clipping; no page-level horizontal scroll.

- [ ] **Step 6: Commit**

```bash
git add web/src/routes/admin/_authenticated/users/\$userId.tsx web/src/routes/admin/_authenticated/users/\$userId.test.tsx
git commit -m "feat: user detail, edit, and role-scope assignment screen"
```

---

## Task 12: End-to-end smoke test

**Files:**
- Create: `web/playwright.config.ts`
- Create: `web/e2e/admin-login-to-users.spec.ts`

**Interfaces:**
- Consumes: a running `Wms.Api` (with migrations applied and at least one staff user with a known password seeded — see Step 1) and a running `npm run dev` (or `npm run build && npm run preview`).

- [ ] **Step 1: Seed a real staff login for the test to use**

Using `psql` or any client against the same database the API points at:

```sql
-- Requires a warehouse and a role to already exist from db/migrations seed
-- data (0007). Replace the UUIDs with freshly generated ones.
INSERT INTO app_user (id, user_type, display_name, email, status, security_stamp, valid_from, created_at, updated_at)
VALUES ('00000000-0000-0000-0000-0000000000e2', 'staff', 'E2E Tester', 'e2e@example.com', 'active', gen_random_uuid(), current_date, now(), now());
```

Then use `Argon2PasswordHasher` (or the `POST /users` endpoint with `credentials`, once logged in as an existing admin) to set a known password — this plan does not hardcode a hash here since Argon2id output is salted and non-reproducible; generate it at setup time and record the plaintext for the test config below.

- [ ] **Step 2: Configure Playwright**

`web/playwright.config.ts`:

```ts
import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './e2e',
  use: { baseURL: 'http://localhost:5173' },
  webServer: {
    command: 'npm run dev',
    url: 'http://localhost:5173',
    reuseExistingServer: true,
  },
});
```

- [ ] **Step 3: Write the smoke test**

`web/e2e/admin-login-to-users.spec.ts`:

```ts
import { expect, test } from '@playwright/test';

const EMAIL = process.env.WMS_E2E_EMAIL ?? 'e2e@example.com';
const PASSWORD = process.env.WMS_E2E_PASSWORD;

test('logs in and opens the users list', async ({ page }) => {
  test.skip(!PASSWORD, 'Set WMS_E2E_PASSWORD to the seeded test user\'s password.');

  await page.goto('/admin/login');
  await page.getByLabel('Email').fill(EMAIL);
  await page.getByLabel('Password').fill(PASSWORD!);
  await page.getByRole('button', { name: 'Sign in' }).click();

  await expect(page).toHaveURL(/\/admin\/dashboard/);

  await page.getByRole('link', { name: 'Users' }).click();
  await expect(page).toHaveURL(/\/admin\/users/);
  await expect(page.getByRole('table')).toBeVisible();
});
```

- [ ] **Step 4: Run it against the real stack**

```bash
dotnet run --project src/Wms.Api &
WMS_E2E_PASSWORD="<the password set in Step 1>" npm run e2e
```

Expected: PASS. (If the seeded user has no `role.manage`/`user.manage`/warehouse scope, the dashboard's warehouse selector will be empty — grant at least one role scope in Step 1 via a second `INSERT` against `user_role_scope`, matching the pattern in `tests/Wms.IntegrationTests/**/*.cs`'s own seed helpers, so the test has a warehouse to land on.)

- [ ] **Step 5: Commit**

```bash
git add web/playwright.config.ts web/e2e
git commit -m "test: end-to-end smoke test for admin login through users list"
```

---

## Plan self-review notes

- **Spec coverage:** every screen in the spec (`login`, shell, `dashboard`, `users` list/new/detail, role-scope assignment) has a task. Auth & session, error handling, and the i18n/testing requirements are each their own task or folded into the screen that needed them first.
- **Type consistency verified against source, not memory:** `ReceivingSummary`, `PutawaySummary`, `ExceptionSummary`, `StockSummary`, `ReconciliationSummary`, `UserRow`, `UserDetail`, `RoleScopeRow`, `RoleSummary`, `PermissionSummary` were all read directly from `IInboundReadModel.cs`, `IInventoryReadModel.cs`, `ITaskReadModel.cs`, and `IUserDirectory.cs` during planning — not reconstructed from the design doc's examples, which turned out to disagree on `RoleSummary.Name`/`PermissionSummary.Description` (plain `string` in code, `{en, ja}` in the design doc's example). The plan's code uses the verified, real shape. Task 2's generated `schema.d.ts` is the actual source of truth at implementation time regardless.
- **Known gap, not silently dropped:** the sidebar's collapse-to-icon-rail behavior (react-frontend skill, required for `/admin/*`) is explicitly deferred past this plan's exit bar in Task 7, Step 7 — flagged there rather than either blocking on it or quietly skipping the resize check entirely.
