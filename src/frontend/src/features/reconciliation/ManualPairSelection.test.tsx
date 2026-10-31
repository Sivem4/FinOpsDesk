import { QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router';
import { describe, expect, it, vi } from 'vitest';

import { queryClient } from '../../shared/api/query-client';
import { sessionStore } from '../../shared/api/session-store';
import { setAccessToken } from '../../shared/auth/session';
import { server } from '../../test/server';
import { explicitAmountAttemptStorageKey } from './allocation-attempt';
import { ReconciliationQueuePage } from './ReconciliationQueuePage';

const organizationA = '11111111-1111-1111-1111-111111111111';
const organizationB = '22222222-2222-2222-2222-222222222222';
const invoiceA = 'bbbbbbbb-1111-1111-1111-111111111111';
const invoiceB = 'bbbbbbbb-2222-2222-2222-222222222222';
const invoiceZero = 'bbbbbbbb-3333-3333-3333-333333333333';
const invoicePage = 'bbbbbbbb-4444-4444-4444-444444444444';
const invoiceSearch = 'bbbbbbbb-5555-5555-5555-555555555555';
const bankNull = 'cccccccc-1111-1111-1111-111111111111';
const bankPay = 'cccccccc-2222-2222-2222-222222222222';
const bankSearch = 'cccccccc-3333-3333-3333-333333333333';
const allocationId = 'eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee';

describe('manual pair selection', () => {
  it('stays available when matching data is missing and does not ask Smart-1', async () => {
    const user = userEvent.setup();
    let smartCalls = 0;
    server.use(
      http.get('/api/matches', () => problem(500, 'reconciliation.matches.failed', 'Matches failed.', 'The book could not be read.')),
      http.get('/api/invoices', () => json({ items: [], page: 1, pageSize: 25, totalCount: 0 })),
      http.get('/api/bank-transactions', () => json({ items: [], page: 1, pageSize: 25, totalCount: 0 })),
      http.get('/api/matches/smart', () => {
        smartCalls += 1;
        return json(emptySmart());
      }),
    );
    renderQueue();

    expect(await screen.findByRole('button', { name: 'Choose records' })).toBeVisible();
    expect(await screen.findByText(/could not be loaded/)).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Choose records' }));
    expect(smartCalls).toBe(0);
  });

  it('browses, searches, and keeps the two selections independent', async () => {
    const user = userEvent.setup();
    const invoiceUrls: URL[] = [];
    const bankUrls: URL[] = [];
    const posts: string[] = [];
    const randomUUID = vi.spyOn(crypto, 'randomUUID');
    server.use(
      http.get('/api/matches', () => json(emptyExact())),
      http.get('/api/invoices', ({ request }) => {
        const url = new URL(request.url);
        invoiceUrls.push(url);
        return json(invoicePageFor(url));
      }),
      http.get('/api/bank-transactions', ({ request }) => {
        const url = new URL(request.url);
        bankUrls.push(url);
        return json(bankPageFor(url));
      }),
      http.post('/api/allocations', () => {
        posts.push('POST');
        return allocationResult(201, false, '10');
      }),
    );

    try {
      renderQueue();
      await screen.findByText(/Nothing is open to classify/);
      await user.click(screen.getByRole('button', { name: 'Choose records' }));

      const invoices = await screen.findByRole('region', { name: 'Invoices' });
      const banks = screen.getByRole('region', { name: 'Bank transactions' });
      expect(within(invoices).getByRole('button', { name: selectInvoice('INV-DUP', invoiceA) })).toBeVisible();
      expect(within(invoices).getByRole('button', { name: selectInvoice('INV-DUP', invoiceB) })).toBeVisible();
      expect(within(invoices).getByRole('button', { name: selectInvoice('INV-ZERO', invoiceZero) })).toBeVisible();
      expect(within(invoices).getByText('0.00 PLN')).toBeVisible();
      expect(within(banks).getByRole('button', { name: selectBank('without a reference', bankNull) })).toBeVisible();
      expect(within(banks).getAllByText('None').length).toBeGreaterThan(0);
      expect(within(banks).getByText('DEMO · ext-null')).toBeVisible();
      expect(screen.getByText(/do not reserve/)).toBeVisible();

      const invoiceCount = invoiceUrls.length;
      await user.type(screen.getByRole('textbox', { name: 'Invoice number' }), 'INV');
      expect(invoiceUrls).toHaveLength(invoiceCount);
      expect(invoiceUrls.every((url) => !url.searchParams.has('invoiceNumber'))).toBe(true);

      await user.click(within(invoices).getByRole('button', { name: selectInvoice('INV-DUP', invoiceB) }));
      expect(within(invoices).getByRole('button', { name: selectInvoice('INV-DUP', invoiceB), pressed: true })).toBeVisible();
      await user.click(within(banks).getByRole('button', { name: selectBank('without a reference', bankNull) }));
      expect(screen.getByText('Invoice INV-DUP, outstanding 10.00 PLN.')).toBeVisible();
      expect(screen.getByText('Bank reference None, unapplied 5.00 PLN.')).toBeVisible();

      await user.click(within(invoices).getByRole('button', { name: 'Go to next page' }));
      await user.click(await screen.findByRole('button', { name: selectInvoice('INV-PAGE', invoicePage) }));
      expect(screen.getByText('Invoice INV-PAGE, outstanding 15.00 PLN.')).toBeVisible();
      expect(screen.getByText('Bank reference None, unapplied 5.00 PLN.')).toBeVisible();

      await user.click(within(invoices).getByRole('combobox', { name: 'Rows per page:' }));
      await user.click(screen.getByRole('option', { name: '50' }));
      expect(await within(invoices).findByRole('button', { name: selectInvoice('INV-DUP', invoiceA) })).toBeVisible();
      expect(invoiceUrls.some((url) => url.searchParams.get('page') === '1' && url.searchParams.get('pageSize') === '50')).toBe(true);
      expect(screen.getByText('Invoice INV-PAGE, outstanding 15.00 PLN.')).toBeVisible();

      await user.type(screen.getByRole('textbox', { name: 'Payment reference' }), '  PAY-9  ');
      await user.click(screen.getByRole('button', { name: 'Search bank transactions' }));
      await user.click(await screen.findByRole('button', { name: selectBank('PAY-9', bankSearch) }));
      expect(bankUrls.some((url) => url.searchParams.get('reference') === 'PAY-9')).toBe(true);
      expect(screen.getByText('Invoice INV-PAGE, outstanding 15.00 PLN.')).toBeVisible();
      expect(screen.getByText('Bank reference PAY-9, unapplied 40.00 PLN.')).toBeVisible();

      await user.click(screen.getByRole('button', { name: 'Close' }));
      expect(screen.queryByRole('heading', { name: 'Choose records' })).not.toBeInTheDocument();
      await user.click(screen.getByRole('button', { name: 'Choose records' }));
      expect(screen.getByText('Invoice INV-PAGE, outstanding 15.00 PLN.')).toBeVisible();
      await user.click(screen.getByRole('button', { name: 'Clear selected invoice' }));
      expect(screen.getByText('No invoice selected.')).toBeVisible();
      expect(screen.getByText('Bank reference PAY-9, unapplied 40.00 PLN.')).toBeVisible();
      await user.click(screen.getByRole('button', { name: 'Clear selected bank transaction' }));
      expect(screen.getByText('No bank transaction selected.')).toBeVisible();

      await user.clear(screen.getByRole('textbox', { name: 'Invoice number' }));
      await user.type(screen.getByRole('textbox', { name: 'Invoice number' }), '  Inv-Case  ');
      await user.click(screen.getByRole('button', { name: 'Search invoices' }));
      expect(await screen.findByRole('button', { name: selectInvoice('Inv-Case', invoiceSearch) })).toBeVisible();
      expect(invoiceUrls.some((url) => url.searchParams.get('invoiceNumber') === 'Inv-Case')).toBe(true);

      await user.clear(screen.getByRole('textbox', { name: 'Invoice number' }));
      await user.click(screen.getByRole('button', { name: 'Search invoices' }));
      expect(await screen.findByRole('button', { name: selectInvoice('INV-DUP', invoiceA) })).toBeVisible();
      expect(invoiceUrls.every((url) => url.searchParams.get('invoiceNumber') !== '')).toBe(true);

      const beforeLong = invoiceUrls.length;
      await user.click(screen.getByRole('textbox', { name: 'Invoice number' }));
      await user.paste('a'.repeat(257));
      await user.click(screen.getByRole('button', { name: 'Search invoices' }));
      expect(screen.getByText('Use 1 to 256 characters.')).toBeVisible();
      expect(invoiceUrls).toHaveLength(beforeLong);

      await user.clear(screen.getByRole('textbox', { name: 'Invoice number' }));
      await user.type(screen.getByRole('textbox', { name: 'Invoice number' }), 'MISSING');
      await user.click(screen.getByRole('button', { name: 'Search invoices' }));
      expect(await screen.findByText('No invoices match that number.')).toBeVisible();
      expect(posts).toEqual([]);
      expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBeNull();
      expect(randomUUID).not.toHaveBeenCalled();
    } finally {
      randomUUID.mockRestore();
    }
  }, 15_000);

  it('reports an empty later page, a rejected search, and a list failure that can be retried', async () => {
    const user = userEvent.setup();
    let fail = false;
    server.use(
      http.get('/api/matches', () => json(emptyExact())),
      http.get('/api/invoices', ({ request }) => {
        const url = new URL(request.url);
        if (fail) {
          return problem(500, 'reconciliation.invoices.failed', 'Invoices failed.', 'The list failed.');
        }
        if (url.searchParams.get('invoiceNumber') === 'BAD') {
          return problem(400, 'reconciliation.invoices.invalid_invoice_number', 'Invalid invoice number.', 'The invoice number is invalid.');
        }
        if (url.searchParams.get('page') === '2') {
          return json({ items: [], page: 2, pageSize: 25, totalCount: 26 });
        }
        return json({ items: [invoice(invoiceA, 'INV-100', '100', '40', '60')], page: 1, pageSize: 25, totalCount: 26 });
      }),
      http.get('/api/bank-transactions', () => json({
        items: [bank(bankPay, 'PAY-9', '80', '40', '40', 'DEMO', 'ext-1')],
        page: 1,
        pageSize: 25,
        totalCount: 1,
      })),
    );
    renderQueue();
    await screen.findByText(/Nothing is open to classify/);
    await user.click(screen.getByRole('button', { name: 'Choose records' }));
    const invoices = await screen.findByRole('region', { name: 'Invoices' });
    await user.click(within(invoices).getByRole('button', { name: 'Go to next page' }));
    expect(await screen.findByText('This page has no records.')).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Go to first page' }));
    expect(await screen.findByRole('button', { name: selectInvoice('INV-100', invoiceA) })).toBeVisible();

    await user.type(screen.getByRole('textbox', { name: 'Invoice number' }), 'BAD');
    await user.click(screen.getByRole('button', { name: 'Search invoices' }));
    expect(await screen.findByText('The search was rejected.')).toBeVisible();

    fail = true;
    await user.click(screen.getByRole('button', { name: 'Clear invoice search' }));
    expect(await screen.findByText('The list could not be loaded.')).toBeVisible();
    fail = false;
    await user.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByRole('button', { name: selectInvoice('INV-100', invoiceA) })).toBeVisible();

    server.use(http.get('/api/bank-transactions', () => problem(403, 'forbidden', 'Forbidden', 'This organization cannot read these records.')));
    await user.type(screen.getByRole('textbox', { name: 'Payment reference' }), 'X');
    await user.click(screen.getByRole('button', { name: 'Search bank transactions' }));
    expect(await screen.findByText('These records are not available for this organization.')).toBeVisible();

    server.use(http.get('/api/invoices', () => problem(401, 'unauthorized', 'Unauthorized', 'Sign in again.')));
    await user.type(screen.getByRole('textbox', { name: 'Invoice number' }), 'AGAIN');
    await user.click(screen.getByRole('button', { name: 'Search invoices' }));
    expect(await screen.findByText('Your access has expired or is no longer valid.')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Update access' })).toBeVisible();
  });

  it('hides retained rows after a failed refetch and restores them after retry', async () => {
    const user = userEvent.setup();
    let invoiceMode: 'ok' | 'error' = 'ok';
    let bankMode: 'ok' | 'forbidden' = 'ok';
    server.use(
      http.get('/api/matches', () => json(emptyExact())),
      http.get('/api/invoices', () => invoiceMode === 'error'
        ? problem(500, 'reconciliation.invoices.failed', 'Invoices failed.', 'The list failed.')
        : json({ items: [invoice(invoiceA, 'INV-KEEP', '100', '40', '60')], page: 1, pageSize: 25, totalCount: 1 })),
      http.get('/api/bank-transactions', () => bankMode === 'forbidden'
        ? problem(403, 'forbidden', 'Forbidden', 'This organization cannot read these records.')
        : json({ items: [bank(bankPay, 'PAY-9', '80', '40', '40', 'DEMO', 'ext-1')], page: 1, pageSize: 25, totalCount: 1 })),
    );
    renderQueue();
    await screen.findByText(/Nothing is open to classify/);
    await user.click(screen.getByRole('button', { name: 'Choose records' }));
    await user.click(await screen.findByRole('button', { name: selectInvoice('INV-KEEP', invoiceA) }));
    await user.click(screen.getByRole('button', { name: selectBank('PAY-9', bankPay) }));
    expect(screen.getByText('Invoice INV-KEEP, outstanding 60.00 PLN.')).toBeVisible();
    expect(screen.getByText('Bank reference PAY-9, unapplied 40.00 PLN.')).toBeVisible();

    const epoch = sessionStore.getSnapshot().epoch;
    invoiceMode = 'error';
    await queryClient.invalidateQueries({ queryKey: ['invoices', epoch, 1, 25, ''] }).catch(() => undefined);
    expect(await screen.findByText('The list could not be loaded.')).toBeVisible();
    expect(queryClient.getQueryData(['invoices', epoch, 1, 25, ''])).toMatchObject({
      items: [expect.objectContaining({ id: invoiceA, invoiceNumber: 'INV-KEEP' })],
    });
    expect(screen.queryByRole('table', { name: 'Invoices' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: selectInvoice('INV-KEEP', invoiceA) })).not.toBeInTheDocument();
    expect(screen.getByText('Invoice INV-KEEP, outstanding 60.00 PLN.')).toBeVisible();
    expect(screen.getByRole('button', { name: selectBank('PAY-9', bankPay) })).toBeVisible();
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBeNull();

    invoiceMode = 'ok';
    await user.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByRole('button', { name: selectInvoice('INV-KEEP', invoiceA) })).toBeVisible();

    bankMode = 'forbidden';
    await queryClient.invalidateQueries({ queryKey: ['bank-transactions', epoch, 1, 25, ''] }).catch(() => undefined);
    expect(await screen.findByText('These records are not available for this organization.')).toBeVisible();
    expect(queryClient.getQueryData(['bank-transactions', epoch, 1, 25, ''])).toMatchObject({
      items: [expect.objectContaining({ id: bankPay, reference: 'PAY-9' })],
    });
    expect(screen.queryByRole('table', { name: 'Bank transactions' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: selectBank('PAY-9', bankPay) })).not.toBeInTheDocument();
    expect(screen.getByText('Bank reference PAY-9, unapplied 40.00 PLN.')).toBeVisible();
    expect(screen.getByRole('button', { name: selectInvoice('INV-KEEP', invoiceA) })).toBeVisible();
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBeNull();
  });

  it('reviews the selected ids with an empty amount and allocates a partial amount and the maximum', async () => {
    const user = userEvent.setup();
    const calls: string[] = [];
    const posts: Posted[] = [];
    server.use(
      matches(),
      lists(),
      banks(),
      http.get('/api/allocations/context', ({ request }) => {
        const url = new URL(request.url);
        calls.push(`${url.searchParams.get('invoiceId')}|${url.searchParams.get('bankTransactionId')}`);
        return json(contextBody({
          invoiceId: url.searchParams.get('invoiceId') ?? invoiceA,
          bankTransactionId: url.searchParams.get('bankTransactionId') ?? bankPay,
        }));
      }),
      http.post('/api/allocations', async ({ request }) => {
        posts.push(await posted(request));
        const body = JSON.parse(posts[posts.length - 1]?.body ?? '{}') as { amount?: { amount?: number } };
        return allocationResult(201, false, String(body.amount?.amount));
      }),
    );
    renderQueue();
    await choosePair(user);

    expect(await screen.findByRole('heading', { name: 'Allocate an amount' })).toBeVisible();
    expect(screen.getByLabelText('Allocation amount')).toHaveValue('');
    expect(screen.getByText(/Choosing both records does not authorize/)).toBeVisible();
    expect(screen.queryByText(/suggestion score/)).not.toBeInTheDocument();
    expect(screen.queryByText(/starting value/)).not.toBeInTheDocument();
    expect(screen.queryByText(/points/)).not.toBeInTheDocument();
    expect(calls).toEqual([`${invoiceA}|${bankPay}`]);

    const amount = screen.getByLabelText('Allocation amount');
    await user.type(amount, '10.50');
    await user.click(screen.getByRole('button', { name: 'Allocate this amount' }));
    expect(await screen.findByText('Allocation recorded.')).toBeVisible();
    expect(posts[0]?.body).toContain('"amount":10.5');
    expect(posts[0]?.key).toMatch(/^[0-9a-f-]{36}$/i);
    await user.click(screen.getByRole('button', { name: 'Close' }));
    await user.click(screen.getByRole('button', { name: 'Done' }));

    await choosePair(user);
    const second = await screen.findByLabelText('Allocation amount');
    await user.clear(second);
    await user.type(second, '40');
    await user.click(screen.getByRole('button', { name: 'Allocate this amount' }));
    expect(await screen.findByText('Allocation recorded.')).toBeVisible();
    expect(posts[1]?.body).toContain('"amount":40');
    expect(posts[1]?.key).not.toBe(posts[0]?.key);
  });

  it('blocks a new allocation for a currency mismatch or a zero balance', async () => {
    const user = userEvent.setup();
    server.use(
      matches(),
      lists(),
      banks(),
      http.get('/api/allocations/context', () => json(contextBody({ bankCurrency: 'USD' }))),
    );
    const mismatch = renderQueue();
    await choosePair(user);
    expect(await screen.findByText(/does not convert currency/)).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Allocate this amount' })).not.toBeInTheDocument();
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBeNull();
    mismatch.unmount();

    server.use(http.get('/api/allocations/context', () => json(contextBody({ outstanding: 0, unapplied: 40 }))));
    renderQueue();
    await choosePair(user);
    expect(await screen.findByText(/no remaining amount on the invoice/)).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Allocate this amount' })).not.toBeInTheDocument();
  });

  it('does not start an allocation when the context read fails or a refresh keeps stale data', async () => {
    const user = userEvent.setup();
    const posts: string[] = [];
    let failRefresh = true;
    const randomUUID = vi.spyOn(crypto, 'randomUUID');
    server.use(
      matches(),
      lists(),
      banks(),
      http.get('/api/allocations/context', () => failRefresh
        ? problem(500, 'reconciliation.allocation_context.inconsistent_money', 'Stored financial amounts are inconsistent.', 'The stored money is inconsistent.')
        : json(contextBody())),
      http.post('/api/allocations', () => {
        posts.push('POST');
        return allocationResult(201, false, '10');
      }),
    );
    const failed = renderQueue();
    try {
      await choosePair(user);
      expect(await screen.findByText(/Stored amounts for this pair are inconsistent/)).toBeVisible();
      expect(screen.queryByRole('button', { name: 'Allocate this amount' })).not.toBeInTheDocument();
      expect(posts).toEqual([]);
      expect(randomUUID).not.toHaveBeenCalled();
      failed.unmount();

      failRefresh = false;
      renderQueue();
      await choosePair(user);
      expect(await screen.findByText('Invoice INV-100')).toBeVisible();
      failRefresh = true;
      await user.click(screen.getByRole('button', { name: 'Refresh balances' }));
      expect(await screen.findByText(/Stored amounts for this pair are inconsistent/)).toBeVisible();
      const epoch = sessionStore.getSnapshot().epoch;
      expect(queryClient.getQueryData(['allocations', 'context', epoch, invoiceA, bankPay])).toMatchObject({
        invoice: { invoiceNumber: 'INV-100' },
      });
      expect(screen.queryByRole('button', { name: 'Allocate this amount' })).not.toBeInTheDocument();
      expect(posts).toEqual([]);
      expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBeNull();
    } finally {
      randomUUID.mockRestore();
    }
  });

  it('does not allocate from an earlier pair while the next context is loading', async () => {
    const user = userEvent.setup();
    const posts: Posted[] = [];
    let releaseFirst: () => void = () => {};
    let releaseSecond: () => void = () => {};
    const firstGate = new Promise<void>((resolve) => {
      releaseFirst = resolve;
    });
    const secondGate = new Promise<void>((resolve) => {
      releaseSecond = resolve;
    });
    const randomUUID = vi.spyOn(crypto, 'randomUUID');
    server.use(
      matches(),
      http.get('/api/invoices', () => json({
        items: [
          invoice(invoiceA, 'INV-A', '100', '40', '60'),
          invoice(invoiceB, 'INV-B', '100', '40', '60'),
        ],
        page: 1,
        pageSize: 25,
        totalCount: 2,
      })),
      http.get('/api/bank-transactions', () => json({
        items: [bank(bankPay, 'PAY-9', '80', '40', '40', 'DEMO', 'ext-1')],
        page: 1,
        pageSize: 25,
        totalCount: 1,
      })),
      http.get('/api/allocations/context', async ({ request }) => {
        const url = new URL(request.url);
        const invoiceId = url.searchParams.get('invoiceId');
        if (invoiceId === invoiceA) {
          await firstGate;
          return json(contextBody({ invoiceId: invoiceA, invoiceNumber: 'INV-A' }));
        }
        await secondGate;
        return json(contextBody({ invoiceId: invoiceB, invoiceNumber: 'INV-B' }));
      }),
      http.post('/api/allocations', async ({ request }) => {
        const entry = await posted(request);
        posts.push(entry);
        const parsed = JSON.parse(entry.body) as { invoiceId?: string; bankTransactionId?: string; amount?: { amount?: number } };
        return allocationResult(201, false, String(parsed.amount?.amount ?? 10), parsed.invoiceId ?? invoiceB, parsed.bankTransactionId ?? bankPay);
      }),
    );

    try {
      renderQueue();
      await screen.findByText(/Nothing is open to classify/);
      await user.click(screen.getByRole('button', { name: 'Choose records' }));
      await user.click(await screen.findByRole('button', { name: selectInvoice('INV-A', invoiceA) }));
      await user.click(screen.getByRole('button', { name: selectBank('PAY-9', bankPay) }));
      await user.click(screen.getByRole('button', { name: 'Review balances' }));
      expect(await screen.findByText('Loading current balances')).toBeVisible();
      await user.click(screen.getByRole('button', { name: 'Close' }));
      await user.click(screen.getByRole('button', { name: 'Choose records' }));
      await user.click(screen.getByRole('button', { name: selectInvoice('INV-B', invoiceB) }));
      await user.click(screen.getByRole('button', { name: 'Review balances' }));
      expect(await screen.findByText('Loading current balances')).toBeVisible();
      expect(screen.queryByText('Invoice INV-A')).not.toBeInTheDocument();
      releaseFirst();
      await waitFor(() => expect(screen.queryByText('Invoice INV-A')).not.toBeInTheDocument());
      expect(screen.queryByRole('button', { name: 'Allocate this amount' })).not.toBeInTheDocument();
      expect(posts).toEqual([]);
      expect(randomUUID).not.toHaveBeenCalled();
      releaseSecond();
      expect(await screen.findByText('Invoice INV-B')).toBeVisible();
      await user.type(screen.getByLabelText('Allocation amount'), '10');
      await user.click(screen.getByRole('button', { name: 'Allocate this amount' }));
      expect(await screen.findByText('Allocation recorded.')).toBeVisible();
      expect(posts[0]?.body).toContain(invoiceB);
      expect(posts[0]?.body).not.toContain(invoiceA);
    } finally {
      randomUUID.mockRestore();
    }
  });

  it('opens recovery for an unresolved attempt and retries the original request', async () => {
    const user = userEvent.setup();
    const posts: Posted[] = [];
    const randomUUID = vi.spyOn(crypto, 'randomUUID');
    sessionStorage.setItem(explicitAmountAttemptStorageKey, JSON.stringify(storedAttempt()));
    server.use(
      http.get('/api/matches', () => json(emptyExact())),
      http.get('/api/invoices', () => json({ items: [], page: 1, pageSize: 25, totalCount: 0 })),
      http.get('/api/bank-transactions', () => json({ items: [], page: 1, pageSize: 25, totalCount: 0 })),
      http.get('/api/allocations/context', () => problem(500, 'reconciliation.allocation_context.inconsistent_money', 'Stored financial amounts are inconsistent.', 'The stored money is inconsistent.')),
      http.post('/api/allocations', async ({ request }) => {
        posts.push(await posted(request));
        return allocationResult(200, true, '25');
      }),
    );

    try {
      renderQueue();
      expect(await screen.findByText('An allocation attempt is stored for this organization')).toBeVisible();
      expect(posts).toEqual([]);
      await user.click(screen.getByRole('button', { name: 'Choose records' }));
      expect(await screen.findByRole('heading', { name: 'Recover allocation attempt' })).toBeVisible();
      expect(screen.queryByRole('heading', { name: 'Choose records' })).not.toBeInTheDocument();
      expect(screen.queryByRole('region', { name: 'Invoices' })).not.toBeInTheDocument();
      expect(await screen.findByText(/You can still send the original stored attempt/)).toBeVisible();
      expect(randomUUID).not.toHaveBeenCalled();
      await user.click(screen.getByRole('button', { name: 'Send the same allocation again' }));
      expect(await screen.findByText('This allocation was already recorded.')).toBeVisible();
      expect(posts).toHaveLength(1);
      expect(posts[0]?.key).toBe('99999999-9999-9999-9999-999999999999');
      expect(posts[0]?.body).toContain(`"invoiceId":"${invoiceA}"`);
      expect(randomUUID).not.toHaveBeenCalled();
    } finally {
      randomUUID.mockRestore();
    }
  });

  it('keeps a lost manual attempt when the dialog closes and removes it only after confirmation', async () => {
    const user = userEvent.setup();
    const posts: Posted[] = [];
    let zero = false;
    let postMode: 'fail' | 'ok' = 'fail';
    server.use(
      matches(),
      lists(),
      banks(),
      http.get('/api/allocations/context', () => json(zero ? contextBody({ outstanding: 0, unapplied: 0 }) : contextBody())),
      http.post('/api/allocations', async ({ request }) => {
        posts.push(await posted(request));
        if (postMode === 'fail') {
          return HttpResponse.error();
        }
        return allocationResult(200, true, '10');
      }),
    );
    renderQueue();
    await choosePair(user);
    await user.type(await screen.findByLabelText('Allocation amount'), '10');
    await user.click(screen.getByRole('button', { name: 'Allocate this amount' }));
    expect(await screen.findByText(/before a result was received/)).toBeVisible();
    const stored = sessionStorage.getItem(explicitAmountAttemptStorageKey);
    await user.click(screen.getByRole('button', { name: 'Close' }));
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBe(stored);
    expect(posts).toHaveLength(1);

    zero = true;
    postMode = 'ok';
    await user.click(screen.getByRole('button', { name: 'Choose records' }));
    expect(await screen.findByRole('heading', { name: 'Recover allocation attempt' })).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Refresh balances' }));
    expect(await screen.findByText('Invoice outstanding 0.00 PLN')).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Send the same allocation again' }));
    expect(await screen.findByText('This allocation was already recorded.')).toBeVisible();
    expect(posts[1]?.key).toBe(posts[0]?.key);
    expect(posts[1]?.body).toBe(posts[0]?.body);

    await user.click(screen.getByRole('button', { name: 'Close' }));
    await user.click(screen.getByRole('button', { name: 'Done' }));
    zero = false;
    postMode = 'fail';
    await choosePair(user);
    const amount = await screen.findByLabelText('Allocation amount');
    await user.clear(amount);
    await user.type(amount, '10');
    await user.click(screen.getByRole('button', { name: 'Allocate this amount' }));
    expect(await screen.findByText(/before a result was received/)).toBeVisible();
    const second = sessionStorage.getItem(explicitAmountAttemptStorageKey);
    await user.click(screen.getByRole('button', { name: 'Close' }));
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBe(second);
    await user.click(screen.getByRole('button', { name: 'Review' }));
    await user.click(screen.getByRole('button', { name: 'Stop tracking this attempt' }));
    expect(screen.getByText(/may already have committed/)).toBeVisible();
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBe(second);
    await user.click(screen.getByRole('button', { name: 'Remove local tracking' }));
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBeNull();
  });

  it('clears a partial selection on tenant change and token renewal and ignores a late response', async () => {
    const user = userEvent.setup();
    let release: () => void = () => {};
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    let invoiceCalls = 0;
    server.use(
      http.get('/api/matches', () => json(emptyExact())),
      http.get('/api/invoices', async () => {
        invoiceCalls += 1;
        if (invoiceCalls === 1) {
          await gate;
          return json({ items: [invoice(invoiceA, 'OLD-TENANT-INV', '100', '0', '100')], page: 1, pageSize: 25, totalCount: 1 });
        }
        return json({ items: [invoice(invoiceB, 'NEW-TENANT-INV', '100', '0', '100')], page: 1, pageSize: 25, totalCount: 1 });
      }),
      http.get('/api/bank-transactions', () => json({
        items: [bank(bankPay, 'PAY-9', '80', '40', '40', null, null)],
        page: 1,
        pageSize: 25,
        totalCount: 1,
      })),
    );
    renderQueue();
    await screen.findByText(/Nothing is open to classify/);
    await user.click(screen.getByRole('button', { name: 'Choose records' }));
    expect(await screen.findByText('Loading invoices')).toBeVisible();
    setAccessToken(tokenFor(organizationB), Math.floor(Date.now() / 1000) + 3600);
    release();
    await waitFor(() => expect(screen.queryByRole('heading', { name: 'Choose records' })).not.toBeInTheDocument());
    expect(screen.queryByText('OLD-TENANT-INV')).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Choose records' }));
    expect(await screen.findByText('NEW-TENANT-INV')).toBeVisible();
    expect(screen.queryByText('OLD-TENANT-INV')).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: selectInvoice('NEW-TENANT-INV', invoiceB) }));
    expect(screen.getByText(/Invoice NEW-TENANT-INV/)).toBeVisible();
    setAccessToken(tokenFor(organizationB), Math.floor(Date.now() / 1000) + 3600);
    await waitFor(() => expect(screen.queryByText(/NEW-TENANT-INV/)).not.toBeInTheDocument());
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBeNull();

    sessionStorage.setItem(explicitAmountAttemptStorageKey, JSON.stringify(storedAttempt()));
    setAccessToken(tokenFor(organizationA), Math.floor(Date.now() / 1000) + 3600);
    expect(await screen.findByText(`Invoice ${invoiceA}`)).toBeVisible();
    const stored = sessionStorage.getItem(explicitAmountAttemptStorageKey);
    setAccessToken(tokenFor(organizationA), Math.floor(Date.now() / 1000) + 3600);
    expect(await screen.findByText('An allocation attempt is stored for this organization')).toBeVisible();
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBe(stored);
    expect(screen.queryByText('OLD-TENANT-INV')).not.toBeInTheDocument();
    expect(screen.queryByText('NEW-TENANT-INV')).not.toBeInTheDocument();
  });

  it('invalidates current-session caches after a verified allocation, including after unmount', async () => {
    const user = userEvent.setup();
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    server.use(
      matches(),
      lists(),
      banks(),
      http.get('/api/allocations/context', () => json(contextBody())),
      http.post('/api/allocations', () => allocationResult(201, false, '10')),
    );
    const mounted = renderQueue();
    await choosePair(user);
    await user.type(await screen.findByLabelText('Allocation amount'), '10');
    await user.click(screen.getByRole('button', { name: 'Allocate this amount' }));
    expect(await screen.findByText('Allocation recorded.')).toBeVisible();
    const epoch = sessionStore.getSnapshot().epoch;
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['invoices', epoch] });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['bank-transactions', epoch] });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['matches', epoch] });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['allocations', 'history', epoch] });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['allocations', 'context', epoch, invoiceA, bankPay] });
    mounted.unmount();
    invalidate.mockClear();

    let release: () => void = () => {};
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(http.post('/api/allocations', async () => {
      await gate;
      return allocationResult(201, false, '10');
    }));
    const leaving = renderQueue();
    const leavingEpoch = sessionStore.getSnapshot().epoch;
    await choosePair(user);
    await user.type(await screen.findByLabelText('Allocation amount'), '10');
    await user.click(screen.getByRole('button', { name: 'Allocate this amount' }));
    expect(await screen.findByText('Allocation request in progress')).toBeVisible();
    leaving.unmount();
    release();
    await waitFor(() => {
      expect(invalidate).toHaveBeenCalledWith({ queryKey: ['invoices', leavingEpoch] });
    });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['bank-transactions', leavingEpoch] });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['matches', leavingEpoch] });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['allocations', 'history', leavingEpoch] });
    expect(consoleError.mock.calls.some((call) => String(call[0]).includes('unmounted'))).toBe(false);
    consoleError.mockRestore();
    invalidate.mockRestore();
  });

  it('does not present an unknown outcome as a confirmed allocation', async () => {
    const user = userEvent.setup();
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
    const randomUUID = vi.spyOn(crypto, 'randomUUID');
    server.use(
      matches(),
      lists(),
      banks(),
      http.get('/api/allocations/context', () => json(contextBody())),
      http.post('/api/allocations', () => HttpResponse.error()),
    );
    try {
      renderQueue();
      await choosePair(user);
      await user.type(await screen.findByLabelText('Allocation amount'), '10');
      const keysBefore = randomUUID.mock.calls.length;
      await user.click(screen.getByRole('button', { name: 'Allocate this amount' }));
      expect(await screen.findByText(/before a result was received/)).toBeVisible();
      expect(screen.queryByText('Allocation recorded.')).not.toBeInTheDocument();
      expect(invalidate).not.toHaveBeenCalled();
      expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toContain(invoiceA);
      expect(randomUUID.mock.calls.length).toBe(keysBefore + 1);
    } finally {
      randomUUID.mockRestore();
      invalidate.mockRestore();
    }
  });
});

function renderQueue() {
  sessionStore.rotate(tokenFor(organizationA), Math.floor(Date.now() / 1000) + 3600, null);
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <ReconciliationQueuePage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

async function choosePair(user: ReturnType<typeof userEvent.setup>) {
  await screen.findByText(/Nothing is open to classify/);
  await user.click(screen.getByRole('button', { name: 'Choose records' }));
  await user.click(await screen.findByRole('button', { name: selectInvoice('INV-100', invoiceA) }));
  await user.click(screen.getByRole('button', { name: selectBank('PAY-9', bankPay) }));
  await user.click(screen.getByRole('button', { name: 'Review balances' }));
}

function matches() {
  return http.get('/api/matches', () => json(emptyExact()));
}

function lists() {
  return http.get('/api/invoices', () => json({
    items: [invoice(invoiceA, 'INV-100', '100', '40', '60')],
    page: 1,
    pageSize: 25,
    totalCount: 1,
  }));
}

function banks() {
  return http.get('/api/bank-transactions', () => json({
    items: [bank(bankPay, 'PAY-9', '80', '40', '40', 'DEMO', 'ext-1')],
    page: 1,
    pageSize: 25,
    totalCount: 1,
  }));
}

function selectInvoice(invoiceNumber: string, id: string): string {
  return `Select invoice ${invoiceNumber} ${id}`;
}

function selectBank(reference: string, id: string): string {
  return `Select bank transaction ${reference} ${id}`;
}

function invoicePageFor(url: URL) {
  const term = url.searchParams.get('invoiceNumber');
  const page = Number(url.searchParams.get('page') ?? '1');
  const pageSize = Number(url.searchParams.get('pageSize') ?? '25');
  if (term === 'Inv-Case') {
    return { items: [invoice(invoiceSearch, 'Inv-Case', '100', '40', '60')], page: 1, pageSize, totalCount: 1 };
  }
  if (term !== null) {
    return { items: [], page: 1, pageSize, totalCount: 0 };
  }
  if (page === 2) {
    return { items: [invoice(invoicePage, 'INV-PAGE', '100', '85', '15')], page: 2, pageSize, totalCount: 26 };
  }
  return {
    items: [
      invoice(invoiceA, 'INV-DUP', '100', '40', '60'),
      invoice(invoiceB, 'INV-DUP', '100', '90', '10'),
      invoice(invoiceZero, 'INV-ZERO', '100', '100', '0'),
    ],
    page: 1,
    pageSize,
    totalCount: 26,
  };
}

function bankPageFor(url: URL) {
  const term = url.searchParams.get('reference');
  const pageSize = Number(url.searchParams.get('pageSize') ?? '25');
  if (term === 'PAY-9') {
    return { items: [bank(bankSearch, 'PAY-9', '80', '40', '40', 'DEMO', 'ext-pay')], page: 1, pageSize, totalCount: 1 };
  }
  if (term !== null) {
    return { items: [], page: 1, pageSize, totalCount: 0 };
  }
  return {
    items: [
      bank(bankNull, null, '5', '0', '5', 'DEMO', 'ext-null'),
      bank(bankPay, 'PAY-1', '80', '40', '40', null, null),
    ],
    page: 1,
    pageSize,
    totalCount: 2,
  };
}

function invoice(id: string, invoiceNumber: string, total: string, allocated: string, outstanding: string) {
  return {
    id,
    invoiceNumber,
    totalAmount: { amount: Number(total), currency: 'PLN' },
    allocatedAmount: { amount: Number(allocated), currency: 'PLN' },
    outstandingAmount: { amount: Number(outstanding), currency: 'PLN' },
  };
}

function bank(
  id: string,
  reference: string | null,
  amount: string,
  allocated: string,
  unapplied: string,
  sourceSystem: string | null,
  externalId: string | null,
) {
  return {
    id,
    bookingDate: '2026-03-15',
    reference,
    amount: { amount: Number(amount), currency: 'PLN' },
    allocatedAmount: { amount: Number(allocated), currency: 'PLN' },
    unappliedAmount: { amount: Number(unapplied), currency: 'PLN' },
    sourceSystem,
    externalId,
  };
}

function contextBody(overrides: {
  outstanding?: number;
  unapplied?: number;
  bankCurrency?: string;
  invoiceId?: string;
  bankTransactionId?: string;
  invoiceNumber?: string;
} = {}) {
  const bankCurrency = overrides.bankCurrency ?? 'PLN';
  return {
    invoice: {
      invoiceId: overrides.invoiceId ?? invoiceA,
      invoiceNumber: overrides.invoiceNumber ?? 'INV-100',
      totalAmount: { amount: 100, currency: 'PLN' },
      allocatedAmount: { amount: 40, currency: 'PLN' },
      outstandingAmount: { amount: overrides.outstanding ?? 60, currency: 'PLN' },
      sourceSystem: 'DEMO',
      externalId: 'ext-inv',
    },
    bankTransaction: {
      bankTransactionId: overrides.bankTransactionId ?? bankPay,
      reference: 'PAY-9',
      bookingDate: '2026-03-15',
      amount: { amount: 80, currency: bankCurrency },
      allocatedAmount: { amount: 40, currency: bankCurrency },
      unappliedAmount: { amount: overrides.unapplied ?? 40, currency: bankCurrency },
      sourceSystem: null,
      externalId: null,
    },
  };
}

function storedAttempt() {
  return {
    schemaVersion: 1,
    operation: 'ExplicitAmount',
    idempotencyKey: '99999999-9999-9999-9999-999999999999',
    invoiceId: invoiceA,
    bankTransactionId: bankPay,
    amount: '25',
    currency: 'PLN',
    organizationId: organizationA,
    requestBody: `{"invoiceId":"${invoiceA}","bankTransactionId":"${bankPay}","amount":{"amount":25,"currency":"PLN"}}`,
    lifecycle: 'ready',
  };
}

function emptyExact() {
  return { algorithmVersion: 'exact-1', exactMatches: [], ambiguousGroups: [], unmatched: [], ineligible: [] };
}

function emptySmart() {
  return { algorithmVersion: 'smart-1', advisory: true, truncated: false, coverageLimited: false, suggestions: [] };
}

function json(body: object) {
  return HttpResponse.json(body);
}

function problem(status: number, code: string, title: string, detail: string) {
  return HttpResponse.json({ title, detail, code }, { status });
}

function allocationResult(
  status: 200 | 201,
  replayed: boolean,
  amount: string,
  invoiceId = invoiceA,
  bankTransactionId = bankPay,
) {
  return new HttpResponse(
    `{"allocationId":"${allocationId}","invoiceId":"${invoiceId}","bankTransactionId":"${bankTransactionId}","amount":{"amount":${amount},"currency":"PLN"},"replayed":${replayed}}`,
    { status, headers: { 'Content-Type': 'application/json' } },
  );
}

interface Posted {
  key: string | null;
  body: string;
}

async function posted(request: Request): Promise<Posted> {
  return { key: request.headers.get('Idempotency-Key'), body: await request.text() };
}

function tokenFor(organizationId: string): string {
  const payload = {
    exp: Math.floor(Date.now() / 1000) + 3600,
    org_id: organizationId,
    sub: 'operator-1',
  };
  return `${encode({ alg: 'HS256', typ: 'JWT' })}.${encode(payload)}.signature`;
}

function encode(value: object): string {
  return btoa(JSON.stringify(value)).replace(/=+$/g, '').replace(/\+/g, '-').replace(/\//g, '_');
}
