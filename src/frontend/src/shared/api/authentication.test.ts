import { afterEach, describe, expect, it, vi } from 'vitest';

import { submitReversalAttempt } from '../../features/allocations/reversal-attempt';
import { postImport } from '../../features/imports/import-api';
import { submitExplicitAmountAttempt } from '../../features/reconciliation/allocation-attempt';
import { createConfirmationAttempt, postExactConfirmation } from '../../features/reconciliation/confirmation-api';
import { apiGet } from './http';
import { sessionStore, type CookieSessionFields } from './session-store';
import { authenticationHeaders } from './authentication';

afterEach(() => {
  vi.unstubAllEnvs();
});

describe('request authentication', () => {
  it('keeps the bearer header for a development token', () => {
    sessionStore.rotate('memory-token', 4_000_000_000, null);

    expect(authenticationHeaders(sessionStore.getSnapshot(), 'GET')).toEqual({
      Authorization: 'Bearer memory-token',
    });
    expect(authenticationHeaders(sessionStore.getSnapshot(), 'POST')).toEqual({
      Authorization: 'Bearer memory-token',
    });
  });

  it('sends the CSRF header and no bearer token for a cookie session', () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    sessionStore.replaceCookieSession(cookieFields(true), false);

    expect(authenticationHeaders(sessionStore.getSnapshot(), 'GET')).toEqual({});
    expect(authenticationHeaders(sessionStore.getSnapshot(), 'POST')).toEqual({
      'X-CSRF-Token': 'csrf-session',
    });
  });

  it('does not fetch or sign out when a cookie session has no financial access', async () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    sessionStore.replaceCookieSession(cookieFields(true), false);
    const epoch = sessionStore.getSnapshot().epoch;
    const signal = new AbortController().signal;

    await expect(apiGet('/api/invoices', signal)).rejects.toMatchObject({ status: 403 });
    await expect(
      postImport('invoices', 'DEMO', new File(['a'], 'a.csv'), signal, epoch),
    ).rejects.toMatchObject({ status: 403 });
    await expect(
      postImport('bank-transactions', 'DEMO', new File(['a'], 'bank.csv'), signal, epoch),
    ).rejects.toMatchObject({ status: 403 });
    await expect(
      postExactConfirmation(
        createConfirmationAttempt(
          '11111111-1111-1111-1111-111111111111',
          '22222222-2222-2222-2222-222222222222',
          epoch,
        ),
        signal,
      ),
    ).rejects.toMatchObject({ status: 403 });
    await expect(submitExplicitAmountAttempt(signal)).rejects.toMatchObject({ status: 403 });
    await expect(submitReversalAttempt(signal)).rejects.toMatchObject({ status: 403 });
    expect(sessionStore.getSnapshot().epoch).toBe(epoch);
    expect(sessionStore.getSnapshot().authenticated).toBe(true);
  });

  it('does not treat an anonymous cookie session as permission to retry a financial POST', async () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    sessionStore.replaceCookieSession(cookieFields(false), false);
    const epoch = sessionStore.getSnapshot().epoch;

    await expect(apiGet('/api/invoices', new AbortController().signal)).rejects.toMatchObject({ status: 401 });
    await expect(
      postImport('invoices', 'DEMO', new File(['a'], 'a.csv'), new AbortController().signal, epoch),
    ).rejects.toMatchObject({ status: 401 });
    expect(sessionStore.getSnapshot().epoch).toBe(epoch);
  });
});

function cookieFields(authenticated: boolean): CookieSessionFields {
  if (!authenticated) {
    return {
      authenticated: false,
      csrfToken: 'csrf-anonymous',
      issuer: null,
      subject: null,
      displayName: null,
      expiresAt: null,
      organizations: [],
      activeOrganization: null,
      authorization: 'anonymous',
      capabilities: [],
    };
  }

  return {
    authenticated: true,
    csrfToken: 'csrf-session',
    issuer: 'https://issuer.example',
    subject: 'oidc-operator',
    displayName: null,
    expiresAt: null,
    organizations: [],
    activeOrganization: null,
    authorization: 'restricted',
    capabilities: [],
  };
}
