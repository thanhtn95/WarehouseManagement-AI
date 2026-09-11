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
let snapshot: SessionSnapshot = {
  status: 'anonymous',
  user: null,
  permissions: new Set(),
  scopes: [],
};
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
  snapshot = {
    status: 'anonymous',
    user: null,
    permissions: new Set(),
    scopes: [],
  };
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

    const body = (await response.json()) as {
      accessToken: string;
      refreshToken: string;
    };
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
