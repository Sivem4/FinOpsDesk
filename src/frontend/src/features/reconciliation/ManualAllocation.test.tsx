import { QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router';
import { describe, expect, it, vi } from 'vitest';

import { queryClient } from '../../shared/api/query-client';
import { sessionStore } from '../../shared/api/session-store';
import { setAccessToken } from '../../shared/auth/session';
import { server } from '../../test/server';
import { explicitAmountAttemptStorageKey, resetAllocationAttemptStateForTests } from './allocation-attempt';
import { invalidateVerifiedAllocation } from './allocation-completion';
import { ReconciliationQueuePage } from './ReconciliationQueuePage';

const organizationA = '11111111-1111-1111-1111-111111111111';
const organizationB = '22222222-2222-2222-2222-222222222222';
const invoiceId = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const bankTransactionId = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
const otherInvoiceId = 'cccccccc-cccc-cccc-cccc-cccccccccccc';
const otherBankTransactionId = 'dddddddd-dddd-dddd-dddd-dddddddddddd';
const allocationId = 'eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee';

describe('manual allocation', () => {
  it('opens from a Smart-1 row and shows the allocation context without sending', async () => {
    const user = userEvent.setup();
    const calls: string[] = [];
    server.use(
      http.get('/api/matches', () => json(exactBook())),
      http.get('/api/matches/smart', () => json(smartBook())),
      http.get('/api/allocations/context', ({ request }) => {
        const url = new URL(request.url);
        calls.push(`${url.searchParams.get('invoiceId')}|${url.searchParams.get('bankTransactionId')}`);
        return json(contextBody());
      }),
      http.post('/api/allocations', () => {
        calls.push('POST');
        return allocationResult(201, false, '25');
      }),
    );
    renderQueue();

    expect(await screen.findByRole('button', { name: confirmName(invoiceId, bankTransactionId) })).toBeVisible();
    expect(screen.queryByRole('button', { name: /Allocate invoice/ })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Smart' }));
    await user.click(await screen.findByRole('button', { name: allocateName(invoiceId, bankTransactionId) }));

    expect(await screen.findByRole('heading', { name: 'Allocate an amount' })).toBeVisible();
    expect(screen.getByText('Invoice INV-100')).toBeVisible();
    expect(screen.getByText('Bank reference PAY-9')).toBeVisible();
    expect(screen.getByText('Booking date 2026-03-15')).toBeVisible();
    expect(screen.getByText('Invoice outstanding 60.00 PLN')).toBeVisible();
    expect(screen.getByText('Bank unapplied 40.00 PLN')).toBeVisible();
    expect(screen.getByText(/do not reserve/)).toBeVisible();
    expect(screen.getByText(/does not authorize/)).toBeVisible();
    expect(screen.getByLabelText('Allocation amount')).toHaveValue('25');
    expect(calls).toEqual([`${invoiceId}|${bankTransactionId}`]);
  });

  it('blocks a new allocation when currencies differ or a remaining amount is zero', async () => {
    const user = userEvent.setup();
    server.use(
      http.get('/api/matches', () => json(emptyExact())),
      http.get('/api/matches/smart', () => json(smartBook())),
      http.get('/api/allocations/context', () => json(contextBody({ bankCurrency: 'USD' }))),
    );
    const mismatch = renderQueue();
    await openSuggestion(user);
    expect(await screen.findByText(/does not convert currency/)).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Allocate this amount' })).not.toBeInTheDocument();
    mismatch.unmount();

    server.use(http.get('/api/allocations/context', () => json(contextBody({ outstanding: 0, unapplied: 40 }))));
    renderQueue();
    await openSuggestion(user);
    expect(await screen.findByText(/no remaining amount on the invoice/)).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Allocate this amount' })).not.toBeInTheDocument();
  });

  it('rejects invalid amounts and accepts a partial amount and the exact maximum', async () => {
    const user = userEvent.setup();
    const posts: Posted[] = [];
    server.use(
      http.get('/api/matches', () => json(emptyExact())),
      http.get('/api/matches/smart', () => json(smartBook())),
      http.get('/api/allocations/context', () => json(contextBody())),
      http.post('/api/allocations', async ({ request }) => {
        posts.push(await posted(request));
        const body = JSON.parse(posts[posts.length - 1]?.body ?? '{}') as { amount?: { amount?: number } };
        return allocationResult(201, false, String(body.amount?.amount));
      }),
    );
    renderQueue();
    await openSuggestion(user);
    const amount = await screen.findByLabelText('Allocation amount');

    await user.clear(amount);
    await user.type(amount, '0');
    expect(screen.getByRole('button', { name: 'Allocate this amount' })).toBeDisabled();
    await user.clear(amount);
    await user.type(amount, '-1');
    expect(screen.getByRole('button', { name: 'Allocate this amount' })).toBeDisabled();
    await user.clear(amount);
    await user.type(amount, '0.00001');
    expect(screen.getByRole('button', { name: 'Allocate this amount' })).toBeDisabled();
    await user.clear(amount);
    await user.type(amount, '1000000000000000');
    expect(screen.getByRole('button', { name: 'Allocate this amount' })).toBeDisabled();
    await user.clear(amount);
    await user.type(amount, '40.01');
    expect(screen.getByText(/larger than the smaller remaining balance/)).toBeVisible();
    expect(screen.getByRole('button', { name: 'Allocate this amount' })).toBeDisabled();
    expect(posts).toHaveLength(0);

    await user.clear(amount);
    await user.type(amount, '10.50');
    await user.click(screen.getByRole('button', { name: 'Allocate this amount' }));
    expect(await screen.findByText('Allocation recorded.')).toBeVisible();
    expect(posts[0]?.body).toContain('"amount":10.5');
    expect(posts[0]?.body).not.toContain('"amount":"10.5"');
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBeNull();

    await user.click(screen.getByRole('button', { name: 'Close' }));
    await user.click(screen.getByRole('button', { name: 'Done' }));
    await user.click(screen.getByRole('button', { name: allocateName(invoiceId, bankTransactionId) }));
    const second = await screen.findByLabelText('Allocation amount');
    await user.clear(second);
    await user.type(second, '40');
    await user.click(screen.getByRole('button', { name: 'Allocate this amount' }));
    expect(await screen.findByText('Allocation recorded.')).toBeVisible();
    expect(posts[1]?.body).toContain('"amount":40');
    expect(posts[1]?.key).not.toBe(posts[0]?.key);
  });

  it('stores the attempt before POST and does not send when storage fails', async () => {
    const user = userEvent.setup();
    const posts: string[] = [];
    server.use(
      http.get('/api/matches', () => json(emptyExact())),
      http.get('/api/matches/smart', () => json(smartBook())),
      http.get('/api/allocations/context', () => json(contextBody())),
      http.post('/api/allocations', async ({ request }) => {
        const key = request.headers.get('Idempotency-Key');
        expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toContain(key);
        posts.push('POST');
        return allocationResult(201, false, '25');
      }),
    );
    const saved = renderQueue();
    await openSuggestion(user);
    await user.click(await screen.findByRole('button', { name: 'Allocate this amount' }));
    expect(await screen.findByText('Allocation recorded.')).toBeVisible();
    expect(posts).toEqual(['POST']);
    saved.unmount();

    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('storage full');
    });
    server.use(http.post('/api/allocations', () => {
      posts.push('POST');
      return allocationResult(201, false, '25');
    }));
    try {
      renderQueue();
      await openSuggestion(user);
      await user.click(await screen.findByRole('button', { name: 'Allocate this amount' }));
      expect(await screen.findByText(/could not be stored/)).toBeVisible();
      expect(posts).toEqual(['POST']);
    } finally {
      vi.restoreAllMocks();
    }
  });

  it('shows a verified replay and ignores a second click while the request is running', async () => {
    const user = userEvent.setup();
    let release: () => void = () => {};
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    let posts = 0;
    server.use(
      http.get('/api/matches', () => json(emptyExact())),
      http.get('/api/matches/smart', () => json(smartBook())),
      http.get('/api/allocations/context', () => json(contextBody())),
      http.post('/api/allocations', async () => {
        posts += 1;
        await gate;
        return allocationResult(200, true, '25');
      }),
    );
    renderQueue();
    await openSuggestion(user);
    await user.click(await screen.findByRole('button', { name: 'Allocate this amount' }));
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Send the same allocation again' })).toBeDisabled();
    });
    release();
    expect(await screen.findByText('This allocation was already recorded.')).toBeVisible();
    expect(screen.getByText(`Allocation ${allocationId}`)).toBeVisible();
    expect(posts).toBe(1);
  });

  it('retries a lost response with the original key after balances become zero', async () => {
    const user = userEvent.setup();
    const posts: Posted[] = [];
    let fail = true;
    server.use(
      http.get('/api/matches', () => json(emptyExact())),
      http.get('/api/matches/smart', () => json(smartBook())),
      http.get('/api/allocations/context', () => json(fail ? contextBody() : contextBody({ outstanding: 0, unapplied: 0 }))),
      http.post('/api/allocations', async ({ request }) => {
        posts.push(await posted(request));
        if (fail) {
          return HttpResponse.error();
        }
        return allocationResult(200, true, '25');
      }),
    );
    renderQueue();
    await openSuggestion(user);
    await user.click(await screen.findByRole('button', { name: 'Allocate this amount' }));
    expect(await screen.findByText(/before a result was received/)).toBeVisible();
    fail = false;
    await user.click(screen.getByRole('button', { name: 'Refresh balances' }));
    expect(await screen.findByText('Invoice outstanding 0.00 PLN')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Send the same allocation again' })).toBeEnabled();
    await user.click(screen.getByRole('button', { name: 'Send the same allocation again' }));
    expect(await screen.findByText('This allocation was already recorded.')).toBeVisible();
    expect(posts).toHaveLength(2);
    expect(posts[1]?.key).toBe(posts[0]?.key);
    expect(posts[1]?.body).toBe(posts[0]?.body);
  });

  it('retries when the context read fails and the suggestion row is gone', async () => {
    const user = userEvent.setup();
    const posts: Posted[] = [];
    server.use(
      http.get('/api/matches', () => json(emptyExact())),
      http.get('/api/matches/smart', () => json(smartBook())),
      http.get('/api/allocations/context', () => json(contextBody())),
      http.post('/api/allocations', async ({ request }) => {
        posts.push(await posted(request));
        return HttpResponse.error();
      }),
    );
    const first = renderQueue();
    await openSuggestion(user);
    await user.click(await screen.findByRole('button', { name: 'Allocate this amount' }));
    expect(await screen.findByText(/before a result was received/)).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Close' }));
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toContain(posts[0]?.key ?? 'missing');
    first.unmount();

    server.use(
      http.get('/api/matches/smart', () => json(emptySmart())),
      http.get('/api/allocations/context', () => problem(500, 'reconciliation.allocation_context.inconsistent_money', 'Stored financial amounts are inconsistent.', 'The stored money is inconsistent.')),
      http.post('/api/allocations', async ({ request }) => {
        posts.push(await posted(request));
        return allocationResult(200, true, '25');
      }),
    );
    renderQueue();
    expect(await screen.findByText('An allocation attempt is stored for this organization')).toBeVisible();
    expect(screen.getByText(`Invoice ${invoiceId}`)).toBeVisible();
    expect(posts).toHaveLength(1);
    await user.click(screen.getByRole('button', { name: 'Review' }));
    expect(await screen.findByText(/You can still send the original stored attempt/)).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Send the same allocation again' }));
    expect(await screen.findByText('This allocation was already recorded.')).toBeVisible();
    expect(posts[1]?.key).toBe(posts[0]?.key);
    expect(posts[1]?.body).toBe(posts[0]?.body);
  });

  it('keeps the attempt when the dialog closes and removes it only after confirmation', async () => {
    const user = userEvent.setup();
    server.use(
      http.get('/api/matches', () => json(emptyExact())),
      http.get('/api/matches/smart', () => json(smartBook())),
      http.get('/api/allocations/context', () => json(contextBody())),
      http.post('/api/allocations', () => HttpResponse.error()),
    );
    renderQueue();
    await openSuggestion(user);
    await user.click(await screen.findByRole('button', { name: 'Allocate this amount' }));
    expect(await screen.findByText(/before a result was received/)).toBeVisible();
    const stored = sessionStorage.getItem(explicitAmountAttemptStorageKey);
    await user.click(screen.getByRole('button', { name: 'Close' }));
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBe(stored);
    await user.click(screen.getByRole('button', { name: 'Review' }));
    await user.click(screen.getByRole('button', { name: 'Stop tracking this attempt' }));
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBe(stored);
    await user.click(screen.getByRole('button', { name: 'Remove local tracking' }));
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBeNull();
  });

  it('does not replace a stored attempt from another suggestion', async () => {
    const user = userEvent.setup();
    server.use(
      http.get('/api/matches', () => json(emptyExact())),
      http.get('/api/matches/smart', () => json(smartBook(true))),
      http.get('/api/allocations/context', () => json(contextBody())),
      http.post('/api/allocations', () => HttpResponse.error()),
    );
    renderQueue();
    await openSuggestion(user);
    await user.click(await screen.findByRole('button', { name: 'Allocate this amount' }));
    expect(await screen.findByText(/before a result was received/)).toBeVisible();
    const stored = sessionStorage.getItem(explicitAmountAttemptStorageKey);
    await user.click(screen.getByRole('button', { name: 'Close' }));
    await user.click(screen.getByRole('button', { name: allocateName(otherInvoiceId, otherBankTransactionId) }));
    expect(await screen.findByText(/This row was not started/)).toBeVisible();
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBe(stored);
  });

  it('hides stored details for another organization and does not send after token renewal', async () => {
    const posts: string[] = [];
    server.use(
      http.get('/api/matches', () => json(emptyExact())),
      http.get('/api/matches/smart', () => json(emptySmart())),
      http.post('/api/allocations', () => {
        posts.push('POST');
        return allocationResult(201, false, '25');
      }),
    );
    sessionStorage.setItem(explicitAmountAttemptStorageKey, JSON.stringify(storedAttempt()));
    renderQueue();
    expect(await screen.findByText(`Invoice ${invoiceId}`)).toBeVisible();
    setAccessToken(tokenFor(organizationA), Math.floor(Date.now() / 1000) + 3600);
    expect(await screen.findByText('An allocation attempt is stored for this organization')).toBeVisible();
    expect(posts).toHaveLength(0);
    setAccessToken(tokenFor(organizationB), Math.floor(Date.now() / 1000) + 3600);
    expect(await screen.findByText(/another organization/)).toBeVisible();
    expect(screen.queryByText(`Invoice ${invoiceId}`)).not.toBeInTheDocument();
    expect(screen.queryByText(/Original request/)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Send the same allocation again' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Stop tracking this attempt' })).not.toBeInTheDocument();
    expect(posts).toHaveLength(0);
  });

  it('reports cleanup failure with and without a recoverable attempt', async () => {
    const user = userEvent.setup();
    server.use(
      http.get('/api/matches', () => json(emptyExact())),
      http.get('/api/matches/smart', () => json(smartBook())),
      http.get('/api/allocations/context', () => json(contextBody())),
      http.post('/api/allocations', () => allocationResult(201, false, '25')),
    );
    vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => {
      throw new Error('cleanup blocked');
    });
    const kept = renderQueue();
    try {
      await openSuggestion(user);
      await user.click(await screen.findByRole('button', { name: 'Allocate this amount' }));
      expect(await screen.findByText(/Send the same stored attempt again/)).toBeVisible();
      expect(screen.getByRole('button', { name: 'Send the same allocation again' })).toBeEnabled();
    } finally {
      vi.restoreAllMocks();
    }
    kept.unmount();
    sessionStorage.clear();
    resetAllocationAttemptStateForTests();

    const getItem = Storage.prototype.getItem;
    const removeItem = Storage.prototype.removeItem;
    let removed = false;
    vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(function (this: Storage, key: string) {
      if (key === explicitAmountAttemptStorageKey) {
        removed = true;
      }
      return removeItem.call(this, key);
    });
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(function (this: Storage, key: string) {
      if (removed && key === explicitAmountAttemptStorageKey) {
        throw new Error('post-removal read failed');
      }
      return getItem.call(this, key);
    });
    try {
      renderQueue();
      await openSuggestion(user);
      await user.click(await screen.findByRole('button', { name: 'Allocate this amount' }));
      expect(await screen.findByText(/no longer available/)).toBeVisible();
      expect(screen.queryByRole('button', { name: 'Send the same allocation again' })).not.toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'Allocate this amount' })).not.toBeInTheDocument();
    } finally {
      vi.restoreAllMocks();
    }
  });

  it('does not treat a changed stored attempt as resolved', async () => {
    const user = userEvent.setup();
    let release: () => void = () => {};
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(
      http.get('/api/matches', () => json(emptyExact())),
      http.get('/api/matches/smart', () => json(smartBook())),
      http.get('/api/allocations/context', () => json(contextBody())),
      http.post('/api/allocations', async () => {
        await gate;
        return allocationResult(201, false, '25');
      }),
    );
    renderQueue();
    await openSuggestion(user);
    await user.click(await screen.findByRole('button', { name: 'Allocate this amount' }));
    const stored = JSON.parse(sessionStorage.getItem(explicitAmountAttemptStorageKey) ?? '{}') as { idempotencyKey: string };
    const replacementKey = 'ffffffff-ffff-ffff-ffff-ffffffffffff';
    sessionStorage.setItem(explicitAmountAttemptStorageKey, JSON.stringify({ ...stored, idempotencyKey: replacementKey }));
    release();
    expect(await screen.findByText(/left unchanged/)).toBeVisible();
    expect(screen.queryByText('Allocation recorded.')).not.toBeInTheDocument();
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toContain(replacementKey);
  });

  it('does not invalidate caches for an unknown outcome or a stale session', async () => {
    const user = userEvent.setup();
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
    server.use(
      http.get('/api/matches', () => json(emptyExact())),
      http.get('/api/matches/smart', () => json(smartBook())),
      http.get('/api/allocations/context', () => json(contextBody())),
      http.post('/api/allocations', () => HttpResponse.error()),
    );
    const lost = renderQueue();
    await openSuggestion(user);
    await user.click(await screen.findByRole('button', { name: 'Allocate this amount' }));
    expect(await screen.findByText(/before a result was received/)).toBeVisible();
    expect(invalidate).not.toHaveBeenCalled();
    lost.unmount();
    invalidate.mockClear();

    let release: () => void = () => {};
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(http.post('/api/allocations', async () => {
      await gate;
      return allocationResult(201, false, '25');
    }));
    renderQueue();
    sessionStorage.clear();
    await openSuggestion(user);
    await user.click(await screen.findByRole('button', { name: 'Allocate this amount' }));
    setAccessToken(tokenFor(organizationB), Math.floor(Date.now() / 1000) + 3600);
    release();
    expect(await screen.findByText(/another organization/)).toBeVisible();
    expect(screen.queryByText('Allocation recorded.')).not.toBeInTheDocument();
    expect(invalidate).not.toHaveBeenCalled();
    invalidate.mockRestore();
  });

  it('invalidates financial caches after verified success, including when the page unmounts', async () => {
    const user = userEvent.setup();
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    server.use(
      http.get('/api/matches', () => json(emptyExact())),
      http.get('/api/matches/smart', () => json(smartBook())),
      http.get('/api/allocations/context', () => json(contextBody())),
      http.post('/api/allocations', () => allocationResult(201, false, '25')),
    );
    const mounted = renderQueue();
    await openSuggestion(user);
    await user.click(await screen.findByRole('button', { name: 'Allocate this amount' }));
    expect(await screen.findByText('Allocation recorded.')).toBeVisible();
    const epoch = sessionStore.getSnapshot().epoch;
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['invoices', epoch] });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['matches', epoch] });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['allocations', 'context', epoch, invoiceId, bankTransactionId] });
    mounted.unmount();
    invalidate.mockClear();

    let release: () => void = () => {};
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(http.post('/api/allocations', async () => {
      await gate;
      return allocationResult(201, false, '25');
    }));
    const leaving = renderQueue();
    const leavingEpoch = sessionStore.getSnapshot().epoch;
    await openSuggestion(user);
    await user.click(await screen.findByRole('button', { name: 'Allocate this amount' }));
    expect(await screen.findByText('Allocation request in progress')).toBeVisible();
    leaving.unmount();
    release();
    await waitFor(() => {
      expect(invalidate).toHaveBeenCalledWith({ queryKey: ['invoices', leavingEpoch] });
    });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['matches', leavingEpoch] });
    expect(invalidate).toHaveBeenCalledWith({
      queryKey: ['allocations', 'context', leavingEpoch, invoiceId, bankTransactionId],
    });
    expect(consoleError.mock.calls.some((call) => String(call[0]).includes('unmounted'))).toBe(false);
    consoleError.mockRestore();
    invalidate.mockRestore();
  });

  it('does not start a new allocation from context retained after a failed refresh', async () => {
    const user = userEvent.setup();
    const posts: string[] = [];
    let failRefresh = false;
    const randomUUID = vi.spyOn(crypto, 'randomUUID');
    server.use(
      http.get('/api/matches', () => json(emptyExact())),
      http.get('/api/matches/smart', () => json(smartBook())),
      http.get('/api/allocations/context', () => failRefresh
        ? problem(500, 'reconciliation.allocation_context.inconsistent_money', 'Stored financial amounts are inconsistent.', 'The stored money is inconsistent.')
        : json(contextBody())),
      http.post('/api/allocations', () => {
        posts.push('POST');
        return allocationResult(201, false, '25');
      }),
    );
    try {
      renderQueue();
      await openSuggestion(user);
      expect(await screen.findByText('Invoice INV-100')).toBeVisible();
      expect(screen.getByRole('button', { name: 'Allocate this amount' })).toBeEnabled();
      failRefresh = true;
      await user.click(screen.getByRole('button', { name: 'Refresh balances' }));
      expect(await screen.findByText(/Stored amounts for this pair are inconsistent/)).toBeVisible();
      const epoch = sessionStore.getSnapshot().epoch;
      expect(queryClient.getQueryData(['allocations', 'context', epoch, invoiceId, bankTransactionId])).toMatchObject({
        invoice: { invoiceNumber: 'INV-100' },
      });
      expect(screen.queryByRole('button', { name: 'Allocate this amount' })).not.toBeInTheDocument();
      expect(posts).toEqual([]);
      expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBeNull();
      expect(randomUUID).not.toHaveBeenCalled();
    } finally {
      randomUUID.mockRestore();
    }
  });

  it('does not allocate when the context response names a different pair', async () => {
    const user = userEvent.setup();
    const posts: string[] = [];
    server.use(
      http.get('/api/matches', () => json(emptyExact())),
      http.get('/api/matches/smart', () => json(smartBook())),
      http.get('/api/allocations/context', () => json(contextBody({ invoiceId: otherInvoiceId }))),
      http.post('/api/allocations', () => {
        posts.push('POST');
        return allocationResult(201, false, '25');
      }),
    );
    renderQueue();
    await openSuggestion(user);
    expect(await screen.findByText(/could not be read/)).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Allocate this amount' })).not.toBeInTheDocument();
    expect(posts).toEqual([]);
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBeNull();
  });

  it('hides a tenant-specific allocation error after the session changes and keeps the original attempt', async () => {
    const user = userEvent.setup();
    const posts: Posted[] = [];
    const sentinel = 'TENANT-A-LEDGER-77.77';
    let hideSuggestions = false;
    const randomUUID = vi.spyOn(crypto, 'randomUUID');
    server.use(
      http.get('/api/matches', () => json(emptyExact())),
      http.get('/api/matches/smart', () => json(hideSuggestions ? emptySmart() : smartBook())),
      http.get('/api/allocations/context', () => json(contextBody())),
      http.post('/api/allocations', async ({ request }) => {
        posts.push(await posted(request));
        if (posts.length === 1) {
          return problem(500, 'reconciliation.allocation.failed', 'Allocation was not confirmed.', sentinel);
        }
        return allocationResult(200, true, '25');
      }),
    );
    try {
      renderQueue();
      await openSuggestion(user);
      await user.click(await screen.findByRole('button', { name: 'Allocate this amount' }));
      expect(await screen.findByText(sentinel, { exact: false })).toBeVisible();
      const key = posts[0]?.key ?? '';
      const keysAfterCreate = randomUUID.mock.calls.length;
      hideSuggestions = true;
      setAccessToken(tokenFor(organizationB), Math.floor(Date.now() / 1000) + 3600);
      expect(await screen.findByText(/another organization/)).toBeVisible();
      expect(screen.queryByText(sentinel, { exact: false })).not.toBeInTheDocument();
      expect(screen.queryByText(key, { exact: false })).not.toBeInTheDocument();
      expect(screen.queryByText(`Invoice ${invoiceId}`)).not.toBeInTheDocument();
      expect(screen.queryByText(/Original request/)).not.toBeInTheDocument();
      expect(screen.queryByText('25.00 PLN', { exact: false })).not.toBeInTheDocument();
      expect(posts).toHaveLength(1);
      expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toContain(key);
      setAccessToken(tokenFor(organizationA), Math.floor(Date.now() / 1000) + 3600);
      expect(await screen.findByText(`Invoice ${invoiceId}`)).toBeVisible();
      expect(screen.queryByText(sentinel, { exact: false })).not.toBeInTheDocument();
      await user.click(screen.getByRole('button', { name: 'Send the same allocation again' }));
      expect(await screen.findByText('This allocation was already recorded.')).toBeVisible();
      expect(posts[1]?.key).toBe(key);
      expect(posts[1]?.body).toBe(posts[0]?.body);
      expect(randomUUID.mock.calls.length).toBe(keysAfterCreate);
    } finally {
      randomUUID.mockRestore();
    }
  });

  it('does not show an allocation error that resolves after the session epoch changes', async () => {
    const user = userEvent.setup();
    const posts: string[] = [];
    const sentinel = 'LATE-TENANT-A-SENTINEL';
    let release: () => void = () => {};
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(
      http.get('/api/matches', () => json(emptyExact())),
      http.get('/api/matches/smart', () => json(smartBook())),
      http.get('/api/allocations/context', () => json(contextBody())),
      http.post('/api/allocations', async () => {
        posts.push('POST');
        await gate;
        return problem(500, 'reconciliation.allocation.failed', 'Allocation was not confirmed.', sentinel);
      }),
    );
    renderQueue();
    await openSuggestion(user);
    await user.click(await screen.findByRole('button', { name: 'Allocate this amount' }));
    expect(await screen.findByText('Allocation request in progress')).toBeVisible();
    setAccessToken(tokenFor(organizationB), Math.floor(Date.now() / 1000) + 3600);
    release();
    expect(await screen.findByText(/another organization/)).toBeVisible();
    await waitFor(() => expect(posts).toHaveLength(1));
    expect(screen.queryByText(sentinel, { exact: false })).not.toBeInTheDocument();
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toContain(invoiceId);
    expect(posts).toHaveLength(1);
  });

  it('does not invalidate a result from another session epoch', () => {
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
    sessionStore.rotate(tokenFor(organizationA), Math.floor(Date.now() / 1000) + 3600, null);
    const epoch = sessionStore.getSnapshot().epoch;
    sessionStore.rotate(tokenFor(organizationB), Math.floor(Date.now() / 1000) + 3600, null);
    invalidateVerifiedAllocation({
      allocationId,
      invoiceId,
      bankTransactionId,
      amount: '25' as never,
      currency: 'PLN',
      replayed: false,
      epoch,
    });
    expect(invalidate).not.toHaveBeenCalled();
    invalidate.mockRestore();
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

async function openSuggestion(user: ReturnType<typeof userEvent.setup>) {
  await screen.findByText(/Nothing is open to classify|Exact matches/);
  await user.click(screen.getByRole('button', { name: 'Smart' }));
  await user.click(await screen.findByRole('button', { name: allocateName(invoiceId, bankTransactionId) }));
}

function allocateName(invoice: string, bankTransaction: string): string {
  return `Allocate invoice ${invoice} and bank transaction ${bankTransaction}`;
}

function confirmName(invoice: string, bankTransaction: string): string {
  return `Confirm exact match for invoice ${invoice} and bank transaction ${bankTransaction}`;
}

interface Posted {
  key: string | null;
  body: string;
}

async function posted(request: Request): Promise<Posted> {
  return { key: request.headers.get('Idempotency-Key'), body: await request.text() };
}

function json(body: object) {
  return HttpResponse.json(body);
}

function problem(status: number, code: string, title: string, detail: string) {
  return HttpResponse.json({ title, detail, code }, { status });
}

function allocationResult(status: 200 | 201, replayed: boolean, amount: string) {
  return new HttpResponse(
    `{"allocationId":"${allocationId}","invoiceId":"${invoiceId}","bankTransactionId":"${bankTransactionId}","amount":{"amount":${amount},"currency":"PLN"},"replayed":${replayed}}`,
    { status, headers: { 'Content-Type': 'application/json' } },
  );
}

function contextBody(overrides: {
  outstanding?: number;
  unapplied?: number;
  bankCurrency?: string;
  invoiceId?: string;
  bankTransactionId?: string;
} = {}) {
  const bankCurrency = overrides.bankCurrency ?? 'PLN';
  return {
    invoice: {
      invoiceId: overrides.invoiceId ?? invoiceId,
      invoiceNumber: 'INV-100',
      totalAmount: { amount: 100, currency: 'PLN' },
      allocatedAmount: { amount: 40, currency: 'PLN' },
      outstandingAmount: { amount: overrides.outstanding ?? 60, currency: 'PLN' },
      sourceSystem: 'DEMO',
      externalId: 'ext-inv',
    },
    bankTransaction: {
      bankTransactionId: overrides.bankTransactionId ?? bankTransactionId,
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

function emptyExact() {
  return { algorithmVersion: 'exact-1', exactMatches: [], ambiguousGroups: [], unmatched: [], ineligible: [] };
}

function exactBook() {
  return {
    ...emptyExact(),
    exactMatches: [{
      invoiceId,
      bankTransactionId,
      amount: { amount: 10, currency: 'PLN' },
      normalizedReference: 'REF-Z',
      reason: 'ExactReferenceCurrencyAndOpenAmount',
    }],
  };
}

function emptySmart() {
  return { algorithmVersion: 'smart-1', advisory: true, truncated: false, coverageLimited: false, suggestions: [] };
}

function smartBook(second = false) {
  return {
    ...emptySmart(),
    suggestions: [
      suggestion(1, invoiceId, bankTransactionId, false),
      ...(second ? [suggestion(2, otherInvoiceId, otherBankTransactionId, true)] : []),
    ],
  };
}

function suggestion(rank: number, invoice: string, bankTransaction: string, differs: boolean) {
  return {
    rank,
    invoiceId: invoice,
    bankTransactionId: bankTransaction,
    score: differs ? 70 : 95,
    scoreScale: 'points',
    contested: false,
    exactMatchAmbiguous: false,
    invoiceOutstanding: { amount: 25, currency: 'PLN' },
    bankTransactionUnapplied: { amount: differs ? 40 : 25, currency: 'PLN' },
    suggestedAmount: { amount: 25, currency: 'PLN' },
    signals: differs
      ? [{ code: 'ReferenceExact', points: 70 }, { code: 'AmountDiffers', points: 0 }]
      : [{ code: 'ReferenceExact', points: 70 }, { code: 'AmountExact', points: 25 }],
  };
}

function storedAttempt() {
  return {
    schemaVersion: 1,
    operation: 'ExplicitAmount',
    idempotencyKey: '99999999-9999-9999-9999-999999999999',
    invoiceId,
    bankTransactionId,
    amount: '25',
    currency: 'PLN',
    organizationId: organizationA,
    requestBody: `{"invoiceId":"${invoiceId}","bankTransactionId":"${bankTransactionId}","amount":{"amount":25,"currency":"PLN"}}`,
    lifecycle: 'ready',
  };
}

function tokenFor(organizationId: string, subject: string | null = 'operator-1'): string {
  const payload: Record<string, unknown> = {
    exp: Math.floor(Date.now() / 1000) + 3600,
    org_id: organizationId,
  };
  if (subject !== null) {
    payload.sub = subject;
  }
  return `${encode({ alg: 'HS256', typ: 'JWT' })}.${encode(payload)}.signature`;
}

function encode(value: object): string {
  return btoa(JSON.stringify(value)).replace(/=+$/g, '').replace(/\+/g, '-').replace(/\//g, '_');
}
