import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';

import { queryClient } from '../api/query-client';
import { sessionStore, type CookieSessionFields } from '../api/session-store';
import { server } from '../../test/server';
import { beginSessionRead, commitBrowserSession, loadBrowserSession, parseBrowserSession } from './browser-session';

describe('browser session', () => {
  it('rejects a 200 body that is not a session', () => {
    expect(parseBrowserSession({ ok: true }).kind).toBe('invalid');
    expect(parseBrowserSession({ ...anonymousBody(), issuer: 'https://issuer.example' }).kind).toBe('invalid');
    expect(parseBrowserSession({ ...restrictedBody(), financialAccess: true }).kind).toBe('invalid');
    expect(parseBrowserSession({
      ...restrictedBody(),
      financialAccess: true,
      permissions: ['future.permission'],
    }).kind).toBe('restricted');
    expect(parseBrowserSession({
      ...restrictedBody(),
      financialAccess: false,
      permissions: ['invoices.read'],
    }).kind).toBe('invalid');
    expect(parseBrowserSession({ ...restrictedBody(), subject: '' }).kind).toBe('invalid');
    expect(parseBrowserSession({ ...restrictedBody(), activeOrganization: organization('North Ledger', northId) }).kind).toBe('invalid');
    expect(parseBrowserSession({ ...anonymousBody(), organizations: [organization('North Ledger', northId)] }).kind).toBe('invalid');
    expect(parseBrowserSession({
      ...restrictedBody(),
      organizations: [organization('North Ledger', northId), organization('North Ledger', northId.toUpperCase())],
    }).kind).toBe('invalid');
    expect(parseBrowserSession({
      ...restrictedBody(),
      organizations: [organization('North Ledger', northId)],
      activeOrganization: organization('Other name', northId),
    }).kind).toBe('invalid');
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
    expect(parseBrowserSession({
      ...restrictedBody(),
      financialAccess: true,
      permissions: ['review-cases.read', 'invoices.read', 'invoices.read', 'future.permission'],
      organizations: [organization('North Ledger', northId)],
      activeOrganization: organization('North Ledger', northId),
    })).toMatchObject({
      kind: 'workspace',
      session: {
        authorization: 'verified',
        capabilities: ['invoices.read', 'review-cases.read'],
      },
    });
  });

  it('rotates the epoch when verified capabilities change', () => {
    const current = restrictedFields();
    current.authorization = 'verified';
    current.capabilities = ['invoices.read'];
    current.organizations = [organization('North Ledger', northId)];
    current.activeOrganization = organization('North Ledger', northId);
    sessionStore.replaceCookieSession(current, true);
    const epoch = sessionStore.getSnapshot().epoch;
    const readId = beginSessionRead();
    const next = restrictedFields();
    next.authorization = 'verified';
    next.capabilities = ['bank-transactions.read'];
    next.organizations = [organization('North Ledger', northId)];
    next.activeOrganization = organization('North Ledger', northId);

    expect(commitBrowserSession(epoch, readId, next)).toBe('applied');
    expect(sessionStore.getSnapshot().epoch).toBe(epoch + 1);
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
    const readId = beginSessionRead();
    sessionStore.rotate(null, null, null);

    expect(commitBrowserSession(epoch, readId, restrictedFields())).toBe('stale');
    expect(sessionStore.getSnapshot().authenticated).toBe(false);
    expect(sessionStore.getSnapshot().subject).toBeNull();
  });

  it('clears cached financial data when the signed-in identity changes', () => {
    sessionStore.replaceCookieSession(restrictedFields(), true);
    queryClient.setQueryData(['invoices', 'secret'], { invoiceNumber: 'INV-SECRET' });
    const epoch = sessionStore.getSnapshot().epoch;
    const readId = beginSessionRead();

    expect(commitBrowserSession(epoch, readId, restrictedFields('other-operator', 'csrf-next'))).toBe('applied');

    expect(sessionStore.getSnapshot().subject).toBe('other-operator');
    expect(sessionStore.getSnapshot().epoch).toBe(epoch + 1);
    expect(queryClient.getQueryCache().getAll()).toHaveLength(0);
  });

  it('refreshes the CSRF token for the same identity without clearing cached data', () => {
    sessionStore.replaceCookieSession(restrictedFields(), true);
    queryClient.setQueryData(['invoices', 'kept'], { invoiceNumber: 'INV-KEPT' });
    const epoch = sessionStore.getSnapshot().epoch;
    const readId = beginSessionRead();

    expect(commitBrowserSession(epoch, readId, restrictedFields('oidc-operator', 'csrf-refreshed'))).toBe('applied');

    expect(sessionStore.getSnapshot().epoch).toBe(epoch);
    expect(sessionStore.getSnapshot().csrfToken).toBe('csrf-refreshed');
    expect(queryClient.getQueryData(['invoices', 'kept'])).toEqual({ invoiceNumber: 'INV-KEPT' });
  });

  it('clears cached data when the verified organization changes', () => {
    sessionStore.replaceCookieSession(restrictedFields(), true);
    queryClient.setQueryData(['invoices', 'secret'], { invoiceNumber: 'INV-SECRET' });
    const epoch = sessionStore.getSnapshot().epoch;
    const readId = beginSessionRead();
    const next = restrictedFields();
    next.organizations = [organization('North Ledger', northId)];
    next.activeOrganization = organization('North Ledger', northId);

    expect(commitBrowserSession(epoch, readId, next)).toBe('applied');
    expect(sessionStore.getSnapshot().epoch).toBe(epoch + 1);
    expect(queryClient.getQueryCache().getAll()).toHaveLength(0);
  });

  it('keeps the epoch when only the organization name changes', () => {
    const current = restrictedFields();
    current.organizations = [organization('North Ledger', northId)];
    current.activeOrganization = organization('North Ledger', northId);
    sessionStore.replaceCookieSession(current, true);
    queryClient.setQueryData(['invoices', 'kept'], { invoiceNumber: 'INV-KEPT' });
    const epoch = sessionStore.getSnapshot().epoch;
    const readId = beginSessionRead();
    const next = restrictedFields();
    next.organizations = [organization('North Ledger renamed', northId)];
    next.activeOrganization = organization('North Ledger renamed', northId);

    expect(commitBrowserSession(epoch, readId, next)).toBe('applied');
    expect(sessionStore.getSnapshot().epoch).toBe(epoch);
    expect(queryClient.getQueryData(['invoices', 'kept'])).toEqual({ invoiceNumber: 'INV-KEPT' });
  });

  it('ignores an older session read after a newer read starts', () => {
    const epoch = sessionStore.getSnapshot().epoch;
    const older = beginSessionRead();
    const newer = beginSessionRead();

    expect(commitBrowserSession(epoch, older, restrictedFields())).toBe('stale');
    expect(commitBrowserSession(epoch, newer, restrictedFields('oidc-operator', 'csrf-newer'))).toBe('applied');
    expect(sessionStore.getSnapshot().csrfToken).toBe('csrf-newer');
  });

  it('does not rotate the epoch when the same permissions arrive in a different order', () => {
    const current = restrictedFields();
    current.authorization = 'verified';
    current.capabilities = ['invoices.read', 'review-cases.read'];
    current.organizations = [organization('North Ledger', northId)];
    current.activeOrganization = organization('North Ledger', northId);
    sessionStore.replaceCookieSession(current, true);
    const epoch = sessionStore.getSnapshot().epoch;
    const readId = beginSessionRead();
    const parsed = parseBrowserSession({
      ...restrictedBody(),
      financialAccess: true,
      permissions: ['review-cases.read', 'invoices.read'],
      organizations: [organization('North Ledger', northId)],
      activeOrganization: organization('North Ledger', northId),
    });

    expect(parsed.kind).toBe('workspace');
    if (parsed.kind !== 'workspace') {
      return;
    }

    expect(commitBrowserSession(epoch, readId, parsed.session)).toBe('applied');
    expect(sessionStore.getSnapshot().epoch).toBe(epoch);
    expect(sessionStore.getSnapshot().capabilities).toEqual(['invoices.read', 'review-cases.read']);
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
    organizations: [],
    activeOrganization: null,
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
    organizations: [],
    activeOrganization: null,
  };
}

const northId = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';

function organization(displayName: string, organizationId: string) {
  return { organizationId, displayName };
}

function restrictedFields(subject = 'oidc-operator', csrfToken = 'csrf-session'): CookieSessionFields {
  return {
    authenticated: true,
    csrfToken,
    issuer: 'https://issuer.example',
    subject,
    displayName: 'Ada Operator',
    expiresAt: 4_000_000_000,
    organizations: [],
    activeOrganization: null,
    authorization: 'restricted',
    capabilities: [],
  };
}
