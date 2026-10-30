import { renderHook } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';

import { ApiError, StaleSessionError } from '../../shared/api/errors';
import { sessionStore } from '../../shared/api/session-store';
import { endSession, setAccessToken } from '../../shared/auth/session';
import { MoneyParseError } from '../../shared/money/money';
import { server } from '../../test/server';
import {
  abandonExplicitAmountAttempt,
  AllocationActorRequiredError,
  AllocationAttemptBlockedError,
  AllocationAttemptChangedError,
  AllocationAuthenticationError,
  AllocationStorageCleanupError,
  AllocationStorageError,
  AllocationSubmissionInProgressError,
  AllocationTenantMismatchError,
  beginExplicitAmountAttempt,
  explicitAmountAttemptStorageKey,
  inspectAllocationAttempt,
  submitExplicitAmountAttempt,
  UnknownAllocationOutcomeError,
  UnreadableAllocationResultError,
  UnresolvedAllocationAttemptError,
  useAllocationAttempt,
  type BeginExplicitAmountInput,
} from './allocation-attempt';

const organizationA = '11111111-1111-1111-1111-111111111111';
const organizationB = '22222222-2222-2222-2222-222222222222';
const invoiceId = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const bankTransactionId = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
const allocationId = 'cccccccc-cccc-cccc-cccc-cccccccccccc';

describe('explicit amount allocation attempt', () => {
  it('does not persist an attempt without an authenticated organization', () => {
    expect(() => begin()).toThrow(AllocationAuthenticationError);
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBeNull();
  });

  it('creates one idempotency key and persists the canonical body before POST', async () => {
    signIn(organizationA);
    const order: string[] = [];
    const originalSetItem = Storage.prototype.setItem;
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(function setItem(this: Storage, key: string, value: string) {
      if (key === explicitAmountAttemptStorageKey) {
        order.push('persist');
      }

      originalSetItem.call(this, key, value);
    });
    server.use(http.post('/api/allocations', async ({ request }) => {
      order.push('post');
      const stored = JSON.parse(sessionStorage.getItem(explicitAmountAttemptStorageKey) ?? 'null') as { requestBody?: string };
      expect(stored.requestBody).toBe(await request.text());
      return allocationResponse(201, false);
    }));

    const attempt = begin();

    expect(attempt.idempotencyKey).toMatch(/^[0-9a-f-]{36}$/i);
    expect(attempt.idempotencyKey).not.toBe('00000000-0000-0000-0000-000000000000');

    try {
      await submitExplicitAmountAttempt();
      expect(order).toEqual(['persist', 'post']);
    } finally {
      vi.restoreAllMocks();
    }
  });

  it('does not send when sessionStorage rejects the write', () => {
    signIn(organizationA);
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('storage blocked');
    });
    server.use(http.post('/api/allocations', () => {
      throw new Error('POST was sent');
    }));

    try {
      expect(() => begin()).toThrow(AllocationStorageError);
      expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBeNull();
    } finally {
      vi.restoreAllMocks();
    }
  });

  it('does not replace an unresolved attempt', () => {
    signIn(organizationA);
    const first = begin();

    expect(() => begin()).toThrow(UnresolvedAllocationAttemptError);
    expect(storedDescriptor().idempotencyKey).toBe(first.idempotencyKey);
  });

  it('restores a pending attempt after a simulated reload without sending', () => {
    signIn(organizationA);
    const created = begin({ amount: '100.2500' });
    const restored = inspectAllocationAttempt();

    expect(restored.status).toBe('pending');
    if (restored.status !== 'pending') {
      return;
    }

    expect(restored.attempt.idempotencyKey).toBe(created.idempotencyKey);
    expect(restored.attempt.amount).toBe('100.25');
    expect(restored.attempt.requestBody).toBe(created.requestBody);
    expect(restored.attempt.requestBody).toContain('"amount":100.25');
    expect(restored.attempt.requestBody).not.toContain('"amount":"');
  });

  it('keeps the attempt mounted again and does not send it', () => {
    signIn(organizationA);
    begin();
    const first = renderHook(() => useAllocationAttempt());
    expect(first.result.current.status).toBe('pending');
    first.unmount();

    const second = renderHook(() => useAllocationAttempt());
    expect(second.result.current.status).toBe('pending');
  });

  it('preserves exact decimal amounts and repeats the same body', async () => {
    signIn(organizationA);
    expect(begin({ amount: '100' }).amount).toBe('100');
    abandonExplicitAmountAttempt();
    expect(begin({ amount: '0.0001' }).amount).toBe('0.0001');
    abandonExplicitAmountAttempt();
    const attempt = begin({ amount: '100.2500' });
    const bodies: string[] = [];
    const keys: string[] = [];
    server.use(http.post('/api/allocations', async ({ request }) => {
      bodies.push(await request.text());
      keys.push(request.headers.get('Idempotency-Key') ?? '');
      return bodies.length === 1
        ? HttpResponse.error()
        : allocationResponse(200, true, '100.25');
    }));

    await expect(submitExplicitAmountAttempt()).rejects.toBeInstanceOf(UnknownAllocationOutcomeError);
    await submitExplicitAmountAttempt();

    expect(keys).toEqual([attempt.idempotencyKey, attempt.idempotencyKey]);
    expect(bodies[0]).toBe(attempt.requestBody);
    expect(bodies[1]).toBe(attempt.requestBody);
    expect(bodies[0]).toContain('"amount":100.25');
  });

  it.each(['0', '-1', '0.00001', '1000000000000000'])('rejects an invalid amount %s before storage', (amount) => {
    signIn(organizationA);

    expect(() => begin({ amount })).toThrow(MoneyParseError);
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBeNull();
  });

  it('keeps the attempt after a network failure', async () => {
    signIn(organizationA);
    const attempt = begin();
    server.use(http.post('/api/allocations', () => HttpResponse.error()));

    await expect(submitExplicitAmountAttempt()).rejects.toBeInstanceOf(UnknownAllocationOutcomeError);

    const view = inspectAllocationAttempt();
    expect(view.status).toBe('pending');
    if (view.status === 'pending') {
      expect(view.attempt.idempotencyKey).toBe(attempt.idempotencyKey);
      expect(view.attempt.lifecycle).toBe('uncertain');
    }
  });

  it('keeps the attempt when a success response cannot be verified', async () => {
    signIn(organizationA);
    begin();
    server.use(http.post('/api/allocations', () => new HttpResponse('not-json', { status: 201 })));

    await expect(submitExplicitAmountAttempt()).rejects.toBeInstanceOf(UnreadableAllocationResultError);
    expect(inspectAllocationAttempt().status).toBe('pending');
  });

  it('removes the unresolved attempt after a verified 201', async () => {
    signIn(organizationA);
    begin();
    server.use(http.post('/api/allocations', () => allocationResponse(201, false)));

    const result = await submitExplicitAmountAttempt();

    expect(result.replayed).toBe(false);
    expect(result.allocationId).toBe(allocationId);
    expect(result.epoch).toBe(sessionStore.getSnapshot().epoch);
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBeNull();
  });

  it('removes the unresolved attempt after a verified replay', async () => {
    signIn(organizationA);
    begin();
    server.use(http.post('/api/allocations', () => allocationResponse(200, true)));

    const result = await submitExplicitAmountAttempt();

    expect(result.replayed).toBe(true);
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBeNull();
  });

  it('keeps the attempt when the verified payload does not match', async () => {
    signIn(organizationA);
    begin({ amount: '100.2500' });
    server.use(http.post('/api/allocations', () => allocationResponse(200, true, '9.0001')));

    await expect(submitExplicitAmountAttempt()).rejects.toBeInstanceOf(UnreadableAllocationResultError);
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).not.toBeNull();
  });

  it('does not create a new key after an idempotency payload conflict', async () => {
    signIn(organizationA);
    const attempt = begin();
    server.use(http.post('/api/allocations', () => HttpResponse.json(
      {
        title: 'Idempotency key was already used.',
        status: 409,
        detail: 'This idempotency key was already used for a different allocation request.',
        code: 'reconciliation.confirmation.idempotency_payload_conflict',
      },
      { status: 409 },
    )));

    await expect(submitExplicitAmountAttempt()).rejects.toBeInstanceOf(ApiError);
    expect(storedDescriptor().idempotencyKey).toBe(attempt.idempotencyKey);
    expect(() => begin()).toThrow(UnresolvedAllocationAttemptError);
  });

  it('ignores a response that arrives after the session epoch changes', async () => {
    signIn(organizationA);
    begin();
    let release: () => void = () => {};
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(http.post('/api/allocations', async () => {
      await gate;
      return allocationResponse(201, false);
    }));
    const pending = submitExplicitAmountAttempt();
    sessionStore.rotate(tokenFor(organizationA), Math.floor(Date.now() / 1000) + 3600, null);
    release();

    await expect(pending).rejects.toBeInstanceOf(StaleSessionError);
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).not.toBeNull();
  });

  it('does not send a tenant A attempt with a tenant B token', async () => {
    signIn(organizationA);
    begin();
    setAccessToken(tokenFor(organizationB), Math.floor(Date.now() / 1000) + 3600);
    server.use(http.post('/api/allocations', () => {
      throw new Error('POST was sent');
    }));

    expect(inspectAllocationAttempt().status).toBe('blocked-tenant');
    await expect(submitExplicitAmountAttempt()).rejects.toBeInstanceOf(AllocationTenantMismatchError);
    expect(inspectAllocationAttempt().status).toBe('blocked-tenant');
  });

  it('allows a manual retry after the same organization renews its token', async () => {
    signIn(organizationA);
    const attempt = begin();
    const renewed = tokenFor(organizationA, 'operator-2');
    setAccessToken(renewed, Math.floor(Date.now() / 1000) + 3600);
    let authorization = '';
    server.use(http.post('/api/allocations', ({ request }) => {
      authorization = request.headers.get('Authorization') ?? '';
      return allocationResponse(201, false);
    }));

    expect(inspectAllocationAttempt().status).toBe('pending');
    await submitExplicitAmountAttempt();

    expect(authorization).toBe(`Bearer ${renewed}`);
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBeNull();
    expect(attempt.organizationId.toLowerCase()).toBe(organizationA);
  });

  it('does not send an attempt when the session ends or returns', () => {
    signIn(organizationA);
    begin();
    endSession(null);

    expect(inspectAllocationAttempt().status).toBe('blocked-authentication');

    signIn(organizationA);
    expect(inspectAllocationAttempt().status).toBe('pending');
  });

  it('does not send a malformed or unsupported descriptor', async () => {
    signIn(organizationA);
    sessionStorage.setItem(explicitAmountAttemptStorageKey, '{');
    server.use(http.post('/api/allocations', () => {
      throw new Error('POST was sent');
    }));

    expect(inspectAllocationAttempt().status).toBe('invalid-storage');
    await expect(submitExplicitAmountAttempt()).rejects.toBeInstanceOf(AllocationAttemptBlockedError);
    expect(() => begin()).toThrow(AllocationAttemptBlockedError);
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBe('{');

    sessionStorage.setItem(explicitAmountAttemptStorageKey, JSON.stringify({ schemaVersion: 2, operation: 'ExplicitAmount' }));
    expect(inspectAllocationAttempt().status).toBe('invalid-storage');
    await expect(submitExplicitAmountAttempt()).rejects.toBeInstanceOf(AllocationAttemptBlockedError);
  });

  it('abandons only the local descriptor', () => {
    signIn(organizationA);
    begin();
    server.use(http.post('/api/allocations', () => {
      throw new Error('POST was sent');
    }));

    abandonExplicitAmountAttempt();

    expect(inspectAllocationAttempt().status).toBe('none');
  });

  it('issues a new key only after the stored attempt is explicitly abandoned', () => {
    signIn(organizationA);
    const first = begin();
    abandonExplicitAmountAttempt();
    const second = begin();

    expect(second.idempotencyKey).not.toBe(first.idempotencyKey);
  });

  it('rejects abandonment while the POST is still in flight', async () => {
    signIn(organizationA);
    const attempt = begin();
    let release: () => void = () => {};
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(http.post('/api/allocations', async () => {
      await gate;
      return allocationResponse(201, false);
    }));
    const pending = submitExplicitAmountAttempt();

    expect(() => abandonExplicitAmountAttempt()).toThrow(AllocationSubmissionInProgressError);
    expect(storedDescriptor().idempotencyKey).toBe(attempt.idempotencyKey);
    release();

    const result = await pending;
    expect(result.replayed).toBe(false);
    expect(result.allocationId).toBe(allocationId);
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBeNull();
  });

  it('does not let a completed request delete a newer stored attempt', async () => {
    signIn(organizationA);
    const attempt = begin();
    const replacementKey = 'dddddddd-dddd-dddd-dddd-dddddddddddd';
    let release: () => void = () => {};
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(http.post('/api/allocations', async () => {
      await gate;
      return allocationResponse(201, false);
    }));
    const pending = submitExplicitAmountAttempt();
    const stored = storedDescriptor();
    sessionStorage.setItem(explicitAmountAttemptStorageKey, JSON.stringify({
      ...stored,
      idempotencyKey: replacementKey,
    }));
    release();

    await expect(pending).rejects.toBeInstanceOf(AllocationAttemptChangedError);

    const retained = storedDescriptor();
    expect(retained.idempotencyKey).toBe(replacementKey);
    expect(retained.requestBody).toBe(attempt.requestBody);
    expect(retained.lifecycle).toBe('ready');
  });

  it('does not begin a new attempt while a submission is in flight', async () => {
    signIn(organizationA);
    begin();
    let release: () => void = () => {};
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(http.post('/api/allocations', async () => {
      await gate;
      return allocationResponse(201, false);
    }));
    const pending = submitExplicitAmountAttempt();
    sessionStorage.removeItem(explicitAmountAttemptStorageKey);
    const randomUUID = vi.spyOn(crypto, 'randomUUID');
    const setItem = vi.spyOn(Storage.prototype, 'setItem');

    try {
      expect(() => begin()).toThrow(AllocationSubmissionInProgressError);
      expect(randomUUID).not.toHaveBeenCalled();
      expect(setItem).not.toHaveBeenCalled();
      expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBeNull();
    } finally {
      vi.restoreAllMocks();
    }

    release();
    await expect(pending).rejects.toBeInstanceOf(AllocationAttemptChangedError);
    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBeNull();
  });

  it('reports storage cleanup failure after a verified response', async () => {
    signIn(organizationA);
    const attempt = begin();
    let posts = 0;
    server.use(http.post('/api/allocations', () => {
      posts += 1;
      return allocationResponse(201, false);
    }));
    vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => {
      throw new Error('cleanup blocked');
    });

    try {
      await expect(submitExplicitAmountAttempt()).rejects.toBeInstanceOf(AllocationStorageCleanupError);
      expect(posts).toBe(1);
      expect(storedDescriptor().idempotencyKey).toBe(attempt.idempotencyKey);
      expect(storedDescriptor().requestBody).toBe(attempt.requestBody);
      expect(() => begin()).toThrow(UnresolvedAllocationAttemptError);
    } finally {
      vi.restoreAllMocks();
    }
  });

  it('does not treat a no-op removal as successful cleanup', async () => {
    signIn(organizationA);
    const attempt = begin();
    let posts = 0;
    let removals = 0;
    server.use(http.post('/api/allocations', () => {
      posts += 1;
      return allocationResponse(201, false);
    }));
    vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => {
      removals += 1;
    });

    try {
      await expect(submitExplicitAmountAttempt()).rejects.toBeInstanceOf(AllocationStorageCleanupError);
      expect(posts).toBe(1);
      expect(removals).toBe(1);
      expect(storedDescriptor().idempotencyKey).toBe(attempt.idempotencyKey);
      expect(storedDescriptor().requestBody).toBe(attempt.requestBody);
      expect(() => begin()).toThrow(UnresolvedAllocationAttemptError);
    } finally {
      vi.restoreAllMocks();
    }
  });

  it('does not treat an unverifiable removal as successful cleanup', async () => {
    signIn(organizationA);
    const attempt = begin();
    let posts = 0;
    let removals = 0;
    let removed = false;
    const getItem = Storage.prototype.getItem;
    const removeItem = Storage.prototype.removeItem;
    server.use(http.post('/api/allocations', () => {
      posts += 1;
      return allocationResponse(201, false);
    }));
    vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(function (this: Storage, key: string) {
      removals += 1;
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
      await expect(submitExplicitAmountAttempt()).rejects.toBeInstanceOf(AllocationStorageCleanupError);
      expect(posts).toBe(1);
      expect(removals).toBe(1);
      expect(attempt.idempotencyKey).toBeTruthy();
    } finally {
      vi.restoreAllMocks();
    }

    expect(sessionStorage.getItem(explicitAmountAttemptStorageKey)).toBeNull();
  });

  it('rejects a second submission while the first is still running', async () => {
    signIn(organizationA);
    begin();
    let release: () => void = () => {};
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(http.post('/api/allocations', async () => {
      await gate;
      return allocationResponse(201, false);
    }));
    const first = submitExplicitAmountAttempt();

    await expect(submitExplicitAmountAttempt()).rejects.toBeInstanceOf(AllocationSubmissionInProgressError);
    release();
    await first;
  });

  it('does not send when the renewed token has no actor', async () => {
    signIn(organizationA);
    begin();
    setAccessToken(tokenFor(organizationA, null), Math.floor(Date.now() / 1000) + 3600);
    server.use(http.post('/api/allocations', () => {
      throw new Error('POST was sent');
    }));

    await expect(submitExplicitAmountAttempt()).rejects.toBeInstanceOf(AllocationActorRequiredError);
    expect(inspectAllocationAttempt().status).toBe('pending');
  });
});

function begin(overrides: Partial<BeginExplicitAmountInput> = {}) {
  return beginExplicitAmountAttempt({
    invoiceId,
    bankTransactionId,
    amount: '100.2500',
    currency: 'PLN',
    organizationId: organizationA,
    ...overrides,
  });
}

function signIn(organizationId: string, subject: string | null = 'operator-1'): void {
  setAccessToken(tokenFor(organizationId, subject), Math.floor(Date.now() / 1000) + 3600);
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

function storedDescriptor(): { idempotencyKey: string; requestBody: string; lifecycle: string } {
  return JSON.parse(sessionStorage.getItem(explicitAmountAttemptStorageKey) ?? 'null') as {
    idempotencyKey: string;
    requestBody: string;
    lifecycle: string;
  };
}

function allocationResponse(status: 200 | 201, replayed: boolean, amount = '100.25'): HttpResponse<string> {
  return new HttpResponse(
    `{"allocationId":"${allocationId}","invoiceId":"${invoiceId}","bankTransactionId":"${bankTransactionId}","amount":{"amount":${amount},"currency":"PLN"},"replayed":${replayed}}`,
    { status, headers: { 'Content-Type': 'application/json' } },
  );
}
