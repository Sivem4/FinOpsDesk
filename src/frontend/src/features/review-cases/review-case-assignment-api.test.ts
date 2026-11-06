import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';

import { UnexpectedResponseError } from '../../shared/api/errors';
import { sessionStore } from '../../shared/api/session-store';
import { server } from '../../test/server';
import { fakeJwt } from '../../test/jwt';
import { isReviewCaseTimestamp } from './review-case-comment-api';
import {
  getReviewCaseAssignment,
  readReviewCaseAssignmentState,
  readVerifiedReviewCaseAssignment,
  startedAssignmentReadCount,
} from './review-case-assignment-api';

const caseId = '11111111-1111-4111-8111-111111111111';
const token = 'AAAAAAAAAAA=';

describe('review case assignment parser', () => {
  it('accepts a null assignee and rejects another case', () => {
    expect(readReviewCaseAssignmentState({
      reviewCaseId: caseId,
      assignee: null,
      concurrencyToken: token,
    }, caseId).assignee).toBeNull();

    expect(() => readReviewCaseAssignmentState({
      reviewCaseId: '22222222-2222-4222-8222-222222222222',
      assignee: null,
      concurrencyToken: token,
    }, caseId)).toThrow(UnexpectedResponseError);
  });

  it('uses the comment timestamp rules for assignedAt and recordedAt', () => {
    expect(isReviewCaseTimestamp('2026-11-06T12:00:00+14:00')).toBe(true);
    expect(isReviewCaseTimestamp('2026-11-06T12:00:00+14:01')).toBe(false);
    const assignee = {
      actorSource: 'developmentJwt',
      actorId: 'operator-1',
      assignedAt: '2026-11-06T12:00:00Z',
    };
    expect(readVerifiedReviewCaseAssignment({
      reviewCaseId: caseId,
      operation: 'claim',
      assignee,
      recordedAt: '2026-11-06T12:00:01Z',
    }, {
      reviewCaseId: caseId,
      operation: 'claim',
      actorSource: 'developmentJwt',
      actorId: 'operator-1',
    }).assignee).toEqual(assignee);

    expect(() => readVerifiedReviewCaseAssignment({
      reviewCaseId: caseId,
      operation: 'claim',
      assignee: { ...assignee, assignedAt: '2026-11-06T12:00:00+14:01' },
      recordedAt: '2026-11-06T12:00:01Z',
    }, {
      reviewCaseId: caseId,
      operation: 'claim',
      actorSource: 'developmentJwt',
      actorId: 'operator-1',
    })).toThrow(UnexpectedResponseError);

    expect(() => readVerifiedReviewCaseAssignment({
      reviewCaseId: caseId,
      operation: 'claim',
      assignee: { actorSource: 'developmentJwt', actorId: 'operator-1' },
      recordedAt: '2026-11-06T12:00:01Z',
    }, {
      reviewCaseId: caseId,
      operation: 'claim',
      actorSource: 'developmentJwt',
      actorId: 'operator-1',
    })).toThrow(UnexpectedResponseError);
  });

  it('requires release to send assignee null and counts a read before the request', async () => {
    expect(readVerifiedReviewCaseAssignment({
      reviewCaseId: caseId,
      operation: 'release',
      assignee: null,
      recordedAt: '2026-11-06T12:00:01Z',
    }, {
      reviewCaseId: caseId,
      operation: 'release',
      actorSource: 'developmentJwt',
      actorId: 'operator-1',
    }).assignee).toBeNull();

    expect(() => readVerifiedReviewCaseAssignment({
      reviewCaseId: caseId,
      operation: 'release',
      recordedAt: '2026-11-06T12:00:01Z',
    }, {
      reviewCaseId: caseId,
      operation: 'release',
      actorSource: 'developmentJwt',
      actorId: 'operator-1',
    })).toThrow(UnexpectedResponseError);

    let observed = 0;
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    server.use(http.get('/api/review-cases/:id/assignment', () => {
      observed = startedAssignmentReadCount();
      return HttpResponse.json({ reviewCaseId: caseId, assignee: null, concurrencyToken: token });
    }));
    const read = await getReviewCaseAssignment(caseId, 4, new AbortController().signal);
    expect(observed).toBe(1);
    expect(read.readSequence).toBe(1);
    expect(read.epoch).toBe(4);
  });
});
