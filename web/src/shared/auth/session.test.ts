import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  clearSession,
  getAccessToken,
  getSnapshot,
  refreshAccessToken,
  setSession,
  subscribe,
} from './session';

describe('session', () => {
  afterEach(() => {
    clearSession();
    vi.unstubAllGlobals();
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

  it('dedupes concurrent refreshAccessToken calls into a single fetch', async () => {
    setSession({
      accessToken: 'access-1',
      refreshToken: 'refresh-1',
      user: { id: 'u1', displayName: 'Ada', userType: 'staff', locale: 'en' },
      permissions: [],
      scopes: [],
    });

    // A real backend would treat a second concurrent refresh of the same
    // token as a replay and revoke the whole family (§5.1) — the dedup
    // this test pins is what keeps two near-simultaneous 401s from
    // triggering that and spuriously logging the user out.
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(
        JSON.stringify({ accessToken: 'access-2', refreshToken: 'refresh-2' }),
        {
          status: 200,
        }
      )
    );
    vi.stubGlobal('fetch', fetchMock);

    const [first, second] = await Promise.all([
      refreshAccessToken(),
      refreshAccessToken(),
    ]);

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(first).toBe(true);
    expect(second).toBe(true);
    expect(getAccessToken()).toBe('access-2');
  });
});
