import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';

import { queryClient } from '../api/query-client';
import { sessionStore, type CookieSessionFields } from '../api/session-store';
import { server } from '../../test/server';
import { commitBrowserSession, loadBrowserSession, parseBrowserSession } from './browser-session';

describe('browser session', () => {
  it('rejects a 200 body that is not a session', () => {
    expect(parseBrowserSession({ ok: true }).kind).toBe('invalid');
    expect(parseBrowserSession({ ...anonymousBody(), issuer: 'https://issuer.example' }).kind).toBe('invalid');
    expect(parseBrowserSession({ ...restrictedBody(), financialAccess: true }).kind).toBe('invalid');
    expect(parseBrowserSession({ ...restrictedBody(), subject: '' }).kind).toBe('invalid');
  });

  it('reads an anonymous session and a restricted session', () => {
    expect(parseBrowserSession(anonymousBody())).toMatchObject({
      kind: 'anonymous',
      session: { authenticated: false, csrfToken: 'csrf-anonymous', issuer: null, subject: null },
    });
    expect(parseBrowserSession(restrictedBody())).toMatchObject({
      kind: 'restricted',
      session: {
        authenticated: true,
        issuer: 'https://issuer.example',
        subject: 'oidc-operator',
        displayName: 'Ada Operator',
      },
    });
  });

  it('treats a network failure as a session check failure', async () => {
    server.use(http.get('/api/session', () => HttpResponse.error()));
    await expect(loadBrowserSession(new AbortController().signal)).resolves.toEqual({ kind: 'network' });
    expect(sessionStore.getSnapshot().epoch).toBe(0);
  });

  it('does not send a bearer token when checking the session', async () => {
    let authorization: string | null = 'missing';
    server.use(
      http.get('/api/session', ({ request }) => {
        authorization = request.headers.get('authorization');
        return HttpResponse.json(anonymousBody());
      }),
    );

    await expect(loadBrowserSession(new AbortController().signal)).resolves.toMatchObject({ kind: 'anonymous' });
    expect(authorization).toBeNull();
  });

  it('ignores a session response that arrives after the epoch changes', () => {
    const epoch = sessionStore.getSnapshot().epoch;
    sessionStore.rotate(null, null, null);

    expect(commitBrowserSession(epoch, restrictedFields())).toBe('stale');
    expect(sessionStore.getSnapshot().authenticated).toBe(false);
    expect(sessionStore.getSnapshot().subject).toBeNull();
  });

  it('clears cached financial data when the signed-in identity changes', () => {
    sessionStore.replaceCookieSession(restrictedFields(), true);
    queryClient.setQueryData(['invoices', 'secret'], { invoiceNumber: 'INV-SECRET' });
    const epoch = sessionStore.getSnapshot().epoch;

    expect(commitBrowserSession(epoch, restrictedFields('other-operator', 'csrf-next'))).toBe('applied');

    expect(sessionStore.getSnapshot().subject).toBe('other-operator');
    expect(sessionStore.getSnapshot().epoch).toBe(epoch + 1);
    expect(queryClient.getQueryCache().getAll()).toHaveLength(0);
  });

  it('refreshes the CSRF token for the same identity without clearing cached data', () => {
    sessionStore.replaceCookieSession(restrictedFields(), true);
    queryClient.setQueryData(['invoices', 'kept'], { invoiceNumber: 'INV-KEPT' });
    const epoch = sessionStore.getSnapshot().epoch;

    expect(commitBrowserSession(epoch, restrictedFields('oidc-operator', 'csrf-refreshed'))).toBe('applied');

    expect(sessionStore.getSnapshot().epoch).toBe(epoch);
    expect(sessionStore.getSnapshot().csrfToken).toBe('csrf-refreshed');
    expect(queryClient.getQueryData(['invoices', 'kept'])).toEqual({ invoiceNumber: 'INV-KEPT' });
  });
});

function anonymousBody() {
  return {
    authenticated: false,
    financialAccess: false,
    csrfToken: 'csrf-anonymous',
    issuer: null,
    subject: null,
    displayName: null,
    expiresAt: null,
  };
}

function restrictedBody() {
  return {
    authenticated: true,
    financialAccess: false,
    csrfToken: 'csrf-session',
    issuer: 'https://issuer.example',
    subject: 'oidc-operator',
    displayName: 'Ada Operator',
    expiresAt: '2099-01-01T00:00:00.000Z',
  };
}

function restrictedFields(subject = 'oidc-operator', csrfToken = 'csrf-session'): CookieSessionFields {
  return {
    authenticated: true,
    csrfToken,
    issuer: 'https://issuer.example',
    subject,
    displayName: 'Ada Operator',
    expiresAt: 4_000_000_000,
  };
}
