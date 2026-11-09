import { QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { queryClient } from '../../shared/api/query-client';
import { sessionStore, type CookieSessionFields } from '../../shared/api/session-store';
import { server } from '../../test/server';
import { fakeJwt } from '../../test/jwt';
import { OperationsDashboardPage } from './OperationsDashboardPage';

afterEach(() => {
  vi.unstubAllEnvs();
  sessionStore.resetForTests();
  queryClient.clear();
});

describe('operations dashboard', () => {
  it('shows precise dispute counters and the open review-case link', async () => {
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    let disputeReads = 0;
    server.use(
      http.get('/api/review-cases/summary', () => text(reviewSummary('4'))),
      http.get('/api/allocation-disputes/summary', () => {
        disputeReads += 1;
        return text('{"needsDecision":9223372036854775807,"needsCorrection":1}');
      }),
    );
    renderDashboard();

    expect(await screen.findByText('Open 4')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Open review cases' })).toHaveAttribute('href', '/review-cases?status=Open');
    expect(await screen.findByText('Needs decision 9223372036854775807')).toBeInTheDocument();
    expect(screen.getByText('Upheld disputes whose allocation can still be fully reversed 1')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Disputes without a decision' })).toHaveAttribute(
      'href',
      '/allocation-disputes?queue=needs-decision',
    );
    expect(screen.getByRole('link', { name: 'Disputes that can be corrected' })).toHaveAttribute(
      'href',
      '/allocation-disputes?queue=needs-correction',
    );
    expect(disputeReads).toBe(1);
  });

  it('does not fetch dispute counters in a cookie session and hides review counts after a denial', async () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    let reviewReads = 0;
    let disputeReads = 0;
    server.use(
      http.get('/api/review-cases/summary', () => {
        reviewReads += 1;
        return text(reviewSummary('3'));
      }),
      http.get('/api/allocation-disputes/summary', () => {
        disputeReads += 1;
        return text('{"needsDecision":"1","needsCorrection":"1"}');
      }),
    );
    sessionStore.replaceCookieSession(cookieSession(['invoices.read']), true);
    renderDashboard();
    expect(await screen.findByText('Operations are not available for this session.')).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Allocation dispute queues' })).not.toBeInTheDocument();
    expect(reviewReads).toBe(0);
    expect(disputeReads).toBe(0);

    sessionStore.replaceCookieSession(cookieSession(['review-cases.read']), true);
    expect(await screen.findByText('Open 3')).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Allocation dispute queues' })).not.toBeInTheDocument();
    expect(disputeReads).toBe(0);

    server.use(http.get('/api/review-cases/summary', () => HttpResponse.json(
      { title: 'Forbidden', code: 'auth.forbidden' },
      { status: 403 },
    )));
    await queryClient.refetchQueries({ type: 'active' });
    expect(await screen.findByText('Review case counts are hidden for this session.')).toBeInTheDocument();
    expect(screen.queryByText('Open 3')).not.toBeInTheDocument();
    expect(disputeReads).toBe(0);
  });
});

function renderDashboard(): void {
  render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <OperationsDashboardPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

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

function reviewSummary(openCount: string): string {
  return `{"totalCount":${openCount},"openCount":${openCount},"resolvedCount":0,"dismissedCount":0,"openCompetingMatchCount":0,"openNeedsInvestigationCount":${openCount}}`;
}

function text(body: string): Response {
  return new Response(body, { headers: { 'Content-Type': 'application/json' } });
}
