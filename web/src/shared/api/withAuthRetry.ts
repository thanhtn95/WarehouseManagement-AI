import { refreshAccessToken } from '@/shared/auth/session';

interface ApiResult<T> {
  data?: T;
  error?: unknown;
  response: Response;
}

export async function withAuthRetry<T>(
  call: () => Promise<ApiResult<T>>
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
