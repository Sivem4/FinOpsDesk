import { QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router';
import { describe, expect, it } from 'vitest';

import { queryClient } from '../../shared/api/query-client';
import { sessionStore } from '../../shared/api/session-store';
import { server } from '../../test/server';
import { InvoiceRegisterPage } from './InvoiceRegisterPage';

const invoiceId = '11111111-1111-1111-1111-111111111111';

describe('invoice register', () => {
  it('shows a loading state and then the invoice amounts', async () => {
    let release: () => void = () => {};
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(
      http.get('/api/invoices', async () => {
        await gate;
        return json(listBody([invoice('INV-100', '1250.5', '0', '1250.5')]));
      }),
    );

    renderRegister();

    expect(screen.getByRole('status')).toHaveTextContent('Loading invoices');
    release();

    expect(await screen.findByRole('button', { name: 'INV-100' })).toBeInTheDocument();
    expect(screen.getByText('0.00 PLN')).toBeInTheDocument();
    expect(screen.getAllByText('1,250.50 PLN')).toHaveLength(2);
  });

  it('shows an empty organization', async () => {
    server.use(http.get('/api/invoices', () => json(listBody([]))));

    renderRegister();

    expect(
      await screen.findByText(
        'No invoices are in this organization yet. Import a CSV file to add invoices.',
      ),
    ).toBeInTheDocument();
    expect(screen.getByText(/Review your invoices and the amounts still outstanding/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Import invoices' })).toHaveAttribute('href', '/imports');
    expect(screen.queryByRole('button', { name: 'Go to first page' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Go to next page' })).not.toBeInTheDocument();
  });

  it('offers the first page when a later page is empty but invoices exist', async () => {
    const user = userEvent.setup();
    server.use(
      http.get('/api/invoices', ({ request }) => {
        const page = new URL(request.url).searchParams.get('page');
        if (page === '1') {
          return json(listBody([invoice('INV-1', '10', '0', '10')], 1, 1));
        }

        return json(listBody([], 1, 9));
      }),
    );

    renderRegister('/invoices?page=9&pageSize=50');

    expect(
      await screen.findByText('This page has no invoices. Other pages still contain invoices.'),
    ).toBeInTheDocument();
    expect(
      screen.queryByText('No invoices are in this organization yet. Import a CSV file to add invoices.'),
    ).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Go to first page' })).toBeInTheDocument();
    expect(screen.queryByRole('table', { name: 'Invoices' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Go to previous page' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Go to next page' })).not.toBeInTheDocument();
    expect(screen.queryByText('Rows per page:')).not.toBeInTheDocument();
    expect(screen.queryByText(/of 1/)).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Go to first page' }));

    expect(await screen.findByRole('button', { name: 'INV-1' })).toBeInTheDocument();
    expect(screen.getByRole('table', { name: 'Invoices' })).toBeInTheDocument();
    expect(screen.getByText('1\u20131 of 1')).toBeInTheDocument();
  });

  it('loads invoices after a manual retry', async () => {
    const user = userEvent.setup();
    let release: () => void = () => {};
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    let calls = 0;
    server.use(
      http.get('/api/invoices', async () => {
        calls += 1;
        if (calls === 1) {
          return HttpResponse.error();
        }

        await gate;
        return json(listBody([invoice('INV-100', '10', '0', '10')]));
      }),
    );

    renderRegister();
    expect(await screen.findByText('We couldn’t connect. Check your connection and try again.')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByRole('status')).toHaveTextContent('Loading invoices');
    expect(screen.queryByRole('button', { name: 'Try again' })).not.toBeInTheDocument();
    release();

    expect(await screen.findByRole('button', { name: 'INV-100' })).toBeInTheDocument();
    expect(calls).toBe(2);
    expect(screen.getByRole('button', { name: 'Refresh' })).toBeEnabled();
  });

  it('renders a zero allocated amount and a large total exactly', async () => {
    const body = [
      '{"items":[{"id":"',
      invoiceId,
      '","invoiceNumber":"BIG-1","totalAmount":{"amount":999999999999999.9999,"currency":"PLN"},',
      '"allocatedAmount":{"amount":0,"currency":"PLN"},',
      '"outstandingAmount":{"amount":999999999999999.9999,"currency":"PLN"}}],',
      '"page":1,"pageSize":50,"totalCount":1}',
    ].join('');
    server.use(http.get('/api/invoices', () => HttpResponse.text(body)));

    renderRegister();

    expect(await screen.findByRole('button', { name: 'BIG-1' })).toBeInTheDocument();
    expect(screen.getAllByText('999,999,999,999,999.9999 PLN')).toHaveLength(2);
    expect(screen.getByText('0.00 PLN')).toBeInTheDocument();
  });

  it('requests the next page', async () => {
    const user = userEvent.setup();
    const seen: string[] = [];
    server.use(
      http.get('/api/invoices', ({ request }) => {
        const url = new URL(request.url);
        seen.push(url.search);
        const page = url.searchParams.get('page');
        const invoiceNumber = page === '2' ? 'INV-2' : 'INV-1';
        const pageNumber = page === '2' ? 2 : 1;
        return json(listBody([invoice(invoiceNumber, '10', '0', '10')], 60, pageNumber));
      }),
    );

    renderRegister();
    expect(await screen.findByRole('button', { name: 'INV-1' })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Go to next page' }));

    expect(await screen.findByRole('button', { name: 'INV-2' })).toBeInTheDocument();
    expect(seen.some((query) => query.includes('page=2'))).toBe(true);
  });

  it('allows keyboard selection and explains that balances come from the loaded register', async () => {
    const user = userEvent.setup();
    server.use(http.get('/api/invoices', () => json(listBody([invoice('INV-100', '10', '0', '10')]))));
    renderRegister();

    const invoiceButton = await screen.findByRole('button', { name: 'INV-100' });
    expect(invoiceButton).toHaveAttribute('aria-pressed', 'false');
    expect(invoiceButton).not.toHaveAttribute('aria-controls');
    invoiceButton.focus();
    await user.keyboard('{Enter}');

    expect(invoiceButton).toHaveAttribute('aria-pressed', 'true');
    expect(invoiceButton).toHaveAttribute('aria-controls', 'selected-invoice');
    const selected = screen.getByRole('region', { name: 'Selected invoice' });
    expect(selected).toHaveAttribute('id', invoiceButton.getAttribute('aria-controls'));
    expect(selected).toHaveTextContent('These are the balances for the selected invoice.');
    expect(screen.getByText(/Select an invoice number/)).toBeInTheDocument();
  });

  it('shows invalid pagination from the URL and from the API', async () => {
    let calls = 0;
    server.use(
      http.get('/api/invoices', () => {
        calls += 1;
        return HttpResponse.json(
          {
            title: 'Invalid pagination.',
            detail: 'Page must be at least 1.',
            code: 'reconciliation.invoices.invalid_pagination',
          },
          { status: 400 },
        );
      }),
    );

    const view = renderRegister('/invoices?page=0');
    expect(await screen.findByText('This page can’t be opened. Open Invoices again from the menu.')).toBeInTheDocument();
    expect(calls).toBe(0);

    view.unmount();
    renderRegister('/invoices');
    expect(await screen.findByText('This page can’t be opened. Open Invoices again from the menu.')).toBeInTheDocument();
    expect(calls).toBe(1);
  });

  it('distinguishes unauthorized, forbidden, and network failures', async () => {
    server.use(http.get('/api/invoices', () => new HttpResponse(null, { status: 401 })));
    const unauthorized = renderRegister();
    expect(await screen.findByText('Your access has expired or is no longer valid. Update it to continue.')).toBeInTheDocument();
    unauthorized.unmount();

    server.use(http.get('/api/invoices', () => new HttpResponse(null, { status: 403 })));
    const forbidden = renderRegister();
    expect(
      await screen.findByText('You don’t have access to this workspace. Check your access details.'),
    ).toBeInTheDocument();
    forbidden.unmount();

    server.use(http.get('/api/invoices', () => HttpResponse.error()));
    renderRegister();
    expect(await screen.findByText('We couldn’t connect. Check your connection and try again.')).toBeInTheDocument();
  });
});

function renderRegister(path = '/invoices') {
  sessionStore.rotate('memory-token', Math.floor(Date.now() / 1000) + 3600, null);
  return render(<RegisterFrame path={path} />);
}

function RegisterFrame({ path }: { path: string }) {
  return (
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[path]}>
        <InvoiceRegisterPage />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

interface InvoiceJson {
  id: string;
  invoiceNumber: string;
  totalAmount: { amount: number; currency: string };
  allocatedAmount: { amount: number; currency: string };
  outstandingAmount: { amount: number; currency: string };
}

function listBody(items: InvoiceJson[], totalCount = items.length, page = 1) {
  return {
    items,
    page,
    pageSize: 50,
    totalCount,
  };
}

function invoice(invoiceNumber: string, total: string, allocated: string, outstanding: string): InvoiceJson {
  return {
    id: invoiceId,
    invoiceNumber,
    totalAmount: { amount: Number(total), currency: 'PLN' },
    allocatedAmount: { amount: Number(allocated), currency: 'PLN' },
    outstandingAmount: { amount: Number(outstanding), currency: 'PLN' },
  };
}

function json(body: ReturnType<typeof listBody>) {
  return HttpResponse.json(body);
}
