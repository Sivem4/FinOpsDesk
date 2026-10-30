import { QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router';
import { describe, expect, it } from 'vitest';

import { AppRouter } from '../../app/router';
import { queryClient } from '../../shared/api/query-client';
import { sessionStore } from '../../shared/api/session-store';
import { AppShell } from '../../shared/ui/AppShell';
import { server } from '../../test/server';
import { BankTransactionRegisterPage } from './BankTransactionRegisterPage';

const fullId = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const openId = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
const partialId = 'cccccccc-cccc-cccc-cccc-cccccccccccc';
const preciseId = 'dddddddd-dddd-dddd-dddd-dddddddddddd';

describe('bank transaction register', () => {
  it('shows the navigation entry and the register route', async () => {
    server.use(
      http.get('/api/invoices', () => json(emptyInvoiceList())),
      http.get('/api/bank-transactions', () => json(listBody([]))),
    );
    renderApp('/invoices');

    await userEvent.setup().click(screen.getByRole('link', { name: 'Bank transactions' }));

    expect(await screen.findByRole('heading', { name: 'Bank transactions' })).toBeVisible();
    expect(screen.getByRole('link', { name: 'Import bank transactions' })).toHaveAttribute('href', '/imports');
  });

  it('shows a loading state and then current balances', async () => {
    let release: () => void = () => {};
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(http.get('/api/bank-transactions', async () => {
      await gate;
      return text(registerBody());
    }));
    renderRegister();

    expect(screen.getByRole('status')).toHaveTextContent('Loading bank transactions');
    release();

    const table = await screen.findByRole('table', { name: 'Bank transactions' });
    const full = row(table, '2026-03-15');
    expect(within(full).getByText('PAY-FULL')).toBeVisible();
    expect(within(full).getByText('Fully allocated')).toBeVisible();
    expect(within(full).getByText('BANK_CSV · TX-1')).toBeVisible();
    expect(within(full).getAllByText('40.00 PLN')).toHaveLength(2);
    expect(within(full).getByText('0.00 PLN')).toBeVisible();

    const open = row(table, '2026-03-16');
    expect(within(open).getAllByText('None')).toHaveLength(2);
    expect(within(open).getByText('Unallocated')).toBeVisible();

    const partial = row(table, '2026-03-17');
    expect(within(partial).getByText('Partially allocated')).toBeVisible();
    expect(within(partial).getByText('25.00 PLN')).toBeVisible();
    expect(within(partial).getByText('15.00 PLN')).toBeVisible();

    const precise = row(table, '2026-03-18');
    expect(within(precise).getByText('Partially allocated')).toBeVisible();
    expect(within(precise).getByText('999,999,999,999,999.9999 PLN')).toBeVisible();
    expect(within(precise).getByText('999,999,999,999,999.9998 PLN')).toBeVisible();
    expect(within(precise).getByText('0.0001 PLN')).toBeVisible();
    expect(screen.getByText(/do not reserve a bank transaction/)).toBeVisible();
  });

  it('shows an empty organization', async () => {
    server.use(http.get('/api/bank-transactions', () => json(listBody([]))));
    renderRegister();

    expect(await screen.findByText(/No bank transactions are in this organization yet/)).toBeVisible();
    expect(screen.queryByRole('table', { name: 'Bank transactions' })).not.toBeInTheDocument();
  });

  it('offers the first page when a later page is empty', async () => {
    server.use(http.get('/api/bank-transactions', () => json(listBody([], 3, 9))));
    renderRegister('/bank-transactions?page=9&pageSize=50');

    expect(await screen.findByText(/This page has no bank transactions/)).toBeVisible();
    expect(screen.getByRole('button', { name: 'Go to first page' })).toBeVisible();
    expect(screen.queryByRole('table', { name: 'Bank transactions' })).not.toBeInTheDocument();
  });

  it('pages and changes page size through the query string', async () => {
    const user = userEvent.setup();
    const seen: string[] = [];
    server.use(http.get('/api/bank-transactions', ({ request }) => {
      const url = new URL(request.url);
      seen.push(url.search);
      const page = url.searchParams.get('page') === '2' ? 2 : 1;
      const reference = page === 2 ? 'PAY-2' : 'PAY-1';
      return json(listBody([transaction({
        id: openId,
        bookingDate: '2026-04-01',
        reference,
        amount: 10,
        allocated: 0,
        unapplied: 10,
      })], 60, page));
    }));
    renderRegister();

    expect(await screen.findByText('PAY-1')).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Go to next page' }));
    expect(await screen.findByText('PAY-2')).toBeVisible();
    expect(seen.some((query) => query.includes('page=2'))).toBe(true);

    await user.click(screen.getByRole('combobox', { name: 'Rows per page:' }));
    await user.click(screen.getByRole('option', { name: '25' }));
    await screen.findByText('PAY-1');
    expect(seen.some((query) => query.includes('pageSize=25') && query.includes('page=1'))).toBe(true);
  });

  it('distinguishes forbidden, unreadable, and inconsistent responses', async () => {
    server.use(http.get('/api/bank-transactions', () => new HttpResponse(null, { status: 403 })));
    const forbidden = renderRegister();
    expect(await screen.findByText(/don’t have access to this workspace/)).toBeVisible();
    forbidden.unmount();

    server.use(http.get('/api/bank-transactions', () => text(
      '{"items":[{"id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","bookingDate":"2026-03-15T00:00:00","reference":null,"amount":{"amount":1,"currency":"PLN"},"allocatedAmount":{"amount":0,"currency":"PLN"},"unappliedAmount":{"amount":1,"currency":"PLN"},"sourceSystem":null,"externalId":null}],"page":1,"pageSize":50,"totalCount":1}',
    )));
    const unreadable = renderRegister();
    expect(await screen.findByText(/couldn’t load your bank transactions/)).toBeVisible();
    unreadable.unmount();

    server.use(http.get('/api/bank-transactions', () => HttpResponse.json({
      title: 'Stored financial amounts are inconsistent.',
      detail: 'SECRET-PAY 77.77 USD',
      code: 'reconciliation.bank_transactions.inconsistent_money',
    }, { status: 500 })));
    renderRegister();
    expect(await screen.findByText(/The page was not shown/)).toBeVisible();
    expect(screen.queryByText(/SECRET-PAY/)).not.toBeInTheDocument();
    expect(screen.queryByText(/77.77/)).not.toBeInTheDocument();
  });

  it('replaces the previous organization rows when the session changes', async () => {
    let organization = 'A';
    server.use(http.get('/api/bank-transactions', () => json(listBody([
      transaction({
        id: organization === 'A' ? openId : partialId,
        bookingDate: '2026-05-01',
        reference: organization === 'A' ? 'PAY-A' : 'PAY-B',
        amount: organization === 'A' ? 10 : 20,
        allocated: 0,
        unapplied: organization === 'A' ? 10 : 20,
      }),
    ]))));
    renderRegister();
    expect(await screen.findByText('PAY-A')).toBeVisible();

    organization = 'B';
    sessionStore.rotate('next-token', Math.floor(Date.now() / 1000) + 3600, null);

    expect(await screen.findByText('PAY-B')).toBeVisible();
    expect(screen.queryByText('PAY-A')).not.toBeInTheDocument();
    expect(screen.queryByText('10.00 PLN')).not.toBeInTheDocument();
  });
});

function renderRegister(path = '/bank-transactions') {
  sessionStore.rotate('memory-token', Math.floor(Date.now() / 1000) + 3600, null);
  return render(<RegisterFrame path={path} />);
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
    </QueryClientProvider>
  );
}

function RegisterFrame({ path }: { path: string }) {
  return (
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[path]}>
        <BankTransactionRegisterPage />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

function row(table: HTMLElement, bookingDate: string): HTMLElement {
  const match = within(table).getByText(bookingDate).closest('tr');
  if (match === null) {
    throw new Error(`Missing row ${bookingDate}`);
  }
  return match;
}

function emptyInvoiceList() {
  return { items: [], page: 1, pageSize: 50, totalCount: 0 };
}

function listBody(
  items: ReturnType<typeof transaction>[],
  totalCount = items.length,
  page = 1,
  pageSize = 50,
) {
  return { items, page, pageSize, totalCount };
}

function transaction(values: {
  id: string;
  bookingDate: string;
  reference: string | null;
  amount: number;
  allocated: number;
  unapplied: number;
  sourceSystem?: string | null;
  externalId?: string | null;
}) {
  return {
    id: values.id,
    bookingDate: values.bookingDate,
    reference: values.reference,
    amount: { amount: values.amount, currency: 'PLN' },
    allocatedAmount: { amount: values.allocated, currency: 'PLN' },
    unappliedAmount: { amount: values.unapplied, currency: 'PLN' },
    sourceSystem: values.sourceSystem ?? null,
    externalId: values.externalId ?? null,
  };
}

function registerBody(): string {
  return `{"items":[${JSON.stringify(transaction({
    id: fullId,
    bookingDate: '2026-03-15',
    reference: 'PAY-FULL',
    amount: 40,
    allocated: 40,
    unapplied: 0,
    sourceSystem: 'BANK_CSV',
    externalId: 'TX-1',
  }))},${JSON.stringify(transaction({
    id: openId,
    bookingDate: '2026-03-16',
    reference: null,
    amount: 10,
    allocated: 0,
    unapplied: 10,
  }))},${JSON.stringify(transaction({
    id: partialId,
    bookingDate: '2026-03-17',
    reference: 'PAY-PART',
    amount: 40,
    allocated: 25,
    unapplied: 15,
  }))},{"id":"${preciseId}","bookingDate":"2026-03-18","reference":"PAY-PRECISE","amount":{"amount":999999999999999.9999,"currency":"PLN"},"allocatedAmount":{"amount":999999999999999.9998,"currency":"PLN"},"unappliedAmount":{"amount":0.0001,"currency":"PLN"},"sourceSystem":null,"externalId":null}],"page":1,"pageSize":50,"totalCount":4}`;
}

function json(body: object) {
  return HttpResponse.json(body);
}

function text(body: string) {
  return new HttpResponse(body, { headers: { 'Content-Type': 'application/json' } });
}
