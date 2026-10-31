import { QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { createMemoryRouter, MemoryRouter, RouterProvider } from 'react-router';
import { describe, expect, it } from 'vitest';

import { AppRouter } from '../../app/router';
import { queryClient } from '../../shared/api/query-client';
import { sessionStore } from '../../shared/api/session-store';
import { server } from '../../test/server';
import { AppShell } from '../../shared/ui/AppShell';
import { explicitAmountAttemptStorageKey } from '../reconciliation/allocation-attempt';
import { AllocationHistoryPage } from './AllocationHistoryPage';

const confirmedId = '11111111-1111-4111-8111-111111111111';
const reversedId = '22222222-2222-4222-8222-222222222222';
const invoiceA = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
const invoiceB = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
const bankA = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc';
const bankB = 'dddddddd-dddd-4ddd-8ddd-dddddddddddd';
const batchId = 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee';
const auditId = 'ffffffff-ffff-4fff-8fff-ffffffffffff';
const reversalAuditId = '99999999-9999-4999-8999-999999999999';
const emptyAuditId = '55555555-5555-4555-8555-555555555555';

describe('allocation history', () => {
  it('opens unfiltered history from the main navigation', async () => {
    const user = userEvent.setup();
    const seen: string[] = [];
    server.use(
      http.get('/api/invoices', () => text(emptyInvoiceList())),
      http.get('/api/allocations', ({ request }) => {
        seen.push(new URL(request.url).search);
        return text(historyPage([confirmedItem()], 1));
      }),
    );

    renderApp('/invoices');
    expect(screen.getByRole('link', { name: 'Allocation history' })).toHaveAttribute('href', '/allocations');
    await user.click(screen.getByRole('link', { name: 'Allocation history' }));

    expect(await screen.findByRole('heading', { name: 'Allocation history' })).toBeVisible();
    expect(await screen.findByText('SAME-NO')).toBeVisible();
    expect(seen.some((query) => query.includes('page=1') && query.includes('pageSize=50') && !query.includes('invoiceId') && !query.includes('bankTransactionId'))).toBe(true);
  });

  it('shows confirmed and reversed rows without changing the original amount', async () => {
    server.use(http.get('/api/allocations', () => text(historyPage([
      confirmedItem({ amount: '0.0001' }),
      reversedItem({ amount: '999999999999999.9999' }),
      confirmedItem({
        allocationId: '33333333-3333-4333-8333-333333333333',
        invoiceId: invoiceB,
        bankTransactionId: bankB,
        invoiceNumber: 'SAME-NO',
        reference: 'SAME-REF',
        amount: '10',
      }),
      confirmedItem({
        allocationId: '44444444-4444-4444-8444-444444444444',
        invoiceId: invoiceA,
        bankTransactionId: bankA,
        invoiceNumber: 'NULL-REF',
        reference: null,
        amount: '2',
      }),
    ], 4))));

    renderHistory();

    expect(await screen.findByText('0.0001 PLN')).toBeVisible();
    expect(screen.getByText('999,999,999,999,999.9999 PLN')).toBeVisible();
    expect(screen.getByText('Reversed')).toBeVisible();
    expect(screen.getByText(/2026-10-31T09:30:00\+02:00/)).toBeVisible();
    expect(screen.getByText(/Duplicate payment/)).toBeVisible();
    expect(screen.queryByText(/-999,999,999,999,999.9999/)).not.toBeInTheDocument();
    expect(screen.getAllByText('SAME-NO')).toHaveLength(3);
    expect(screen.getAllByText('SAME-REF')).toHaveLength(3);
    expect(screen.getByText('None')).toBeVisible();
    expect(screen.getByLabelText(`Allocation ${confirmedId}`)).toBeVisible();
    expect(screen.getByLabelText(`Allocation ${reversedId}`)).toBeVisible();
    expect(screen.getAllByLabelText(`Invoice ${invoiceA}`)).toHaveLength(2);
    expect(screen.getAllByLabelText(`Invoice ${invoiceB}`)).toHaveLength(2);
    expect(screen.getAllByText('2026-10-31T12:00:00.123Z').length).toBeGreaterThan(0);
  });

  it('shows an empty organization and an empty filter without fetching every page', async () => {
    const seen: string[] = [];
    server.use(http.get('/api/allocations', ({ request }) => {
      seen.push(new URL(request.url).search);
      return text(historyPage([], 0));
    }));

    const unfiltered = renderHistory();
    expect(await screen.findByText('No allocations are in this organization yet.')).toBeVisible();
    unfiltered.unmount();

    renderHistory(`/allocations?invoiceId=${invoiceA}`);
    expect(await screen.findByText('No allocations match this filter.')).toBeVisible();
    expect(seen.every((query) => !query.includes('page=2'))).toBe(true);
    expect(seen.some((query) => query.includes(`invoiceId=${invoiceA}`) && !query.includes('bankTransactionId'))).toBe(true);
  });

  it('pages on the server, changes page size, and does not loop on an empty later page', async () => {
    const user = userEvent.setup();
    const seen: string[] = [];
    server.use(http.get('/api/allocations', ({ request }) => {
      const url = new URL(request.url);
      seen.push(url.search);
      const page = url.searchParams.get('page');
      const pageSize = url.searchParams.get('pageSize');
      if (page === '3') {
        return text(historyPage([], 60, 3, Number(pageSize)));
      }
      return text(historyPage([
        confirmedItem({ allocationId: page === '2' ? reversedId : confirmedId, invoiceNumber: page === '2' ? 'PAGE-2' : 'PAGE-1' }),
      ], 60, Number(page), Number(pageSize)));
    }));

    renderHistory('/allocations?page=3&pageSize=50');
    expect(await screen.findByText('This page has no allocations. Other pages still contain allocations.')).toBeVisible();
    expect(screen.queryByRole('table', { name: 'Allocation history' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Go to first page' }));
    expect(await screen.findByText('PAGE-1')).toBeVisible();

    await user.click(screen.getByRole('button', { name: 'Go to next page' }));
    expect(await screen.findByText('PAGE-2')).toBeVisible();
    await user.click(screen.getByRole('combobox', { name: 'Rows per page:' }));
    await user.click(screen.getByRole('option', { name: '25' }));
    await screen.findByText('PAGE-1');
    expect(seen.filter((query) => query.includes('page=3'))).toHaveLength(1);
    expect(seen.some((query) => query.includes('page=1') && query.includes('pageSize=25'))).toBe(true);
  });

  it('rejects malformed filters before calling the API and can clear a valid filter', async () => {
    const user = userEvent.setup();
    const seen: string[] = [];
    server.use(http.get('/api/allocations', ({ request }) => {
      seen.push(new URL(request.url).search);
      return text(historyPage([confirmedItem()], 1));
    }));

    const invalid = renderHistory('/allocations?invoiceId=not-a-guid');
    expect(await screen.findByText(/This history address is not valid/)).toBeVisible();
    expect(seen).toHaveLength(0);
    invalid.unmount();

    const empty = renderHistory('/allocations?bankTransactionId=');
    expect(await screen.findByText(/This history address is not valid/)).toBeVisible();
    expect(seen).toHaveLength(0);
    empty.unmount();

    const repeated = renderHistory(`/allocations?invoiceId=${invoiceA}&invoiceId=${invoiceB}`);
    expect(await screen.findByText(/This history address is not valid/)).toBeVisible();
    expect(seen).toHaveLength(0);
    repeated.unmount();

    renderHistory(`/allocations?invoiceId=${invoiceA}&bankTransactionId=${bankA}&page=2`);
    expect(await screen.findByText(`Showing allocations for invoice ${invoiceA} and bank transaction ${bankA}.`)).toBeVisible();
    expect(seen.some((query) => query.includes(`invoiceId=${invoiceA}`) && query.includes(`bankTransactionId=${bankA}`) && query.includes('page=2'))).toBe(true);
    await user.click(screen.getByRole('button', { name: 'Clear filters' }));
    await waitFor(() => {
      expect(seen.some((query) => query.includes('page=1') && !query.includes('invoiceId') && !query.includes('bankTransactionId'))).toBe(true);
    });
    expect(screen.queryByText(/Showing allocations for/)).not.toBeInTheDocument();
  });

  it('keeps filter state across browser back navigation', async () => {
    const user = userEvent.setup();
    const seen: string[] = [];
    server.use(http.get('/api/allocations', ({ request }) => {
      seen.push(new URL(request.url).search);
      return text(historyPage([confirmedItem()], 1));
    }));
    const router = createMemoryRouter(
      [{ path: '/allocations', element: <AllocationHistoryPage /> }],
      { initialEntries: ['/allocations', `/allocations?invoiceId=${invoiceA}`], initialIndex: 1 },
    );
    sessionStore.rotate('memory-token', Math.floor(Date.now() / 1000) + 3600, null);
    render(
      <QueryClientProvider client={queryClient}>
        <RouterProvider router={router} />
      </QueryClientProvider>,
    );

    expect(await screen.findByText(`Showing allocations for invoice ${invoiceA}.`)).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Clear filters' }));
    await screen.findByRole('button', { name: 'Refresh' });
    await router.navigate(-1);
    expect(await screen.findByText(`Showing allocations for invoice ${invoiceA}.`)).toBeVisible();
    expect(seen.some((query) => !query.includes('invoiceId'))).toBe(true);
    expect(seen.filter((query) => query.includes(`invoiceId=${invoiceA}`)).length).toBeGreaterThan(0);
  });

  it('distinguishes unauthorized, forbidden, and retryable failures', async () => {
    const user = userEvent.setup();
    server.use(http.get('/api/allocations', () => new HttpResponse(null, { status: 401 })));
    const unauthorized = renderHistory();
    expect(await screen.findByText(/access has expired or is no longer valid/)).toBeVisible();
    unauthorized.unmount();

    server.use(http.get('/api/allocations', () => new HttpResponse(null, { status: 403 })));
    const forbidden = renderHistory();
    expect(await screen.findByText(/don’t have access to this workspace/)).toBeVisible();
    forbidden.unmount();

    let calls = 0;
    server.use(http.get('/api/allocations', () => {
      calls += 1;
      if (calls === 1) {
        return HttpResponse.error();
      }
      return text(historyPage([confirmedItem()], 1));
    }));
    renderHistory();
    expect(await screen.findByText(/couldn’t connect/)).toBeVisible();
    expect(screen.queryByRole('table', { name: 'Allocation history' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByText('SAME-NO')).toBeVisible();
  });

  it('replaces the previous organization and ignores a late response', async () => {
    let releaseOld: () => void = () => {};
    const oldGate = new Promise<void>((resolve) => {
      releaseOld = resolve;
    });
    const oldBody = historyPage([
      confirmedItem({ invoiceNumber: 'OLD-ORG', amount: '10' }),
    ], 1);
    const newBody = historyPage([
      confirmedItem({ invoiceNumber: 'NEW-ORG', amount: '20' }),
    ], 1);
    let oldRequestBound = false;
    let oldResponseBody = '';
    let oldRequestAborted = false;
    server.use(http.get('/api/allocations', async ({ request }) => {
      const authorization = request.headers.get('authorization');
      if (authorization === 'Bearer memory-token') {
        const responseBody = oldBody;
        oldRequestBound = true;
        await oldGate;
        oldRequestAborted = request.signal.aborted;
        oldResponseBody = responseBody;
        return text(responseBody);
      }

      if (authorization === 'Bearer next-token') {
        return text(newBody);
      }

      return new HttpResponse(null, { status: 500 });
    }));
    renderHistory();
    expect(screen.getByRole('status')).toHaveTextContent('Loading allocation history');
    // Session rotation aborts the in-flight fetch. Wait until MSW has bound the
    // organization A payload, otherwise the handler never runs and the test only
    // observes cancellation. The delayed body is still returned after B is visible.
    await waitFor(() => {
      expect(oldRequestBound).toBe(true);
    });
    sessionStore.rotate('next-token', Math.floor(Date.now() / 1000) + 3600, null);
    expect(await screen.findByText('NEW-ORG')).toBeVisible();
    expect(screen.getByText('20.00 PLN')).toBeVisible();
    expect(screen.queryByText('OLD-ORG')).not.toBeInTheDocument();
    expect(screen.queryByText('10.00 PLN')).not.toBeInTheDocument();

    releaseOld();
    await waitFor(() => {
      expect(oldResponseBody).toContain('"invoiceNumber":"OLD-ORG"');
    });
    expect(oldResponseBody).toContain('"amount":10');
    expect(oldResponseBody).not.toContain('NEW-ORG');
    expect(oldRequestAborted).toBe(true);
    expect(screen.getByText('NEW-ORG')).toBeVisible();
    expect(screen.getByText('20.00 PLN')).toBeVisible();
    expect(screen.queryByText('OLD-ORG')).not.toBeInTheDocument();
    expect(screen.queryByText('10.00 PLN')).not.toBeInTheDocument();
  });

  it('opens filtered history from the invoice and bank transaction registers', async () => {
    const user = userEvent.setup();
    const seen: string[] = [];
    server.use(
      http.get('/api/invoices', () => text(invoiceList())),
      http.get('/api/bank-transactions', () => text(bankList())),
      http.get('/api/allocations', ({ request }) => {
        seen.push(new URL(request.url).search);
        return text(historyPage([confirmedItem()], 1));
      }),
    );

    const invoices = renderApp('/invoices');
    await user.click(await screen.findByRole('button', { name: 'INV-100' }));
    await user.click(screen.getByRole('link', { name: `View allocations for invoice ${invoiceA}` }));
    expect(await screen.findByRole('heading', { name: 'Allocation history' })).toBeVisible();
    expect(seen.some((query) => query === `?page=1&pageSize=50&invoiceId=${invoiceA}` || query.includes(`invoiceId=${invoiceA}`))).toBe(true);
    expect(seen.some((query) => query.includes('bankTransactionId'))).toBe(false);
    invoices.unmount();
    seen.length = 0;

    renderApp('/bank-transactions');
    await user.click(await screen.findByRole('link', { name: `View allocations for bank transaction ${bankA}` }));
    expect(await screen.findByRole('heading', { name: 'Allocation history' })).toBeVisible();
    expect(seen.some((query) => query.includes(`bankTransactionId=${bankA}`) && !query.includes('invoiceId'))).toBe(true);
  });

  it('loads audit only for the expanded allocation and keeps an empty audit empty', async () => {
    const user = userEvent.setup();
    const auditUrls: string[] = [];
    const posts: string[] = [];
    server.use(
      http.get('/api/allocations', () => text(historyPage([
        confirmedItem(),
        reversedItem({ idempotencyOperation: 'batchExactMatch' }),
        confirmedItem({
          allocationId: emptyAuditId,
          invoiceNumber: 'EMPTY-AUDIT',
          reference: 'EMPTY-REF',
          amount: '3',
        }),
      ], 3))),
      http.get('/api/allocations/:allocationId/audit', ({ request, params }) => {
        auditUrls.push(`${params.allocationId}?${new URL(request.url).searchParams.toString()}`);
        if (params.allocationId === emptyAuditId) {
          return text(auditPage([], 0));
        }
        if (params.allocationId === reversedId) {
          return text(auditPage([
            auditEntry({
              allocationId: reversedId,
              invoiceId: invoiceB,
              bankTransactionId: bankB,
              action: 'confirmation',
              operation: 'batchExactMatch',
              actorId: 'batch-actor',
              reason: null,
              recordedAt: '2026-10-31T08:00:00+02:00',
              batchExecutionId: batchId,
            }),
            auditEntry({
              id: reversalAuditId,
              allocationId: reversedId,
              invoiceId: invoiceB,
              bankTransactionId: bankB,
              action: 'reversal',
              operation: 'reversal',
              actorId: 'reversal-actor',
              reason: 'Duplicate payment',
              recordedAt: '2026-10-31T09:30:00+02:00',
              batchExecutionId: null,
            }),
          ], 2));
        }
        return text(auditPage([
          auditEntry({
            action: 'confirmation',
            operation: 'exactMatch',
            actorId: 'exact-actor',
            reason: null,
            recordedAt: '2026-10-31T12:00:00.123Z',
            batchExecutionId: null,
          }),
        ], 1));
      }),
      http.post('/api/allocations', () => {
        posts.push('post');
        return HttpResponse.json({});
      }),
      http.post('/api/allocations/:allocationId/reverse', () => {
        posts.push('reverse');
        return HttpResponse.json({});
      }),
    );
    sessionStorage.setItem(explicitAmountAttemptStorageKey, 'sentinel-attempt');

    renderHistory();
    expect(await screen.findByLabelText(`Allocation ${confirmedId}`)).toBeVisible();
    expect(auditUrls).toHaveLength(0);

    const confirmedRow = screen.getByLabelText(`Allocation ${confirmedId}`).closest('tr');
    if (confirmedRow === null) {
      throw new Error('Missing confirmed row');
    }
    expect(within(confirmedRow).getByRole('button', { name: `Reverse allocation ${confirmedId}` })).toBeVisible();
    await user.click(within(confirmedRow).getByRole('button', { name: 'View audit' }));
    const confirmedAudit = await screen.findByRole('table', { name: `Audit entries for allocation ${confirmedId}` });
    expect(within(confirmedAudit).getByText('exact-actor')).toBeVisible();
    expect(within(confirmedAudit).getByText('Confirmation')).toBeVisible();
    expect(within(confirmedAudit).getByText('Exact match')).toBeVisible();
    expect(within(confirmedAudit).queryByText('Reversal')).not.toBeInTheDocument();
    expect(within(confirmedAudit).queryByLabelText(`Batch ${batchId}`)).not.toBeInTheDocument();
    expect(auditUrls).toEqual([`${confirmedId}?page=1&pageSize=50`]);
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBe('sentinel-attempt');
    expect(posts).toHaveLength(0);

    const reversedRow = screen.getByLabelText(`Allocation ${reversedId}`).closest('tr');
    if (reversedRow === null) {
      throw new Error('Missing reversed row');
    }
    expect(within(reversedRow).queryByRole('button', { name: `Reverse allocation ${reversedId}` })).not.toBeInTheDocument();
    await user.click(within(reversedRow).getByRole('button', { name: 'View audit' }));
    const reversedAudit = await screen.findByRole('table', { name: `Audit entries for allocation ${reversedId}` });
    expect(within(reversedAudit).getByText('Confirmation')).toBeVisible();
    expect(within(reversedAudit).getByText('Batch exact match')).toBeVisible();
    expect(within(reversedAudit).getByText('batch-actor')).toBeVisible();
    expect(within(reversedAudit).getByText('reversal-actor')).toBeVisible();
    expect(within(reversedAudit).getAllByText('Reversal')).toHaveLength(2);
    expect(within(reversedAudit).getByLabelText(`Batch ${batchId}`)).toBeVisible();
    const reversedAuditText = reversedAudit.textContent ?? '';
    const confirmedAt = reversedAuditText.indexOf('2026-10-31T08:00:00+02:00');
    const reversedAt = reversedAuditText.indexOf('2026-10-31T09:30:00+02:00');
    expect(confirmedAt).toBeGreaterThanOrEqual(0);
    expect(reversedAt).toBeGreaterThan(confirmedAt);
    expect(screen.queryByText('exact-actor')).not.toBeInTheDocument();
    expect(auditUrls).toEqual([
      `${confirmedId}?page=1&pageSize=50`,
      `${reversedId}?page=1&pageSize=50`,
    ]);

    const emptyRow = screen.getByLabelText(`Allocation ${emptyAuditId}`).closest('tr');
    if (emptyRow === null) {
      throw new Error('Missing empty audit row');
    }
    await user.click(within(emptyRow).getByRole('button', { name: 'View audit' }));
    expect(await screen.findByText('No audit entries were found for this allocation.')).toBeVisible();
    expect(screen.queryByText('batch-actor')).not.toBeInTheDocument();
    expect(screen.queryByText('reversal-actor')).not.toBeInTheDocument();
    expect(auditUrls).toEqual([
      `${confirmedId}?page=1&pageSize=50`,
      `${reversedId}?page=1&pageSize=50`,
      `${emptyAuditId}?page=1&pageSize=50`,
    ]);
  });

  it('pages audit results and reports missing, forbidden, and unauthorized audit reads', async () => {
    const user = userEvent.setup();
    const pages: string[] = [];
    server.use(http.get('/api/allocations', () => text(historyPage([confirmedItem()], 1))));
    server.use(http.get('/api/allocations/:allocationId/audit', ({ request }) => {
      const page = new URL(request.url).searchParams.get('page');
      pages.push(page ?? '');
      if (page === '2') {
        return text(auditPage([auditEntry({ actorId: 'page-two-actor' })], 51, 2));
      }
      return text(auditPage([auditEntry()], 51, 1));
    }));
    const paged = renderHistory();
    await user.click(await screen.findByRole('button', { name: 'View audit' }));
    expect(await screen.findByText('audit-actor')).toBeVisible();
    const audit = screen.getByRole('region', { name: `Audit for allocation ${confirmedId}` });
    await user.click(within(audit).getByRole('button', { name: 'Go to next page' }));
    expect(await screen.findByText('page-two-actor')).toBeVisible();
    expect(pages).toEqual(['1', '2']);
    paged.unmount();

    server.use(http.get('/api/allocations/:allocationId/audit', () => new HttpResponse(null, { status: 404 })));
    const missing = renderHistory();
    await user.click(await screen.findByRole('button', { name: 'View audit' }));
    expect(await screen.findByText('This allocation was not found.')).toBeVisible();
    missing.unmount();

    server.use(http.get('/api/allocations/:allocationId/audit', () => new HttpResponse(null, { status: 401 })));
    const unauthorized = renderHistory();
    await user.click(await screen.findByRole('button', { name: 'View audit' }));
    expect(await screen.findByText(/access has expired or is no longer valid/)).toBeVisible();
    unauthorized.unmount();

    server.use(http.get('/api/allocations/:allocationId/audit', () => new HttpResponse(null, { status: 403 })));
    renderHistory();
    await user.click(await screen.findByRole('button', { name: 'View audit' }));
    expect(await screen.findByText(/don’t have access to this workspace/)).toBeVisible();
  });
});

function renderHistory(path = '/allocations') {
  sessionStore.rotate('memory-token', Math.floor(Date.now() / 1000) + 3600, null);
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[path]}>
        <AllocationHistoryPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

function renderApp(path: string) {
  sessionStore.rotate('memory-token', Math.floor(Date.now() / 1000) + 3600, null);
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[path]}>
        <AppShell onLogout={() => undefined} onReplace={() => undefined}>
          <AppRouter />
        </AppShell>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

function text(body: string) {
  return HttpResponse.text(body, { headers: { 'Content-Type': 'application/json' } });
}

function historyPage(items: string[], totalCount: number, page = 1, pageSize = 50) {
  return `{"items":[${items.join(',')}],"page":${page},"pageSize":${pageSize},"totalCount":${totalCount}}`;
}

function confirmedItem(overrides: Partial<{
  allocationId: string;
  invoiceId: string;
  bankTransactionId: string;
  invoiceNumber: string;
  reference: string | null;
  amount: string;
  confirmedAt: string;
}> = {}) {
  const reference = overrides.reference === undefined ? 'SAME-REF' : overrides.reference;
  return `{
    "allocationId":"${overrides.allocationId ?? confirmedId}",
    "invoiceId":"${overrides.invoiceId ?? invoiceA}",
    "bankTransactionId":"${overrides.bankTransactionId ?? bankA}",
    "invoiceNumber":"${overrides.invoiceNumber ?? 'SAME-NO'}",
    "reference":${reference === null ? 'null' : `"${reference}"`},
    "amount":{"amount":${overrides.amount ?? '15.5'},"currency":"PLN"},
    "status":"confirmed",
    "confirmedAt":"${overrides.confirmedAt ?? '2026-10-31T12:00:00.123Z'}",
    "reversedAt":null,
    "reversalReason":null,
    "idempotencyOperation":"exactMatch"
  }`;
}

function reversedItem(overrides: Partial<{
  allocationId: string;
  amount: string;
  idempotencyOperation: string | null;
}> = {}) {
  const operation = overrides.idempotencyOperation === undefined ? null : overrides.idempotencyOperation;
  return `{
    "allocationId":"${overrides.allocationId ?? reversedId}",
    "invoiceId":"${invoiceB}",
    "bankTransactionId":"${bankB}",
    "invoiceNumber":"SAME-NO",
    "reference":"SAME-REF",
    "amount":{"amount":${overrides.amount ?? '15.5'},"currency":"PLN"},
    "status":"reversed",
    "confirmedAt":"2026-10-31T08:00:00+02:00",
    "reversedAt":"2026-10-31T09:30:00+02:00",
    "reversalReason":"Duplicate payment",
    "idempotencyOperation":${operation === null ? 'null' : `"${operation}"`}
  }`;
}

function auditPage(items: string[], totalCount: number, page = 1) {
  return `{"items":[${items.join(',')}],"page":${page},"pageSize":50,"totalCount":${totalCount}}`;
}

function auditEntry(overrides: Partial<{
  id: string;
  allocationId: string;
  invoiceId: string;
  bankTransactionId: string;
  action: string;
  operation: string;
  actorId: string;
  reason: string | null;
  recordedAt: string;
  batchExecutionId: string | null;
}> = {}) {
  const reason = overrides.reason === undefined ? null : overrides.reason;
  const batch = overrides.batchExecutionId === undefined ? null : overrides.batchExecutionId;
  return `{
    "id":"${overrides.id ?? auditId}",
    "allocationId":"${overrides.allocationId ?? confirmedId}",
    "invoiceId":"${overrides.invoiceId ?? invoiceA}",
    "bankTransactionId":"${overrides.bankTransactionId ?? bankA}",
    "batchExecutionId":${batch === null ? 'null' : `"${batch}"`},
    "action":"${overrides.action ?? 'confirmation'}",
    "operation":"${overrides.operation ?? 'exactMatch'}",
    "actorId":"${overrides.actorId ?? 'audit-actor'}",
    "amount":{"amount":15.5,"currency":"PLN"},
    "recordedAt":"${overrides.recordedAt ?? '2026-10-31T12:00:00.123Z'}",
    "reason":${reason === null ? 'null' : `"${reason}"`}
  }`;
}

function emptyInvoiceList() {
  return '{"items":[],"page":1,"pageSize":50,"totalCount":0}';
}

function invoiceList() {
  return `{"items":[{
    "id":"${invoiceA}",
    "invoiceNumber":"INV-100",
    "totalAmount":{"amount":10,"currency":"PLN"},
    "allocatedAmount":{"amount":0,"currency":"PLN"},
    "outstandingAmount":{"amount":10,"currency":"PLN"}
  }],"page":1,"pageSize":50,"totalCount":1}`;
}

function bankList() {
  return `{"items":[{
    "id":"${bankA}",
    "bookingDate":"2026-08-24",
    "reference":"PAY-1",
    "amount":{"amount":10,"currency":"PLN"},
    "allocatedAmount":{"amount":0,"currency":"PLN"},
    "unappliedAmount":{"amount":10,"currency":"PLN"},
    "sourceSystem":null,
    "externalId":null
  }],"page":1,"pageSize":50,"totalCount":1}`;
}
