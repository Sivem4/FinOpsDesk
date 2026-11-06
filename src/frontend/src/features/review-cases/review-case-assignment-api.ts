import { isLosslessNumber } from 'lossless-json';

import { UnexpectedResponseError } from '../../shared/api/errors';
import { apiGet } from '../../shared/api/http';
import { isReviewCaseTimestamp } from './review-case-comment-api';
import { isExactGuid, trimDotNetWhiteSpace } from './review-case-open-request';
import { readReviewCaseConcurrencyToken } from './review-case-api';

const maximumActorLength = 256;

export type ReviewCaseActorSourceName = 'developmentJwt' | 'productionCookie';

export interface ReviewCaseAssignee {
  actorSource: ReviewCaseActorSourceName;
  actorId: string;
  assignedAt: string;
}

export interface ReviewCaseAssignmentState {
  reviewCaseId: string;
  assignee: ReviewCaseAssignee | null;
  concurrencyToken: string;
}

export interface ReviewCaseAssignmentRead {
  state: ReviewCaseAssignmentState;
  epoch: number;
  readSequence: number;
}

export interface VerifiedReviewCaseAssignmentBody {
  reviewCaseId: string;
  operation: 'claim' | 'release';
  assignee: ReviewCaseAssignee | null;
  recordedAt: string;
}

let startedAssignmentReads = 0;
const latestAssignmentRead = new Map<string, number>();

export function startedAssignmentReadCount(): number {
  return startedAssignmentReads;
}

export function resetReviewCaseAssignmentReadsForTests(): void {
  startedAssignmentReads = 0;
  latestAssignmentRead.clear();
}

export function readReviewCaseAssignee(value: unknown): ReviewCaseAssignee {
  if (!isRecord(value)) {
    throw new UnexpectedResponseError('The review case assignment could not be read.');
  }

  const actorSource = readActorSource(value.actorSource);
  const actorId = readActorId(value.actorId);
  if (!isReviewCaseTimestamp(value.assignedAt)) {
    throw new UnexpectedResponseError('The review case assignment could not be read.');
  }

  return { actorSource, actorId, assignedAt: value.assignedAt };
}

export function readReviewCaseAssignmentState(value: unknown, expectedCaseId: string): ReviewCaseAssignmentState {
  if (!isRecord(value) || !isExactGuid(expectedCaseId)) {
    throw new UnexpectedResponseError('The review case assignment could not be read.');
  }

  const reviewCaseId = readGuid(value.reviewCaseId);
  if (reviewCaseId !== expectedCaseId.toLowerCase()) {
    throw new UnexpectedResponseError('The review case assignment could not be read.');
  }

  const concurrencyToken = readReviewCaseConcurrencyToken(value.concurrencyToken);
  if (concurrencyToken === null || !Object.hasOwn(value, 'assignee')) {
    throw new UnexpectedResponseError('The review case assignment could not be read.');
  }

  return {
    reviewCaseId,
    assignee: value.assignee === null ? null : readReviewCaseAssignee(value.assignee),
    concurrencyToken,
  };
}

export function readVerifiedReviewCaseAssignment(
  value: unknown,
  expected: {
    reviewCaseId: string;
    operation: 'claim' | 'release';
    actorSource: 'developmentJwt';
    actorId: string;
  },
): VerifiedReviewCaseAssignmentBody {
  if (!isRecord(value) || !isExactGuid(expected.reviewCaseId)) {
    throw new UnexpectedResponseError('The review case assignment could not be read.');
  }

  const reviewCaseId = readGuid(value.reviewCaseId);
  if (reviewCaseId !== expected.reviewCaseId.toLowerCase() || value.operation !== expected.operation) {
    throw new UnexpectedResponseError('The review case assignment could not be read.');
  }

  if (!isReviewCaseTimestamp(value.recordedAt) || !Object.hasOwn(value, 'assignee')) {
    throw new UnexpectedResponseError('The review case assignment could not be read.');
  }

  if (expected.operation === 'release') {
    if (value.assignee !== null) {
      throw new UnexpectedResponseError('The review case assignment could not be read.');
    }

    return { reviewCaseId, operation: 'release', assignee: null, recordedAt: value.recordedAt };
  }

  const assignee = readReviewCaseAssignee(value.assignee);
  if (assignee.actorSource !== expected.actorSource || assignee.actorId !== expected.actorId) {
    throw new UnexpectedResponseError('The review case assignment could not be read.');
  }

  return { reviewCaseId, operation: 'claim', assignee, recordedAt: value.recordedAt };
}

export async function getReviewCaseAssignment(
  reviewCaseId: string,
  epoch: number,
  signal: AbortSignal,
): Promise<ReviewCaseAssignmentRead> {
  const readSequence = assignAssignmentReadSequence();
  const identity = assignmentReadIdentity(epoch, reviewCaseId);
  if (identity !== null) {
    latestAssignmentRead.set(identity, readSequence);
  }

  if (!isExactGuid(reviewCaseId) || !Number.isSafeInteger(epoch) || epoch < 0) {
    throw new UnexpectedResponseError('The review case assignment could not be read.');
  }

  const body = await apiGet(`/api/review-cases/${reviewCaseId}/assignment`, signal);
  assertCurrentAssignmentRead(identity, readSequence);
  const state = readReviewCaseAssignmentState(body, reviewCaseId);
  assertCurrentAssignmentRead(identity, readSequence);
  return {
    state,
    epoch,
    readSequence,
  };
}

function assignmentReadIdentity(epoch: number, reviewCaseId: string): string | null {
  if (!isExactGuid(reviewCaseId) || !Number.isSafeInteger(epoch) || epoch < 0) {
    return null;
  }

  return `${epoch}:${reviewCaseId.toLowerCase()}`;
}

function assertCurrentAssignmentRead(identity: string | null, readSequence: number): void {
  if (identity !== null && latestAssignmentRead.get(identity) !== readSequence) {
    throw new DOMException('The review case assignment read was superseded.', 'AbortError');
  }
}

function assignAssignmentReadSequence(): number {
  startedAssignmentReads += 1;
  return startedAssignmentReads;
}

function readActorSource(value: unknown): ReviewCaseActorSourceName {
  if (value !== 'developmentJwt' && value !== 'productionCookie') {
    throw new UnexpectedResponseError('The review case assignment could not be read.');
  }

  return value;
}

function readActorId(value: unknown): string {
  if (typeof value !== 'string' || value.length < 1 || value.length > maximumActorLength || trimDotNetWhiteSpace(value) !== value) {
    throw new UnexpectedResponseError('The review case assignment could not be read.');
  }

  return value;
}

function readGuid(value: unknown): string {
  if (typeof value !== 'string' || !isExactGuid(value)) {
    throw new UnexpectedResponseError('The review case assignment could not be read.');
  }

  return value.toLowerCase();
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value) && !isLosslessNumber(value);
}
