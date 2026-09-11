import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  clearSession,
  getAccessToken,
  getSnapshot,
  setSession,
  subscribe,
} from './session';

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
