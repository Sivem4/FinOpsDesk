import { QueryClientProvider, useQuery } from '@tanstack/react-query';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';

vi.mock('../../shared/auth/auth-mode', () => ({
  browserAuthMode: () => 'cookie',
}));

import { queryClient } from '../../shared/api/query-client';
import { sessionStore } from '../../shared/api/session-store';
import { prepareCookieFinancialPost } from '../../shared/api/cookie-financial-post';
import { cookieRecoveryKeys } from '../../shared/api/cookie-recovery';
import { readMoneyAmount, type DecimalString } from '../../shared/money/money';
import { explicitAmountAttemptStorageKey } from './allocation-attempt';
import { server } from '../../test/server';
import {
  CookieAllocationForm,
  CookieConfirmationDialog,
  CookieReversalControl,
  CookieReversalOutcome,
  CookieReversalRecovery,
} from './CookieFinancialWrites';

const invoiceA = '11111111-1111-4111-8111-111111111111';
const invoiceB = '33333333-3333-4333-8333-333333333333';
const bankA = '22222222-2222-4222-8222-222222222222';
const bankB = '44444444-4444-4444-8444-444444444444';
const allocationA = '55555555-5555-4555-8555-555555555555';
const allocationB = '66666666-6666-4666-8666-666666666666';
const createdAllocation = '77777777-7777-4777-8777-777777777777';

describe('cookie financial writes', () => {
  it('blocks a second confirmation and keeps the original key', async () => {
    signIn();
    let calls = 0;
    server.use(http.post('/api/matches/confirm', () => {
      calls += 1;
      return new Promise(() => undefined);
    }));
    renderDialog(pair(invoiceA, bankA));
    fireEvent.click(screen.getByRole('button', { name: 'Confirm allocation' }));
    fireEvent.click(screen.getByRole('button', { name: 'Confirm allocation' }));
    await waitFor(() => expect(calls).toBe(1));
    const stored = sessionStorage.getItem(cookieRecoveryKeys.ConfirmExactMatch);
    renderDialog(pair(invoiceB, bankB));
    const dialogs = screen.getAllByRole('dialog');
    expect(within(dialogs[dialogs.length - 1]).getByRole('button', { name: 'Confirm allocation' })).toBeDisabled();
    expect(screen.getAllByRole('button', { name: 'Send the same request again', hidden: true }).length).toBeGreaterThan(0);
    expect(sessionStorage.getItem(cookieRecoveryKeys.ConfirmExactMatch)).toBe(stored);
    expect(calls).toBe(1);
  });

  it('blocks a second allocation and a second reversal without a new key', async () => {
    signIn();
    server.use(http.post('/api/allocations', () => new Promise(() => undefined)));
    renderAllocation();
    fireEvent.change(screen.getByLabelText('Invoice'), { target: { value: invoiceA } });
    fireEvent.change(screen.getByLabelText('Bank transaction'), { target: { value: bankA } });
    fireEvent.change(screen.getByLabelText('Amount'), { target: { value: '10.50' } });
    fireEvent.click(screen.getByRole('button', { name: 'Allocate an amount' }));
    await waitFor(() => expect(sessionStorage.getItem(cookieRecoveryKeys.CreateAllocation)).toContain(invoiceA));
    const allocation = sessionStorage.getItem(cookieRecoveryKeys.CreateAllocation);
    fireEvent.change(screen.getByLabelText('Invoice'), { target: { value: invoiceB } });
    fireEvent.click(screen.getByRole('button', { name: 'Allocate an amount' }));
    expect(sessionStorage.getItem(cookieRecoveryKeys.CreateAllocation)).toBe(allocation);

    server.use(http.post('/api/allocations/:id/reverse', () => new Promise(() => undefined)));
    renderReversal(allocationA);
    fireEvent.click(screen.getByRole('button', { name: 'Reverse allocation' }));
    const dialog = screen.getByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText('Reason'), { target: { value: 'duplicate receipt' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Reverse allocation' }));
    await waitFor(() => expect(sessionStorage.getItem(cookieRecoveryKeys.ReverseAllocation)).toContain(allocationA));
    const reversal = sessionStorage.getItem(cookieRecoveryKeys.ReverseAllocation);
    renderReversal(allocationB);
    fireEvent.click(screen.getAllByRole('button', { name: 'Reverse allocation' }).at(-1)!);
    expect(sessionStorage.getItem(cookieRecoveryKeys.ReverseAllocation)).toBe(reversal);
  });

  it('sends the stored reversal after a lost response and ignores a tampered allocation id', async () => {
    signIn();
    const seen: Array<{ url: string; key: string | null; authorization: string | null; organization: string | null; csrf: string | null }> = [];
    server.use(http.post('/api/allocations/:id/reverse', ({ request }) => {
      seen.push({
        url: request.url,
        key: request.headers.get('Idempotency-Key'),
        authorization: request.headers.get('Authorization'),
        organization: request.headers.get('X-Organization-Id'),
        csrf: request.headers.get('X-CSRF-Token'),
      });
      return HttpResponse.json({ replayed: false });
    }));
    prepareCookieFinancialPost('ReverseAllocation', JSON.stringify({ reason: 'duplicate receipt' }), {
      allocationId: allocationA,
      invoiceId: invoiceA,
      bankTransactionId: bankA,
      reason: 'duplicate receipt',
      amount: '10.5',
      currency: 'PLN',
    }, '88888888-8888-4888-8888-888888888888');
    renderRecovery();
    expect(await screen.findByRole('button', { name: 'Send the same request again' })).toBeVisible();
    fireEvent.click(screen.getByRole('button', { name: 'Send the same request again' }));
    await waitFor(() => expect(seen).toHaveLength(1));
    expect(seen[0]?.authorization).toBeNull();
    expect(seen[0]?.organization).toBeNull();
    expect(seen[0]?.csrf).toBe('csrf-session');
    expect(seen[0]?.url).toContain(allocationA);
    expect(sessionStorage.getItem(cookieRecoveryKeys.ReverseAllocation)).not.toBeNull();
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBeNull();

    const tampered = JSON.parse(sessionStorage.getItem(cookieRecoveryKeys.ReverseAllocation) ?? '{}') as { context: { allocationId: string } };
    tampered.context.allocationId = allocationB;
    sessionStorage.setItem(cookieRecoveryKeys.ReverseAllocation, JSON.stringify(tampered));
    sessionStore.replaceCookieSession(sessionStore.getSnapshot(), false);
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Send the same request again' })).not.toBeInTheDocument());
    expect(seen).toHaveLength(1);
  });

  it('keeps an uncertain descriptor when the body arrives after an organization switch', async () => {
    signIn();
    let release: ((bytes: Uint8Array) => void) | undefined;
    server.use(http.post('/api/matches/confirm', () => new HttpResponse(new ReadableStream({
      start(controller) {
        release = (bytes) => {
          controller.enqueue(bytes);
          controller.close();
        };
      },
    }), { status: 201, headers: { 'Content-Type': 'application/json' } })));
    queryClient.setQueryData(['matches', 99, 0], { foreign: true });
    renderDialog(pair(invoiceA, bankA));
    fireEvent.click(screen.getByRole('button', { name: 'Confirm allocation' }));
    await waitFor(() => expect(sessionStorage.getItem(cookieRecoveryKeys.ConfirmExactMatch)).toContain(invoiceA));
    const stored = sessionStorage.getItem(cookieRecoveryKeys.ConfirmExactMatch);
    sessionStore.replaceCookieSession({
      ...cookieSession(),
      subject: 'other-subject',
    }, true);
    const current = sessionStore.getSnapshot();
    queryClient.setQueryData(['matches', current.epoch, current.readiness], { foreign: true });
    release?.(new TextEncoder().encode(JSON.stringify(successBody(false))));
    await waitFor(() => expect(screen.queryByText('Allocation recorded.')).not.toBeInTheDocument());
    expect(sessionStorage.getItem(cookieRecoveryKeys.ConfirmExactMatch)).toBe(stored);
    expect(queryClient.getQueryData(['matches', current.epoch, current.readiness])).toEqual({ foreign: true });
  });

  it('does not confirm a malformed success body or an HTTP 500', async () => {
    signIn();
    server.use(http.post('/api/matches/confirm', () => HttpResponse.json({ replayed: false }, { status: 201 })));
    renderDialog(pair(invoiceA, bankA));
    fireEvent.click(screen.getByRole('button', { name: 'Confirm allocation' }));
    expect(await screen.findByText(/outcome is uncertain/i)).toBeInTheDocument();
    expect(sessionStorage.getItem(cookieRecoveryKeys.ConfirmExactMatch)).toContain(invoiceA);
    cleanup();
    sessionStorage.clear();

    server.use(http.post('/api/allocations', () => new HttpResponse('unavailable', { status: 500 })));
    renderAllocation();
    fireEvent.change(screen.getByLabelText('Invoice'), { target: { value: invoiceA } });
    fireEvent.change(screen.getByLabelText('Bank transaction'), { target: { value: bankA } });
    fireEvent.change(screen.getByLabelText('Amount'), { target: { value: '10.50' } });
    fireEvent.click(screen.getByRole('button', { name: 'Allocate an amount' }));
    expect((await screen.findAllByText(/does not show that the server rolled/i)).length).toBeGreaterThan(0);
    expect(screen.queryByText('This attempt was rejected. The original idempotency key was not replaced.')).not.toBeInTheDocument();
    expect(sessionStorage.getItem(cookieRecoveryKeys.CreateAllocation)).toContain(invoiceA);
  });

  it('hides recovery after role revocation and keeps the original body', async () => {
    signIn();
    const record = prepareCookieFinancialPost('ReverseAllocation', JSON.stringify({ reason: 'duplicate receipt' }), {
      allocationId: allocationA,
      invoiceId: invoiceA,
      bankTransactionId: bankA,
      reason: 'duplicate receipt',
      amount: '10.5',
      currency: 'PLN',
    }, '88888888-8888-4888-8888-888888888888');
    renderRecovery();
    expect(await screen.findByRole('button', { name: 'Send the same request again' })).toBeVisible();
    sessionStore.replaceCookieSession({
      ...cookieSession(),
      capabilities: ['allocations.read'],
    }, true);
    expect(await screen.findByText(/was not rolled back/i)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Send the same request again' })).not.toBeInTheDocument();
    expect(sessionStorage.getItem(cookieRecoveryKeys.ReverseAllocation)).toContain(record.idempotencyKey);
  });

  it('does not describe a confirmed allocation as not sent when cleanup fails', async () => {
    signIn();
    const original = Storage.prototype.removeItem;
    const remove = vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(function (this: Storage, key: string) {
      if (key === cookieRecoveryKeys.CreateAllocation) {
        throw new Error('locked');
      }

      return original.call(this, key);
    });
    server.use(http.post('/api/allocations', () => HttpResponse.json(successBody(false), { status: 201 })));
    renderAllocation();
    fireEvent.change(screen.getByLabelText('Invoice'), { target: { value: invoiceA } });
    fireEvent.change(screen.getByLabelText('Bank transaction'), { target: { value: bankA } });
    fireEvent.change(screen.getByLabelText('Amount'), { target: { value: '10.50' } });
    fireEvent.click(screen.getByRole('button', { name: 'Allocate an amount' }));
    expect(await screen.findByText(/could not remove the local recovery record/i)).toBeVisible();
    expect(screen.queryByText(/not sent/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/rolled back/i)).toBeInTheDocument();
    expect(sessionStorage.getItem(cookieRecoveryKeys.CreateAllocation)).toContain(invoiceA);
    remove.mockRestore();
  });

  it('keeps a confirmed allocation when the active history refetch fails', async () => {
    const reasons: unknown[] = [];
    const onUnhandled = (reason: unknown) => {
      reasons.push(reason);
    };
    process.on('unhandledRejection', onUnhandled);
    try {
      signIn();
      const origin = sessionStore.getSnapshot();
      const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
      const traffic = installAllocationTraffic({
        status: 201,
        replayed: false,
        onRead: (count) => count === 1
          ? HttpResponse.json({ items: [] })
          : HttpResponse.json({ message: 'unavailable' }, { status: 500 }),
      });
      renderReadAllocation(origin.epoch, origin.readiness);
      await waitFor(() => expect(queryClient.getQueryState(['allocations', 'history', origin.epoch, origin.readiness])?.status).toBe('success'));
      fillAllocation();
      expect(await screen.findByText('Allocation recorded.')).toBeVisible();
      expect(await screen.findByText(/could not be refreshed/i)).toBeVisible();
      expect(screen.getByText(/may be outdated/i)).toBeVisible();
      expect(screen.queryByText(/outcome is uncertain/i)).not.toBeInTheDocument();
      expect(screen.queryByText(/not sent/i)).not.toBeInTheDocument();
      expect(screen.queryByText(/rolled back/i)).not.toBeInTheDocument();
      expect(traffic.posts).toHaveLength(1);
      expect(sessionStorage.getItem(cookieRecoveryKeys.CreateAllocation)).toBeNull();
      expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBeNull();
      expect(sessionStorage.getItem('finopsdesk.reversal-attempt')).toBeNull();
      expect(invalidate).toHaveBeenCalledWith(
        { queryKey: ['allocations', 'history', origin.epoch, origin.readiness], refetchType: 'active' },
        { throwOnError: true },
      );
      expect(invalidate).not.toHaveBeenCalledWith(
        { queryKey: ['matches', origin.epoch, origin.readiness], refetchType: 'active' },
        { throwOnError: true },
      );
      await Promise.resolve();
      expect(reasons).toEqual([]);
    } finally {
      process.off('unhandledRejection', onUnhandled);
      vi.restoreAllMocks();
    }
  });

  it('keeps a replayed allocation confirmed when the active history refetch fails', async () => {
    signIn();
    const origin = sessionStore.getSnapshot();
    const traffic = installAllocationTraffic({
      status: 200,
      replayed: true,
      onRead: (count) => count === 1
        ? HttpResponse.json({ items: [] })
        : HttpResponse.json({ message: 'unavailable' }, { status: 500 }),
    });
    renderReadAllocation(origin.epoch, origin.readiness);
    await waitFor(() => expect(queryClient.getQueryState(['allocations', 'history', origin.epoch, origin.readiness])?.status).toBe('success'));
    fillAllocation();
    expect(await screen.findByText('Allocation recorded.')).toBeVisible();
    expect(await screen.findByText(/could not be refreshed/i)).toBeVisible();
    expect(traffic.posts).toHaveLength(1);
    expect(sessionStorage.getItem(cookieRecoveryKeys.CreateAllocation)).toBeNull();
  });

  it('retries only the financial read and clears the stale warning when the read succeeds', async () => {
    signIn();
    const origin = sessionStore.getSnapshot();
    let failReads = true;
    const traffic = installAllocationTraffic({
      status: 201,
      replayed: false,
      onRead: (count) => count === 1 || !failReads
        ? HttpResponse.json({ items: [] })
        : HttpResponse.json({ message: 'unavailable' }, { status: 500 }),
    });
    renderReadAllocation(origin.epoch, origin.readiness);
    await waitFor(() => expect(queryClient.getQueryState(['allocations', 'history', origin.epoch, origin.readiness])?.status).toBe('success'));
    fillAllocation();
    expect(await screen.findByRole('button', { name: 'Refresh displayed data' })).toBeVisible();
    const key = traffic.posts[0];
    failReads = false;
    fireEvent.click(screen.getByRole('button', { name: 'Refresh displayed data' }));
    await waitFor(() => expect(screen.queryByText(/could not be refreshed/i)).not.toBeInTheDocument());
    expect(screen.getByText('Allocation recorded.')).toBeVisible();
    expect(traffic.posts).toEqual([key]);
    expect(sessionStorage.getItem(cookieRecoveryKeys.CreateAllocation)).toBeNull();
  });

  it('keeps the stale warning when the manual read refresh fails again', async () => {
    signIn();
    const origin = sessionStore.getSnapshot();
    let release: (() => void) | undefined;
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    let reads = 0;
    const posts: string[] = [];
    server.use(
      http.get('/api/allocations', async () => {
        reads += 1;
        if (reads === 1) {
          return HttpResponse.json({ items: [] });
        }

        if (reads === 2) {
          return HttpResponse.json({ message: 'unavailable' }, { status: 500 });
        }

        await held;
        return HttpResponse.json({ message: 'unavailable' }, { status: 500 });
      }),
      http.post('/api/allocations', ({ request }) => {
        posts.push(request.headers.get('Idempotency-Key') ?? '');
        return HttpResponse.json(successBody(false), { status: 201 });
      }),
    );
    renderReadAllocation(origin.epoch, origin.readiness);
    await waitFor(() => expect(queryClient.getQueryState(['allocations', 'history', origin.epoch, origin.readiness])?.status).toBe('success'));
    fillAllocation();
    expect(await screen.findByRole('button', { name: 'Refresh displayed data' })).toBeEnabled();
    fireEvent.click(screen.getByRole('button', { name: 'Refresh displayed data' }));
    await waitFor(() => expect(reads).toBe(3));
    expect(screen.getByRole('button', { name: 'Refresh displayed data' })).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: 'Refresh displayed data' }));
    expect(reads).toBe(3);
    expect(posts).toHaveLength(1);
    release?.();
    expect(await screen.findByText(/could not be refreshed/i)).toBeVisible();
    expect(screen.getByText('Allocation recorded.')).toBeVisible();
    expect(posts).toHaveLength(1);
    expect(sessionStorage.getItem(cookieRecoveryKeys.CreateAllocation)).toBeNull();
  });

  it('does not treat a missing active query as a failed refresh', async () => {
    signIn();
    const origin = sessionStore.getSnapshot();
    let failReads = false;
    server.use(
      http.get('/api/allocations', () => failReads
        ? HttpResponse.json({ message: 'unavailable' }, { status: 500 })
        : HttpResponse.json({ items: [] })),
      http.post('/api/allocations', () => HttpResponse.json(successBody(false), { status: 201 })),
    );
    await queryClient.prefetchQuery({
      queryKey: ['allocations', 'history', origin.epoch, origin.readiness],
      queryFn: async () => {
        const response = await fetch('/api/allocations');
        if (!response.ok) {
          throw new Error('history unavailable');
        }

        return response.json();
      },
    });
    failReads = true;
    renderAllocation();
    fillAllocation();
    expect(await screen.findByText('Allocation recorded.')).toBeVisible();
    await waitFor(() => expect(queryClient.isFetching()).toBe(0));
    expect(screen.queryByText(/could not be refreshed/i)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Refresh displayed data' })).not.toBeInTheDocument();
  });

  it('does not show a stale warning after the organization changes during refresh', async () => {
    await expectStaleRefreshStaysHidden(() => {
      sessionStore.replaceCookieSession({
        ...cookieSession(),
        organizations: [{ organizationId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', displayName: 'South' }],
        activeOrganization: { organizationId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', displayName: 'South' },
      }, true);
    });
  });

  it('does not show a stale warning after logout during refresh', async () => {
    await expectStaleRefreshStaysHidden(() => {
      sessionStore.replaceCookieSession({
        authenticated: false,
        csrfToken: null,
        issuer: null,
        subject: null,
        displayName: null,
        expiresAt: null,
        organizations: [],
        activeOrganization: null,
        authorization: 'anonymous',
        capabilities: [],
      }, true);
    });
  });

  it('does not publish a stale warning after the write capability is removed during refresh', async () => {
    await expectStaleRefreshStaysHidden(() => {
      sessionStore.replaceCookieSession({
        ...cookieSession(),
        capabilities: ['allocations.read'],
      }, true);
    });
  });

  it('does not publish a stale warning after readiness changes during refresh', async () => {
    await expectStaleRefreshStaysHidden(() => {
      sessionStore.markAuthorizationUnavailable();
    });
  });

  it('keeps the recovery cleanup warning when the read refresh also fails', async () => {
    signIn();
    const origin = sessionStore.getSnapshot();
    const original = Storage.prototype.removeItem;
    const remove = vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(function (this: Storage, key: string) {
      if (key === cookieRecoveryKeys.CreateAllocation) {
        throw new Error('locked');
      }

      return original.call(this, key);
    });
    const traffic = installAllocationTraffic({
      status: 201,
      replayed: false,
      onRead: (count) => count === 1
        ? HttpResponse.json({ items: [] })
        : HttpResponse.json({ message: 'unavailable' }, { status: 500 }),
    });
    renderReadAllocation(origin.epoch, origin.readiness);
    await waitFor(() => expect(queryClient.getQueryState(['allocations', 'history', origin.epoch, origin.readiness])?.status).toBe('success'));
    fillAllocation();
    expect(await screen.findByText(/could not remove the local recovery record/i)).toBeVisible();
    expect(await screen.findByText(/could not be refreshed/i)).toBeVisible();
    expect(screen.getByText(/was not rolled back/i)).toBeVisible();
    expect(screen.queryByText(/not sent/i)).not.toBeInTheDocument();
    expect(traffic.posts).toHaveLength(1);
    expect(sessionStorage.getItem(cookieRecoveryKeys.CreateAllocation)).toContain(invoiceA);
    remove.mockRestore();
  });

  it('keeps a confirmed match when the active match refetch fails and scopes both read keys', async () => {
    signIn();
    const origin = sessionStore.getSnapshot();
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
    let matchReads = 0;
    const posts: string[] = [];
    server.use(
      http.get('/api/matches', () => {
        matchReads += 1;
        return matchReads === 1
          ? HttpResponse.json({ items: [] })
          : HttpResponse.json({ message: 'unavailable' }, { status: 500 });
      }),
      http.get('/api/allocations', () => HttpResponse.json({ items: [] })),
      http.post('/api/matches/confirm', ({ request }) => {
        posts.push(request.headers.get('Idempotency-Key') ?? '');
        return HttpResponse.json(successBody(false), { status: 201 });
      }),
    );
    render(
      <QueryClientProvider client={queryClient}>
        <FrozenRead queryKey={['matches', origin.epoch, origin.readiness, 'exact']} path="/api/matches" />
        <FrozenRead queryKey={['allocations', 'history', origin.epoch, origin.readiness]} path="/api/allocations" />
        <CookieConfirmationDialog item={pair(invoiceA, bankA)} onClose={() => undefined} />
      </QueryClientProvider>,
    );
    await waitFor(() => expect(queryClient.getQueryState(['matches', origin.epoch, origin.readiness, 'exact'])?.status).toBe('success'));
    fireEvent.click(screen.getByRole('button', { name: 'Confirm allocation' }));
    expect(await screen.findByText('Allocation recorded.')).toBeVisible();
    expect(await screen.findByText(/could not be refreshed/i)).toBeVisible();
    expect(posts).toHaveLength(1);
    expect(sessionStorage.getItem(cookieRecoveryKeys.ConfirmExactMatch)).toBeNull();
    expect(invalidate).toHaveBeenCalledWith(
      { queryKey: ['matches', origin.epoch, origin.readiness], refetchType: 'active' },
      { throwOnError: true },
    );
    expect(invalidate).toHaveBeenCalledWith(
      { queryKey: ['allocations', 'history', origin.epoch, origin.readiness], refetchType: 'active' },
      { throwOnError: true },
    );
  });

  it('keeps a recovered reversal visible after the descriptor is removed and the history refresh fails', async () => {
    signIn();
    const origin = sessionStore.getSnapshot();
    prepareCookieFinancialPost('ReverseAllocation', JSON.stringify({ reason: 'duplicate receipt' }), reversalContext(), '88888888-8888-4888-8888-888888888888');
    const posts: string[] = [];
    server.use(
      http.get('/api/allocations', () => HttpResponse.json({ message: 'unavailable' }, { status: 500 })),
      http.post('/api/allocations/:id/reverse', ({ request }) => {
        posts.push(request.headers.get('Idempotency-Key') ?? '');
        return HttpResponse.json(reversalBody(false), { status: 200 });
      }),
    );
    render(
      <QueryClientProvider client={queryClient}>
        <CookieReversalOutcome>
          <FrozenRead queryKey={['allocations', 'history', origin.epoch, origin.readiness]} path="/api/allocations" />
          <CookieReversalRecovery />
        </CookieReversalOutcome>
      </QueryClientProvider>,
    );
    await waitFor(() => expect(queryClient.getQueryState(['allocations', 'history', origin.epoch, origin.readiness])?.status).toBe('error'));
    fireEvent.click(screen.getByRole('button', { name: 'Send the same request again' }));
    expect(await screen.findByText('Reversal recorded.')).toBeVisible();
    expect(await screen.findByRole('button', { name: 'Refresh displayed data' })).toBeVisible();
    expect(sessionStorage.getItem(cookieRecoveryKeys.ReverseAllocation)).toBeNull();
    expect(screen.queryByRole('button', { name: 'Send the same request again' })).not.toBeInTheDocument();
    expect(posts).toHaveLength(1);
  });

  it('keeps a new reversal visible after the row is no longer confirmed', async () => {
    signIn();
    const origin = sessionStore.getSnapshot();
    let failReads = true;
    const posts: string[] = [];
    server.use(
      http.get('/api/allocations', () => failReads
        ? HttpResponse.json({ message: 'unavailable' }, { status: 500 })
        : HttpResponse.json({ items: [] })),
      http.post('/api/allocations/:id/reverse', ({ request }) => {
        posts.push(request.headers.get('Idempotency-Key') ?? '');
        return HttpResponse.json(reversalBody(false), { status: 200 });
      }),
    );
    render(
      <QueryClientProvider client={queryClient}>
        <CookieReversalOutcome>
          <ReversalRowHarness epoch={origin.epoch} readiness={origin.readiness} />
        </CookieReversalOutcome>
      </QueryClientProvider>,
    );
    await waitFor(() => expect(queryClient.getQueryState(['allocations', 'history', origin.epoch, origin.readiness])?.status).toBe('error'));
    fireEvent.click(screen.getByRole('button', { name: 'Reverse allocation' }));
    fireEvent.change(screen.getByLabelText('Reason'), { target: { value: 'duplicate receipt' } });
    fireEvent.click(screen.getAllByRole('button', { name: 'Reverse allocation' }).at(-1)!);
    expect(await screen.findByText('Reversal recorded.')).toBeVisible();
    fireEvent.click(screen.getByRole('button', { name: 'Show reversed row', hidden: true }));
    expect(screen.getByText('Reversed')).toBeVisible();
    expect(screen.getByText('Reversal recorded.')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Refresh displayed data' })).toBeVisible();
    failReads = false;
    fireEvent.click(screen.getByRole('button', { name: 'Refresh displayed data' }));
    await waitFor(() => expect(screen.queryByText(/could not be refreshed/i)).not.toBeInTheDocument());
    expect(screen.getByText('Reversal recorded.')).toBeVisible();
    expect(posts).toHaveLength(1);
    expect(sessionStorage.getItem(cookieRecoveryKeys.ReverseAllocation)).toBeNull();
  });

  it('hides reversal feedback after an organization switch during refresh', async () => {
    signIn();
    const origin = sessionStore.getSnapshot();
    let release: (() => void) | undefined;
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    let reads = 0;
    server.use(
      http.get('/api/allocations', async () => {
        reads += 1;
        if (reads === 1) {
          return HttpResponse.json({ items: [] });
        }

        await held;
        return HttpResponse.json({ message: 'unavailable' }, { status: 500 });
      }),
      http.post('/api/allocations/:id/reverse', () => HttpResponse.json(reversalBody(false), { status: 200 })),
    );
    render(
      <QueryClientProvider client={queryClient}>
        <CookieReversalOutcome>
          <FrozenRead queryKey={['allocations', 'history', origin.epoch, origin.readiness]} path="/api/allocations" />
          <CookieReversalControl allocationId={allocationA} invoiceId={invoiceA} bankTransactionId={bankA} amount={readMoneyAmount('10.5')} currency="PLN" />
        </CookieReversalOutcome>
      </QueryClientProvider>,
    );
    await waitFor(() => expect(queryClient.getQueryState(['allocations', 'history', origin.epoch, origin.readiness])?.status).toBe('success'));
    fireEvent.click(screen.getByRole('button', { name: 'Reverse allocation' }));
    fireEvent.change(screen.getByLabelText('Reason'), { target: { value: 'duplicate receipt' } });
    fireEvent.click(screen.getAllByRole('button', { name: 'Reverse allocation' }).at(-1)!);
    await waitFor(() => expect(reads).toBeGreaterThanOrEqual(2));
    expect(await screen.findByText('Reversal recorded.')).toBeVisible();
    sessionStore.replaceCookieSession({
      ...cookieSession(),
      organizations: [{ organizationId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', displayName: 'South' }],
      activeOrganization: { organizationId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', displayName: 'South' },
    }, true);
    const current = sessionStore.getSnapshot();
    queryClient.setQueryData(['allocations', 'history', current.epoch, current.readiness], { foreign: true });
    await act(async () => {
      release?.();
    });
    expect(screen.queryByText('Reversal recorded.')).not.toBeInTheDocument();
    expect(screen.queryByText(/could not be refreshed/i)).not.toBeInTheDocument();
    expect(queryClient.getQueryData(['allocations', 'history', current.epoch, current.readiness])).toEqual({ foreign: true });
  });

  it('keeps a later rejection when an earlier allocation refresh completes', async () => {
    signIn();
    const origin = sessionStore.getSnapshot();
    let release: (() => void) | undefined;
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    let reads = 0;
    let posts = 0;
    server.use(
      http.get('/api/allocations', async () => {
        reads += 1;
        if (reads === 1) {
          return HttpResponse.json({ items: [] });
        }

        await held;
        return HttpResponse.json({ message: 'unavailable' }, { status: 500 });
      }),
      http.post('/api/allocations', () => {
        posts += 1;
        return posts === 1
          ? HttpResponse.json(successBody(false), { status: 201 })
          : new HttpResponse(null, { status: 403 });
      }),
    );
    renderReadAllocation(origin.epoch, origin.readiness);
    await waitFor(() => expect(queryClient.getQueryState(['allocations', 'history', origin.epoch, origin.readiness])?.status).toBe('success'));
    fillAllocation();
    await waitFor(() => expect(reads).toBeGreaterThanOrEqual(2));
    expect(await screen.findByText('Allocation recorded.')).toBeVisible();
    fillAllocation();
    expect((await screen.findAllByText(/rejected before the financial change was saved/i)).length).toBeGreaterThan(0);
    await act(async () => {
      release?.();
    });
    expect(screen.getAllByText(/rejected before the financial change was saved/i).length).toBeGreaterThan(0);
    expect(screen.queryByText('Allocation recorded.')).not.toBeInTheDocument();
    expect(screen.queryByText(/could not be refreshed/i)).not.toBeInTheDocument();
    expect(posts).toBe(2);
  });

  it('refreshes the second confirmed allocation and keeps that outcome', async () => {
    signIn();
    const origin = sessionStore.getSnapshot();
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
    let release: (() => void) | undefined;
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    let reads = 0;
    let posts = 0;
    server.use(
      http.get('/api/allocations', async () => {
        reads += 1;
        if (reads === 1) {
          return HttpResponse.json({ items: [] });
        }

        if (reads === 2) {
          await held;
          return HttpResponse.json({ message: 'unavailable' }, { status: 500 });
        }

        return HttpResponse.json({ items: [] });
      }),
      http.post('/api/allocations', () => {
        posts += 1;
        return HttpResponse.json(successBody(false), { status: 201 });
      }),
    );
    renderReadAllocation(origin.epoch, origin.readiness);
    await waitFor(() => expect(queryClient.getQueryState(['allocations', 'history', origin.epoch, origin.readiness])?.status).toBe('success'));
    fillAllocation();
    await waitFor(() => expect(invalidate).toHaveBeenCalledTimes(1));
    fillAllocation();
    await waitFor(() => expect(invalidate.mock.calls.length).toBeGreaterThanOrEqual(2));
    await act(async () => {
      release?.();
    });
    expect(screen.getByText('Allocation recorded.')).toBeVisible();
    expect(screen.queryByText(/could not be refreshed/i)).not.toBeInTheDocument();
    expect(posts).toBe(2);
    invalidate.mockRestore();
  });
});

function renderDialog(item: { invoiceId: string; bankTransactionId: string; amount: DecimalString; currency: 'PLN'; normalizedReference: string; reason: string }) {
  return render(
    <QueryClientProvider client={queryClient}>
      <CookieConfirmationDialog item={item} onClose={() => undefined} />
    </QueryClientProvider>,
  );
}

function renderAllocation() {
  return render(<QueryClientProvider client={queryClient}><CookieAllocationForm /></QueryClientProvider>);
}

function renderReversal(allocationId: string) {
  return render(
    <QueryClientProvider client={queryClient}>
      <CookieReversalControl allocationId={allocationId} invoiceId={invoiceA} bankTransactionId={bankA} amount={readMoneyAmount('10.5')} currency="PLN" />
    </QueryClientProvider>,
  );
}

function renderRecovery() {
  return render(<QueryClientProvider client={queryClient}><CookieReversalRecovery /></QueryClientProvider>);
}

function pair(invoiceId: string, bankTransactionId: string) {
  return {
    invoiceId,
    bankTransactionId,
    amount: readMoneyAmount('10.50'),
    currency: 'PLN' as const,
    normalizedReference: 'COOKIE',
    reason: 'ExactReferenceCurrencyAndOpenAmount',
  };
}

function successBody(replayed: boolean) {
  return {
    allocationId: createdAllocation,
    invoiceId: invoiceA,
    bankTransactionId: bankA,
    amount: { amount: 10.5, currency: 'PLN' },
    replayed,
  };
}

function cookieSession() {
  return {
    authenticated: true,
    csrfToken: 'csrf-session',
    issuer: 'https://issuer.example',
    subject: 'oidc-operator',
    displayName: 'Ada',
    expiresAt: null,
    organizations: [{ organizationId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', displayName: 'North' }],
    activeOrganization: { organizationId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', displayName: 'North' },
    authorization: 'verified' as const,
    capabilities: ['invoices.read', 'reconciliation.confirm', 'allocations.create', 'allocations.reverse', 'allocations.read'],
  };
}

function signIn(): void {
  sessionStore.replaceCookieSession(cookieSession(), true);
}

function reversalContext() {
  return {
    allocationId: allocationA,
    invoiceId: invoiceA,
    bankTransactionId: bankA,
    reason: 'duplicate receipt',
    amount: '10.5',
    currency: 'PLN',
  };
}

function reversalBody(replayed: boolean) {
  return {
    allocationId: allocationA,
    status: 'reversed',
    reason: 'duplicate receipt',
    reversedAt: '2026-11-04T10:00:00Z',
    amount: { amount: 10.5, currency: 'PLN' },
    replayed,
  };
}

function ReversalRowHarness({ epoch, readiness }: { epoch: number; readiness: number }) {
  const [reversed, setReversed] = useState(false);
  return (
    <>
      <FrozenRead queryKey={['allocations', 'history', epoch, readiness]} path="/api/allocations" />
      {reversed
        ? <span>Reversed</span>
        : <CookieReversalControl allocationId={allocationA} invoiceId={invoiceA} bankTransactionId={bankA} amount={readMoneyAmount('10.5')} currency="PLN" />}
      <button type="button" onClick={() => setReversed(true)}>Show reversed row</button>
    </>
  );
}

function fillAllocation(): void {
  fireEvent.change(screen.getByLabelText('Invoice'), { target: { value: invoiceA } });
  fireEvent.change(screen.getByLabelText('Bank transaction'), { target: { value: bankA } });
  fireEvent.change(screen.getByLabelText('Amount'), { target: { value: '10.50' } });
  fireEvent.click(screen.getByRole('button', { name: 'Allocate an amount' }));
}

function renderReadAllocation(epoch: number, readiness: number) {
  return render(
    <QueryClientProvider client={queryClient}>
      <FrozenRead queryKey={['allocations', 'history', epoch, readiness]} path="/api/allocations" />
      <CookieAllocationForm />
    </QueryClientProvider>,
  );
}

function installAllocationTraffic(input: {
  status: 200 | 201;
  replayed: boolean;
  onRead: (count: number) => Response;
}) {
  const posts: string[] = [];
  let reads = 0;
  server.use(
    http.get('/api/allocations', () => {
      reads += 1;
      return input.onRead(reads);
    }),
    http.post('/api/allocations', ({ request }) => {
      posts.push(request.headers.get('Idempotency-Key') ?? '');
      return HttpResponse.json(successBody(input.replayed), { status: input.status });
    }),
  );
  return { posts, reads: () => reads };
}

function FrozenRead({ queryKey, path }: { queryKey: readonly unknown[]; path: string }) {
  useQuery({
    queryKey,
    queryFn: async () => {
      const response = await fetch(path);
      if (!response.ok) {
        throw new Error('read unavailable');
      }

      return response.json() as Promise<unknown>;
    },
  });
  return null;
}

async function expectStaleRefreshStaysHidden(changeSession: () => void): Promise<void> {
  signIn();
  const origin = sessionStore.getSnapshot();
  let release: (() => void) | undefined;
  const held = new Promise<void>((resolve) => {
    release = resolve;
  });
  let reads = 0;
  const posts: string[] = [];
  server.use(
    http.get('/api/allocations', async () => {
      reads += 1;
      if (reads === 1) {
        return HttpResponse.json({ items: [] });
      }

      await held;
      return HttpResponse.json({ message: 'unavailable' }, { status: 500 });
    }),
    http.post('/api/allocations', ({ request }) => {
      posts.push(request.headers.get('Idempotency-Key') ?? '');
      return HttpResponse.json(successBody(false), { status: 201 });
    }),
  );
  const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
  renderReadAllocation(origin.epoch, origin.readiness);
  await waitFor(() => expect(queryClient.getQueryState(['allocations', 'history', origin.epoch, origin.readiness])?.status).toBe('success'));
  fillAllocation();
  await waitFor(() => expect(reads).toBeGreaterThanOrEqual(2));
  expect(await screen.findByText('Allocation recorded.')).toBeVisible();
  await waitFor(() => expect(invalidate.mock.results.length).toBeGreaterThan(0));
  const refresh = invalidate.mock.results.at(-1)?.value as Promise<unknown>;
  changeSession();
  const current = sessionStore.getSnapshot();
  queryClient.setQueryData(['allocations', 'history', current.epoch, current.readiness], { foreign: true });
  await act(async () => {
    release?.();
    await refresh.catch(() => undefined);
  });
  expect(screen.queryByText(/could not be refreshed/i)).not.toBeInTheDocument();
  expect(screen.queryByText('Allocation recorded.')).not.toBeInTheDocument();
  expect(queryClient.getQueryData(['allocations', 'history', current.epoch, current.readiness])).toEqual({ foreign: true });
  expect(posts).toHaveLength(1);
  invalidate.mockRestore();
}
