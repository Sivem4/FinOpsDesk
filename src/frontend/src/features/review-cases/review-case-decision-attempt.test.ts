import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { ApiError } from '../../shared/api/errors';
import { sessionStore } from '../../shared/api/session-store';
import { server } from '../../test/server';
import {
  beginReviewCaseDecisionAttempt,
  resetReviewCaseDecisionAttemptStateForTests,
  reviewCaseDecisionAttemptStorageKey,
  ReviewCaseDecisionAttemptBlockedError,
  ReviewCaseDecisionAttemptChangedError,
  ReviewCaseDecisionConflictError,
  ReviewCaseDecisionRefusedError,
  ReviewCaseDecisionStorageError,
  ReviewCaseDecisionSubmissionInProgressError,
  ReviewCaseDecisionUncertaintyRemainsError,
  submitReviewCaseDecisionAttempt,
  UnreadableReviewCaseDecisionError,
  type ReviewCaseDecisionAttempt,
} from './review-case-decision-attempt';
import {
  serializeReviewCaseDecisionBody,
  type NormalizedReviewCaseDecision,
} from './review-case-decision-request';
import { reviewCaseOpenAttemptStorageKey } from './review-case-open-attempt';
import { normalizeReviewCaseNote } from './review-case-open-request';

const organizationId = '11111111-1111-4111-8111-111111111111';
const caseId = '55555555-5555-4555-8555-555555555555';
const allocationId = '66666666-6666-4666-8666-666666666666';
const requestToken = 'AQIDBAUGBwg=';
const responseToken = 'AAAAAAAAB+M=';

afterEach(() => {
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
  sessionStorage.removeItem(reviewCaseDecisionAttemptStorageKey);
  sessionStorage.removeItem(reviewCaseOpenAttemptStorageKey);
  resetReviewCaseDecisionAttemptStateForTests();
});

describe('review case decision attempt', () => {
  it('keeps the .NET note normalization, including U+FEFF and the 500 character limit', () => {
    expect(readNote('  \u00A0kept\uFEFF  ')).toBe('kept\uFEFF');
    expect(readNote('\uFEFFkept')).toBe('\uFEFFkept');
    expect(readNote(' \u1680 ')).toBeNull();
    expect(readNote('a'.repeat(500))).toBe('a'.repeat(500));
    expect(readNote(` ${'a'.repeat(500)} `)).toBe('a'.repeat(500));
    expect(normalizeReviewCaseNote('a'.repeat(501)).ok).toBe(false);
  });

  it('serializes each resolve outcome and dismiss without an allocation field unless one is required', () => {
    expect(JSON.parse(serializeReviewCaseDecisionBody(decision()))).toEqual({
      outcome: 'AllocationRecorded',
      allocationId,
      resolutionNote: 'kept\uFEFF',
      concurrencyToken: requestToken,
    });
    expect(JSON.parse(serializeReviewCaseDecisionBody(decision({
      outcome: 'NoActionRequired',
      allocationId: null,
      resolutionNote: null,
    })))).toEqual({
      outcome: 'NoActionRequired',
      concurrencyToken: requestToken,
    });
    expect(JSON.parse(serializeReviewCaseDecisionBody(decision({
      operation: 'ReviewCaseDismiss',
      outcome: null,
      allocationId: null,
      resolutionNote: null,
    })))).toEqual({ concurrencyToken: requestToken });
  });

  it('does not send a contradictory or unstorable descriptor', () => {
    sessionStore.rotate(encode({ sub: 'operator-1', org_id: organizationId }), 4_000_000_000, null);
    sessionStorage.setItem(reviewCaseDecisionAttemptStorageKey, JSON.stringify({
      ...stored(decision()),
      requestBody: '{"outcome":"HandledOutside","concurrencyToken":"AQIDBAUGBwg="}',
    }));
    const setItem = vi.spyOn(Storage.prototype, 'setItem');

    expect(() => beginReviewCaseDecisionAttempt(decision())).toThrow(ReviewCaseDecisionAttemptBlockedError);
    expect(setItem).not.toHaveBeenCalled();
  });

  it('does not send when storage cannot keep the descriptor', async () => {
    sessionStore.rotate(encode({ sub: 'a', org_id: organizationId }), 4_000_000_000, null);
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    const fetchSpy = vi.spyOn(globalThis, 'fetch');

    expect(() => beginReviewCaseDecisionAttempt(decision())).toThrow(ReviewCaseDecisionStorageError);
    await expect(submitReviewCaseDecisionAttempt()).rejects.toThrow(ReviewCaseDecisionAttemptBlockedError);
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it.each([
    ['AllocationRecorded', allocationId],
    ['NoActionRequired', null],
    ['HandledOutside', null],
  ] as const)('verifies a %s decision and removes only that descriptor', async (outcome, allocation) => {
    const openRecord = '{"schemaVersion":1,"operation":"ReviewCaseOpen"}';
    sessionStorage.setItem(reviewCaseOpenAttemptStorageKey, openRecord);
    sessionStore.rotate(encode({ sub: 'a', org_id: organizationId }), 4_000_000_000, null);
    let headerNames: string[] = [];
    server.use(http.post(`/api/review-cases/${caseId}/resolve`, ({ request }) => {
      headerNames = [...request.headers.keys()];
      return HttpResponse.json(caseBody({
        status: 'Resolved',
        outcome,
        allocationId: allocation,
        resolutionNote: 'kept\uFEFF',
        closedAt: '2026-11-05T09:00:00Z',
        concurrencyToken: responseToken,
      }));
    }));

    beginReviewCaseDecisionAttempt(decision({ outcome, allocationId: allocation }));
    const verified = await submitReviewCaseDecisionAttempt();

    expect(headerNames).not.toContain('idempotency-key');
    expect(verified).toMatchObject({
      reviewCaseId: caseId,
      status: 'Resolved',
      outcome,
      allocationId: allocation,
      resolutionNote: 'kept\uFEFF',
      cleanup: 'removed',
    });
    expect(sessionStorage.getItem(reviewCaseDecisionAttemptStorageKey)).toBeNull();
    expect(sessionStorage.getItem(reviewCaseOpenAttemptStorageKey)).toBe(openRecord);
  });

  it('verifies dismiss without an outcome or allocation and retries the original body without a new read', async () => {
    sessionStore.rotate(encode({ sub: 'a', org_id: organizationId }), 4_000_000_000, null);
    const bodies: string[] = [];
    let gets = 0;
    server.use(
      http.get('/api/review-cases/:id', () => {
        gets += 1;
        return new HttpResponse(null, { status: 500 });
      }),
      http.post(`/api/review-cases/${caseId}/dismiss`, async ({ request }) => {
        bodies.push(await request.text());
        if (bodies.length === 1) {
          return new HttpResponse(null, { status: 500 });
        }

        return HttpResponse.json(caseBody({
          status: 'Dismissed',
          closedAt: '2026-11-05T09:30:00Z',
          concurrencyToken: responseToken,
        }));
      }),
    );

    beginReviewCaseDecisionAttempt(decision({
      operation: 'ReviewCaseDismiss',
      outcome: null,
      allocationId: null,
      resolutionNote: null,
    }));
    const original = storedBody();
    await expect(submitReviewCaseDecisionAttempt()).rejects.toThrow();
    const verified = await submitReviewCaseDecisionAttempt();

    expect(gets).toBe(0);
    expect(bodies).toEqual([original, original]);
    expect(verified.status).toBe('Dismissed');
    expect(verified.outcome).toBeNull();
    expect(verified.allocationId).toBeNull();
    expect(JSON.parse(original)).toEqual({ concurrencyToken: requestToken });
  });

  it('keeps the original body for concurrency and terminal conflicts', async () => {
    sessionStore.rotate(encode({ sub: 'a', org_id: organizationId }), 4_000_000_000, null);
    const codes = [
      'reconciliation.review_case.concurrency_conflict',
      'reconciliation.review_case.terminal_conflict',
    ];
    let index = 0;
    server.use(http.post(`/api/review-cases/${caseId}/resolve`, () => problem(409, codes[index++] ?? '')));
    beginReviewCaseDecisionAttempt(decision());
    const original = storedBody();

    await expect(submitReviewCaseDecisionAttempt()).rejects.toBeInstanceOf(ReviewCaseDecisionConflictError);
    expect(storedLifecycle()).toBe('sending');
    await expect(submitReviewCaseDecisionAttempt()).rejects.toBeInstanceOf(ReviewCaseDecisionUncertaintyRemainsError);
    expect(storedBody()).toBe(original);
    expect(storedLifecycle()).toBe('uncertain');
  });

  it('refuses an allocation mismatch once and keeps later uncertainty', async () => {
    sessionStore.rotate(encode({ sub: 'a', org_id: organizationId }), 4_000_000_000, null);
    let count = 0;
    server.use(http.post(`/api/review-cases/${caseId}/resolve`, () => {
      count += 1;
      return count === 1
        ? problem(409, 'reconciliation.review_case.allocation_mismatch')
        : problem(403, 'reconciliation.actor.required');
    }));
    beginReviewCaseDecisionAttempt(decision());

    await expect(submitReviewCaseDecisionAttempt()).rejects.toBeInstanceOf(ReviewCaseDecisionRefusedError);
    expect(storedLifecycle()).toBe('refused');
    await expect(submitReviewCaseDecisionAttempt()).rejects.toBeInstanceOf(ReviewCaseDecisionRefusedError);
    expect(count).toBe(1);
  });

  it('keeps uncertainty when a lost response is followed by a refusal', async () => {
    sessionStore.rotate(encode({ sub: 'a', org_id: organizationId }), 4_000_000_000, null);
    let count = 0;
    server.use(http.post(`/api/review-cases/${caseId}/resolve`, () => {
      count += 1;
      return count === 1 ? new HttpResponse(null, { status: 500 }) : problem(401, 'unauthorized');
    }));
    beginReviewCaseDecisionAttempt(decision());
    const original = storedBody();

    await expect(submitReviewCaseDecisionAttempt()).rejects.toThrow();
    await expect(submitReviewCaseDecisionAttempt()).rejects.toBeInstanceOf(ReviewCaseDecisionUncertaintyRemainsError);
    expect(storedLifecycle()).toBe('uncertain');
    expect(storedBody()).toBe(original);
  });

  it('keeps the original body when a success response has another token and rejects a bad success', async () => {
    sessionStore.rotate(encode({ sub: 'a', org_id: organizationId }), 4_000_000_000, null);
    server.use(http.post(`/api/review-cases/${caseId}/resolve`, () => HttpResponse.json(caseBody({
      status: 'Resolved',
      outcome: 'NoActionRequired',
      closedAt: '2026-02-31T09:00:00Z',
      concurrencyToken: responseToken,
    }))));
    beginReviewCaseDecisionAttempt(decision({ outcome: 'NoActionRequired', allocationId: null }));
    const original = storedBody();

    await expect(submitReviewCaseDecisionAttempt()).rejects.toBeInstanceOf(UnreadableReviewCaseDecisionError);
    expect(storedBody()).toBe(original);
    expect(storedLifecycle()).toBe('uncertain');
  });

  it('accepts a terminal replay whose response token differs from the request', async () => {
    sessionStore.rotate(encode({ sub: 'a', org_id: organizationId }), 4_000_000_000, null);
    server.use(http.post(`/api/review-cases/${caseId}/resolve`, () => HttpResponse.json(caseBody({
      status: 'Resolved',
      outcome: 'HandledOutside',
      closedAt: '2026-11-05T09:00:00Z',
      concurrencyToken: responseToken,
    }))));
    beginReviewCaseDecisionAttempt(decision({ outcome: 'HandledOutside', allocationId: null, resolutionNote: null }));

    const verified = await submitReviewCaseDecisionAttempt();

    expect(verified.outcome).toBe('HandledOutside');
    expect(verified.allocationId).toBeNull();
    expect(sessionStorage.getItem(reviewCaseDecisionAttemptStorageKey)).toBeNull();
  });

  it('does not let a second call or a changed record replace the original body', async () => {
    sessionStore.rotate(encode({ sub: 'a', org_id: organizationId }), 4_000_000_000, null);
    let release: (response: Response) => void = () => {
      throw new Error('The response was not held.');
    };
    let markStarted: () => void = () => undefined;
    const started = new Promise<void>((resolve) => {
      markStarted = resolve;
    });
    server.use(http.post(`/api/review-cases/${caseId}/resolve`, () => {
      markStarted();
      return new Promise((resolve) => {
        release = resolve;
      });
    }));
    beginReviewCaseDecisionAttempt(decision());
    const first = submitReviewCaseDecisionAttempt();
    await expect(submitReviewCaseDecisionAttempt()).rejects.toBeInstanceOf(ReviewCaseDecisionSubmissionInProgressError);
    await started;
    const changed: ReviewCaseDecisionAttempt = {
      ...storedAttempt(),
      resolutionNote: 'changed',
      requestBody: serializeReviewCaseDecisionBody({ ...decision(), resolutionNote: 'changed' }),
    };
    sessionStorage.setItem(reviewCaseDecisionAttemptStorageKey, JSON.stringify(changed));
    release(HttpResponse.json(caseBody({
      status: 'Resolved',
      outcome: 'AllocationRecorded',
      allocationId,
      resolutionNote: 'kept\uFEFF',
      closedAt: '2026-11-05T09:00:00Z',
    })));

    await expect(first).rejects.toBeInstanceOf(ReviewCaseDecisionAttemptChangedError);
    expect(sessionStorage.getItem(reviewCaseDecisionAttemptStorageKey)).toBe(JSON.stringify(changed));
  });

  it('does not delete a decision after the session changes', async () => {
    sessionStore.rotate(encode({ sub: 'a', org_id: organizationId }), 4_000_000_000, null);
    let markStarted: () => void = () => undefined;
    const started = new Promise<void>((resolve) => {
      markStarted = resolve;
    });
    server.use(http.post(`/api/review-cases/${caseId}/resolve`, () => {
      markStarted();
      return new Promise(() => undefined);
    }));
    beginReviewCaseDecisionAttempt(decision());
    const pending = submitReviewCaseDecisionAttempt();
    await started;
    const replacement = '{"replaced":true}';
    sessionStorage.setItem(reviewCaseDecisionAttemptStorageKey, replacement);
    sessionStore.rotate(encode({ sub: 'b', org_id: organizationId }), 4_000_000_000, null);

    await expect(pending).rejects.toThrow();
    expect(sessionStorage.getItem(reviewCaseDecisionAttemptStorageKey)).toBe(replacement);
  });

  it('does not post a planted decision from cookie mode', async () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    const planted = JSON.stringify(stored(decision()));
    sessionStorage.setItem(reviewCaseDecisionAttemptStorageKey, planted);
    const fetchSpy = vi.spyOn(globalThis, 'fetch');

    await expect(submitReviewCaseDecisionAttempt()).rejects.toBeInstanceOf(ApiError);
    expect(fetchSpy).not.toHaveBeenCalled();
    expect(sessionStorage.getItem(reviewCaseDecisionAttemptStorageKey)).toBe(planted);
  });
});

function decision(overrides: Partial<NormalizedReviewCaseDecision> = {}): NormalizedReviewCaseDecision {
  return {
    operation: 'ReviewCaseResolve',
    caseId,
    outcome: 'AllocationRecorded',
    allocationId,
    resolutionNote: 'kept\uFEFF',
    concurrencyToken: requestToken,
    ...overrides,
  };
}

function stored(value: NormalizedReviewCaseDecision): ReviewCaseDecisionAttempt {
  return {
    schemaVersion: 1,
    organizationId,
    requestBody: serializeReviewCaseDecisionBody(value),
    lifecycle: 'ready',
    uncertaintyLocked: false,
    ...value,
  };
}

function caseBody(overrides: Record<string, unknown>): Record<string, unknown> {
  return {
    reviewCaseId: caseId,
    reason: 'CompetingMatch',
    status: 'Open',
    invoiceId: '33333333-3333-4333-8333-333333333333',
    bankTransactionId: '44444444-4444-4444-8444-444444444444',
    sourceLabel: 'manual',
    note: 'opened',
    outcome: null,
    resolutionNote: null,
    allocationId: null,
    openedAt: '2026-11-05T08:00:00Z',
    closedAt: null,
    concurrencyToken: responseToken,
    ...overrides,
  };
}

function problem(status: number, code: string) {
  return HttpResponse.json({ title: 'Conflict', code }, { status });
}

function readNote(value: string): string | null {
  const note = normalizeReviewCaseNote(value);
  return note.ok ? note.note : null;
}

function storedBody(): string {
  return storedAttempt().requestBody;
}

function storedLifecycle(): string {
  return storedAttempt().lifecycle;
}

function storedAttempt(): ReviewCaseDecisionAttempt {
  return JSON.parse(sessionStorage.getItem(reviewCaseDecisionAttemptStorageKey) ?? '{}') as ReviewCaseDecisionAttempt;
}

function encode(payload: { sub: string; org_id: string }): string {
  const bytes = new TextEncoder().encode(JSON.stringify({ alg: 'none' }));
  const body = new TextEncoder().encode(JSON.stringify(payload));
  return `${btoa(String.fromCharCode(...bytes)).replace(/=+$/g, '')}.${btoa(String.fromCharCode(...body)).replace(/=+$/g, '')}.signature`;
}
