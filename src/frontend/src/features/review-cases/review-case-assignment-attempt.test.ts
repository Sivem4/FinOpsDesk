import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { sessionStore } from '../../shared/api/session-store';
import { server } from '../../test/server';
import { fakeJwt } from '../../test/jwt';
import {
  abandonReviewCaseAssignmentAttempt,
  assignmentReadFloorForTests,
  beginReviewCaseAssignmentAttempt,
  inspectReviewCaseAssignmentAttempt,
  resetReviewCaseAssignmentAttemptStateForTests,
  reviewCaseAssignmentAttemptStorageKey,
  ReviewCaseAssignmentActorMismatchError,
  ReviewCaseAssignmentActorRequiredError,
  UnresolvedReviewCaseAssignmentError,
  ReviewCaseAssignmentCommandNotSentError,
  ReviewCaseAssignmentConcurrencyUncheckedError,
  ReviewCaseAssignmentRejectedError,
  ReviewCaseAssignmentRetryNotSentError,
  ReviewCaseAssignmentSubmissionInProgressError,
  submitReviewCaseAssignmentAttempt,
  subscribeReviewCaseAssignmentVerified,
  UnknownReviewCaseAssignmentOutcomeError,
  UnreadableReviewCaseAssignmentError,
  type ReviewCaseAssignmentAttempt,
  type ReviewCaseAssignmentClick,
} from './review-case-assignment-attempt';
import { reviewCaseCommentAttemptStorageKey } from './review-case-comment-attempt';
import { reviewCaseDecisionAttemptStorageKey } from './review-case-decision-attempt';
import { reviewCaseOpenAttemptStorageKey } from './review-case-open-attempt';

const caseId = '11111111-1111-4111-8111-111111111111';
const token = 'AAAAAAAAAAA=';
const assignedAt = '2026-11-06T12:00:00Z';
const recordedAt = '2026-11-06T12:00:01Z';

afterEach(() => {
  vi.restoreAllMocks();
  sessionStorage.clear();
  resetReviewCaseAssignmentAttemptStateForTests();
  sessionStore.rotate(null, null, null);
});

describe('review case assignment attempt', () => {
  it('keeps the key when 200 and 201 omit or spoil assignedAt or the release assignee', async () => {
    signIn();
    const seen: unknown[] = [];
    subscribeReviewCaseAssignmentVerified((result) => seen.push(result));
    const bodies = [
      claimBody({ assignee: { actorSource: 'developmentJwt', actorId: 'operator-1' } }),
      claimBody({ assignee: { actorSource: 'developmentJwt', actorId: 'operator-1', assignedAt: '2026-11-06T12:00:00+14:01' } }),
      { reviewCaseId: caseId, operation: 'release', recordedAt },
      claimBody({ operation: 'release', assignee: { actorSource: 'developmentJwt', actorId: 'operator-1', assignedAt } }),
    ];
    for (const status of [200, 201]) {
      for (const body of bodies) {
        sessionStorage.clear();
        beginReviewCaseAssignmentAttempt('claim', click());
        const before = stored();
        server.use(http.post(`/api/review-cases/${caseId}/assignment`, () => HttpResponse.json(body, { status })));
        await expect(submitReviewCaseAssignmentAttempt()).rejects.toBeInstanceOf(UnreadableReviewCaseAssignmentError);
        expect(stored().idempotencyKey).toBe(before.idempotencyKey);
        expect(stored().requestBody).toBe(before.requestBody);
        expect(stored().lifecycle).toBe('uncertain');
        expect(stored().uncertaintyLocked).toBe(true);
      }
    }

    expect(seen).toEqual([]);
    expect(sessionStorage.getItem(reviewCaseOpenAttemptStorageKey)).toBeNull();
  });

  it('rejects the first payload conflict and keeps the lock after an uncertain result', async () => {
    signIn();
    beginReviewCaseAssignmentAttempt('claim', click());
    const first = stored();
    server.use(conflict(payload));
    await expect(submitReviewCaseAssignmentAttempt()).rejects.toBeInstanceOf(ReviewCaseAssignmentRejectedError);
    expect(stored().lifecycle).toBe('rejected');
    expect(stored().uncertaintyLocked).toBe(false);
    expect(stored().idempotencyKey).toBe(first.idempotencyKey);
    expect(stored().requestBody).toBe(first.requestBody);
    await expect(submitReviewCaseAssignmentAttempt()).rejects.toBeInstanceOf(ReviewCaseAssignmentRejectedError);

    const locked = stored();
    locked.lifecycle = 'uncertain';
    locked.uncertaintyLocked = true;
    sessionStorage.setItem(reviewCaseAssignmentAttemptStorageKey, JSON.stringify(locked));
    await expect(submitReviewCaseAssignmentAttempt()).rejects.toBeInstanceOf(ReviewCaseAssignmentRejectedError);
    expect(stored().lifecycle).toBe('rejected');
    expect(stored().uncertaintyLocked).toBe(true);
    expect(stored().idempotencyKey).toBe(first.idempotencyKey);
    expect(stored().requestBody).toBe(first.requestBody);
  });

  it('splits concurrency before and after a lost response', async () => {
    signIn();
    beginReviewCaseAssignmentAttempt('claim', click());
    const first = stored();
    server.use(conflict(concurrency));
    const error = await submitReviewCaseAssignmentAttempt().catch((caught: unknown) => caught);
    expect(error).toBeInstanceOf(ReviewCaseAssignmentRejectedError);
    expect((error as ReviewCaseAssignmentRejectedError).code).toBe(concurrency);
    expect(stored().lifecycle).toBe('rejected');
    expect(stored().uncertaintyLocked).toBe(false);

    const locked = stored();
    locked.lifecycle = 'uncertain';
    locked.uncertaintyLocked = true;
    sessionStorage.setItem(reviewCaseAssignmentAttemptStorageKey, JSON.stringify(locked));
    await expect(submitReviewCaseAssignmentAttempt()).rejects.toBeInstanceOf(ReviewCaseAssignmentConcurrencyUncheckedError);
    expect(stored().lifecycle).toBe('uncertain');
    expect(stored().uncertaintyLocked).toBe(true);
    expect(stored().idempotencyKey).toBe(first.idempotencyKey);
    expect(stored().requestBody).toBe(first.requestBody);
  });

  it.each([
    'reconciliation.review_case_assignment.idempotency_payload_conflict',
    'reconciliation.review_case_assignment.concurrency_conflict',
  ])('keeps an explicit replay when HTTP 500 carries %s', async (code) => {
    signIn();
    beginReviewCaseAssignmentAttempt('claim', click());
    const before = stored();
    server.use(http.post(`/api/review-cases/${caseId}/assignment`, () => HttpResponse.json(
      { title: 'Error', code },
      { status: 500 },
    )));
    await expect(submitReviewCaseAssignmentAttempt()).rejects.toBeInstanceOf(UnknownReviewCaseAssignmentOutcomeError);
    expect(stored().lifecycle).toBe('uncertain');
    expect(stored().uncertaintyLocked).toBe(true);
    expect(stored().idempotencyKey).toBe(before.idempotencyKey);
    expect(stored().requestBody).toBe(before.requestBody);

    let replayKey = '';
    let replayBody = '';
    server.use(http.post(`/api/review-cases/${caseId}/assignment`, async ({ request }) => {
      replayKey = request.headers.get('Idempotency-Key') ?? '';
      replayBody = await request.text();
      return HttpResponse.json(claimBody(), { status: 201 });
    }));
    await expect(submitReviewCaseAssignmentAttempt()).resolves.toMatchObject({ operation: 'claim', created: true });
    expect(replayKey).toBe(before.idempotencyKey);
    expect(replayBody).toBe(before.requestBody);
  });

  it('sends one assignment POST for two overlapping submits', async () => {
    signIn();
    let posts = 0;
    let release: (value: Response) => void = () => undefined;
    const gate = new Promise<Response>((resolve) => {
      release = resolve;
    });
    server.use(http.post(`/api/review-cases/${caseId}/assignment`, async () => {
      posts += 1;
      return gate;
    }));
    beginReviewCaseAssignmentAttempt('claim', click());
    const first = submitReviewCaseAssignmentAttempt();
    const second = submitReviewCaseAssignmentAttempt();
    await expect(second).rejects.toBeInstanceOf(ReviewCaseAssignmentSubmissionInProgressError);
    await vi.waitFor(() => expect(posts).toBe(1));
    release(HttpResponse.json(claimBody(), { status: 201 }));
    await expect(first).resolves.toMatchObject({ operation: 'claim', created: true });
    expect(posts).toBe(1);
  });

  it('sends nothing when the descriptor cannot be saved and keeps the earlier record', async () => {
    signIn();
    let posts = 0;
    server.use(http.post(`/api/review-cases/${caseId}/assignment`, () => {
      posts += 1;
      return HttpResponse.json(claimBody(), { status: 201 });
    }));
    beginReviewCaseAssignmentAttempt('claim', click());
    const ready = sessionStorage.getItem(reviewCaseAssignmentAttemptStorageKey);
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('storage failed');
    });
    await expect(submitReviewCaseAssignmentAttempt()).rejects.toBeInstanceOf(ReviewCaseAssignmentCommandNotSentError);
    expect(posts).toBe(0);
    expect(sessionStorage.getItem(reviewCaseAssignmentAttemptStorageKey)).toBe(ready);
    expect(stored().lifecycle).toBe('ready');
    expect(stored().uncertaintyLocked).toBe(false);

    vi.restoreAllMocks();
    const uncertain = stored();
    uncertain.lifecycle = 'uncertain';
    uncertain.uncertaintyLocked = true;
    sessionStorage.setItem(reviewCaseAssignmentAttemptStorageKey, JSON.stringify(uncertain));
    const replay = sessionStorage.getItem(reviewCaseAssignmentAttemptStorageKey);
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('storage failed');
    });
    await expect(submitReviewCaseAssignmentAttempt()).rejects.toBeInstanceOf(ReviewCaseAssignmentRetryNotSentError);
    expect(posts).toBe(0);
    expect(sessionStorage.getItem(reviewCaseAssignmentAttemptStorageKey)).toBe(replay);
    expect(stored().uncertaintyLocked).toBe(true);
    expect(stored().idempotencyKey).toBe(uncertain.idempotencyKey);
    expect(stored().requestBody).toBe(uncertain.requestBody);
  });

  it('lets operator B read nothing about the command and preserves operator A key', async () => {
    signIn('operator-1');
    sessionStorage.setItem(reviewCaseOpenAttemptStorageKey, 'open');
    sessionStorage.setItem(reviewCaseDecisionAttemptStorageKey, 'decision');
    sessionStorage.setItem(reviewCaseCommentAttemptStorageKey, 'comment');
    beginReviewCaseAssignmentAttempt('claim', click());
    const original = stored();
    let posts = 0;
    let body = '';
    let key = '';
    server.use(http.post(`/api/review-cases/${caseId}/assignment`, async ({ request }) => {
      posts += 1;
      body = await request.text();
      key = request.headers.get('Idempotency-Key') ?? '';
      return HttpResponse.json(claimBody(), { status: 201 });
    }));

    signIn('operator-2');
    expect(inspectReviewCaseAssignmentAttempt().status).toBe('blocked-other-actor');
    expect(() => beginReviewCaseAssignmentAttempt('claim', click())).toThrow(UnresolvedReviewCaseAssignmentError);
    await expect(submitReviewCaseAssignmentAttempt()).rejects.toBeInstanceOf(ReviewCaseAssignmentActorMismatchError);
    expect(() => abandonReviewCaseAssignmentAttempt(original.idempotencyKey, original.requestBody))
      .toThrow(ReviewCaseAssignmentActorMismatchError);
    expect(posts).toBe(0);
    expect(stored().idempotencyKey).toBe(original.idempotencyKey);
    expect(stored().requestBody).toBe(original.requestBody);

    signIn('operator-1');
    await submitReviewCaseAssignmentAttempt();
    expect(posts).toBe(1);
    expect(key).toBe(original.idempotencyKey);
    expect(body).toBe(original.requestBody);
    expect(sessionStorage.getItem(reviewCaseOpenAttemptStorageKey)).toBe('open');
    expect(sessionStorage.getItem(reviewCaseDecisionAttemptStorageKey)).toBe('decision');
    expect(sessionStorage.getItem(reviewCaseCommentAttemptStorageKey)).toBe('comment');
  });

  it('blocks replay without a subject and raises the fresh-read floor on abandon', async () => {
    signIn('operator-1');
    beginReviewCaseAssignmentAttempt('claim', click());
    const original = stored();
    sessionStore.rotate(jwt(null), 4_000_000_000, null);
    expect(inspectReviewCaseAssignmentAttempt().status).toBe('blocked-actor');
    await expect(submitReviewCaseAssignmentAttempt()).rejects.toBeInstanceOf(ReviewCaseAssignmentActorRequiredError);
    expect(stored().idempotencyKey).toBe(original.idempotencyKey);

    signIn('operator-1');
    abandonReviewCaseAssignmentAttempt(original.idempotencyKey, original.requestBody);
    expect(sessionStorage.getItem(reviewCaseAssignmentAttemptStorageKey)).toBeNull();
    expect(assignmentReadFloorForTests()).toEqual({ detailSequence: 0, assignmentSequence: 0 });
  });
});

function signIn(subject = 'operator-1'): void {
  sessionStore.rotate(subject === 'operator-1' ? fakeJwt() : jwt(subject), 4_000_000_000, null);
}

function jwt(subject: string | null): string {
  const exp = Math.floor(Date.now() / 1000) + 3600;
  const payload: { exp: number; org_id: string; sub?: string } = {
    exp,
    org_id: '11111111-1111-1111-1111-111111111111',
  };
  if (subject !== null) {
    payload.sub = subject;
  }

  return `${encode({ alg: 'HS256', typ: 'JWT' })}.${encode(payload)}.signature`;
}

function encode(value: object): string {
  return btoa(JSON.stringify(value)).replace(/=+$/g, '').replace(/\+/g, '-').replace(/\//g, '_');
}

function click(): ReviewCaseAssignmentClick {
  const epoch = sessionStore.getSnapshot().epoch;
  return {
    caseId,
    epoch,
    status: 'Open',
    detailToken: token,
    detailSequence: 1,
    assignmentCaseId: caseId,
    assignmentEpoch: epoch,
    assignmentToken: token,
    assignmentSequence: 1,
    assignee: null,
  };
}

function claimBody(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    reviewCaseId: caseId,
    operation: 'claim',
    assignee: { actorSource: 'developmentJwt', actorId: 'operator-1', assignedAt },
    recordedAt,
    ...overrides,
  };
}

function stored(): ReviewCaseAssignmentAttempt {
  return JSON.parse(sessionStorage.getItem(reviewCaseAssignmentAttemptStorageKey) ?? '{}') as ReviewCaseAssignmentAttempt;
}

function conflict(code: string) {
  return http.post(`/api/review-cases/${caseId}/assignment`, () => HttpResponse.json(
    { title: 'Conflict', code },
    { status: 409 },
  ));
}

const payload = 'reconciliation.review_case_assignment.idempotency_payload_conflict';
const concurrency = 'reconciliation.review_case_assignment.concurrency_conflict';
