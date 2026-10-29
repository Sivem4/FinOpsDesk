import { describe, expect, it, vi } from 'vitest';

import { queryClient } from './query-client';
import { sessionStore } from './session-store';
import { apiGet } from './http';
import { StaleSessionError } from './errors';

describe('session isolation', () => {
  it('clears the query cache when the session ends', () => {
    sessionStore.rotate('token-a', 4_000_000_000, null);
    queryClient.setQueryData(['invoices', sessionStore.getSnapshot().epoch], { kept: true });

    sessionStore.rotate(null, null, null);

    expect(sessionStore.getSnapshot().token).toBeNull();
    expect(queryClient.getQueryCache().getAll()).toHaveLength(0);
  });

  it('does not return a response that finishes after the token is replaced', async () => {
    sessionStore.rotate('token-a', 4_000_000_000, null);
    let resolveFetch: (response: Response) => void = () => {
      throw new Error('The fetch was not started.');
    };
    vi.stubGlobal(
      'fetch',
      vi.fn(
        () =>
          new Promise<Response>((resolve) => {
            resolveFetch = resolve;
          }),
      ),
    );

    const pending = apiGet('/api/invoices?page=1&pageSize=50', new AbortController().signal);
    sessionStore.rotate('token-b', 4_000_000_000, null);
    resolveFetch(new Response('{"items":[]}', { status: 200 }));

    try {
      await expect(pending).rejects.toBeInstanceOf(StaleSessionError);
      expect(queryClient.getQueryCache().getAll()).toHaveLength(0);
    } finally {
      vi.unstubAllGlobals();
    }
  });
});
