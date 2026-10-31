import { QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router';
import { describe, expect, it, vi } from 'vitest';

import { queryClient } from '../../shared/api/query-client';
import { sessionStore } from '../../shared/api/session-store';
import { server } from '../../test/server';
import { ReconciliationQueuePage } from '../reconciliation/ReconciliationQueuePage';
import { explicitAmountAttemptStorageKey } from '../reconciliation/allocation-attempt';
import { AllocationHistoryPage } from './AllocationHistoryPage';
import { reversalAttemptStorageKey } from './reversal-attempt';

const organizationA = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
const organizationB = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
const allocationId = '11111111-1111-4111-8111-111111111111';
const otherAllocationId = '22222222-2222-4222-8222-222222222222';
const invoiceId = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc';
const bankTransactionId = 'dddddddd-dddd-4ddd-8ddd-dddddddddddd';

describe('allocation reversal workflow', () => {
  it('offers reversal only for a confirmed row and loads context after that action', async () => {
    const user = userEvent.setup();
    const contextGets: string[] = [];
    const posts: string[] = [];
    server.use(
      historyHandler([confirmedRow(), reversedRow()]),
      http.get('/api/allocations/:id/reversal-context', ({ request }) => {
        contextGets.push(request.url);
        return text(contextBody());
      }),
      http.post('/api/allocations/:id/reverse', () => {
        posts.push('reverse');
        return text(reversalBody());
      }),
    );
    signIn();
    renderHistory();

    expect(await screen.findByRole('button', { name: `Reverse allocation ${allocationId}` })).toBeVisible();
    expect(screen.queryByRole('button', { name: `Reverse allocation ${otherAllocationId}` })).not.toBeInTheDocument();
    expect(contextGets).toHaveLength(0);
    expect(posts).toHaveLength(0);

    await user.click(screen.getByRole('button', { name: `Reverse allocation ${allocationId}` }));

    expect(await screen.findByText('Invoice number INV-15')).toBeVisible();
    expect(screen.getByText(`Allocation ${allocationId}`)).toBeVisible();
    expect(screen.getByText(`Invoice id ${invoiceId}`)).toBeVisible();
    expect(screen.getByText('Bank reference None')).toBeVisible();
    expect(screen.getByText(`Bank transaction ${bankTransactionId}`)).toBeVisible();
    expect(screen.getByText('Original amount 15.50 PLN')).toBeVisible();
    expect(screen.getByText('Status Confirmed')).toBeVisible();
    expect(contextGets).toHaveLength(1);
    expect(posts).toHaveLength(0);
  });

  it('blocks a new reversal when the context is reversed, mismatched, or the reason is empty', async () => {
    const user = userEvent.setup();
    const posts: string[] = [];
    server.use(
      historyHandler([confirmedRow()]),
      http.get('/api/allocations/:id/reversal-context', () => text(contextBody({ status: 'reversed', reversedAt: '"2026-10-31T09:30:00+00:00"', reversalReason: '"Duplicate payment"' }))),
      http.post('/api/allocations/:id/reverse', () => {
        posts.push('reverse');
        return text(reversalBody());
      }),
    );
    signIn();
    renderHistory();
    await user.click(await screen.findByRole('button', { name: `Reverse allocation ${allocationId}` }));

    expect(await screen.findByText('This allocation is already reversed. A new reversal was not started.')).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Reverse this allocation' })).not.toBeInTheDocument();
    expect(posts).toHaveLength(0);

    queryClient.removeQueries({ queryKey: ['allocations', 'reversal-context'] });
    server.use(http.get('/api/allocations/:id/reversal-context', () => text(contextBody({ allocationId: otherAllocationId }))));
    await user.click(screen.getByRole('button', { name: 'Close' }));
    await user.click(screen.getByRole('button', { name: `Reverse allocation ${allocationId}` }));
    expect(await screen.findByText('The current allocation could not be read. A new reversal was not started.')).toBeVisible();
    expect(posts).toHaveLength(0);

    queryClient.removeQueries({ queryKey: ['allocations', 'reversal-context'] });
    server.use(http.get('/api/allocations/:id/reversal-context', () => text(contextBody())));
    await user.click(screen.getByRole('button', { name: 'Close' }));
    await user.click(screen.getByRole('button', { name: `Reverse allocation ${allocationId}` }));
    expect(await screen.findByLabelText('Reversal reason')).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Reverse this allocation' }));
    expect(await screen.findByText('Enter a reversal reason.')).toBeVisible();
    expect(posts).toHaveLength(0);
  });

  it('stores the trimmed reason and sends that exact body once', async () => {
    const user = userEvent.setup();
    const posts: Array<{ key: string | null; body: string }> = [];
    server.use(
      historyHandler([confirmedRow()]),
      http.get('/api/allocations/:id/reversal-context', () => text(contextBody())),
      http.post('/api/allocations/:id/reverse', async ({ request }) => {
        posts.push({ key: request.headers.get('Idempotency-Key'), body: await request.text() });
        return text(reversalBody('customer refund'));
      }),
    );
    const epoch = signIn();
    seedFinancialCaches(epoch);
    renderHistory();
    await openAndSubmit(user, '  customer refund  ');

    expect(await screen.findByText('Reversal recorded.')).toBeVisible();
    expect(posts).toHaveLength(1);
    expect(posts[0]?.body).toBe('{"reason":"customer refund"}');
    expect(posts[0]?.key).toMatch(/^[0-9a-f-]{36}$/);
    expect(sessionStorage.getItem(reversalAttemptStorageKey)).toBeNull();
    await waitFor(() => {
      expect(queryClient.getQueryState(['allocations', 'history', epoch, 2, 25, invoiceId, bankTransactionId])?.isInvalidated).toBe(true);
      expect(queryClient.getQueryState(['allocations', 'audit', epoch, allocationId, 1])?.isInvalidated).toBe(true);
      expect(queryClient.getQueryState(['invoices', epoch])?.isInvalidated).toBe(true);
      expect(queryClient.getQueryState(['bank-transactions', epoch])?.isInvalidated).toBe(true);
      expect(queryClient.getQueryState(['matches', epoch])?.isInvalidated).toBe(true);
      expect(queryClient.getQueryState(['allocations', 'context', epoch, invoiceId, bankTransactionId])?.isInvalidated).toBe(true);
    });
  });

  it('does not send a reversal when the descriptor cannot be stored', async () => {
    const user = userEvent.setup();
    const posts: string[] = [];
    server.use(
      historyHandler([confirmedRow()]),
      http.get('/api/allocations/:id/reversal-context', () => text(contextBody())),
      http.post('/api/allocations/:id/reverse', () => {
        posts.push('reverse');
        return text(reversalBody());
      }),
    );
    signIn();
    renderHistory();
    await user.click(await screen.findByRole('button', { name: `Reverse allocation ${allocationId}` }));
    expect(await screen.findByLabelText('Reversal reason')).toBeVisible();
    const originalSetItem = Storage.prototype.setItem;
    const setItem = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(function setReversalItem(this: Storage, key: string, value: string) {
      if (key === reversalAttemptStorageKey) {
        throw new Error('full');
      }

      return originalSetItem.call(this, key, value);
    });

    await user.type(screen.getByLabelText('Reversal reason'), 'customer refund');
    await user.click(screen.getByRole('button', { name: 'Reverse this allocation' }));

    expect(await screen.findByText(/The reversal was not stored, so it was not sent/)).toBeVisible();
    expect(posts).toHaveLength(0);
    expect(sessionStorage.getItem(reversalAttemptStorageKey)).toBeNull();
    setItem.mockRestore();
  });

  it('keeps a lost response for a later replay with the same key and body', async () => {
    const user = userEvent.setup();
    const posts: Array<{ key: string | null; body: string }> = [];
    let contextFails = false;
    let postFails = true;
    server.use(
      historyHandler([confirmedRow()]),
      http.get('/api/allocations/:id/reversal-context', () => (
        contextFails ? HttpResponse.text('', { status: 500 }) : text(contextBody())
      )),
      http.post('/api/allocations/:id/reverse', async ({ request }) => {
        posts.push({ key: request.headers.get('Idempotency-Key'), body: await request.text() });
        if (postFails) {
          return HttpResponse.error();
        }
        return text(reversalBody('customer refund', true));
      }),
    );
    signIn();
    const first = renderHistory();
    await openAndSubmit(user, 'customer refund');

    expect(await screen.findByText(/The reversal was not verified/)).toBeVisible();
    expect(screen.queryByText('Reversal recorded.')).not.toBeInTheDocument();
    const stored = sessionStorage.getItem(reversalAttemptStorageKey);
    expect(JSON.parse(stored ?? '{}').requestBody).toBe('{"reason":"customer refund"}');
    await user.click(screen.getByRole('button', { name: 'Close' }));
    expect(sessionStorage.getItem(reversalAttemptStorageKey)).toBe(stored);
    first.unmount();

    contextFails = true;
    postFails = false;
    renderHistory();
    expect(await screen.findByRole('button', { name: 'Review reversal attempt' })).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Review reversal attempt' }));
    expect(await screen.findByText('Reason customer refund')).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Send the same reversal again' }));

    expect(await screen.findByText('This reversal was already recorded for the same request.')).toBeVisible();
    expect(posts).toHaveLength(2);
    expect(posts[1]?.key).toBe(posts[0]?.key);
    expect(posts[1]?.body).toBe(posts[0]?.body);
    expect(sessionStorage.getItem(reversalAttemptStorageKey)).toBeNull();
  });

  it('requires confirmation before removing local tracking', async () => {
    const user = userEvent.setup();
    server.use(historyHandler([confirmedRow()]));
    signIn();
    seedReversal('ready');
    renderHistory();
    await user.click(await screen.findByRole('button', { name: 'Review reversal attempt' }));
    await user.click(await screen.findByRole('button', { name: 'Remove local tracking' }));

    expect(await screen.findByText(/does not reverse or undo a server-side operation/)).toBeVisible();
    expect(sessionStorage.getItem(reversalAttemptStorageKey)).toContain('customer refund');
    await user.click(screen.getByRole('button', { name: 'Remove local tracking now' }));
    await waitFor(() => expect(sessionStorage.getItem(reversalAttemptStorageKey)).toBeNull());
  });

  it('keeps the attempt for an invalid success, a conflict, a concurrency conflict, and an authentication error', async () => {
    const user = userEvent.setup();
    const posts: Array<{ key: string | null; body: string }> = [];
    const responses = [
      text('{"allocationId":"11111111-1111-4111-8111-111111111111","status":"reversed","replayed":"yes"}'),
      problem(409, 'reconciliation.allocation.already_reversed'),
      problem(409, 'reconciliation.concurrency_conflict'),
      problem(403, 'reconciliation.actor.required'),
    ];
    server.use(
      historyHandler([confirmedRow()]),
      http.post('/api/allocations/:id/reverse', async ({ request }) => {
        posts.push({ key: request.headers.get('Idempotency-Key'), body: await request.text() });
        return responses.shift() ?? problem(500, 'reconciliation.allocation.reversal_invalid_request');
      }),
    );
    signIn();
    seedReversal('uncertain');
    renderHistory();
    await user.click(await screen.findByRole('button', { name: 'Review reversal attempt' }));

    await user.click(await screen.findByRole('button', { name: 'Send the same reversal again' }));
    expect(await screen.findByText(/could not be verified/)).toBeVisible();
    expect(screen.queryByText('Reversal recorded.')).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Send the same reversal again' }));
    expect(await screen.findByText(/did not verify that it performed that reversal/)).toBeVisible();

    await user.click(screen.getByRole('button', { name: 'Send the same reversal again' }));
    expect(await screen.findByText(/was not recorded as a reversal/)).toBeVisible();

    await user.click(screen.getByRole('button', { name: 'Send the same reversal again' }));
    expect(await screen.findByText(/stored reversal was kept/)).toBeVisible();
    expect(posts.map((post) => post.key)).toEqual([posts[0]?.key, posts[0]?.key, posts[0]?.key, posts[0]?.key]);
    expect(new Set(posts.map((post) => post.body)).size).toBe(1);
    expect(JSON.parse(sessionStorage.getItem(reversalAttemptStorageKey) ?? '{}').requestBody).toBe(posts[0]?.body);
    expect(posts).toHaveLength(4);
  });

  it('does not start another reversal when verified tracking cannot be removed', async () => {
    const user = userEvent.setup();
    const posts: string[] = [];
    server.use(
      historyHandler([confirmedRow()]),
      http.post('/api/allocations/:id/reverse', () => {
        posts.push('reverse');
        return text(reversalBody('customer refund', false, '123456789.1234'));
      }),
    );
    signIn();
    seedReversal('uncertain');
    renderHistory();
    await user.click(await screen.findByRole('button', { name: 'Review reversal attempt' }));
    const originalRemoveItem = Storage.prototype.removeItem;
    const removeItem = vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(function removeReversalItem(this: Storage, key: string) {
      if (key === reversalAttemptStorageKey) {
        throw new Error('blocked');
      }

      return originalRemoveItem.call(this, key);
    });
    await user.click(await screen.findByRole('button', { name: 'Send the same reversal again' }));

    expect(await screen.findByText(/local tracking could not be cleared/)).toBeVisible();
    expect(screen.queryByText('Reversal recorded.')).not.toBeInTheDocument();
    expect(posts).toHaveLength(1);
    expect(sessionStorage.getItem(reversalAttemptStorageKey)).toContain('customer refund');
    expect(screen.queryByRole('button', { name: 'Reverse this allocation' })).not.toBeInTheDocument();
    removeItem.mockRestore();
  });

  it('drops a stale context and a stale reversal result when the session changes', async () => {
    const user = userEvent.setup();
    let releaseContext!: () => void;
    let contextEntered!: () => void;
    const contextEnteredPromise = new Promise<void>((resolve) => {
      contextEntered = resolve;
    });
    const contextGate = new Promise<void>((resolve) => {
      releaseContext = resolve;
    });
    server.use(
      historyHandler([confirmedRow('NEW-15')]),
      http.get('/api/allocations/:id/reversal-context', async () => {
        contextEntered();
        await contextGate;
        return text(contextBody({ invoiceNumber: 'OLD-INVOICE' }));
      }),
    );
    signIn();
    renderHistory();
    await user.click(await screen.findByRole('button', { name: `Reverse allocation ${allocationId}` }));
    await contextEnteredPromise;
    signIn(organizationB);
    releaseContext();

    await waitFor(() => expect(screen.getByText('NEW-15')).toBeVisible());
    expect(screen.queryByText('OLD-INVOICE')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Reverse this allocation' })).not.toBeInTheDocument();
  });

  it('does not apply an in-flight reversal to the next session', async () => {
    const user = userEvent.setup();
    let release!: () => void;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    let requestEntered = false;
    let handlerFinished = false;
    let requestAborted = false;
    const posts: string[] = [];
    server.use(
      historyHandler([confirmedRow()]),
      http.get('/api/allocations/:id/reversal-context', () => text(contextBody())),
      http.post('/api/allocations/:id/reverse', async ({ request }) => {
        posts.push('reverse');
        requestEntered = true;
        await gate;
        requestAborted = request.signal.aborted;
        handlerFinished = true;
        return text(reversalBody('customer refund'));
      }),
    );
    signIn();
    renderHistory();
    await openAndSubmit(user, 'customer refund');
    await waitFor(() => {
      expect(requestEntered).toBe(true);
    });
    const stored = sessionStorage.getItem(reversalAttemptStorageKey);
    const nextEpoch = signIn();
    queryClient.setQueryData(['invoices', nextEpoch], { ok: true });
    release();
    await waitFor(() => {
      expect(handlerFinished).toBe(true);
    });

    expect(requestAborted).toBe(true);
    expect(posts).toHaveLength(1);
    expect(stored).not.toBeNull();
    expect(sessionStorage.getItem(reversalAttemptStorageKey)).toBe(stored);
    expect(screen.queryByText('Reversal recorded.')).not.toBeInTheDocument();
    expect(screen.queryByText('This reversal was already recorded for the same request.')).not.toBeInTheDocument();
    expect(queryClient.getQueryState(['invoices', nextEpoch])?.isInvalidated).not.toBe(true);
  });

  it('hides another organization and still recovers the same organization after signing in again', async () => {
    const user = userEvent.setup();
    const posts: Array<{ key: string | null; body: string }> = [];
    server.use(
      historyHandler([confirmedRow()]),
      http.post('/api/allocations/:id/reverse', async ({ request }) => {
        posts.push({ key: request.headers.get('Idempotency-Key'), body: await request.text() });
        return text(reversalBody('customer refund', true, '123456789.1234'));
      }),
    );
    signIn();
    seedReversal('uncertain');
    const stored = sessionStorage.getItem(reversalAttemptStorageKey);
    signIn(organizationB);
    const foreign = renderHistory();

    expect(await screen.findByText('A stored reversal belongs to another organization. Its details stay hidden.')).toBeVisible();
    expect(screen.queryByText('123,456,789.1234 PLN')).not.toBeInTheDocument();
    expect(screen.queryByText('Reason customer refund')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Send the same reversal again' })).not.toBeInTheDocument();
    expect(posts).toHaveLength(0);
    foreign.unmount();

    signIn();
    renderHistory();
    await user.click(await screen.findByRole('button', { name: 'Review reversal attempt' }));
    await user.click(await screen.findByRole('button', { name: 'Send the same reversal again' }));
    expect(await screen.findByText('This reversal was already recorded for the same request.')).toBeVisible();
    expect(posts).toHaveLength(1);
    expect(posts[0]?.body).toBe('{"reason":"customer refund"}');
    expect(stored).toContain(posts[0]?.key ?? 'missing');
  });

  it('keeps an explicit-amount attempt beside a reversal attempt and shows both recovery links', async () => {
    server.use(
      historyHandler([confirmedRow()]),
      http.get('/api/matches', () => HttpResponse.json({ title: 'Unavailable' }, { status: 500 })),
    );
    signIn();
    seedReversal('ready');
    sessionStorage.setItem(explicitAmountAttemptStorageKey, explicitAmountDescriptor());
    const history = renderHistory();

    expect(await screen.findByRole('link', { name: 'Open Reconciliation' })).toHaveAttribute('href', '/reconciliation');
    expect(screen.getByText('An unresolved reversal attempt is stored for this organization.')).toBeVisible();
    const explicitAmount = sessionStorage.getItem(explicitAmountAttemptStorageKey);
    history.unmount();

    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter>
          <ReconciliationQueuePage />
        </MemoryRouter>
      </QueryClientProvider>,
    );

    expect(await screen.findByRole('link', { name: 'Open Allocation history' })).toHaveAttribute('href', '/allocations');
    expect(screen.getByText('An allocation attempt is stored for this organization')).toBeVisible();
    expect(screen.getByText('An unresolved reversal attempt is stored for this organization.')).toBeVisible();
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBe(explicitAmount);
    expect(sessionStorage.getItem(reversalAttemptStorageKey)).toContain('customer refund');
  });

  it('invalidates after a verified replay and after unmount, and does not invalidate an unknown outcome', async () => {
    const user = userEvent.setup();
    let release!: () => void;
    let entered!: () => void;
    const enteredPromise = new Promise<void>((resolve) => {
      entered = resolve;
    });
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(
      historyHandler([confirmedRow()]),
      http.post('/api/allocations/:id/reverse', async () => {
        entered();
        await gate;
        return text(reversalBody('customer refund', true, '123456789.1234'));
      }),
    );
    const epoch = signIn();
    seedFinancialCaches(epoch);
    seedReversal('uncertain');
    const view = renderHistory();
    await user.click(await screen.findByRole('button', { name: 'Review reversal attempt' }));
    await user.click(await screen.findByRole('button', { name: 'Send the same reversal again' }));
    await enteredPromise;
    view.unmount();
    release();

    await waitFor(() => {
      expect(queryClient.getQueryState(['allocations', 'history', epoch, 2, 25, invoiceId, bankTransactionId])?.isInvalidated).toBe(true);
    });
    expect(sessionStorage.getItem(reversalAttemptStorageKey)).toBeNull();
  });

  it('does not invalidate or show success when the response is lost', async () => {
    const user = userEvent.setup();
    server.use(
      historyHandler([confirmedRow()]),
      http.post('/api/allocations/:id/reverse', () => HttpResponse.error()),
    );
    const epoch = signIn();
    seedFinancialCaches(epoch);
    seedReversal('uncertain');
    renderHistory();
    await user.click(await screen.findByRole('button', { name: 'Review reversal attempt' }));
    await user.click(await screen.findByRole('button', { name: 'Send the same reversal again' }));

    expect(await screen.findByText(/The reversal was not verified/)).toBeVisible();
    expect(screen.queryByText(/already recorded/)).not.toBeInTheDocument();
    expect(queryClient.getQueryState(['allocations', 'history', epoch, 2, 25, invoiceId, bankTransactionId])?.isInvalidated).not.toBe(true);
    expect(sessionStorage.getItem(reversalAttemptStorageKey)).toContain('customer refund');
  });

  it('sends one reversal when the confirmation is clicked twice', async () => {
    let release!: () => void;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    const posts: string[] = [];
    server.use(
      historyHandler([confirmedRow()]),
      http.get('/api/allocations/:id/reversal-context', () => text(contextBody())),
      http.post('/api/allocations/:id/reverse', async () => {
        posts.push('reverse');
        await gate;
        return text(reversalBody('customer refund'));
      }),
    );
    signIn();
    renderHistory();
    fireEvent.click(await screen.findByRole('button', { name: `Reverse allocation ${allocationId}` }));
    const reason = await screen.findByLabelText('Reversal reason');
    fireEvent.change(reason, { target: { value: 'customer refund' } });
    await waitFor(() => expect(screen.getByLabelText('Reversal reason')).toHaveValue('customer refund'));
    const confirm = screen.getByRole('button', { name: 'Reverse this allocation' });
    fireEvent.click(confirm);
    fireEvent.click(confirm);
    await waitFor(() => expect(posts).toHaveLength(1));
    release();
    expect(await screen.findByText('Reversal recorded.')).toBeVisible();
    expect(posts).toHaveLength(1);
  });
});

async function openAndSubmit(user: ReturnType<typeof userEvent.setup>, reason: string) {
  await user.click(await screen.findByRole('button', { name: `Reverse allocation ${allocationId}` }));
  await user.type(await screen.findByLabelText('Reversal reason'), reason);
  await user.click(screen.getByRole('button', { name: 'Reverse this allocation' }));
}

function signIn(organizationId = organizationA): number {
  sessionStore.rotate(tokenFor(organizationId), Math.floor(Date.now() / 1000) + 3600, null);
  return sessionStore.getSnapshot().epoch;
}

function renderHistory() {
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <AllocationHistoryPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

function seedFinancialCaches(epoch: number): void {
  queryClient.setQueryData(['allocations', 'history', epoch, 2, 25, invoiceId, bankTransactionId], { ok: true });
  queryClient.setQueryData(['allocations', 'audit', epoch, allocationId, 1], { ok: true });
  queryClient.setQueryData(['invoices', epoch], { ok: true });
  queryClient.setQueryData(['bank-transactions', epoch], { ok: true });
  queryClient.setQueryData(['matches', epoch], { ok: true });
  queryClient.setQueryData(['allocations', 'context', epoch, invoiceId, bankTransactionId], { ok: true });
}

function seedReversal(lifecycle: 'ready' | 'uncertain'): void {
  const body = '{"reason":"customer refund"}';
  sessionStorage.setItem(reversalAttemptStorageKey, JSON.stringify({
    schemaVersion: 1,
    operation: 'Reversal',
    allocationId,
    invoiceId,
    bankTransactionId,
    organizationId: organizationA,
    idempotencyKey: '99999999-9999-4999-8999-999999999999',
    reason: 'customer refund',
    requestBody: body,
    amount: '123456789.1234',
    currency: 'PLN',
    lifecycle,
    createdAt: '2026-10-31T10:00:00.000Z',
  }));
}

function explicitAmountDescriptor(): string {
  return JSON.stringify({
    schemaVersion: 1,
    operation: 'ExplicitAmount',
    idempotencyKey: '88888888-8888-4888-8888-888888888888',
    invoiceId,
    bankTransactionId,
    amount: '25',
    currency: 'PLN',
    organizationId: organizationA,
    requestBody: `{"invoiceId":"${invoiceId}","bankTransactionId":"${bankTransactionId}","amount":{"amount":25,"currency":"PLN"}}`,
    lifecycle: 'ready',
  });
}

function historyHandler(items: string[]) {
  return http.get('/api/allocations', () => text(`{"items":[${items.join(',')}],"page":1,"pageSize":50,"totalCount":${items.length}}`));
}

function confirmedRow(invoiceNumber = 'INV-15'): string {
  return `{
    "allocationId":"${allocationId}",
    "invoiceId":"${invoiceId}",
    "bankTransactionId":"${bankTransactionId}",
    "invoiceNumber":"${invoiceNumber}",
    "reference":null,
    "amount":{"amount":15.5,"currency":"PLN"},
    "status":"confirmed",
    "confirmedAt":"2026-10-31T08:00:00+00:00",
    "reversedAt":null,
    "reversalReason":null,
    "idempotencyOperation":"exactMatch"
  }`;
}

function reversedRow(): string {
  return `{
    "allocationId":"${otherAllocationId}",
    "invoiceId":"${invoiceId}",
    "bankTransactionId":"${bankTransactionId}",
    "invoiceNumber":"INV-16",
    "reference":"REF",
    "amount":{"amount":15.5,"currency":"PLN"},
    "status":"reversed",
    "confirmedAt":"2026-10-31T08:00:00+00:00",
    "reversedAt":"2026-10-31T09:30:00+00:00",
    "reversalReason":"Duplicate payment",
    "idempotencyOperation":"explicitAmount"
  }`;
}

function contextBody(overrides: {
  allocationId?: string;
  status?: string;
  invoiceNumber?: string;
  reversedAt?: string;
  reversalReason?: string;
} = {}): string {
  return `{
    "allocationId":"${overrides.allocationId ?? allocationId}",
    "invoiceId":"${invoiceId}",
    "bankTransactionId":"${bankTransactionId}",
    "invoiceNumber":"${overrides.invoiceNumber ?? 'INV-15'}",
    "reference":null,
    "amount":{"amount":15.5,"currency":"PLN"},
    "status":"${overrides.status ?? 'confirmed'}",
    "confirmedAt":"2026-10-31T08:00:00+00:00",
    "reversedAt":${overrides.reversedAt ?? 'null'},
    "reversalReason":${overrides.reversalReason ?? 'null'}
  }`;
}

function reversalBody(reason = 'customer refund', replayed = false, amount = '15.5'): string {
  return `{
    "allocationId":"${allocationId}",
    "status":"reversed",
    "amount":{"amount":${amount},"currency":"PLN"},
    "reversedAt":"2026-10-31T10:15:00+00:00",
    "reason":${JSON.stringify(reason)},
    "replayed":${replayed ? 'true' : 'false'}
  }`;
}

function problem(status: number, code: string) {
  return HttpResponse.json({ title: 'Rejected', detail: 'Kept', code }, { status });
}

function text(body: string) {
  return HttpResponse.text(body, { headers: { 'Content-Type': 'application/json' } });
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
