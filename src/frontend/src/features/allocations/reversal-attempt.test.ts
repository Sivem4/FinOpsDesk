import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';

import { queryClient } from '../../shared/api/query-client';
import { sessionStore } from '../../shared/api/session-store';
import { server } from '../../test/server';
import type { DecimalString } from '../../shared/money/money';
import { explicitAmountAttemptStorageKey } from '../reconciliation/allocation-attempt';
import { getReversalContext } from './reversal-api';
import { invalidateVerifiedReversal } from './reversal-completion';
import {
  abandonReversalAttempt,
  beginReversalAttempt,
  inspectReversalAttempt,
  reversalAttemptStorageKey,
  ReversalActorRequiredError,
  ReversalAttemptBlockedError,
  ReversalAttemptChangedError,
  ReversalAuthenticationError,
  ReversalStorageCleanupError,
  ReversalStorageError,
  ReversalSubmissionInProgressError,
  ReversalTenantMismatchError,
  submitReversalAttempt,
  UnknownReversalOutcomeError,
  UnreadableReversalResultError,
  UnresolvedReversalAttemptError,
  type BeginReversalInput,
} from './reversal-attempt';
import { normalizeReversalReason, trimLikeDotNet } from './reversal-reason';

const organizationA = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
const organizationB = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
const allocationId = '11111111-1111-4111-8111-111111111111';
const invoiceId = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc';
const bankTransactionId = 'dddddddd-dddd-4ddd-8ddd-dddddddddddd';

describe('reversal reason normalization', () => {
  it('matches .NET Trim for next-line and keeps a leading byte-order mark', () => {
    expect(trimLikeDotNet('\u0085 customer refund \u0085')).toBe('customer refund');
    expect(trimLikeDotNet('\u00A0customer refund\u00A0')).toBe('customer refund');
    expect(trimLikeDotNet('\uFEFFcustomer refund')).toBe('\uFEFFcustomer refund');
    expect(normalizeReversalReason('  customer refund  ')).toEqual({ ok: true, reason: 'customer refund' });
    expect(normalizeReversalReason(' \u0085 ')).toEqual({ ok: false, message: 'Enter a reversal reason.' });
    expect(normalizeReversalReason(` ${'a'.repeat(500)} `).ok).toBe(true);
    expect(normalizeReversalReason('a'.repeat(501))).toEqual({
      ok: false,
      message: 'A reversal reason can contain at most 500 characters.',
    });
  });
});

describe('reversal context parsing', () => {
  it('reads the exact amount, status, and allocation id', async () => {
    signIn();
    server.use(http.get(`/api/allocations/${allocationId}/reversal-context`, () => text(contextBody())));

    const context = await getReversalContext(allocationId, new AbortController().signal);

    expect(context.amount).toBe('15.5');
    expect(typeof context.amount).toBe('string');
    expect(context.currency).toBe('PLN');
    expect(context.status).toBe('confirmed');
    expect(context.allocationId).toBe(allocationId);
    expect(context.reference).toBeNull();
  });

  it('rejects a mismatched allocation id, a bad status, and a negative amount', async () => {
    signIn();
    server.use(http.get(`/api/allocations/${allocationId}/reversal-context`, () => text(contextBody({
      allocationId: '22222222-2222-4222-8222-222222222222',
    }))));

    await expect(getReversalContext(allocationId, new AbortController().signal)).rejects.toThrow(/could not be read/);

    server.use(http.get(`/api/allocations/${allocationId}/reversal-context`, () => text(contextBody({ status: 'pending' }))));
    await expect(getReversalContext(allocationId, new AbortController().signal)).rejects.toThrow(/could not be read/);

    server.use(http.get(`/api/allocations/${allocationId}/reversal-context`, () => text(contextBody({ amount: '-15.5' }))));
    await expect(getReversalContext(allocationId, new AbortController().signal)).rejects.toThrow(/could not be read/);
  });
});

describe('reversal attempt descriptor', () => {
  it('persists the exact body and key, then reads them back', () => {
    signIn();
    const attempt = beginReversalAttempt(input('  customer refund  '));
    const stored = sessionStorage.getItem(reversalAttemptStorageKey);

    expect(attempt.lifecycle).toBe('ready');
    expect(attempt.reason).toBe('customer refund');
    expect(attempt.requestBody).toBe('{"reason":"customer refund"}');
    expect(JSON.parse(stored ?? '{}').requestBody).toBe(attempt.requestBody);
    expect(inspectReversalAttempt().status).toBe('pending');
  });

  it('does not replace a ready or uncertain attempt and does not touch explicit-amount storage', () => {
    signIn();
    sessionStorage.setItem(explicitAmountAttemptStorageKey, 'explicit-sentinel');
    const first = beginReversalAttempt(input('customer refund'));
    sessionStorage.setItem(reversalAttemptStorageKey, sessionStorage.getItem(reversalAttemptStorageKey)!.replace('"ready"', '"uncertain"'));

    expect(() => beginReversalAttempt(input('another reason'))).toThrow(UnresolvedReversalAttemptError);
    expect(sessionStorage.getItem(reversalAttemptStorageKey)).toContain(first.idempotencyKey);
    expect(sessionStorage.getItem(reversalAttemptStorageKey)).not.toContain('another reason');
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBe('explicit-sentinel');
  });

  it('fails closed for corrupted storage and a failed write', () => {
    signIn();
    sessionStorage.setItem(reversalAttemptStorageKey, '{');
    sessionStorage.setItem(explicitAmountAttemptStorageKey, 'explicit-sentinel');

    expect(() => beginReversalAttempt(input('customer refund'))).toThrow(ReversalAttemptBlockedError);
    expect(sessionStorage.getItem(reversalAttemptStorageKey)).toBe('{');
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBe('explicit-sentinel');

    sessionStorage.removeItem(reversalAttemptStorageKey);
    const setItem = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('full');
    });

    expect(() => beginReversalAttempt(input('customer refund'))).toThrow(ReversalStorageError);
    expect(sessionStorage.getItem(reversalAttemptStorageKey)).toBeNull();
    setItem.mockRestore();
  });

  it('hides another organization and does not send its descriptor', async () => {
    signIn(organizationA);
    const attempt = beginReversalAttempt(input('customer refund'));
    signIn(organizationB);
    const posts: string[] = [];
    server.use(http.post(`/api/allocations/${allocationId}/reverse`, async ({ request }) => {
      posts.push(await request.text());
      return text(reversalBody(attempt.reason));
    }));

    expect(inspectReversalAttempt().status).toBe('blocked-tenant');
    await expect(submitReversalAttempt()).rejects.toBeInstanceOf(ReversalTenantMismatchError);
    expect(posts).toEqual([]);
    expect(sessionStorage.getItem(reversalAttemptStorageKey)).toContain(attempt.idempotencyKey);
  });

  it('sends the original body once and keeps it when the response cannot be verified', async () => {
    signIn();
    const attempt = beginReversalAttempt(input('customer refund'));
    const posts: Array<{ key: string | null; body: string }> = [];
    server.use(http.post(`/api/allocations/${allocationId}/reverse`, async ({ request }) => {
      posts.push({ key: request.headers.get('Idempotency-Key'), body: await request.text() });
      return text('{"allocationId":"11111111-1111-4111-8111-111111111111","status":"reversed","replayed":"yes"}');
    }));

    await expect(submitReversalAttempt()).rejects.toBeInstanceOf(UnreadableReversalResultError);
    await expect(submitReversalAttempt()).rejects.toBeInstanceOf(UnreadableReversalResultError);

    expect(posts).toEqual([
      { key: attempt.idempotencyKey, body: attempt.requestBody },
      { key: attempt.idempotencyKey, body: attempt.requestBody },
    ]);
    const kept = JSON.parse(sessionStorage.getItem(reversalAttemptStorageKey) ?? '{}') as { lifecycle?: string; requestBody?: string };
    expect(kept.lifecycle).toBe('uncertain');
    expect(kept.requestBody).toBe(attempt.requestBody);
  });

  it('treats cancellation after dispatch as an unknown outcome and keeps the descriptor', async () => {
    signIn();
    beginReversalAttempt(input('customer refund'));
    let entered!: () => void;
    let release!: () => void;
    const enteredPromise = new Promise<void>((resolve) => {
      entered = resolve;
    });
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(http.post(`/api/allocations/${allocationId}/reverse`, async () => {
      entered();
      await gate;
      return text(reversalBody('customer refund'));
    }));
    const controller = new AbortController();
    const pending = submitReversalAttempt(controller.signal);

    await enteredPromise;
    controller.abort();

    await expect(pending).rejects.toBeInstanceOf(UnknownReversalOutcomeError);
    expect(sessionStorage.getItem(reversalAttemptStorageKey)).toContain('"uncertain"');
    release();
  });

  it('clears only the matching descriptor after a verified replay and invalidates the current epoch', async () => {
    const epoch = signIn();
    const attempt = beginReversalAttempt(input('customer refund'));
    seedCaches(epoch);
    server.use(http.post(`/api/allocations/${allocationId}/reverse`, () => text(reversalBody('customer refund', true, '15.50'))));

    const verified = await submitReversalAttempt();
    invalidateVerifiedReversal(verified);

    expect(verified.replayed).toBe(true);
    expect(verified.amount).toBe('15.5');
    expect(sessionStorage.getItem(reversalAttemptStorageKey)).toBeNull();
    expect(queryClient.getQueryState(['allocations', 'history', epoch, 1, 50, invoiceId, ''])?.isInvalidated).toBe(true);
    expect(queryClient.getQueryState(['allocations', 'audit', epoch, allocationId, 1])?.isInvalidated).toBe(true);
    expect(queryClient.getQueryState(['invoices', epoch, 'register'])?.isInvalidated).toBe(true);
    expect(queryClient.getQueryState(['bank-transactions', epoch, 'register'])?.isInvalidated).toBe(true);
    expect(queryClient.getQueryState(['matches', epoch, 'exact'])?.isInvalidated).toBe(true);
    expect(queryClient.getQueryState(['allocations', 'context', epoch, invoiceId, bankTransactionId])?.isInvalidated).toBe(true);
    expect(attempt.organizationId).toBe(organizationA);
  });

  it('does not invalidate a newer session from an older result', async () => {
    const firstEpoch = signIn();
    beginReversalAttempt(input('customer refund'));
    const verifiedEpoch = firstEpoch;
    const secondEpoch = signIn();
    queryClient.setQueryData(['invoices', secondEpoch, 'register'], { ok: true });

    invalidateVerifiedReversal({
      allocationId,
      invoiceId,
      bankTransactionId,
      amount: '15.5' as DecimalString,
      currency: 'PLN',
      reason: 'customer refund',
      reversedAt: '2026-10-31T10:15:00+00:00',
      replayed: false,
      epoch: verifiedEpoch,
    });

    expect(queryClient.getQueryState(['invoices', secondEpoch, 'register'])?.isInvalidated).not.toBe(true);
  });

  it('keeps the descriptor when removal fails and when authentication is missing', () => {
    signIn();
    const attempt = beginReversalAttempt(input('customer refund'));
    const removeItem = vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => {
      throw new Error('blocked');
    });

    expect(() => abandonReversalAttempt(attempt.idempotencyKey)).toThrow(ReversalStorageCleanupError);
    expect(JSON.parse(sessionStorage.getItem(reversalAttemptStorageKey) ?? '{}').requestBody).toBe(attempt.requestBody);
    removeItem.mockRestore();

    sessionStore.rotate(null, null, 'unauthorized');
    expect(inspectReversalAttempt().status).toBe('blocked-authentication');
    expect(sessionStorage.getItem(reversalAttemptStorageKey)).toContain(attempt.idempotencyKey);
  });

  it('removes local tracking only for the authenticated organization', () => {
    const posts: string[] = [];
    server.use(http.post(`/api/allocations/${allocationId}/reverse`, () => {
      posts.push('reverse');
      return text(reversalBody('customer refund'));
    }));
    signIn(organizationA);
    const attempt = beginReversalAttempt(input('customer refund'));
    const stored = sessionStorage.getItem(reversalAttemptStorageKey);

    abandonReversalAttempt(attempt.idempotencyKey);
    expect(sessionStorage.getItem(reversalAttemptStorageKey)).toBeNull();
    expect(posts).toEqual([]);

    const retained = beginReversalAttempt(input('customer refund'));
    const retainedValue = sessionStorage.getItem(reversalAttemptStorageKey);
    signIn(organizationB);
    expect(() => abandonReversalAttempt(retained.idempotencyKey)).toThrow(ReversalTenantMismatchError);
    expect(sessionStorage.getItem(reversalAttemptStorageKey)).toBe(retainedValue);

    sessionStore.rotate(null, null, 'unauthorized');
    expect(() => abandonReversalAttempt(retained.idempotencyKey)).toThrow(ReversalAuthenticationError);
    expect(sessionStorage.getItem(reversalAttemptStorageKey)).toBe(retainedValue);

    sessionStore.rotate(tokenFor(organizationA, null), Math.floor(Date.now() / 1000) + 3600, null);
    abandonReversalAttempt(retained.idempotencyKey);
    expect(sessionStorage.getItem(reversalAttemptStorageKey)).toBeNull();
    expect(posts).toEqual([]);
    expect(stored).toContain(attempt.idempotencyKey);
  });

  it('keeps the in-flight and mismatched-key abandonment guards', async () => {
    signIn();
    const attempt = beginReversalAttempt(input('customer refund'));
    let release!: () => void;
    let entered!: () => void;
    const enteredPromise = new Promise<void>((resolve) => {
      entered = resolve;
    });
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    const posts: string[] = [];
    server.use(http.post(`/api/allocations/${allocationId}/reverse`, async () => {
      posts.push('reverse');
      entered();
      await gate;
      return text(reversalBody('customer refund'));
    }));
    const pending = submitReversalAttempt();
    await enteredPromise;
    const stored = sessionStorage.getItem(reversalAttemptStorageKey);

    expect(() => abandonReversalAttempt(attempt.idempotencyKey)).toThrow(ReversalSubmissionInProgressError);
    expect(() => abandonReversalAttempt('aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaab')).toThrow(ReversalSubmissionInProgressError);
    expect(sessionStorage.getItem(reversalAttemptStorageKey)).toBe(stored);
    expect(posts).toEqual(['reverse']);
    release();
    await pending;

    const replacement = beginReversalAttempt(input('customer refund'));
    const replacementValue = sessionStorage.getItem(reversalAttemptStorageKey);
    expect(() => abandonReversalAttempt(attempt.idempotencyKey)).toThrow(ReversalAttemptChangedError);
    expect(sessionStorage.getItem(reversalAttemptStorageKey)).toBe(replacementValue);
    expect(replacement.idempotencyKey).not.toBe(attempt.idempotencyKey);
    expect(posts).toEqual(['reverse']);
  });

  it('requires an operator before a new attempt is stored', () => {
    sessionStore.rotate(tokenFor(organizationA, null), Math.floor(Date.now() / 1000) + 3600, null);

    expect(() => beginReversalAttempt(input('customer refund'))).toThrow(ReversalActorRequiredError);
    expect(sessionStorage.getItem(reversalAttemptStorageKey)).toBeNull();
  });
});

function signIn(organizationId = organizationA): number {
  sessionStore.rotate(tokenFor(organizationId), Math.floor(Date.now() / 1000) + 3600, null);
  return sessionStore.getSnapshot().epoch;
}

function input(reason: string): BeginReversalInput {
  return {
    allocationId,
    invoiceId,
    bankTransactionId,
    amount: '15.5' as DecimalString,
    currency: 'PLN',
    reason,
  };
}

function seedCaches(epoch: number): void {
  queryClient.setQueryData(['allocations', 'history', epoch, 1, 50, invoiceId, ''], { ok: true });
  queryClient.setQueryData(['allocations', 'audit', epoch, allocationId, 1], { ok: true });
  queryClient.setQueryData(['invoices', epoch, 'register'], { ok: true });
  queryClient.setQueryData(['bank-transactions', epoch, 'register'], { ok: true });
  queryClient.setQueryData(['matches', epoch, 'exact'], { ok: true });
  queryClient.setQueryData(['allocations', 'context', epoch, invoiceId, bankTransactionId], { ok: true });
}

function contextBody(overrides: { allocationId?: string; status?: string; amount?: string } = {}): string {
  return `{
    "allocationId":"${overrides.allocationId ?? allocationId}",
    "invoiceId":"${invoiceId}",
    "bankTransactionId":"${bankTransactionId}",
    "invoiceNumber":"INV-15",
    "reference":null,
    "amount":{"amount":${overrides.amount ?? '15.5'},"currency":"PLN"},
    "status":"${overrides.status ?? 'confirmed'}",
    "confirmedAt":"2026-10-31T08:00:00+00:00",
    "reversedAt":null,
    "reversalReason":null
  }`;
}

function reversalBody(reason: string, replayed = false, amount = '15.5'): string {
  return `{
    "allocationId":"${allocationId}",
    "status":"reversed",
    "amount":{"amount":${amount},"currency":"PLN"},
    "reversedAt":"2026-10-31T10:15:00+00:00",
    "reason":${JSON.stringify(reason)},
    "replayed":${replayed ? 'true' : 'false'}
  }`;
}

function text(body: string) {
  return HttpResponse.text(body, { headers: { 'Content-Type': 'application/json' } });
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
