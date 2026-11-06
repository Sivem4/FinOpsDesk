import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { sessionStore, type CookieSessionFields } from '../../shared/api/session-store';
import { server } from '../../test/server';
import { fakeJwt } from '../../test/jwt';
import {
  beginReviewCaseCommentAttempt,
  inspectReviewCaseCommentAttempt,
  resetReviewCaseCommentAttemptStateForTests,
  reviewCaseCommentAttemptStorageKey,
  ReviewCaseCommentAttemptBlockedError,
  ReviewCaseCommentPayloadConflictError,
  ReviewCaseCommentRejectedError,
  ReviewCaseCommentStorageError,
  ReviewCaseCommentTenantMismatchError,
  ReviewCaseCommentUncertaintyRemainsError,
  submitReviewCaseCommentAttempt,
  UnknownReviewCaseCommentOutcomeError,
  UnreadableReviewCaseCommentError,
} from './review-case-comment-attempt';
import { reviewCaseDecisionAttemptStorageKey } from './review-case-decision-attempt';
import { reviewCaseOpenAttemptStorageKey } from './review-case-open-attempt';

const caseId = '11111111-1111-4111-8111-111111111111';
const commentId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1';
const organizationId = '11111111-1111-1111-1111-111111111111';
const otherOrganizationId = '22222222-2222-4222-8222-222222222222';

afterEach(() => {
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
  sessionStorage.clear();
  resetReviewCaseCommentAttemptStateForTests();
  sessionStore.rotate(null, null, null);
});

describe('review case comment attempt', () => {
  it('stores the exact body before the network and blocks corrupt or failed storage', async () => {
    signIn();
    let posts = 0;
    server.use(http.post(`/api/review-cases/${caseId}/comments`, async ({ request }) => {
      posts += 1;
      const stored = JSON.parse(sessionStorage.getItem(reviewCaseCommentAttemptStorageKey) ?? '{}') as {
        lifecycle: string;
        requestBody: string;
        idempotencyKey: string;
      };
      expect(stored.lifecycle).toBe('sending');
      expect(stored.requestBody).toBe(await request.text());
      expect(request.headers.get('Idempotency-Key')).toBe(stored.idempotencyKey);
      return HttpResponse.json(commentBody());
    }));

    const attempt = beginReviewCaseCommentAttempt(caseId, 'kept');
    expect(attempt.lifecycle).toBe('ready');
    expect(attempt.requestBody).toBe(JSON.stringify({ text: 'kept' }));
    expect(JSON.parse(sessionStorage.getItem(reviewCaseCommentAttemptStorageKey) ?? '{}').lifecycle).toBe('ready');
    const result = await submitReviewCaseCommentAttempt();
    expect(posts).toBe(1);
    expect(result.cleanup).toBe('removed');
    expect(sessionStorage.getItem(reviewCaseCommentAttemptStorageKey)).toBeNull();
    const preserved = beginReviewCaseCommentAttempt(caseId, '\uFEFFkept\uFEFF');
    expect(preserved.requestBody).toBe(JSON.stringify({ text: '\uFEFFkept\uFEFF' }));
    sessionStorage.clear();

    posts = 0;
    sessionStorage.setItem(reviewCaseCommentAttemptStorageKey, '{');
    expect(() => beginReviewCaseCommentAttempt(caseId, 'kept')).toThrow(ReviewCaseCommentAttemptBlockedError);
    expect(sessionStorage.getItem(reviewCaseCommentAttemptStorageKey)).toBe('{');
    expect(posts).toBe(0);

    sessionStorage.clear();
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('storage failed');
    });
    expect(() => beginReviewCaseCommentAttempt(caseId, 'kept')).toThrow(ReviewCaseCommentStorageError);
    expect(posts).toBe(0);
  });

  it('replays the same key and body and keeps the original actor', async () => {
    signIn();
    const keys = ['aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa2', 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb2'];
    vi.spyOn(crypto, 'randomUUID').mockImplementation(
      () => (keys.shift() ?? 'cccccccc-cccc-4ccc-8ccc-ccccccccccc2') as ReturnType<Crypto['randomUUID']>,
    );
    server.use(http.post(`/api/review-cases/${caseId}/comments`, () => HttpResponse.json(
      commentBody({ actorId: 'original-actor' }),
      { status: 200 },
    )));

    beginReviewCaseCommentAttempt(caseId, 'kept');
    const result = await submitReviewCaseCommentAttempt();

    expect(result.created).toBe(false);
    expect(result.actorId).toBe('original-actor');
    expect(result.text).toBe('kept');
    expect(keys).toHaveLength(1);
  });

  it('keeps uncertainty when a stored sending attempt is refused after reload', async () => {
    signIn();
    beginReviewCaseCommentAttempt(caseId, 'kept');
    const stored = JSON.parse(sessionStorage.getItem(reviewCaseCommentAttemptStorageKey) ?? '{}') as {
      lifecycle: string;
      idempotencyKey: string;
      requestBody: string;
    };
    stored.lifecycle = 'sending';
    sessionStorage.setItem(reviewCaseCommentAttemptStorageKey, JSON.stringify(stored));
    server.use(http.post(`/api/review-cases/${caseId}/comments`, () => HttpResponse.json(
      { title: 'Unauthorized', code: 'reconciliation.actor.required' },
      { status: 401 },
    )));

    await expect(submitReviewCaseCommentAttempt()).rejects.toBeInstanceOf(ReviewCaseCommentUncertaintyRemainsError);

    const after = JSON.parse(sessionStorage.getItem(reviewCaseCommentAttemptStorageKey) ?? '{}') as {
      lifecycle: string;
      uncertaintyLocked: boolean;
      idempotencyKey: string;
      requestBody: string;
    };
    expect(after.lifecycle).toBe('uncertain');
    expect(after.uncertaintyLocked).toBe(true);
    expect(after.idempotencyKey).toBe(stored.idempotencyKey);
    expect(after.requestBody).toBe(stored.requestBody);
  });

  it('rejects only the exact comment 400 codes and keeps both 409 outcomes', async () => {
    signIn();
    beginReviewCaseCommentAttempt(caseId, 'kept');
    const first = stored();
    server.use(http.post(`/api/review-cases/${caseId}/comments`, () => HttpResponse.json(
      { title: 'Bad', code: 'reconciliation.review_case_comment.invalid_request' },
      { status: 400 },
    )));
    await expect(submitReviewCaseCommentAttempt()).rejects.toBeInstanceOf(ReviewCaseCommentRejectedError);
    expect(stored().lifecycle).toBe('rejected');
    expect(stored().idempotencyKey).toBe(first.idempotencyKey);

    sessionStorage.clear();
    beginReviewCaseCommentAttempt(caseId, 'kept');
    const second = stored();
    server.use(http.post(`/api/review-cases/${caseId}/comments`, () => HttpResponse.json(
      { title: 'Bad', code: 'reconciliation.review_case_comment.invalid_request_extra' },
      { status: 400 },
    )));
    await expect(submitReviewCaseCommentAttempt()).rejects.toBeInstanceOf(UnknownReviewCaseCommentOutcomeError);
    expect(stored().lifecycle).toBe('uncertain');
    expect(stored().idempotencyKey).toBe(second.idempotencyKey);
    expect(stored().requestBody).toBe(second.requestBody);

    sessionStorage.clear();
    beginReviewCaseCommentAttempt(caseId, 'kept');
    const third = stored();
    server.use(http.post(`/api/review-cases/${caseId}/comments`, () => HttpResponse.json(
      { title: 'Conflict', code: 'reconciliation.review_case_comment.idempotency_payload_conflict' },
      { status: 409 },
    )));
    await expect(submitReviewCaseCommentAttempt()).rejects.toBeInstanceOf(ReviewCaseCommentPayloadConflictError);
    expect(stored().lifecycle).toBe('sending');
    expect(stored().uncertaintyLocked).toBe(false);
    expect(stored().idempotencyKey).toBe(third.idempotencyKey);

    const locked = stored();
    locked.lifecycle = 'uncertain';
    locked.uncertaintyLocked = true;
    sessionStorage.setItem(reviewCaseCommentAttemptStorageKey, JSON.stringify(locked));
    server.use(http.post(`/api/review-cases/${caseId}/comments`, () => HttpResponse.json(
      { title: 'Conflict', code: 'reconciliation.review_case_comment.concurrency_conflict' },
      { status: 409 },
    )));
    await expect(submitReviewCaseCommentAttempt()).rejects.toBeInstanceOf(UnknownReviewCaseCommentOutcomeError);
    expect(stored().lifecycle).toBe('uncertain');
    expect(stored().uncertaintyLocked).toBe(true);
    expect(stored().idempotencyKey).toBe(third.idempotencyKey);
    expect(stored().requestBody).toBe(third.requestBody);
  });

  it('keeps the original key when a success timestamp is outside the UTC range', async () => {
    signIn();
    for (const status of [201, 200]) {
      for (const recordedAt of ['0001-01-01T00:00:00+14:00', '9999-12-31T23:59:59-14:00']) {
        sessionStorage.clear();
        beginReviewCaseCommentAttempt(caseId, 'kept');
        const before = stored();
        server.use(http.post(`/api/review-cases/${caseId}/comments`, () => HttpResponse.json(
          commentBody({ recordedAt }),
          { status },
        )));

        await expect(submitReviewCaseCommentAttempt()).rejects.toBeInstanceOf(UnreadableReviewCaseCommentError);

        expect(stored().idempotencyKey).toBe(before.idempotencyKey);
        expect(stored().requestBody).toBe(before.requestBody);
        expect(stored().lifecycle).toBe('uncertain');
        expect(stored().uncertaintyLocked).toBe(true);
      }
    }
  });

  it('keeps the original key when success cannot be read and leaves the other slots alone', async () => {
    signIn();
    sessionStorage.setItem(reviewCaseOpenAttemptStorageKey, 'open-slot');
    sessionStorage.setItem(reviewCaseDecisionAttemptStorageKey, 'decision-slot');
    beginReviewCaseCommentAttempt(caseId, 'kept');
    const before = stored();
    server.use(http.post(`/api/review-cases/${caseId}/comments`, () => HttpResponse.json(
      commentBody({ text: 'other' }),
      { status: 201 },
    )));

    await expect(submitReviewCaseCommentAttempt()).rejects.toBeInstanceOf(UnreadableReviewCaseCommentError);

    expect(stored().idempotencyKey).toBe(before.idempotencyKey);
    expect(stored().requestBody).toBe(before.requestBody);
    expect(stored().lifecycle).toBe('uncertain');
    expect(sessionStorage.getItem(reviewCaseOpenAttemptStorageKey)).toBe('open-slot');
    expect(sessionStorage.getItem(reviewCaseDecisionAttemptStorageKey)).toBe('decision-slot');
  });

  it('hides another organization and rejects a cookie before fetch', async () => {
    signIn();
    beginReviewCaseCommentAttempt(caseId, 'kept');
    const raw = sessionStorage.getItem(reviewCaseCommentAttemptStorageKey);
    sessionStore.rotate(token(otherOrganizationId), 4_000_000_000, null);
    expect(inspectReviewCaseCommentAttempt().status).toBe('blocked-tenant');
    await expect(submitReviewCaseCommentAttempt()).rejects.toBeInstanceOf(ReviewCaseCommentTenantMismatchError);
    expect(sessionStorage.getItem(reviewCaseCommentAttemptStorageKey)).toBe(raw);

    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    sessionStore.replaceCookieSession(cookieSession(), true);
    let posts = 0;
    server.use(http.post(`/api/review-cases/${caseId}/comments`, () => {
      posts += 1;
      return HttpResponse.json(commentBody());
    }));
    await expect(submitReviewCaseCommentAttempt()).rejects.toMatchObject({ status: 403 });
    expect(posts).toBe(0);
    expect(sessionStorage.getItem(reviewCaseCommentAttemptStorageKey)).toBe(raw);
  });

  it('reports cleanup failure without turning a confirmed comment into a failure', async () => {
    signIn();
    beginReviewCaseCommentAttempt(caseId, 'kept');
    vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => {
      throw new Error('cleanup failed');
    });
    server.use(http.post(`/api/review-cases/${caseId}/comments`, () => HttpResponse.json(commentBody(), { status: 201 })));

    const result = await submitReviewCaseCommentAttempt();

    expect(result.cleanup).toBe('failed');
    expect(result.id).toBe(commentId);
    expect(result.actorId).toBe('original-actor');
    expect(sessionStorage.getItem(reviewCaseCommentAttemptStorageKey)).toContain('kept');
  });
});

function signIn(): void {
  sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
}

function token(org: string): string {
  const exp = Math.floor(Date.now() / 1000) + 3600;
  return `${encode({ alg: 'HS256', typ: 'JWT' })}.${encode({ exp, sub: 'operator-1', org_id: org })}.signature`;
}

function encode(value: object): string {
  return btoa(JSON.stringify(value)).replace(/=+$/g, '').replace(/\+/g, '-').replace(/\//g, '_');
}

function stored(): {
  lifecycle: string;
  uncertaintyLocked: boolean;
  idempotencyKey: string;
  requestBody: string;
} {
  return JSON.parse(sessionStorage.getItem(reviewCaseCommentAttemptStorageKey) ?? '{}') as {
    lifecycle: string;
    uncertaintyLocked: boolean;
    idempotencyKey: string;
    requestBody: string;
  };
}

function commentBody(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: commentId,
    text: 'kept',
    actorId: 'original-actor',
    recordedAt: '2026-11-06T12:00:00Z',
    ...overrides,
  };
}

function cookieSession(): CookieSessionFields {
  return {
    authenticated: true,
    csrfToken: 'csrf-session',
    issuer: 'https://issuer.example',
    subject: 'oidc-operator',
    displayName: 'Ada Operator',
    expiresAt: null,
    organizations: [],
    activeOrganization: {
      organizationId,
      displayName: 'North Ledger',
    },
    authorization: 'verified',
    capabilities: ['review-cases.read', 'audit.read'],
  };
}
