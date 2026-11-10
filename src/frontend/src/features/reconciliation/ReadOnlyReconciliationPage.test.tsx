import { QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { CookieSessionFields } from '../../shared/api/session-store';
import { queryClient } from '../../shared/api/query-client';
import { sessionStore } from '../../shared/api/session-store';
import { server } from '../../test/server';
import { ReadOnlyReconciliationPage } from './ReadOnlyReconciliationPage';

afterEach(() => {
  vi.unstubAllEnvs();
});

describe('read-only reconciliation', () => {
  it('refreshes a rate-limited read manually', async () => {
    const user = userEvent.setup();
    let calls = 0;
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    sessionStore.replaceCookieSession(cookieSession(), true);
    server.use(http.get('/api/matches', () => {
      calls += 1;
      return HttpResponse.json(
        { title: 'Too many requests.', status: 429, code: 'finopsdesk.rate_limit.exceeded' },
        { status: 429, headers: calls === 1 ? { 'Retry-After': 'Mon, 09 Nov 2099 21:00:00 GMT' } : undefined },
      );
    }));

    render(
      <QueryClientProvider client={queryClient}>
        <ReadOnlyReconciliationPage />
      </QueryClientProvider>,
    );

    expect(await screen.findByText(/wait about/)).toBeVisible();
    expect(calls).toBe(1);
    await user.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByText(/asked to wait before loading matches again/)).toBeVisible();
    expect(calls).toBe(2);
  });
});

function cookieSession(): CookieSessionFields {
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
    capabilities: ['reconciliation.read'],
  };
}
