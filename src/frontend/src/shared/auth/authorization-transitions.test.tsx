import { QueryClientProvider } from '@tanstack/react-query';
import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { apiGet } from '../api/http';
import { queryClient } from '../api/query-client';
import { sessionStore } from '../api/session-store';
import { server } from '../../test/server';
import { CookieAuthGate } from './CookieAuthGate';

const northId = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const southId = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
const invoiceId = '11111111-1111-4111-8111-111111111111';
const allocationId = '33333333-3333-4333-8333-333333333333';
const bankId = '22222222-2222-4222-8222-222222222222';
const auditId = '55555555-5555-4555-8555-555555555555';

const viewerPermissions = [
  'invoices.read',
  'bank-transactions.read',
  'reconciliation.read',
  'allocations.read',
  'review-cases.read',
];

const operatorPermissions = [
  'invoices.read',
  'bank-transactions.read',
  'reconciliation.read',
  'allocations.read',
  'audit.read',
  'review-cases.read',
];

describe('authorization transitions', () => {
  beforeEach(() => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
  });

  afterEach(() => {
    vi.unstubAllEnvs();
  });

  it('hides organization A invoices after a switch to an administrator-only organization', async () => {
    const user = userEvent.setup();
    let posts = 0;
    const invoiceGate = deferred<void>();
    server.use(
      http.get('/api/session', () => HttpResponse.json(posts === 0
        ? workspaceBody(northId, 'North Ledger', viewerPermissions, [northOrg(), southOrg()])
        : restrictedOrganization(southId, 'South Ledger', [northOrg(), southOrg()]))),
      http.post('/api/session/organization', () => {
        posts += 1;
        return new HttpResponse(null, { status: 204 });
      }),
      http.get('/api/invoices', () => invoiceGate.promise.then(() => HttpResponse.json(invoiceList('INV-NORTH')))),
    );

    renderGate();
    expect(await screen.findByRole('heading', { name: 'Invoices' })).toBeInTheDocument();
    invoiceGate.resolve();
    expect(await screen.findByText('INV-NORTH')).toBeInTheDocument();
    const epoch = sessionStore.getSnapshot().epoch;

    await user.click(screen.getByRole('button', { name: 'Switch to South Ledger' }));

    expect(await screen.findByText(/Financial access is not available for this organization/)).toBeInTheDocument();
    expect(screen.queryByText('INV-NORTH')).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Invoices' })).not.toBeInTheDocument();
    expect(sessionStore.getSnapshot().epoch).toBeGreaterThan(epoch);
    expect(sessionStore.getSnapshot().authenticated).toBe(true);
    expect(sessionStore.getSnapshot().activeOrganization?.organizationId).toBe(southId);
    expect(cachedText()).not.toContain('INV-NORTH');
    expect(screen.getByRole('button', { name: 'Sign out' })).toBeInTheDocument();
  });

  it('drops an invoice response that finishes after the next organization is accepted', async () => {
    const user = userEvent.setup();
    let posts = 0;
    const invoiceGate = deferred<void>();
    server.use(
      http.get('/api/session', () => HttpResponse.json(posts === 0
        ? workspaceBody(northId, 'North Ledger', viewerPermissions, [northOrg(), southOrg()])
        : restrictedOrganization(southId, 'South Ledger', [northOrg(), southOrg()]))),
      http.post('/api/session/organization', () => {
        posts += 1;
        return new HttpResponse(null, { status: 204 });
      }),
      http.get('/api/invoices', () => invoiceGate.promise.then(() => HttpResponse.json(invoiceList('INV-LATE-A')))),
    );

    renderGate();
    expect(await screen.findByRole('heading', { name: 'Invoices' })).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Switch to South Ledger' }));
    expect(await screen.findByText(/Financial access is not available for this organization/)).toBeInTheDocument();

    invoiceGate.resolve();
    await flush();

    expect(screen.queryByText('INV-LATE-A')).not.toBeInTheDocument();
    expect(cachedText()).not.toContain('INV-LATE-A');
    expect(screen.queryByRole('link', { name: 'Invoices' })).not.toBeInTheDocument();
  });

  it('removes the finance screen when the viewer role is revoked', async () => {
    let revoked = false;
    server.use(
      http.get('/api/session', () => HttpResponse.json(revoked
        ? restrictedOrganization(northId, 'North Ledger', [northOrg()])
        : workspaceBody(northId, 'North Ledger', viewerPermissions, [northOrg()]))),
      http.get('/api/invoices', () => HttpResponse.json(invoiceList('INV-OPEN'))),
    );

    renderGate();
    expect(await screen.findByText('INV-OPEN')).toBeInTheDocument();
    revoked = true;
    revalidate();

    expect(await screen.findByText(/Financial access is not available for this organization/)).toBeInTheDocument();
    expect(screen.queryByText('INV-OPEN')).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Invoices' })).not.toBeInTheDocument();
    expect(sessionStore.getSnapshot().authenticated).toBe(true);
    expect(sessionStore.getSnapshot().authorization).toBe('restricted');
    expect(screen.getByRole('button', { name: 'Sign out' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Sign in' })).not.toBeInTheDocument();
    expect(cachedText()).not.toContain('INV-OPEN');
  });

  it('ignores a financial response that finishes after revocation is verified', async () => {
    let revoked = false;
    const invoiceGate = deferred<void>();
    server.use(
      http.get('/api/session', () => HttpResponse.json(revoked
        ? restrictedOrganization(northId, 'North Ledger', [northOrg()])
        : workspaceBody(northId, 'North Ledger', viewerPermissions, [northOrg()]))),
      http.get('/api/invoices', () => invoiceGate.promise.then(() => HttpResponse.json(invoiceList('INV-AFTER-REVOKE')))),
    );

    renderGate();
    expect(await screen.findByRole('heading', { name: 'Invoices' })).toBeInTheDocument();
    revoked = true;
    revalidate();
    expect(await screen.findByText(/Financial access is not available for this organization/)).toBeInTheDocument();

    invoiceGate.resolve();
    await flush();

    expect(screen.queryByText('INV-AFTER-REVOKE')).not.toBeInTheDocument();
    expect(cachedText()).not.toContain('INV-AFTER-REVOKE');
  });

  it('hides financial records when authorization becomes unavailable and drops the older response', async () => {
    const invoiceGate = deferred<void>();
    let invoiceCalls = 0;
    server.use(
      http.get('/api/session', () => HttpResponse.json(workspaceBody(northId, 'North Ledger', viewerPermissions, [northOrg()]))),
      http.get('/api/invoices', () => {
        invoiceCalls += 1;
        if (invoiceCalls === 1) {
          return HttpResponse.json(invoiceList('INV-VISIBLE'));
        }

        return invoiceGate.promise.then(() => HttpResponse.json(invoiceList('INV-AFTER-OUTAGE')));
      }),
      http.get('/api/bank-transactions', () => HttpResponse.json(
        { code: 'auth.authorization.unavailable', title: 'Financial authorization could not be completed.' },
        { status: 503 },
      )),
    );

    renderGate();
    expect(await screen.findByText('INV-VISIBLE')).toBeInTheDocument();
    const epoch = sessionStore.getSnapshot().epoch;
    const readiness = sessionStore.getSnapshot().readiness;
    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'Refresh' }));
    await act(async () => {
      await apiGet('/api/bank-transactions', new AbortController().signal).catch(() => undefined);
    });

    expect(await screen.findByText('Authorization could not be verified. Financial records stay hidden. This is not a sign-out.')).toBeInTheDocument();
    expect(screen.queryByText('INV-VISIBLE')).not.toBeInTheDocument();
    expect(sessionStore.getSnapshot().authorization).toBe('unavailable');
    expect(sessionStore.getSnapshot().authenticated).toBe(true);
    expect(sessionStore.getSnapshot().financialAccess).toBe(false);
    expect(sessionStore.getSnapshot().epoch).toBe(epoch);
    expect(sessionStore.getSnapshot().readiness).toBeGreaterThan(readiness);
    expect(sessionStore.getSnapshot().capabilities).toEqual(viewerPermissions);
    expect(queryClient.getQueryCache().getAll()).toHaveLength(0);

    invoiceGate.resolve();
    await flush();

    expect(screen.queryByText('INV-AFTER-OUTAGE')).not.toBeInTheDocument();
    expect(screen.queryByText('INV-VISIBLE')).not.toBeInTheDocument();
    expect(cachedText()).not.toContain('INV-');
    expect(screen.getByRole('button', { name: 'Sign out' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Sign in' })).not.toBeInTheDocument();
  });

  it('starts fresh invoice queries after recovery without rotating an unchanged capability set', async () => {
    let outage = false;
    let invoiceCalls = 0;
    server.use(
      http.get('/api/session', () => {
        if (outage) {
          return HttpResponse.json(
            { code: 'auth.authorization.unavailable', title: 'Financial authorization could not be completed.' },
            { status: 503 },
          );
        }

        return HttpResponse.json(workspaceBody(northId, 'North Ledger', viewerPermissions, [northOrg()]));
      }),
      http.get('/api/invoices', () => {
        invoiceCalls += 1;
        return HttpResponse.json(invoiceList('INV-RECOVERED'));
      }),
    );

    const user = userEvent.setup();
    renderGate();
    expect(await screen.findByText('INV-RECOVERED')).toBeInTheDocument();
    const epoch = sessionStore.getSnapshot().epoch;
    const callsBeforeOutage = invoiceCalls;

    outage = true;
    revalidate();
    expect(await screen.findByText('Authorization could not be verified. Financial records stay hidden. This is not a sign-out.')).toBeInTheDocument();
    expect(sessionStore.getSnapshot().epoch).toBe(epoch);
    expect(screen.queryByText('INV-RECOVERED')).not.toBeInTheDocument();

    outage = false;
    await user.click(screen.getByRole('button', { name: 'Try again' }));

    expect(await screen.findByText('INV-RECOVERED')).toBeInTheDocument();
    expect(sessionStore.getSnapshot().epoch).toBe(epoch);
    expect(sessionStore.getSnapshot().readiness).toBeGreaterThan(0);
    expect(sessionStore.getSnapshot().authorization).toBe('verified');
    expect(invoiceCalls).toBeGreaterThan(callsBeforeOutage);
  });

  it('rotates the epoch and removes forbidden navigation when recovery changes capabilities', async () => {
    let narrowed = false;
    server.use(
      http.get('/api/session', () => HttpResponse.json(narrowed
        ? workspaceBody(northId, 'North Ledger', ['bank-transactions.read'], [northOrg()])
        : workspaceBody(northId, 'North Ledger', viewerPermissions, [northOrg()]))),
      http.get('/api/invoices', () => HttpResponse.json(invoiceList('INV-BEFORE-NARROW'))),
      http.get('/api/bank-transactions', () => HttpResponse.json({
        items: [],
        page: 1,
        pageSize: 50,
        totalCount: 0,
      })),
    );

    renderGate();
    expect(await screen.findByText('INV-BEFORE-NARROW')).toBeInTheDocument();
    const epoch = sessionStore.getSnapshot().epoch;
    narrowed = true;
    revalidate();

    await vi.waitFor(() => {
      expect(sessionStore.getSnapshot().epoch).toBeGreaterThan(epoch);
      expect(screen.queryByRole('link', { name: 'Invoices' })).not.toBeInTheDocument();
      expect(screen.getByRole('link', { name: 'Bank transactions' })).toBeInTheDocument();
    });
    expect(screen.queryByText('INV-BEFORE-NARROW')).not.toBeInTheDocument();
    expect(cachedText()).not.toContain('INV-BEFORE-NARROW');
    const user = userEvent.setup();
    await user.click(screen.getByRole('link', { name: 'Bank transactions' }));
    expect(await screen.findByRole('heading', { name: 'Bank transactions' })).toBeInTheDocument();
  });

  it('mounts allocation audit only for a caller with audit.read', async () => {
    let auditCalls = 0;
    server.use(
      http.get('/api/session', () => HttpResponse.json(workspaceBody(northId, 'North Ledger', operatorPermissions, [northOrg()]))),
      http.get('/api/invoices', () => HttpResponse.json(invoiceList('INV-IGNORED'))),
      http.get('/api/allocations', () => HttpResponse.json(historyList())),
      http.get('/api/allocations/:allocationId/audit', () => {
        auditCalls += 1;
        return HttpResponse.json(auditList());
      }),
    );

    const user = userEvent.setup();
    const operator = renderGate();
    await user.click(await screen.findByRole('link', { name: 'Allocation history' }));
    expect(await screen.findByText('ALLOC-1')).toBeInTheDocument();
    expect(auditCalls).toBe(0);
    await user.click(screen.getByRole('button', { name: 'View audit' }));
    expect(await screen.findByRole('heading', { name: 'Audit' })).toBeInTheDocument();
    expect(auditCalls).toBe(1);
    operator.unmount();

    auditCalls = 0;
    server.use(
      http.get('/api/session', () => HttpResponse.json(workspaceBody(northId, 'North Ledger', viewerPermissions, [northOrg()]))),
    );
    renderGate('/allocations');
    expect(await screen.findByText('ALLOC-1')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'View audit' })).not.toBeInTheDocument();
    await flush();
    expect(auditCalls).toBe(0);
  });

  it('does not start a financial query for a deep link the caller cannot read', async () => {
    let financialCalls = 0;
    server.use(
      http.get('/api/session', () => HttpResponse.json(workspaceBody(northId, 'North Ledger', ['invoices.read'], [northOrg()]))),
      http.get('/api/allocations', () => {
        financialCalls += 1;
        return HttpResponse.json(historyList());
      }),
      http.get('/api/invoices', () => {
        financialCalls += 1;
        return HttpResponse.json(invoiceList('INV-SHOULD-NOT-LOAD'));
      }),
    );

    renderGate('/allocations');

    expect(await screen.findByText('That page is not available.')).toBeInTheDocument();
    await flush();
    expect(financialCalls).toBe(0);
    expect(screen.queryByText('INV-SHOULD-NOT-LOAD')).not.toBeInTheDocument();
  });

  it('shows no-available-view when financial access has no implemented screen', async () => {
    let invoiceCalls = 0;
    server.use(
      http.get('/api/session', () => HttpResponse.json(workspaceBody(northId, 'North Ledger', ['audit.read'], [northOrg()]))),
      http.get('/api/invoices', () => {
        invoiceCalls += 1;
        return HttpResponse.json(invoiceList('INV-BLIND'));
      }),
    );

    renderGate('/invoices');

    expect(await screen.findByText('This organization has no financial screens available in this browser. Financial access stays closed here.')).toBeInTheDocument();
    await flush();
    expect(invoiceCalls).toBe(0);
    expect(screen.queryByRole('link', { name: 'Invoices' })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Review cases' })).not.toBeInTheDocument();
    expect(screen.queryByText('INV-BLIND')).not.toBeInTheDocument();
  });

  it('mounts review cases for review-cases.read and does not post', async () => {
    let reviewGets = 0;
    let posts = 0;
    server.use(
      http.get('/api/session', () => HttpResponse.json(workspaceBody(northId, 'North Ledger', ['review-cases.read'], [northOrg()]))),
      http.get('/api/review-cases', () => {
        reviewGets += 1;
        return HttpResponse.json({ items: [], page: 1, pageSize: 50, totalCount: 0 });
      }),
      http.post('/api/review-cases', () => {
        posts += 1;
        return new HttpResponse(null, { status: 500 });
      }),
      http.post('/api/review-cases/:id/resolve', () => {
        posts += 1;
        return new HttpResponse(null, { status: 500 });
      }),
      http.post('/api/review-cases/:id/dismiss', () => {
        posts += 1;
        return new HttpResponse(null, { status: 500 });
      }),
    );

    renderGate('/review-cases');

    expect(await screen.findByRole('heading', { name: 'Review cases' })).toBeInTheDocument();
    expect(await screen.findByText('No review cases are in this organization yet.')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Review cases' })).toHaveAttribute('href', '/review-cases');
    await flush();
    expect(reviewGets).toBeGreaterThan(0);
    expect(posts).toBe(0);
  });

  it('does not fetch review cases for a deep link without review-cases.read', async () => {
    let reviewGets = 0;
    server.use(
      http.get('/api/session', () => HttpResponse.json(workspaceBody(northId, 'North Ledger', ['invoices.read'], [northOrg()]))),
      http.get('/api/review-cases', () => {
        reviewGets += 1;
        return HttpResponse.json({ items: [], page: 1, pageSize: 50, totalCount: 0 });
      }),
      http.get('/api/invoices', () => HttpResponse.json(invoiceList('INV-HOME'))),
    );

    renderGate('/review-cases');

    expect(await screen.findByText('That page is not available.')).toBeInTheDocument();
    await flush();
    expect(reviewGets).toBe(0);
    expect(screen.queryByRole('heading', { name: 'Review cases' })).not.toBeInTheDocument();
  });

  it('keeps the verified cookie session when a review case read stays forbidden', async () => {
    const user = userEvent.setup();
    let sessionCalls = 0;
    let reviewCalls = 0;
    let logoutPosts = 0;
    let signedOut = false;
    server.use(
      http.get('/api/session', () => {
        sessionCalls += 1;
        return HttpResponse.json(signedOut
          ? anonymousBody()
          : workspaceBody(northId, 'North Ledger', viewerPermissions, [northOrg()]));
      }),
      http.get('/api/review-cases', () => {
        reviewCalls += 1;
        return HttpResponse.json({ code: 'auth.permission.denied', title: 'Forbidden' }, { status: 403 });
      }),
      http.post('/logout', ({ request }) => {
        logoutPosts += 1;
        expect(request.headers.get('authorization')).toBeNull();
        expect(request.headers.get('x-csrf-token')).toBe('csrf-session');
        signedOut = true;
        return new HttpResponse(null, { status: 204 });
      }),
    );

    renderGate('/review-cases');

    expect(await screen.findByText('Review cases are not available for this organization.')).toBeInTheDocument();
    await vi.waitFor(() => {
      expect(sessionCalls).toBe(2);
      expect(reviewCalls).toBe(2);
    });
    await flush();
    expect(sessionCalls).toBe(2);
    expect(reviewCalls).toBe(2);
    expect(screen.getByText('Signed in as Ada Operator')).toBeInTheDocument();
    expect(screen.getByText('Active organization: North Ledger')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Log out' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Update access' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Sign in' })).not.toBeInTheDocument();
    expect(sessionStore.getSnapshot().authenticated).toBe(true);
    expect(sessionStore.getSnapshot().authorization).toBe('verified');
    expect(sessionStore.getSnapshot().csrfToken).toBe('csrf-session');
    expect(sessionStore.getSnapshot().issuer).toBe('https://issuer.example');
    expect(sessionStore.getSnapshot().subject).toBe('oidc-operator');
    expect(sessionStore.getSnapshot().activeOrganization?.organizationId).toBe(northId);
    expect(sessionStore.getSnapshot().capabilities).toContain('review-cases.read');

    await user.click(screen.getByRole('button', { name: 'Try again' }));
    await vi.waitFor(() => expect(reviewCalls).toBe(3));
    await flush();
    expect(sessionCalls).toBe(2);
    expect(sessionStore.getSnapshot().authenticated).toBe(true);
    expect(sessionStore.getSnapshot().csrfToken).toBe('csrf-session');
    expect(screen.getByText('Review cases are not available for this organization.')).toBeInTheDocument();
    expect(screen.getByText('Signed in as Ada Operator')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Log out' }));
    expect(await screen.findByRole('button', { name: 'Sign in' })).toBeInTheDocument();
    expect(logoutPosts).toBe(1);
    expect(sessionStore.getSnapshot().authenticated).toBe(false);
    expect(screen.queryByText('Review cases are not available for this organization.')).not.toBeInTheDocument();
  });

  it('revalidates a financial 403 once and does not retry forever', async () => {
    let sessionCalls = 0;
    let invoiceCalls = 0;
    server.use(
      http.get('/api/session', () => {
        sessionCalls += 1;
        return HttpResponse.json(workspaceBody(northId, 'North Ledger', viewerPermissions, [northOrg()]));
      }),
      http.get('/api/invoices', () => {
        invoiceCalls += 1;
        return HttpResponse.json({ code: 'auth.permission.denied', title: 'Forbidden' }, { status: 403 });
      }),
    );

    renderGate();
    await vi.waitFor(() => {
      expect(sessionCalls).toBe(2);
      expect(invoiceCalls).toBe(2);
    });
    expect(screen.getByText(/have access to this workspace/)).toBeInTheDocument();
    await flush();

    expect(sessionCalls).toBe(2);
    expect(invoiceCalls).toBe(2);
  });

  it('does not revalidate permissions after a financial 404', async () => {
    let sessionCalls = 0;
    server.use(
      http.get('/api/session', () => {
        sessionCalls += 1;
        return HttpResponse.json(workspaceBody(northId, 'North Ledger', viewerPermissions, [northOrg()]));
      }),
      http.get('/api/invoices', () => HttpResponse.json({ title: 'Missing' }, { status: 404 })),
    );

    renderGate();
    expect(await screen.findByText('The invoice list could not be loaded.')).toBeInTheDocument();
    await flush();
    expect(sessionCalls).toBe(1);
  });

  it('signs out when a revoked browser session is reported as anonymous after a financial 401', async () => {
    let sessionCalls = 0;
    server.use(
      http.get('/api/session', () => {
        sessionCalls += 1;
        if (sessionCalls === 1) {
          return HttpResponse.json(workspaceBody(northId, 'North Ledger', viewerPermissions, [northOrg()]));
        }

        return HttpResponse.json(anonymousBody());
      }),
      http.get('/api/invoices', () => HttpResponse.json({ title: 'Unauthorized' }, { status: 401 })),
    );

    renderGate();
    expect(await screen.findByRole('button', { name: 'Sign in' })).toBeInTheDocument();
    expect(screen.queryByText('INV-OPEN')).not.toBeInTheDocument();
    expect(sessionStore.getSnapshot().authenticated).toBe(false);
    expect(sessionCalls).toBe(2);
  });
});

function renderGate(path = '/') {
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[path]}>
        <CookieAuthGate />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

function revalidate(): void {
  window.dispatchEvent(new Event('blur'));
  window.dispatchEvent(new Event('focus'));
}

async function flush(): Promise<void> {
  await act(async () => {
    await Promise.resolve();
  });
}

function cachedText(): string {
  return JSON.stringify(queryClient.getQueryCache().getAll().map((query) => query.state.data));
}

function deferred<T>() {
  let resolve: (value: T) => void = () => undefined;
  const promise = new Promise<T>((complete) => {
    resolve = complete;
  });
  return { promise, resolve };
}

function northOrg() {
  return { organizationId: northId, displayName: 'North Ledger' };
}

function southOrg() {
  return { organizationId: southId, displayName: 'South Ledger' };
}

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

function workspaceBody(
  organizationId: string,
  displayName: string,
  permissions: readonly string[],
  organizations: { organizationId: string; displayName: string }[],
) {
  return {
    authenticated: true,
    financialAccess: true,
    permissions,
    csrfToken: 'csrf-session',
    issuer: 'https://issuer.example',
    subject: 'oidc-operator',
    displayName: 'Ada Operator',
    expiresAt: '2099-01-01T00:00:00.000Z',
    organizations,
    activeOrganization: { organizationId, displayName },
  };
}

function restrictedOrganization(
  organizationId: string,
  displayName: string,
  organizations: { organizationId: string; displayName: string }[],
) {
  return {
    authenticated: true,
    financialAccess: false,
    permissions: [],
    csrfToken: 'csrf-session',
    issuer: 'https://issuer.example',
    subject: 'oidc-operator',
    displayName: 'Ada Operator',
    expiresAt: '2099-01-01T00:00:00.000Z',
    organizations,
    activeOrganization: { organizationId, displayName },
  };
}

function invoiceList(invoiceNumber: string) {
  return {
    items: [{
      id: invoiceId,
      invoiceNumber,
      totalAmount: { amount: 12.5, currency: 'PLN' },
      allocatedAmount: { amount: 0, currency: 'PLN' },
      outstandingAmount: { amount: 12.5, currency: 'PLN' },
    }],
    page: 1,
    pageSize: 50,
    totalCount: 1,
  };
}

function historyList() {
  return {
    items: [{
      allocationId,
      invoiceId,
      bankTransactionId: bankId,
      invoiceNumber: 'ALLOC-1',
      reference: null,
      amount: { amount: 15.5, currency: 'PLN' },
      status: 'confirmed',
      confirmedAt: '2026-10-31T12:00:00.123Z',
      reversedAt: null,
      reversalReason: null,
      idempotencyOperation: 'exactMatch',
    }],
    page: 1,
    pageSize: 50,
    totalCount: 1,
  };
}

function auditList() {
  return {
    items: [{
      id: auditId,
      allocationId,
      invoiceId,
      bankTransactionId: bankId,
      batchExecutionId: null,
      action: 'confirmation',
      operation: 'exactMatch',
      actorId: 'operator-1',
      amount: { amount: 15.5, currency: 'PLN' },
      currency: 'PLN',
      recordedAt: '2026-10-31T12:00:00.123Z',
      reason: null,
    }],
    page: 1,
    pageSize: 50,
    totalCount: 1,
  };
}
