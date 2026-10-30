import { QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router';
import { describe, expect, it, vi } from 'vitest';

import { queryClient } from '../../shared/api/query-client';
import { sessionStore } from '../../shared/api/session-store';
import { server } from '../../test/server';
import { ReconciliationQueuePage } from './ReconciliationQueuePage';

const invoiceId = '11111111-1111-1111-1111-111111111111';
const otherInvoiceId = '22222222-2222-2222-2222-222222222222';
const bankTransactionId = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const otherBankTransactionId = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';

describe('reconciliation work queue', () => {
  it('shows exact matches, ambiguous groups, unmatched rows, and ineligible rows in server order', async () => {
    server.use(http.get('/api/matches', () => json(exactBody())));
    renderQueue();

    expect(await screen.findByRole('heading', { name: 'Exact matches (1)' })).toBeVisible();
    expect(screen.getByRole('cell', { name: 'REF-Z' })).toBeVisible();
    expect(screen.getByText('10.50 PLN')).toBeVisible();
    expect(screen.getByText('Exact reference, currency, and open amount')).toBeVisible();
    expect(screen.getAllByRole('button', { name: `Invoice identifier ${invoiceId}` }).length).toBeGreaterThan(0);
    expect(screen.getAllByRole('button', { name: `Bank transaction identifier ${bankTransactionId}` }).length).toBeGreaterThan(0);
    expect(screen.getAllByText(invoiceId).length).toBeGreaterThan(0);

    expect(screen.getByRole('heading', { name: 'Ambiguous groups (1)' })).toBeVisible();
    expect(screen.getByRole('cell', { name: 'REF-A' })).toBeVisible();
    expect(screen.getByText('Competing invoices')).toBeVisible();
    expect(screen.getByRole('button', { name: `Invoice identifier ${otherInvoiceId}` })).toBeVisible();
    expect(screen.getByRole('button', { name: `Bank transaction identifier ${otherBankTransactionId}` })).toBeVisible();

    expect(screen.getByRole('heading', { name: 'Unmatched (1)' })).toBeVisible();
    expect(screen.getByText('No counterpart')).toBeVisible();
    expect(screen.getByRole('heading', { name: 'Ineligible (1)' })).toBeVisible();
    expect(screen.getByText('Missing reference')).toBeVisible();
    expect(screen.getByText('0.00 PLN')).toBeVisible();
    expect(screen.queryByText('probability')).not.toBeInTheDocument();
  });

  it('treats an empty exact result as an empty book', async () => {
    server.use(http.get('/api/matches', () => json(emptyExact())));
    renderQueue();

    expect(await screen.findByText(/Nothing is open to classify/)).toBeVisible();
    expect(screen.getByText(/exact-1/)).toBeVisible();
    expect(screen.queryByRole('heading', { name: 'Exact matches (0)' })).not.toBeInTheDocument();
  });

  it('pages exact matches without changing server order', async () => {
    const user = userEvent.setup();
    const matches = Array.from({ length: 26 }, (_, index) => exactMatch(
      index === 0 ? 'REF-Z' : index === 25 ? 'REF-LAST' : `REF-${String(index).padStart(2, '0')}`,
      guid(index),
      guid(index + 100),
    ));
    server.use(http.get('/api/matches', () => json({ ...emptyExact(), exactMatches: matches })));
    renderQueue();

    expect(await screen.findByText('Showing 1 to 25 of 26 exact matches')).toBeVisible();
    expect(screen.getByRole('cell', { name: 'REF-Z' })).toBeVisible();
    expect(screen.queryByRole('cell', { name: 'REF-LAST' })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Go to next page' }));
    expect(screen.getByText('Showing 26 to 26 of 26 exact matches')).toBeVisible();
    expect(screen.getByRole('cell', { name: 'REF-LAST' })).toBeVisible();
    expect(screen.queryByRole('cell', { name: 'REF-Z' })).not.toBeInTheDocument();
  });

  it('shows smart rank, points, zero-point signals, and flags without a probability', async () => {
    server.use(
      http.get('/api/matches', () => json(emptyExact())),
      http.get('/api/matches/smart', ({ request }) => {
        const url = new URL(request.url);
        expect(url.pathname).toBe('/api/matches/smart');
        expect(url.search).toBe('');
        return json(smartBody());
      }),
    );
    const user = userEvent.setup();
    renderQueue();
    await screen.findByText(/Nothing is open to classify/);
    await user.click(screen.getByRole('button', { name: 'Smart' }));

    expect(await screen.findByRole('cell', { name: '1' })).toBeVisible();
    expect(screen.getByRole('cell', { name: '95 points' })).toBeVisible();
    expect(screen.getByRole('cell', { name: '2' })).toBeVisible();
    expect(screen.getByRole('cell', { name: '70 points' })).toBeVisible();
    expect(screen.getAllByText('25.00 PLN')).toHaveLength(5);
    expect(screen.getByText('40.00 PLN')).toBeVisible();
    expect(screen.getAllByText('Exact reference · 70')).toHaveLength(2);
    expect(screen.getByText('Same reference tokens · 0')).toBeVisible();
    expect(screen.getByText('Open amounts are equal · 25')).toBeVisible();
    expect(screen.getByText('Open amounts differ · 0')).toBeVisible();
    expect(screen.getByText('Contested')).toBeVisible();
    expect(screen.getByText('Not marked contested')).toBeVisible();
    expect(screen.getByText('Another candidate is close · 0')).toBeVisible();
    expect(screen.getByText('Ambiguous exact match')).toBeVisible();
    expect(screen.getByText('This pair is also an ambiguous exact match · 0')).toBeVisible();
    expect(screen.getByText(/not a probability/)).toBeVisible();
    expect(screen.queryByText(/%/)).not.toBeInTheDocument();
  });

  it('keeps coverage and truncation warnings distinct from an empty suggestion list', async () => {
    const user = userEvent.setup();
    server.use(
      http.get('/api/matches', () => json(emptyExact())),
      http.get('/api/matches/smart', () => json({ ...emptySmart(), coverageLimited: true, truncated: true })),
    );
    renderQueue();
    await screen.findByText(/Nothing is open to classify/);
    await user.click(screen.getByRole('button', { name: 'Smart' }));

    expect(await screen.findByText('Candidate coverage is limited')).toBeVisible();
    expect(screen.getByText(/does not show that no relationship exists/)).toBeVisible();
    expect(screen.getByText('This list is truncated')).toBeVisible();
    expect(screen.getByText(/Some scored suggestions were omitted/)).toBeVisible();
    expect(screen.queryByText(/No open pair met a reference signal/)).not.toBeInTheDocument();
  });

  it('shows an empty smart read when coverage is complete', async () => {
    const user = userEvent.setup();
    server.use(
      http.get('/api/matches', () => json(emptyExact())),
      http.get('/api/matches/smart', () => json(emptySmart())),
    );
    renderQueue();
    await screen.findByText(/Nothing is open to classify/);
    await user.click(screen.getByRole('button', { name: 'Smart' }));

    expect(await screen.findByText(/No open pair met a reference signal/)).toBeVisible();
    expect(screen.queryByText('Candidate coverage is limited')).not.toBeInTheDocument();
  });

  it('shows an unknown signal code without inventing an explanation', async () => {
    const user = userEvent.setup();
    const suggestion = suggestionBody();
    suggestion.signals = [
      { code: 'FutureSignal', points: 70 },
      { code: 'AmountExact', points: 25 },
    ];
    suggestion.score = 95;
    suggestion.contested = false;
    suggestion.exactMatchAmbiguous = false;
    server.use(
      http.get('/api/matches', () => json(emptyExact())),
      http.get('/api/matches/smart', () => json({ ...emptySmart(), suggestions: [suggestion] })),
    );
    renderQueue();
    await screen.findByText(/Nothing is open to classify/);
    await user.click(screen.getByRole('button', { name: 'Smart' }));

    expect(await screen.findByText('FutureSignal · 70')).toBeVisible();
    expect(screen.queryByText(/FutureSignal means/)).not.toBeInTheDocument();
  });

  it('rejects a malformed exact response, a wrong algorithm, and a non-point score', async () => {
    const user = userEvent.setup();
    server.use(http.get('/api/matches', () => json({ algorithmVersion: 'exact-1' })));
    const malformed = renderQueue();
    expect(await screen.findByText('The match result could not be read.')).toBeVisible();
    malformed.unmount();

    server.use(http.get('/api/matches', () => json({ ...emptyExact(), algorithmVersion: 'exact-2' })));
    const wrongAlgorithm = renderQueue();
    expect(await screen.findByText('The match result could not be read.')).toBeVisible();
    wrongAlgorithm.unmount();

    server.use(
      http.get('/api/matches', () => json(emptyExact())),
      http.get('/api/matches/smart', () => json({ ...emptySmart(), suggestions: [{ ...suggestionBody(), scoreScale: 'percent' }] })),
    );
    renderQueue();
    await screen.findByText(/Nothing is open to classify/);
    await user.click(screen.getByRole('button', { name: 'Smart' }));
    expect(await screen.findByText('The match result could not be read.')).toBeVisible();
  });

  it('rejects an amount signal that contradicts the open amounts', async () => {
    const user = userEvent.setup();
    const exactWhenDifferent = unequalSuggestion();
    exactWhenDifferent.rank = 1;
    exactWhenDifferent.signals = [
      { code: 'ReferenceExact', points: 70 },
      { code: 'AmountExact', points: 25 },
    ];
    exactWhenDifferent.score = 95;
    server.use(
      http.get('/api/matches', () => json(emptyExact())),
      http.get('/api/matches/smart', () => json({ ...emptySmart(), suggestions: [exactWhenDifferent] })),
    );
    const different = renderQueue();
    await screen.findByText(/Nothing is open to classify/);
    await user.click(screen.getByRole('button', { name: 'Smart' }));
    expect(await screen.findByText('The match result could not be read.')).toBeVisible();
    expect(screen.queryByText('95 points')).not.toBeInTheDocument();
    different.unmount();

    const differsWhenEqual = suggestionBody();
    differsWhenEqual.signals = [
      { code: 'ReferenceExact', points: 70 },
      { code: 'AmountDiffers', points: 0 },
    ];
    differsWhenEqual.score = 70;
    differsWhenEqual.contested = false;
    differsWhenEqual.exactMatchAmbiguous = false;
    server.use(
      http.get('/api/matches', () => json(emptyExact())),
      http.get('/api/matches/smart', () => json({ ...emptySmart(), suggestions: [differsWhenEqual] })),
    );
    renderQueue();
    await screen.findByText(/Nothing is open to classify/);
    await user.click(screen.getByRole('button', { name: 'Smart' }));
    expect(await screen.findByText('The match result could not be read.')).toBeVisible();
    expect(screen.queryByText('70 points')).not.toBeInTheDocument();
  });

  it('preserves a four-decimal amount from the JSON token', async () => {
    const body = JSON.stringify(exactBody()).replace('10.5', '1.2345');
    server.use(http.get('/api/matches', () => HttpResponse.text(body, {
      headers: { 'Content-Type': 'application/json' },
    })));
    renderQueue();

    expect(await screen.findByText('1.2345 PLN')).toBeVisible();
  });

  it('leaves a late exact response off the smart view without cancelling it', async () => {
    let releaseExact: () => void = () => {};
    const exactGate = new Promise<void>((resolve) => {
      releaseExact = resolve;
    });
    let exactAborted = false;
    server.use(
      http.get('/api/matches', async ({ request }) => {
        request.signal.addEventListener('abort', () => {
          exactAborted = true;
        });
        await exactGate;
        return json(exactBody());
      }),
      http.get('/api/matches/smart', () => json(emptySmart())),
    );
    const user = userEvent.setup();
    renderQueue();
    expect(await screen.findByRole('status')).toHaveTextContent('Loading exact matches');
    await user.click(screen.getByRole('button', { name: 'Smart' }));
    expect(await screen.findByText(/No open pair met a reference signal/)).toBeVisible();
    expect(exactAborted).toBe(false);

    releaseExact();
    await waitFor(() => {
      expect(exactAborted).toBe(false);
    });
    expect(screen.queryByText('REF-Z')).not.toBeInTheDocument();
    expect(screen.getByText(/No open pair met a reference signal/)).toBeVisible();

    await user.click(screen.getByRole('button', { name: 'Exact' }));
    expect(await screen.findByRole('cell', { name: 'REF-Z' })).toBeVisible();
    expect(screen.queryByText(/No open pair met a reference signal/)).not.toBeInTheDocument();
  });

  it('reports a failed identifier copy without an unhandled rejection', async () => {
    const rejections: unknown[] = [];
    const onRejection = (event: PromiseRejectionEvent) => {
      rejections.push(event.reason);
    };
    window.addEventListener('unhandledrejection', onRejection);
    const previousClipboard = navigator.clipboard;
    server.use(http.get('/api/matches', () => json(exactBody())));
    const user = userEvent.setup();
    renderQueue();
    const [copy] = await screen.findAllByRole('button', { name: `Invoice identifier ${invoiceId}` });

    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: undefined });
    await user.click(copy);
    expect(await screen.findByText('Could not copy the identifier.')).toBeVisible();

    const writeText = vi.fn().mockRejectedValue(new Error('denied'));
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } });
    await user.click(copy);
    expect(await screen.findByText('Could not copy the identifier.')).toBeVisible();
    expect(writeText).toHaveBeenCalledWith(invoiceId);
    await waitFor(() => {
      expect(rejections).toEqual([]);
    });

    window.removeEventListener('unhandledrejection', onRejection);
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: previousClipboard });
  });

  it('requests only the active matching view', async () => {
    const calls: string[] = [];
    server.use(
      http.get('/api/matches', () => {
        calls.push('GET /api/matches');
        return json(emptyExact());
      }),
      http.get('/api/matches/smart', () => {
        calls.push('GET /api/matches/smart');
        return json(emptySmart());
      }),
    );
    const user = userEvent.setup();
    renderQueue();
    await screen.findByText(/Nothing is open to classify/);
    expect(calls).toEqual(['GET /api/matches']);

    await user.click(screen.getByRole('button', { name: 'Smart' }));
    await screen.findByText(/No open pair met a reference signal/);
    expect(calls).toEqual(['GET /api/matches', 'GET /api/matches/smart']);
  });

  it('drops the previous session result when the session changes during a request', async () => {
    let releaseFirst: () => void = () => {};
    const first = new Promise<void>((resolve) => {
      releaseFirst = resolve;
    });
    server.use(http.get('/api/matches', async ({ request }) => {
      const token = request.headers.get('authorization');
      if (token === 'Bearer first-token') {
        await Promise.race([
          first,
          new Promise<void>((resolve) => {
            request.signal.addEventListener('abort', () => resolve());
          }),
        ]);
        return json(exactBody());
      }

      return json(emptyExact());
    }));
    sessionStore.rotate('first-token', Math.floor(Date.now() / 1000) + 3600, null);
    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter>
          <ReconciliationQueuePage />
        </MemoryRouter>
      </QueryClientProvider>,
    );
    expect(await screen.findByRole('status')).toHaveTextContent('Loading exact matches');
    sessionStore.rotate('second-token', Math.floor(Date.now() / 1000) + 3600, null);
    releaseFirst();

    expect(await screen.findByText(/Nothing is open to classify/)).toBeVisible();
    expect(screen.queryByText('REF-Z')).not.toBeInTheDocument();
  });

  it('distinguishes unauthorized, forbidden, candidate limit, inconsistent money, other server failures, and network loss', async () => {
    server.use(http.get('/api/matches', () => new HttpResponse(null, { status: 401 })));
    const unauthorized = renderQueue();
    expect(await screen.findByText('Your access has expired or is no longer valid. Update it to continue.')).toBeVisible();
    unauthorized.unmount();

    server.use(http.get('/api/matches', () => new HttpResponse(null, { status: 403 })));
    const forbidden = renderQueue();
    expect(await screen.findByText('You don’t have access to this workspace. Check your access details.')).toBeVisible();
    forbidden.unmount();

    server.use(http.get('/api/matches', () => HttpResponse.json({
      title: 'Open-book matching limit exceeded.',
      detail: 'Exact matching was not run because the open book exceeds 5000 invoices or 5000 bank transactions.',
      code: 'reconciliation.matches.candidate_limit_exceeded',
    }, { status: 409 })));
    const limit = renderQueue();
    const limitAlert = await screen.findByRole('alert');
    expect(limitAlert).toHaveTextContent('Matching was not performed');
    expect(limitAlert).toHaveTextContent('Exact matching was not run');
    expect(screen.queryByRole('heading', { name: /Exact matches/ })).not.toBeInTheDocument();
    limit.unmount();

    server.use(http.get('/api/matches', () => HttpResponse.json({
      title: 'Stored financial amounts are inconsistent.',
      detail: 'Exact matching was not run because stored invoice or bank transaction amounts are inconsistent.',
      code: 'reconciliation.matches.inconsistent_money',
    }, { status: 500 })));
    const inconsistent = renderQueue();
    const inconsistentAlert = await screen.findByRole('alert');
    expect(inconsistentAlert).toHaveTextContent('Stored financial amounts are inconsistent.');
    expect(inconsistentAlert).toHaveTextContent('stored invoice or bank transaction amounts are inconsistent');
    inconsistent.unmount();

    server.use(http.get('/api/matches', () => HttpResponse.json({
      title: 'Internal processing failure',
      detail: 'Diagnostic-only text.',
    }, { status: 500 })));
    const unexpected = renderQueue();
    const unexpectedAlert = await screen.findByRole('alert');
    expect(unexpectedAlert).toHaveTextContent('The match result could not be loaded.');
    expect(unexpectedAlert).toHaveTextContent('Diagnostic-only text.');
    expect(unexpectedAlert).not.toHaveTextContent('inconsistent');
    unexpected.unmount();

    server.use(http.get('/api/matches', () => HttpResponse.error()));
    renderQueue();
    expect(await screen.findByText('We couldn’t connect. Check your connection and try again.')).toBeVisible();
  });

  it('does not call a financial mutation endpoint', async () => {
    const calls: string[] = [];
    server.use(
      http.get('/api/matches', () => {
        calls.push('GET /api/matches');
        return json(exactBody());
      }),
      http.get('/api/matches/smart', () => {
        calls.push('GET /api/matches/smart');
        return json(smartBody());
      }),
    );
    const user = userEvent.setup();
    renderQueue();
    await screen.findByRole('heading', { name: 'Exact matches (1)' });
    await user.click(screen.getByRole('button', { name: 'Smart' }));
    await screen.findByRole('cell', { name: '95 points' });
    await user.click(screen.getByRole('button', { name: 'Refresh' }));
    await waitFor(() => {
      expect(calls.filter((call) => call === 'GET /api/matches/smart').length).toBeGreaterThan(1);
    });

    expect(calls.every((call) => call.startsWith('GET /api/matches'))).toBe(true);
  });
});

function renderQueue() {
  sessionStore.rotate('memory-token', Math.floor(Date.now() / 1000) + 3600, null);
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <ReconciliationQueuePage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

function json(body: object) {
  return HttpResponse.json(body);
}

function money(amount: number, currency = 'PLN') {
  return { amount, currency };
}

function exactBody() {
  return {
    algorithmVersion: 'exact-1',
    exactMatches: [exactMatch('REF-Z', invoiceId, bankTransactionId, 10.5)],
    ambiguousGroups: [{
      normalizedReference: 'REF-A',
      openAmount: money(20),
      reason: 'CompetingInvoices',
      invoiceIds: [invoiceId, otherInvoiceId],
      bankTransactionIds: [otherBankTransactionId],
    }],
    unmatched: [{
      classification: 'NoMatch',
      reason: 'NoCounterpart',
      invoiceId,
      bankTransactionId: null,
      normalizedReference: 'REF-U',
      openAmount: money(5),
    }],
    ineligible: [{
      classification: 'Ineligible',
      reason: 'MissingReference',
      invoiceId: null,
      bankTransactionId,
      normalizedReference: null,
      openAmount: money(0),
    }],
  };
}

function emptyExact() {
  return {
    algorithmVersion: 'exact-1',
    exactMatches: [],
    ambiguousGroups: [],
    unmatched: [],
    ineligible: [],
  };
}

function exactMatch(reference: string, invoice: string, bankTransaction: string, amount = 10) {
  return {
    invoiceId: invoice,
    bankTransactionId: bankTransaction,
    amount: money(amount),
    normalizedReference: reference,
    reason: 'ExactReferenceCurrencyAndOpenAmount',
  };
}

function emptySmart() {
  return {
    algorithmVersion: 'smart-1',
    advisory: true,
    truncated: false,
    coverageLimited: false,
    suggestions: [],
  };
}

function smartBody() {
  return { ...emptySmart(), suggestions: [suggestionBody(), unequalSuggestion()] };
}

function suggestionBody() {
  return {
    rank: 1,
    invoiceId,
    bankTransactionId,
    score: 95,
    scoreScale: 'points',
    contested: true,
    exactMatchAmbiguous: true,
    invoiceOutstanding: money(25),
    bankTransactionUnapplied: money(25),
    suggestedAmount: money(25),
    signals: [
      { code: 'ReferenceExact', points: 70 },
      { code: 'ReferenceTokenSetEqual', points: 0 },
      { code: 'AmountExact', points: 25 },
      { code: 'ContestedCandidates', points: 0 },
      { code: 'ExactMatchAmbiguous', points: 0 },
    ],
  };
}

function unequalSuggestion() {
  return {
    rank: 2,
    invoiceId: otherInvoiceId,
    bankTransactionId: otherBankTransactionId,
    score: 70,
    scoreScale: 'points',
    contested: false,
    exactMatchAmbiguous: false,
    invoiceOutstanding: money(25),
    bankTransactionUnapplied: money(40),
    suggestedAmount: money(25),
    signals: [
      { code: 'ReferenceExact', points: 70 },
      { code: 'AmountDiffers', points: 0 },
    ],
  };
}

function guid(seed: number): string {
  return `${seed.toString(16).padStart(8, '0')}-1111-4111-8111-111111111111`;
}
