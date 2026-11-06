import { afterEach, describe, expect, it, vi } from 'vitest';

import { queryClient } from './query-client';
import { sessionStore, type CookieSessionFields } from './session-store';
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

describe('review case reads', () => {
  afterEach(() => {
    vi.unstubAllEnvs();
    vi.unstubAllGlobals();
  });

  it('does not fetch a cookie review case list or detail without review-cases.read', async () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    sessionStore.replaceCookieSession(cookieSession(['invoices.read']), true);
    const fetchSpy = vi.spyOn(globalThis, 'fetch');

    await expect(apiGet('/api/review-cases?page=1&pageSize=50', new AbortController().signal)).rejects.toMatchObject({ status: 403 });
    await expect(apiGet(`/api/review-cases/${caseId}`, new AbortController().signal)).rejects.toMatchObject({ status: 403 });
    await expect(apiGet(`/api/review-cases/${caseId}/audit?page=1&pageSize=50`, new AbortController().signal)).rejects.toMatchObject({ status: 403 });
    await expect(apiGet(`/api/review-cases/${caseId}/resolve`, new AbortController().signal)).rejects.toMatchObject({ status: 403 });

    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it('maps a review case audit read to audit.read and still rejects the write', async () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    sessionStore.replaceCookieSession(cookieSession(['review-cases.read', 'audit.read']), true);
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response('{}', { status: 200 }));

    await apiGet(`/api/review-cases/${caseId}/audit?page=1&pageSize=50`, new AbortController().signal);
    await expect(apiGet(`/api/review-cases/${caseId}/dismiss`, new AbortController().signal)).rejects.toMatchObject({ status: 403 });

    expect(fetchSpy).toHaveBeenCalledOnce();
    expect(String(fetchSpy.mock.calls[0]?.[0])).toBe(`/api/review-cases/${caseId}/audit?page=1&pageSize=50`);
  });

  it('maps review case comments to audit.read and does not fetch them for review-cases.read alone', async () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    sessionStore.replaceCookieSession(cookieSession(['review-cases.read']), true);
    const fetchSpy = vi.spyOn(globalThis, 'fetch');

    await expect(apiGet(`/api/review-cases/${caseId}/comments?page=1&pageSize=50`, new AbortController().signal)).rejects.toMatchObject({ status: 403 });
    expect(fetchSpy).not.toHaveBeenCalled();

    sessionStore.replaceCookieSession(cookieSession(['audit.read']), true);
    fetchSpy.mockResolvedValue(new Response('{}', { status: 200 }));
    await apiGet(`/api/review-cases/${caseId}/comments?page=2&pageSize=50`, new AbortController().signal);

    expect(fetchSpy).toHaveBeenCalledOnce();
    expect(String(fetchSpy.mock.calls[0]?.[0])).toBe(`/api/review-cases/${caseId}/comments?page=2&pageSize=50`);
  });

  it('does not fetch review case audit for a cookie session that can only read cases', async () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    sessionStore.replaceCookieSession(cookieSession(['review-cases.read']), true);
    const fetchSpy = vi.spyOn(globalThis, 'fetch');

    await expect(apiGet(`/api/review-cases/${caseId}/audit`, new AbortController().signal)).rejects.toMatchObject({ status: 403 });
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it('fetches a cookie summary for review-cases.read without audit.read', async () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    sessionStore.replaceCookieSession(cookieSession(['review-cases.read']), true);
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response('{}', { status: 200 }));

    await apiGet('/api/review-cases/summary', new AbortController().signal);

    expect(fetchSpy).toHaveBeenCalledOnce();
    expect(String(fetchSpy.mock.calls[0]?.[0])).toBe('/api/review-cases/summary');
  });

  it('does not fetch a cookie summary without review-cases.read', async () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    sessionStore.replaceCookieSession(cookieSession(['audit.read']), true);
    const fetchSpy = vi.spyOn(globalThis, 'fetch');

    await expect(apiGet('/api/review-cases/summary', new AbortController().signal)).rejects.toMatchObject({ status: 403 });
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it('fetches a development review case detail when a token is present', async () => {
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response('{}', { status: 200 }));

    await apiGet(`/api/review-cases/${caseId}`, new AbortController().signal);

    expect(fetchSpy).toHaveBeenCalledOnce();
    expect(String(fetchSpy.mock.calls[0]?.[0])).toBe(`/api/review-cases/${caseId}`);
  });
});

const caseId = '11111111-1111-4111-8111-111111111111';

function cookieSession(capabilities: readonly string[]): CookieSessionFields {
  return {
    authenticated: true,
    csrfToken: 'csrf-session',
    issuer: 'https://issuer.example',
    subject: 'oidc-operator',
    displayName: 'Ada Operator',
    expiresAt: null,
    organizations: [],
    activeOrganization: {
      organizationId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
      displayName: 'North Ledger',
    },
    authorization: 'verified',
    capabilities,
  };
}
