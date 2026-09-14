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

export const api = createClient<paths>({ baseUrl: '' });
api.use(authMiddleware);
