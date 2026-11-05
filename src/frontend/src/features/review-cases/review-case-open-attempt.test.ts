import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { ApiError } from '../../shared/api/errors';
import { sessionStore } from '../../shared/api/session-store';
import { server } from '../../test/server';
import {
  abandonReviewCaseOpenAttempt,
  beginReviewCaseOpenAttempt,
  inspectReviewCaseOpenAttempt,
  resetReviewCaseOpenAttemptStateForTests,
  reviewCaseOpenAttemptStorageKey,
  ReviewCaseOpenActorRequiredError,
  ReviewCaseOpenAttemptBlockedError,
  ReviewCaseOpenAttemptChangedError,
  ReviewCaseOpenPayloadConflictError,
  ReviewCaseOpenRejectedError,
  ReviewCaseOpenStorageError,
  ReviewCaseOpenSubmissionInProgressError,
  ReviewCaseOpenUncertaintyRemainsError,
  submitReviewCaseOpenAttempt,
  UnknownReviewCaseOpenOutcomeError,
  UnreadableReviewCaseOpenError,
  type ReviewCaseOpenAttempt,
} from './review-case-open-attempt';
import {
  developmentActorId,
  normalizeReviewCaseNote,
  serializeReviewCaseOpenBody,
  type NormalizedReviewCaseOpen,
} from './review-case-open-request';

const organizationId = '11111111-1111-4111-8111-111111111111';
const otherOrganizationId = '22222222-2222-4222-8222-222222222222';
const invoiceId = '33333333-3333-4333-8333-333333333333';
const bankTransactionId = '44444444-4444-4444-8444-444444444444';
const reviewCaseId = '55555555-5555-4555-8555-555555555555';

afterEach(() => {
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
  sessionStorage.clear();
  resetReviewCaseOpenAttemptStateForTests();
  sessionStore.rotate(null, null, null);
});

describe('review case open normalization', () => {
  it('trims .NET whitespace, keeps a byte-order mark, and limits the note to 500 characters', () => {
    expect(readNote(' \u0085\u00A0kept\u1680 ')).toBe('kept');
    expect(readNote(' \uFEFFkept\uFEFF ')).toBe('\uFEFFkept\uFEFF');
    expect(readNote(' \u0085 ')).toBeNull();
    expect(normalizeReviewCaseNote(`${'a'.repeat(500)}`).ok).toBe(true);
    expect(normalizeReviewCaseNote(` ${'a'.repeat(501)} `).ok).toBe(false);
  });

  it('accepts a one-character actor and rejects a blank or oversized actor', () => {
    expect(developmentActorId(token({ sub: 'a' }))).toBe('a');
    expect(developmentActorId(token({ sub: ' \u0085a\u00A0' }))).toBe('a');
    expect(developmentActorId(token({ sub: '\uFEFFa' }))).toBe('\uFEFFa');
    expect(developmentActorId(token({ sub: ' ' }))).toBeNull();
    expect(developmentActorId(token({ sub: 'a'.repeat(256) }))).toHaveLength(256);
    expect(developmentActorId(token({ sub: 'a'.repeat(257) }))).toBeNull();
  });
});

describe('review case open attempt', () => {
  it('stores the exact body before sending and rejects a contradictory record without rewriting it', () => {
    signIn();
    const attempt = beginReviewCaseOpenAttempt(openRequest('\uFEFFkept\uFEFF'));
    expect(attempt.requestBody).toBe(JSON.stringify({
      reason: 'CompetingMatch',
      invoiceId,
      bankTransactionId,
      note: '\uFEFFkept\uFEFF',
    }));
    expect(JSON.parse(sessionStorage.getItem(reviewCaseOpenAttemptStorageKey) ?? '{}').requestBody).toBe(attempt.requestBody);

    const contradictory = JSON.parse(sessionStorage.getItem(reviewCaseOpenAttemptStorageKey) ?? '{}') as ReviewCaseOpenAttempt;
    contradictory.requestBody = JSON.stringify({ reason: 'NeedsInvestigation', invoiceId });
    sessionStorage.setItem(reviewCaseOpenAttemptStorageKey, JSON.stringify(contradictory));
    const setItem = vi.spyOn(Storage.prototype, 'setItem');
    expect(inspectReviewCaseOpenAttempt().status).toBe('invalid-storage');
    expect(() => beginReviewCaseOpenAttempt(openRequest(null))).toThrow(ReviewCaseOpenAttemptBlockedError);
    expect(setItem).not.toHaveBeenCalled();
    expect(sessionStorage.getItem(reviewCaseOpenAttemptStorageKey)).toBe(JSON.stringify(contradictory));
  });

  it('does not send when storage cannot save the attempt', () => {
    signIn();
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('quota');
    });
    let posts = 0;
    server.use(http.post('/api/review-cases', () => {
      posts += 1;
      return new HttpResponse(null, { status: 500 });
    }));
    expect(() => beginReviewCaseOpenAttempt(openRequest(null))).toThrow(ReviewCaseOpenStorageError);
    expect(posts).toBe(0);
  });

  it('requires an actor before fetch and accepts a one-character subject', async () => {
    sessionStore.rotate(token({ sub: ' ' }), 4_000_000_000, null);
    let posts = 0;
    server.use(http.post('/api/review-cases', () => {
      posts += 1;
      return new HttpResponse(null, { status: 500 });
    }));
    expect(() => beginReviewCaseOpenAttempt(openRequest(null))).toThrow(ReviewCaseOpenActorRequiredError);
    expect(posts).toBe(0);

    sessionStore.rotate(token({ sub: 'a' }), 4_000_000_000, null);
    beginReviewCaseOpenAttempt(openRequest(null));
    server.use(http.post('/api/review-cases', () => HttpResponse.json(
      caseBody({ status: 'Open', note: null }),
      { status: 201, headers: { Location: `/api/review-cases/${reviewCaseId}` } },
    )));
    await expect(submitReviewCaseOpenAttempt()).resolves.toMatchObject({ created: true, reviewCaseId });
  });

  it('verifies a created open and a terminal replay, and keeps a malformed 201', async () => {
    signIn();
    beginReviewCaseOpenAttempt(openRequest('kept'));
    server.use(http.post('/api/review-cases', () => HttpResponse.json(
      caseBody({ status: 'Open' }),
      { status: 201, headers: { Location: `/api/review-cases/${reviewCaseId}` } },
    )));
    await expect(submitReviewCaseOpenAttempt()).resolves.toMatchObject({ created: true, status: 'Open' });
    expect(sessionStorage.getItem(reviewCaseOpenAttemptStorageKey)).toBeNull();

    beginReviewCaseOpenAttempt(openRequest('kept'));
    const stored = sessionStorage.getItem(reviewCaseOpenAttemptStorageKey);
    if (stored === null) {
      throw new Error('The opening attempt was not stored.');
    }

    server.use(http.post('/api/review-cases', () => HttpResponse.json(caseBody({
      status: 'Dismissed',
      closedAt: '2026-11-05T09:00:00Z',
      resolutionNote: 'closed elsewhere',
    }))));
    await expect(submitReviewCaseOpenAttempt()).resolves.toMatchObject({ created: false, status: 'Dismissed' });
    expect(sessionStorage.getItem(reviewCaseOpenAttemptStorageKey)).toBeNull();

    sessionStorage.setItem(reviewCaseOpenAttemptStorageKey, stored);
    server.use(http.post('/api/review-cases', () => HttpResponse.json(
      caseBody({ status: 'Resolved', outcome: 'NoActionRequired', closedAt: '2026-11-05T09:00:00Z' }),
      { status: 201, headers: { Location: `/api/review-cases/${reviewCaseId}` } },
    )));
    await expect(submitReviewCaseOpenAttempt()).rejects.toBeInstanceOf(UnreadableReviewCaseOpenError);
    expect(sessionStorage.getItem(reviewCaseOpenAttemptStorageKey)).not.toBeNull();
  });

  it('keeps the original key and body when a success response has a missing or invalid date', async () => {
    signIn();
    const attempt = beginReviewCaseOpenAttempt(openRequest('kept'));
    const missingClosedAt = caseBody({ status: 'Open' });
    delete missingClosedAt.closedAt;
    const responses = [
      { status: 201, body: missingClosedAt },
      { status: 201, body: caseBody({ status: 'Open', closedAt: 'not-a-date' }) },
      { status: 200, body: caseBody({ status: 'Dismissed', closedAt: '2026-02-31T09:00:00Z', resolutionNote: 'closed elsewhere' }) },
      { status: 201, body: caseBody({ status: 'Open', openedAt: '2026-11-05T25:00:00Z' }) },
      { status: 200, body: caseBody({ status: 'Open', openedAt: '2026-02-31T08:00:00Z' }) },
    ];

    for (const response of responses) {
      server.use(http.post('/api/review-cases', () => HttpResponse.json(response.body, {
        status: response.status,
        headers: { Location: `/api/review-cases/${reviewCaseId}` },
      })));
      await expect(submitReviewCaseOpenAttempt()).rejects.toBeInstanceOf(UnreadableReviewCaseOpenError);
      const stored = JSON.parse(sessionStorage.getItem(reviewCaseOpenAttemptStorageKey) ?? '{}') as ReviewCaseOpenAttempt;
      expect(stored.idempotencyKey).toBe(attempt.idempotencyKey);
      expect(stored.requestBody).toBe(attempt.requestBody);
    }
  });

  it('keeps the original key after a payload conflict, a first refusal, and a later refusal', async () => {
    signIn();
    const attempt = beginReviewCaseOpenAttempt(openRequest('kept'));
    server.use(http.post('/api/review-cases', () => HttpResponse.json(
      { title: 'Conflict', code: 'reconciliation.review_case.idempotency_payload_conflict' },
      { status: 409 },
    )));
    await expect(submitReviewCaseOpenAttempt()).rejects.toBeInstanceOf(ReviewCaseOpenPayloadConflictError);
    expect(storedBody()).toBe(attempt.requestBody);
    expect(storedLifecycle()).toBe('sending');

    sessionStorage.clear();
    const rejected = beginReviewCaseOpenAttempt(openRequest('kept'));
    server.use(http.post('/api/review-cases', () => HttpResponse.json(
      { title: 'Conflict', code: 'reconciliation.review_case.open_request_conflict' },
      { status: 409 },
    )));
    await expect(submitReviewCaseOpenAttempt()).rejects.toBeInstanceOf(ReviewCaseOpenRejectedError);
    expect(storedLifecycle()).toBe('rejected');
    expect(storedBody()).toBe(rejected.requestBody);

    sessionStorage.clear();
    const uncertain = beginReviewCaseOpenAttempt(openRequest('kept'));
    server.use(http.post('/api/review-cases', () => new HttpResponse(null, { status: 500 })));
    await expect(submitReviewCaseOpenAttempt()).rejects.toBeInstanceOf(UnknownReviewCaseOpenOutcomeError);
    expect(storedLifecycle()).toBe('uncertain');
    server.use(http.post('/api/review-cases', () => HttpResponse.json(
      { title: 'Forbidden', code: 'reconciliation.actor.required' },
      { status: 403 },
    )));
    await expect(submitReviewCaseOpenAttempt()).rejects.toBeInstanceOf(ReviewCaseOpenUncertaintyRemainsError);
    expect(storedLifecycle()).toBe('uncertain');
    expect(storedBody()).toBe(uncertain.requestBody);
    expect(JSON.parse(sessionStorage.getItem(reviewCaseOpenAttemptStorageKey) ?? '{}').idempotencyKey).toBe(uncertain.idempotencyKey);
  });

  it('sends one request for two callers and does not clear a body that changed during the response', async () => {
    signIn();
    beginReviewCaseOpenAttempt(openRequest('kept'));
    let posts = 0;
    let release: (() => void) | undefined;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(http.post('/api/review-cases', async () => {
      posts += 1;
      await gate;
      return HttpResponse.json(
        caseBody({ status: 'Open' }),
        { status: 201, headers: { Location: `/api/review-cases/${reviewCaseId}` } },
      );
    }));
    const first = submitReviewCaseOpenAttempt();
    const second = submitReviewCaseOpenAttempt();
    await expect(second).rejects.toBeInstanceOf(ReviewCaseOpenSubmissionInProgressError);
    release?.();
    await expect(first).resolves.toMatchObject({ created: true });
    expect(posts).toBe(1);

    beginReviewCaseOpenAttempt(openRequest('kept'));
    server.use(http.post('/api/review-cases', () => {
      const parsed = JSON.parse(sessionStorage.getItem(reviewCaseOpenAttemptStorageKey) ?? '{}') as ReviewCaseOpenAttempt;
      const changed: ReviewCaseOpenAttempt = {
        ...parsed,
        note: 'changed',
        requestBody: serializeReviewCaseOpenBody({ ...openRequest('changed'), note: 'changed' }),
      };
      sessionStorage.setItem(reviewCaseOpenAttemptStorageKey, JSON.stringify(changed));
      return HttpResponse.json(
        caseBody({ status: 'Open', note: 'kept' }),
        { status: 201, headers: { Location: `/api/review-cases/${reviewCaseId}` } },
      );
    }));
    await expect(submitReviewCaseOpenAttempt()).rejects.toBeInstanceOf(ReviewCaseOpenAttemptChangedError);
    expect(JSON.parse(sessionStorage.getItem(reviewCaseOpenAttemptStorageKey) ?? '{}').note).toBe('changed');
  });

  it('does not let a late response clear another organization attempt', async () => {
    signIn();
    beginReviewCaseOpenAttempt(openRequest('kept'));
    const replacement = JSON.stringify({
      schemaVersion: 1,
      operation: 'ReviewCaseOpen',
      organizationId: otherOrganizationId,
      idempotencyKey: '66666666-6666-4666-8666-666666666666',
      requestBody: serializeReviewCaseOpenBody(openRequest('other')),
      ...openRequest('other'),
      lifecycle: 'ready',
      uncertaintyLocked: false,
    });
    server.use(http.post('/api/review-cases', () => {
      sessionStore.rotate(token({ org_id: otherOrganizationId }), 4_000_000_000, null);
      sessionStorage.setItem(reviewCaseOpenAttemptStorageKey, replacement);
      return HttpResponse.json(
        caseBody({ status: 'Open' }),
        { status: 201, headers: { Location: `/api/review-cases/${reviewCaseId}` } },
      );
    }));
    await expect(submitReviewCaseOpenAttempt()).rejects.toThrow();
    expect(sessionStorage.getItem(reviewCaseOpenAttemptStorageKey)).toBe(replacement);
  });

  it('does not fetch a cookie session', async () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    sessionStore.replaceCookieSession({
      authenticated: true,
      csrfToken: 'csrf',
      issuer: 'https://issuer.example',
      subject: 'oidc-operator',
      displayName: 'Ada',
      expiresAt: null,
      organizations: [],
      activeOrganization: { organizationId, displayName: 'North' },
      authorization: 'verified',
      capabilities: ['review-cases.read', 'review-cases.open'],
    }, true);
    sessionStorage.setItem(reviewCaseOpenAttemptStorageKey, 'planted');
    let posts = 0;
    server.use(http.post('/api/review-cases', () => {
      posts += 1;
      return new HttpResponse(null, { status: 500 });
    }));
    await expect(submitReviewCaseOpenAttempt()).rejects.toBeInstanceOf(ApiError);
    expect(() => beginReviewCaseOpenAttempt(openRequest(null))).toThrow(ApiError);
    expect(posts).toBe(0);
    expect(sessionStorage.getItem(reviewCaseOpenAttemptStorageKey)).toBe('planted');
  });

  it('removes local tracking only for the same key and body', () => {
    signIn();
    const attempt = beginReviewCaseOpenAttempt(openRequest('kept'));
    expect(() => abandonReviewCaseOpenAttempt(attempt.idempotencyKey, '{"reason":"CompetingMatch"}')).toThrow(ReviewCaseOpenAttemptChangedError);
    expect(sessionStorage.getItem(reviewCaseOpenAttemptStorageKey)).not.toBeNull();
    abandonReviewCaseOpenAttempt(attempt.idempotencyKey, attempt.requestBody);
    expect(sessionStorage.getItem(reviewCaseOpenAttemptStorageKey)).toBeNull();
  });
});

function signIn(): void {
  sessionStore.rotate(token(), 4_000_000_000, null);
}

function token(overrides: { sub?: string; org_id?: string } = {}): string {
  const payload = {
    exp: 4_000_000_000,
    sub: overrides.sub ?? 'operator-1',
    org_id: overrides.org_id ?? organizationId,
  };
  return `${encode({ alg: 'none' })}.${encode(payload)}.signature`;
}

function encode(value: object): string {
  const bytes = new TextEncoder().encode(JSON.stringify(value));
  let binary = '';
  bytes.forEach((byte) => {
    binary += String.fromCharCode(byte);
  });
  return btoa(binary).replace(/=+$/g, '').replace(/\+/g, '-').replace(/\//g, '_');
}

function openRequest(note: string | null): NormalizedReviewCaseOpen {
  return {
    reason: 'CompetingMatch',
    invoiceId,
    bankTransactionId,
    sourceLabel: null,
    note,
  };
}

function caseBody(overrides: Record<string, unknown>): Record<string, unknown> {
  return {
    reviewCaseId,
    reason: 'CompetingMatch',
    status: 'Open',
    invoiceId,
    bankTransactionId,
    sourceLabel: null,
    note: 'kept',
    outcome: null,
    resolutionNote: null,
    allocationId: null,
    openedAt: '2026-11-05T08:00:00Z',
    closedAt: null,
    concurrencyToken: 'AAAAAAAAB+M=',
    ...overrides,
  };
}

function readNote(value: string): string | null {
  const decision = normalizeReviewCaseNote(value);
  return decision.ok ? decision.note : null;
}

function storedBody(): string {
  return JSON.parse(sessionStorage.getItem(reviewCaseOpenAttemptStorageKey) ?? '{}').requestBody as string;
}

function storedLifecycle(): string {
  return JSON.parse(sessionStorage.getItem(reviewCaseOpenAttemptStorageKey) ?? '{}').lifecycle as string;
}
