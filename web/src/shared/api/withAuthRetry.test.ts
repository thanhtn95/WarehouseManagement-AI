import { afterEach, describe, expect, it, vi } from 'vitest';
import { withAuthRetry } from './withAuthRetry';
import * as session from '@/shared/auth/session';

describe('withAuthRetry', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('returns the result unchanged when the call succeeds', async () => {
    const call = vi.fn().mockResolvedValue({
      data: { ok: true },
      response: new Response(null, { status: 200 }),
    });

    const result = await withAuthRetry(call);

    expect(result.data).toEqual({ ok: true });
    expect(call).toHaveBeenCalledTimes(1);
  });

  it('refreshes once and retries after a 401, returning the retry result', async () => {
    const refreshSpy = vi
      .spyOn(session, 'refreshAccessToken')
      .mockResolvedValue(true);
    const call = vi
      .fn()
      .mockResolvedValueOnce({ response: new Response(null, { status: 401 }) })
      .mockResolvedValueOnce({
        data: { ok: true },
        response: new Response(null, { status: 200 }),
      });

    const result = await withAuthRetry(call);

    expect(refreshSpy).toHaveBeenCalledTimes(1);
    expect(call).toHaveBeenCalledTimes(2);
    expect(result.data).toEqual({ ok: true });
  });

  it('does not retry a second time if the retried call also 401s', async () => {
    vi.spyOn(session, 'refreshAccessToken').mockResolvedValue(true);
    const call = vi
      .fn()
      .mockResolvedValue({ response: new Response(null, { status: 401 }) });

    const result = await withAuthRetry(call);

    expect(call).toHaveBeenCalledTimes(2);
    expect(result.response.status).toBe(401);
  });

  it('does not retry when refreshAccessToken itself fails', async () => {
    vi.spyOn(session, 'refreshAccessToken').mockResolvedValue(false);
    const call = vi
      .fn()
      .mockResolvedValue({ response: new Response(null, { status: 401 }) });

    const result = await withAuthRetry(call);

    expect(call).toHaveBeenCalledTimes(1);
    expect(result.response.status).toBe(401);
  });
});
