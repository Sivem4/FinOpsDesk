import { QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { createMemoryRouter, MemoryRouter, RouterProvider } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { queryClient } from '../../shared/api/query-client';
import { sessionStore, type CookieSessionFields } from '../../shared/api/session-store';
import { server } from '../../test/server';
import { ReviewCasePage } from './ReviewCasePage';

const caseId = '11111111-1111-4111-8111-111111111111';
const invoiceId = '22222222-2222-4222-8222-222222222222';
const bankId = '33333333-3333-4333-8333-333333333333';
const allocationId = '44444444-4444-4444-8444-444444444444';
const secretToken = 'concurrency-token-must-stay-hidden';

afterEach(() => {
  vi.unstubAllEnvs();
});

describe('review case page', () => {
  it('shows a development list and detail without sending the selected id as a list filter', async () => {
    const user = userEvent.setup();
    const listUrls: string[] = [];
    let posts = 0;
    server.use(
      http.get('/api/review-cases', ({ request }) => {
        listUrls.push(request.url);
        return text(listDocument());
      }),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
      http.post('/api/review-cases', () => {
        posts += 1;
        return new HttpResponse(null, { status: 500 });
      }),
    );

    sessionStore.rotate('memory-token', 4_000_000_000, null);
    renderPage(`/review-cases?selected=${caseId}&page=1&pageSize=50`);

    expect(await screen.findByRole('button', { name: `Show review case ${caseId}` })).toBeInTheDocument();
    expect(await screen.findByText('Invoice outstanding 0.00 PLN')).toBeInTheDocument();
    expect(screen.getByText('Bank transaction unapplied 0.0001 EUR')).toBeInTheDocument();
    expect(screen.getByText(/not one atomic snapshot/)).toBeInTheDocument();
    expect(screen.queryByText(secretToken)).not.toBeInTheDocument();
    expect(listUrls.every((url) => !new URL(url).searchParams.has('selected'))).toBe(true);
    expect(posts).toBe(0);

    await user.click(screen.getByLabelText('Status'));
    await user.click(screen.getByRole('option', { name: 'Open' }));
    await user.click(screen.getByRole('button', { name: 'Apply filters' }));
    await waitFor(() => expect(listUrls.some((url) => new URL(url).searchParams.get('status') === 'Open')).toBe(true));
    expect(listUrls.every((url) => !new URL(url).searchParams.has('selected'))).toBe(true);
    expect(posts).toBe(0);
  });

  it('shows the same read in a verified cookie session', async () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    sessionStore.replaceCookieSession(cookieSession(['review-cases.read']), true);
    server.use(
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
    );

    renderPage(`/review-cases?selected=${caseId}`);

    expect(await screen.findByText('Invoice outstanding 0.00 PLN')).toBeInTheDocument();
    expect(screen.getByText('Bank transaction unapplied 0.0001 EUR')).toBeInTheDocument();
    expect(screen.queryByText(secretToken)).not.toBeInTheDocument();
  });

  it('does not fetch when the cookie session lacks review-cases.read', async () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    sessionStore.replaceCookieSession(cookieSession(['invoices.read']), true);
    let calls = 0;
    server.use(http.get('/api/review-cases', () => {
      calls += 1;
      return text(listDocument());
    }));

    renderPage('/review-cases');

    expect(await screen.findByText('Review cases are not available for this session.')).toBeInTheDocument();
    expect(calls).toBe(0);
  });

  it('shows an empty organization, a not-found detail, and a detail error without retained amounts', async () => {
    const user = userEvent.setup();
    let detailCalls = 0;
    server.use(
      http.get('/api/review-cases', () => text('{"items":[],"page":1,"pageSize":50,"totalCount":0}')),
      http.get('/api/review-cases/:id', () => {
        detailCalls += 1;
        if (detailCalls === 1) {
          return text(detailDocument());
        }
        return HttpResponse.json(
          { title: 'Inconsistent', code: 'reconciliation.review_case.inconsistent_linked_money', detail: '12345.67 PLN' },
          { status: 500 },
        );
      }),
    );
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    const empty = renderPage('/review-cases');
    expect(await screen.findByText('No review cases are in this organization yet.')).toBeInTheDocument();
    empty.unmount();

    renderPage(`/review-cases?selected=${caseId}`);
    expect(await screen.findByText('Invoice outstanding 0.00 PLN')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Refresh selected case' }));
    expect(await screen.findByText('The linked balances could not be read. No amounts from that response are shown.')).toBeInTheDocument();
    expect(screen.queryByText('0.00 PLN')).not.toBeInTheDocument();
    expect(screen.queryByText('12345.67 PLN')).not.toBeInTheDocument();
  });

  it('reports a missing review case and an empty filter', async () => {
    server.use(
      http.get('/api/review-cases', ({ request }) => {
        const status = new URL(request.url).searchParams.get('status');
        if (status === 'Resolved') {
          return text('{"items":[],"page":1,"pageSize":50,"totalCount":0}');
        }
        return text(listDocument());
      }),
      http.get('/api/review-cases/:id', () => HttpResponse.json(
        { title: 'Missing', code: 'reconciliation.review_case.not_found' },
        { status: 404 },
      )),
    );
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    renderPage(`/review-cases?selected=${caseId}&status=Resolved`);

    expect(await screen.findByText('No review cases match these filters.')).toBeInTheDocument();
    expect(await screen.findByText('That review case was not found.')).toBeInTheDocument();
    expect(screen.queryByText('0.00 PLN')).not.toBeInTheDocument();
  });

  it('hides the previous detail after access is lost and after a late organization change', async () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    sessionStore.replaceCookieSession(cookieSession(['review-cases.read']), true);
    const gate = deferred<void>();
    let detailCalls = 0;
    server.use(
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', async () => {
        detailCalls += 1;
        if (detailCalls === 1) {
          await gate.promise;
          return text(detailDocument('9999.99'));
        }
        return text(detailDocument('1.00'));
      }),
    );

    renderPage(`/review-cases?selected=${caseId}`);
    expect(await screen.findByText('Loading the selected review case')).toBeInTheDocument();

    sessionStore.replaceCookieSession(cookieSession([]), true);
    gate.resolve();
    expect(await screen.findByText('Review cases are not available for this session.')).toBeInTheDocument();
    expect(screen.queryByText('9999.99 PLN')).not.toBeInTheDocument();
    expect(screen.queryByText('1.00 PLN')).not.toBeInTheDocument();
  });

  it('keeps the filter form aligned with list requests after back and forward', async () => {
    const user = userEvent.setup();
    const listUrls: string[] = [];
    server.use(http.get('/api/review-cases', ({ request }) => {
      listUrls.push(request.url);
      return text(listDocument());
    }));
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    const router = createMemoryRouter(
      [{ path: '/review-cases', element: <ReviewCasePage /> }],
      { initialEntries: ['/review-cases'] },
    );
    render(
      <QueryClientProvider client={queryClient}>
        <RouterProvider router={router} />
      </QueryClientProvider>,
    );

    await screen.findByRole('button', { name: `Show review case ${caseId}` });
    await chooseStatus(user, 'Open');
    await user.type(screen.getByLabelText('Invoice id'), invoiceId);
    await user.type(screen.getByLabelText('Bank transaction id'), bankId);
    await user.click(screen.getByRole('button', { name: 'Apply filters' }));
    await waitFor(() => expect(listQuery(listUrls.at(-1))).toEqual({
      status: 'Open',
      invoiceId,
      bankTransactionId: bankId,
      selected: false,
    }));

    await chooseStatus(user, 'Resolved');
    await user.click(screen.getByRole('button', { name: 'Apply filters' }));
    await waitFor(() => expect(listQuery(listUrls.at(-1)).status).toBe('Resolved'));

    await router.navigate(-1);
    await waitFor(() => expect(listQuery(listUrls.at(-1)).status).toBe('Open'));
    expect(screen.getByRole('combobox', { name: 'Status' })).toHaveTextContent('Open');
    expect(screen.getByLabelText('Invoice id')).toHaveValue(invoiceId);
    expect(screen.getByLabelText('Bank transaction id')).toHaveValue(bankId);
    expect(listQuery(listUrls.at(-1))).toEqual({
      status: 'Open',
      invoiceId,
      bankTransactionId: bankId,
      selected: false,
    });

    await router.navigate(1);
    await waitFor(() => expect(listQuery(listUrls.at(-1)).status).toBe('Resolved'));
    expect(screen.getByRole('combobox', { name: 'Status' })).toHaveTextContent('Resolved');
    expect(screen.getByLabelText('Invoice id')).toHaveValue(invoiceId);
    expect(screen.getByLabelText('Bank transaction id')).toHaveValue(bankId);
    expect(listQuery(listUrls.at(-1))).toEqual({
      status: 'Resolved',
      invoiceId,
      bankTransactionId: bankId,
      selected: false,
    });

    const requestsBeforeSelection = listUrls.length;
    await user.type(screen.getByLabelText('Invoice id'), 'x');
    await user.click(screen.getByRole('button', { name: `Show review case ${caseId}` }));
    expect(screen.getByLabelText('Invoice id')).toHaveValue(`${invoiceId}x`);
    expect(listUrls).toHaveLength(requestsBeforeSelection);
    expect(listQuery(listUrls.at(-1)).invoiceId).toBe(invoiceId);
    expect(listUrls.every((url) => !new URL(url).searchParams.has('selected'))).toBe(true);
  });

  it('offers update access when a development read is forbidden', async () => {
    const user = userEvent.setup();
    server.use(http.get('/api/review-cases', () => HttpResponse.json({ title: 'Forbidden' }, { status: 403 })));
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    renderPage('/review-cases');

    await user.click(await screen.findByRole('button', { name: 'Update access' }));
    expect(sessionStore.getSnapshot().token).toBeNull();
  });

  it('shows a resolved allocation and a dismissed closing note without writing', async () => {
    let posts = 0;
    server.use(
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', ({ params }) => text(params.id === caseId
        ? resolvedDetail()
        : dismissedDetail())),
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
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    const resolved = renderPage(`/review-cases?selected=${caseId}`);

    expect(await screen.findByText(`Case ${caseId}`)).toBeInTheDocument();
    expect(screen.getByText('Source exact-1')).toBeInTheDocument();
    expect(screen.getByText('Opening note recorded from the matcher')).toBeInTheDocument();
    expect(screen.getByText('Resolution note allocation was already recorded')).toBeInTheDocument();
    expect(screen.getByText('Outcome AllocationRecorded')).toBeInTheDocument();
    expect(screen.getByText(`Allocation ${allocationId}`)).toBeInTheDocument();
    expect(screen.getByText('Opened 2026-11-05T09:00:00Z')).toBeInTheDocument();
    expect(screen.getByText('Closed 2026-11-05T09:30:00Z')).toBeInTheDocument();
    expect(screen.getByText(`Invoice id ${invoiceId}`)).toBeInTheDocument();
    expect(screen.getByText(`Bank transaction id ${bankId}`)).toBeInTheDocument();
    expect(screen.getByText('Invoice outstanding 0.00 PLN')).toBeInTheDocument();
    expect(screen.getByText('Bank transaction unapplied 0.0001 EUR')).toBeInTheDocument();
    expect(screen.queryByText(secretToken)).not.toBeInTheDocument();
    expect(posts).toBe(0);
    resolved.unmount();

    renderPage(`/review-cases?selected=${dismissedCaseId}`);
    expect(await screen.findByText('Resolution note closed without an allocation')).toBeInTheDocument();
    expect(screen.getByText('NeedsInvestigation · Dismissed')).toBeInTheDocument();
    expect(screen.getByText('Outcome none')).toBeInTheDocument();
    expect(screen.getByText('Allocation none')).toBeInTheDocument();
    expect(screen.getByText('Opening note none')).toBeInTheDocument();
    expect(screen.getByText('Closed 2026-11-05T10:00:00Z')).toBeInTheDocument();
    expect(screen.getByText('Invoice id: this case does not name one.')).toBeInTheDocument();
    expect(screen.getByText('Invoice: this case does not name one.')).toBeInTheDocument();
    expect(screen.getByText(`Bank transaction id ${bankId}`)).toBeInTheDocument();
    expect(screen.getByText('Bank transaction: the named document was not found.')).toBeInTheDocument();
    expect(screen.queryByText(secretToken)).not.toBeInTheDocument();
    expect(screen.queryByText('0.00 PLN')).not.toBeInTheDocument();
    expect(posts).toBe(0);
  });

  it('does not open an invalid address', () => {
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    renderPage('/review-cases?page=0&selected=not-a-guid');
    expect(screen.getByText('This review case address can’t be opened. Open Review cases again from the menu.')).toBeInTheDocument();
  });
});

function renderPage(path: string) {
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[path]}>
        <ReviewCasePage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

function text(body: string) {
  return HttpResponse.text(body, { headers: { 'Content-Type': 'application/json' } });
}

function listDocument(): string {
  return `{"items":[${caseDocument()}],"page":1,"pageSize":50,"totalCount":1}`;
}

function detailDocument(invoiceAmount = '0'): string {
  return `{"reviewCase":${caseDocument()},"linkedFinancials":{"invoiceFound":true,"invoiceOutstanding":{"amount":${invoiceAmount},"currency":"PLN"},"bankTransactionFound":true,"bankTransactionUnapplied":{"amount":0.0001,"currency":"EUR"}}}`;
}

function caseDocument(overrides: Record<string, string | null> = {}): string {
  return JSON.stringify({
    reviewCaseId: caseId,
    reason: 'CompetingMatch',
    status: 'Open',
    invoiceId,
    bankTransactionId: bankId,
    sourceLabel: 'manual',
    note: 'watch the zero balance',
    outcome: null,
    resolutionNote: null,
    allocationId: null,
    openedAt: '2026-11-05T08:00:00Z',
    closedAt: null,
    concurrencyToken: secretToken,
    ...overrides,
  });
}

const dismissedCaseId = '55555555-5555-4555-8555-555555555555';

function resolvedDetail(): string {
  return detailFrom(caseDocument({
    status: 'Resolved',
    sourceLabel: 'exact-1',
    note: 'recorded from the matcher',
    outcome: 'AllocationRecorded',
    resolutionNote: 'allocation was already recorded',
    allocationId,
    openedAt: '2026-11-05T09:00:00Z',
    closedAt: '2026-11-05T09:30:00Z',
  }));
}

function dismissedDetail(): string {
  return detailFrom(
    caseDocument({
      reviewCaseId: dismissedCaseId,
      reason: 'NeedsInvestigation',
      status: 'Dismissed',
      invoiceId: null,
      sourceLabel: 'smart-1',
      note: null,
      resolutionNote: 'closed without an allocation',
      openedAt: '2026-11-05T09:15:00Z',
      closedAt: '2026-11-05T10:00:00Z',
    }),
    '{"invoiceFound":false,"invoiceOutstanding":null,"bankTransactionFound":false,"bankTransactionUnapplied":null}',
  );
}

function detailFrom(reviewCase: string, linkedFinancials = '{"invoiceFound":true,"invoiceOutstanding":{"amount":0,"currency":"PLN"},"bankTransactionFound":true,"bankTransactionUnapplied":{"amount":0.0001,"currency":"EUR"}}'): string {
  return `{"reviewCase":${reviewCase},"linkedFinancials":${linkedFinancials}}`;
}

function listQuery(url: string | undefined) {
  const params = new URL(url ?? 'http://localhost/missing').searchParams;
  return {
    status: params.get('status'),
    invoiceId: params.get('invoiceId'),
    bankTransactionId: params.get('bankTransactionId'),
    selected: params.has('selected'),
  };
}

async function chooseStatus(user: ReturnType<typeof userEvent.setup>, status: string): Promise<void> {
  await user.click(screen.getByLabelText('Status'));
  await user.click(screen.getByRole('option', { name: status }));
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
    authorization: capabilities.length > 0 ? 'verified' : 'restricted',
    capabilities,
  };
}

function deferred<T>() {
  let resolve: (value: T) => void = () => {
    throw new Error('The gate was not started.');
  };
  const promise = new Promise<T>((next) => {
    resolve = next;
  });
  return { promise, resolve };
}
