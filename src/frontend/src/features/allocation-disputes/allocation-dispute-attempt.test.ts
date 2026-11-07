import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { ApiError } from '../../shared/api/errors';
import { queryClient } from '../../shared/api/query-client';
import { sessionStore } from '../../shared/api/session-store';
import { server } from '../../test/server';
import { fakeJwt } from '../../test/jwt';
import {
  abandonAllocationDisputeAttempt,
  allocationDisputeAttemptStorageKey,
  AllocationDisputeAttemptChangedError,
  AllocationDisputeRetryNotSentError,
  AllocationDisputeRejectedError,
  AllocationDisputeStorageError,
  beginAllocationDisputeRegistration,
  normalizeAllocationDisputeStatement,
  promotePersistedSendingAllocationDispute,
  resetAllocationDisputeAttemptStateForTests,
  retryAllocationDisputeDescriptorRemoval,
  submitAllocationDisputeAttempt,
  UnknownAllocationDisputeOutcomeError,
} from './allocation-dispute-attempt';

const allocationId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
const disputeId = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc';
const statement = 'counterparty challenge';

const recognizedRefusals = [
  [400, 'reconciliation.allocation_dispute.invalid_request'],
  [400, 'reconciliation.allocation_dispute.statement_required'],
  [400, 'reconciliation.allocation_dispute.statement_too_long'],
  [400, 'reconciliation.allocation_dispute.idempotency_key_required'],
  [400, 'reconciliation.allocation_dispute.idempotency_key_invalid'],
  [401, null],
  [403, 'reconciliation.actor.required'],
  [404, 'reconciliation.allocation.not_found'],
  [409, 'reconciliation.allocation_dispute.idempotency_payload_conflict'],
  [409, 'reconciliation.allocation_dispute.allocation_not_confirmed'],
  [409, 'reconciliation.allocation_dispute.concurrency_conflict'],
  [413, 'reconciliation.allocation.request_too_large'],
  [415, 'reconciliation.allocation.unsupported_media_type'],
] as const;

const uncertainResponses = [
  [403, 'reconciliation.allocation.not_found'],
  [400, 'reconciliation.actor.required'],
  [409, 'reconciliation.allocation_dispute.not_found'],
  [500, 'reconciliation.allocation_dispute.concurrency_conflict'],
  [403, null],
  [404, null],
  [418, 'reconciliation.allocation_dispute.invalid_request'],
] as const;

afterEach(() => {
  vi.unstubAllEnvs();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  sessionStorage.clear();
  resetAllocationDisputeAttemptStateForTests();
  queryClient.clear();
  sessionStore.rotate(null, null, null);
});

describe('allocation dispute registration attempt', () => {
  it('stores the exact body and key before fetch', async () => {
    signIn();
    let posts = 0;
    server.use(http.post(`/api/allocations/${allocationId}/disputes`, async ({ request }) => {
      posts += 1;
      const stored = readStored();
      expect(stored.lifecycle).toBe('sending');
      expect(stored.uncertaintyLocked).toBe(false);
      expect(stored.requestBody).toBe(await request.text());
      expect(request.headers.get('Idempotency-Key')).toBe(stored.idempotencyKey);
      return HttpResponse.json(registration());
    }));

    const attempt = beginAllocationDisputeRegistration(allocationId, statement);
    expect(attempt.lifecycle).toBe('ready');
    expect(attempt.requestBody).toBe(JSON.stringify({ statement }));
    const result = await submitAllocationDisputeAttempt();
    expect(posts).toBe(1);
    expect(result.cleanup).toBe('removed');
    expect(result.record.actorId).toBe('historical-operator');
    expect(sessionStorage.getItem(allocationDisputeAttemptStorageKey)).toBeNull();
  });

  it('sends nothing when storage cannot keep the command', () => {
    signIn();
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('storage failed');
    });
    expect(() => beginAllocationDisputeRegistration(allocationId, statement)).toThrow(AllocationDisputeStorageError);
    expect(sessionStorage.getItem(allocationDisputeAttemptStorageKey)).toBeNull();
  });

  it.each(recognizedRefusals)('rejects the first response HTTP %s %s and disables replay', async (status, code) => {
    signIn();
    let posts = 0;
    server.use(http.post(`/api/allocations/${allocationId}/disputes`, () => {
      posts += 1;
      return problem(status, code);
    }));
    beginAllocationDisputeRegistration(allocationId, statement);
    await expect(submitAllocationDisputeAttempt()).rejects.toBeInstanceOf(AllocationDisputeRejectedError);
    expect(readStored()).toMatchObject({ lifecycle: 'rejected', uncertaintyLocked: false });
    await expect(submitAllocationDisputeAttempt()).rejects.toBeInstanceOf(AllocationDisputeRejectedError);
    expect(posts).toBe(1);
  });

  it.each(uncertainResponses)('keeps the first response HTTP %s %s uncertain', async (status, code) => {
    signIn();
    server.use(http.post(`/api/allocations/${allocationId}/disputes`, () => problem(status, code)));
    beginAllocationDisputeRegistration(allocationId, statement);
    await expect(submitAllocationDisputeAttempt()).rejects.toBeInstanceOf(UnknownAllocationDisputeOutcomeError);
    expect(readStored()).toMatchObject({ lifecycle: 'uncertain', uncertaintyLocked: true });
  });

  it.each(recognizedRefusals)('keeps an earlier uncertain attempt after HTTP %s %s', async (status, code) => {
    signIn();
    const first = beginAllocationDisputeRegistration(allocationId, statement);
    server.use(http.post(`/api/allocations/${allocationId}/disputes`, () => HttpResponse.error()));
    await expect(submitAllocationDisputeAttempt()).rejects.toBeInstanceOf(UnknownAllocationDisputeOutcomeError);
    server.use(http.post(`/api/allocations/${allocationId}/disputes`, async ({ request }) => {
      expect(request.headers.get('Idempotency-Key')).toBe(first.idempotencyKey);
      expect(await request.text()).toBe(first.requestBody);
      return problem(status, code);
    }));
    await expect(submitAllocationDisputeAttempt()).rejects.toMatchObject({
      name: 'AllocationDisputeUncertaintyRemainsError',
      status,
      code,
    });
    expect(readStored()).toMatchObject({
      lifecycle: 'uncertain',
      uncertaintyLocked: true,
      idempotencyKey: first.idempotencyKey,
      requestBody: first.requestBody,
    });
  });

  it('retries local removal without another POST and replays the same command after the module is recreated', async () => {
    signIn();
    const keys: string[] = [];
    const bodies: string[] = [];
    server.use(http.post(`/api/allocations/${allocationId}/disputes`, async ({ request }) => {
      keys.push(request.headers.get('Idempotency-Key') ?? '');
      bodies.push(await request.text());
      return HttpResponse.json(registration(), { status: 201 });
    }));
    beginAllocationDisputeRegistration(allocationId, statement);
    vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => {
      throw new Error('storage failed');
    });
    const failed = await submitAllocationDisputeAttempt();
    expect(failed.cleanup).toBe('failed');
    expect(keys).toHaveLength(1);
    vi.restoreAllMocks();
    retryAllocationDisputeDescriptorRemoval();
    expect(sessionStorage.getItem(allocationDisputeAttemptStorageKey)).toBeNull();
    expect(keys).toHaveLength(1);

    beginAllocationDisputeRegistration(allocationId, statement);
    vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => {
      throw new Error('storage failed');
    });
    await submitAllocationDisputeAttempt();
    const leftover = readStored();
    vi.restoreAllMocks();
    resetAllocationDisputeAttemptStateForTests();
    expect(readStored().idempotencyKey).toBe(leftover.idempotencyKey);
    server.use(http.post(`/api/allocations/${allocationId}/disputes`, async ({ request }) => {
      keys.push(request.headers.get('Idempotency-Key') ?? '');
      bodies.push(await request.text());
      return HttpResponse.json(registration(), { status: 200 });
    }));
    const replayed = await submitAllocationDisputeAttempt();
    expect(replayed.created).toBe(false);
    expect(keys[0]).not.toBe(leftover.idempotencyKey);
    expect(keys.slice(1)).toEqual([leftover.idempotencyKey, leftover.idempotencyKey]);
    expect(bodies[bodies.length - 1]).toBe(leftover.requestBody);
    expect(sessionStorage.getItem(allocationDisputeAttemptStorageKey)).toBeNull();
  });

  it('does not let a remembered success remove a later descriptor', async () => {
    signIn();
    server.use(http.post(`/api/allocations/${allocationId}/disputes`, () => HttpResponse.json(registration(), { status: 201 })));
    beginAllocationDisputeRegistration(allocationId, statement);
    vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => {
      throw new Error('storage failed');
    });
    await submitAllocationDisputeAttempt();
    vi.restoreAllMocks();
    const later = { ...readStored(), idempotencyKey: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd', lifecycle: 'ready', uncertaintyLocked: false };
    sessionStorage.setItem(allocationDisputeAttemptStorageKey, JSON.stringify(later));
    expect(() => retryAllocationDisputeDescriptorRemoval()).toThrow(AllocationDisputeAttemptChangedError);
    expect(readStored().idempotencyKey).toBe(later.idempotencyKey);
  });

  it('does not recreate a descriptor removed while the response is still running', async () => {
    signIn();
    let release: ((response: Response) => void) | undefined;
    vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>((resolve) => {
      release = resolve;
    })));
    const attempt = beginAllocationDisputeRegistration(allocationId, statement);
    const pending = submitAllocationDisputeAttempt();
    abandonAllocationDisputeAttempt(attempt.idempotencyKey, attempt.requestBody);
    release?.(new Response(JSON.stringify(registration()), { status: 201 }));
    await expect(pending).rejects.toBeInstanceOf(AllocationDisputeAttemptChangedError);
    expect(sessionStorage.getItem(allocationDisputeAttemptStorageKey)).toBeNull();
  });

  it('does not refresh a new session when the old response arrives after the epoch changes', async () => {
    signIn();
    let release: ((response: Response) => void) | undefined;
    vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>((resolve) => {
      release = resolve;
    })));
    beginAllocationDisputeRegistration(allocationId, statement);
    const pending = submitAllocationDisputeAttempt();
    sessionStore.rotate(fakeJwt(), Math.floor(Date.now() / 1000) + 3600, null);
    const nextEpoch = sessionStore.getSnapshot().epoch;
    queryClient.setQueryData(['allocation-disputes', 'list', nextEpoch, 1, allocationId], { marker: 'kept' });
    release?.(new Response(JSON.stringify(registration()), { status: 201 }));
    await pending;
    expect(queryClient.getQueryData(['allocation-disputes', 'list', nextEpoch, 1, allocationId])).toEqual({ marker: 'kept' });
  });

  it('promotes a persisted sending record without sending', () => {
    signIn();
    const attempt = beginAllocationDisputeRegistration(allocationId, statement);
    sessionStorage.setItem(allocationDisputeAttemptStorageKey, JSON.stringify({ ...attempt, lifecycle: 'sending' }));
    promotePersistedSendingAllocationDispute();
    expect(readStored()).toMatchObject({ lifecycle: 'uncertain', uncertaintyLocked: true, idempotencyKey: attempt.idempotencyKey });
  });

  it('rejects a blank statement and a cookie send before storage', () => {
    expect(normalizeAllocationDisputeStatement(' \t ')).toBeNull();
    expect(normalizeAllocationDisputeStatement(`\uFEFF${statement}`)).toBe(`\uFEFF${statement}`);
    signIn();
    expect(() => beginAllocationDisputeRegistration(allocationId, '   ')).toThrow(AllocationDisputeStorageError);
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    expect(() => beginAllocationDisputeRegistration(allocationId, statement)).toThrow(ApiError);
    expect(sessionStorage.getItem(allocationDisputeAttemptStorageKey)).toBeNull();
  });

  it('keeps the previous descriptor when replay cannot be marked', async () => {
    signIn();
    const attempt = beginAllocationDisputeRegistration(allocationId, statement);
    sessionStorage.setItem(allocationDisputeAttemptStorageKey, JSON.stringify({ ...attempt, lifecycle: 'uncertain', uncertaintyLocked: true }));
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('storage failed');
    });
    await expect(submitAllocationDisputeAttempt()).rejects.toBeInstanceOf(AllocationDisputeRetryNotSentError);
    expect(readStored().idempotencyKey).toBe(attempt.idempotencyKey);
  });
});

function signIn(): void {
  sessionStore.rotate(fakeJwt(), Math.floor(Date.now() / 1000) + 3600, null);
}

function readStored(): {
  lifecycle: string;
  uncertaintyLocked: boolean;
  idempotencyKey: string;
  requestBody: string;
} {
  return JSON.parse(sessionStorage.getItem(allocationDisputeAttemptStorageKey) ?? '{}') as {
    lifecycle: string;
    uncertaintyLocked: boolean;
    idempotencyKey: string;
    requestBody: string;
  };
}

function problem(status: number, code: string | null) {
  return HttpResponse.json(code === null ? { title: 'Refusal' } : { title: 'Refusal', code }, { status });
}

function registration(): Record<string, unknown> {
  return {
    disputeId,
    allocationId,
    statement,
    actorSource: 'developmentJwt',
    actorId: 'historical-operator',
    recordedAt: '2026-11-07T09:00:00Z',
  };
}
