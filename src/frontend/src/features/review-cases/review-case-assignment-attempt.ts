import { parse, isLosslessNumber } from 'lossless-json';
import { useSyncExternalStore } from 'react';

import { authenticationHeaders, rejectCookieFinancialRequest } from '../../shared/api/authentication';
import { ApiError, isAbortError, StaleSessionError, UnexpectedResponseError } from '../../shared/api/errors';
import { toApiError } from '../../shared/api/problem';
import { sessionStore, type SessionSnapshot } from '../../shared/api/session-store';
import { useSession } from '../../shared/auth/session';
import {
  readVerifiedReviewCaseAssignment,
  startedAssignmentReadCount,
  type ReviewCaseAssignee,
} from './review-case-assignment-api';
import { serializeReviewCaseAssignmentBody } from './review-case-assignment-request';
import { startedReviewCaseDetailReadCount, readReviewCaseConcurrencyToken } from './review-case-api';
import {
  developmentActorId,
  developmentOrganizationId,
  isExactGuid,
  trimDotNetWhiteSpace,
} from './review-case-open-request';

export const reviewCaseAssignmentAttemptStorageKey = 'finopsdesk.review-case-assignment-attempt';

const schemaVersion = 1;
const actorSource = 'developmentJwt';
const controlledBadRequestCodes = [
  'reconciliation.review_case_assignment.invalid_request',
  'reconciliation.review_case_assignment.idempotency_key_required',
  'reconciliation.review_case_assignment.idempotency_key_invalid',
] as const;
const domainConflictCodes = [
  'reconciliation.review_case_assignment.terminal',
  'reconciliation.review_case_assignment.already_assigned',
  'reconciliation.review_case_assignment.not_assigned',
  'reconciliation.review_case_assignment.not_assignee',
] as const;
const payloadConflictCode = 'reconciliation.review_case_assignment.idempotency_payload_conflict';
const concurrencyConflictCode = 'reconciliation.review_case_assignment.concurrency_conflict';

let submissionInFlight = false;
let storageVersion = 0;
let readFloor: { detailSequence: number; assignmentSequence: number } | null = null;
const listeners = new Set<() => void>();
const verifiedListeners = new Set<(result: VerifiedReviewCaseAssignment) => void>();

export class ReviewCaseAssignmentStorageError extends Error {
  constructor() {
    super('The review case assignment command could not be stored.');
    this.name = 'ReviewCaseAssignmentStorageError';
  }
}

export class ReviewCaseAssignmentStorageCleanupError extends Error {
  constructor() {
    super('The verified assignment command could not be cleared from this browser tab.');
    this.name = 'ReviewCaseAssignmentStorageCleanupError';
  }
}

export class ReviewCaseAssignmentAttemptBlockedError extends Error {
  constructor() {
    super('The stored review case assignment command blocks a new command.');
    this.name = 'ReviewCaseAssignmentAttemptBlockedError';
  }
}

export class UnresolvedReviewCaseAssignmentError extends Error {
  constructor() {
    super('An unresolved review case assignment command is already stored.');
    this.name = 'UnresolvedReviewCaseAssignmentError';
  }
}

export class ReviewCaseAssignmentAuthenticationError extends Error {
  constructor() {
    super('Sign in before claiming or releasing a review case.');
    this.name = 'ReviewCaseAssignmentAuthenticationError';
  }
}

export class ReviewCaseAssignmentActorRequiredError extends Error {
  constructor() {
    super('This assignment command needs an operator identity.');
    this.name = 'ReviewCaseAssignmentActorRequiredError';
  }
}

export class ReviewCaseAssignmentActorMismatchError extends Error {
  constructor() {
    super('The stored assignment command belongs to another operator.');
    this.name = 'ReviewCaseAssignmentActorMismatchError';
  }
}

export class ReviewCaseAssignmentTenantMismatchError extends Error {
  constructor() {
    super('The stored assignment command belongs to another organization.');
    this.name = 'ReviewCaseAssignmentTenantMismatchError';
  }
}

export class ReviewCaseAssignmentNotReadyError extends Error {
  constructor() {
    super('Claim and release need a fresh matching read of this case.');
    this.name = 'ReviewCaseAssignmentNotReadyError';
  }
}

export class ReviewCaseAssignmentSubmissionInProgressError extends Error {
  constructor() {
    super('A review case assignment command is already being sent.');
    this.name = 'ReviewCaseAssignmentSubmissionInProgressError';
  }
}

export class ReviewCaseAssignmentAttemptChangedError extends Error {
  constructor() {
    super('The stored assignment command changed before the response could be applied.');
    this.name = 'ReviewCaseAssignmentAttemptChangedError';
  }
}

export class ReviewCaseAssignmentCommandNotSentError extends Error {
  constructor() {
    super('The assignment command was not sent.');
    this.name = 'ReviewCaseAssignmentCommandNotSentError';
  }
}

export class ReviewCaseAssignmentRetryNotSentError extends Error {
  constructor() {
    super('The assignment retry was not sent.');
    this.name = 'ReviewCaseAssignmentRetryNotSentError';
  }
}

export class UnknownReviewCaseAssignmentOutcomeError extends Error {
  constructor() {
    super('The assignment command was not verified.');
    this.name = 'UnknownReviewCaseAssignmentOutcomeError';
  }
}

export class UnreadableReviewCaseAssignmentError extends Error {
  constructor() {
    super('The assignment response could not be verified.');
    this.name = 'UnreadableReviewCaseAssignmentError';
  }
}

export class ReviewCaseAssignmentRejectedError extends Error {
  readonly status: number;
  readonly code: string | null;

  constructor(status: number, code: string | null) {
    super('The server refused this assignment command.');
    this.name = 'ReviewCaseAssignmentRejectedError';
    this.status = status;
    this.code = code;
  }
}

export class ReviewCaseAssignmentUncertaintyRemainsError extends Error {
  constructor() {
    super('A later refusal did not clear the unresolved assignment command.');
    this.name = 'ReviewCaseAssignmentUncertaintyRemainsError';
  }
}

export class ReviewCaseAssignmentConcurrencyUncheckedError extends Error {
  constructor() {
    super('The server did not find an entry for this key at the time of the check.');
    this.name = 'ReviewCaseAssignmentConcurrencyUncheckedError';
  }
}

export interface ReviewCaseAssignmentAttempt {
  schemaVersion: 1;
  operation: 'ReviewCaseClaim' | 'ReviewCaseRelease';
  organizationId: string;
  actorSource: 'developmentJwt';
  actorId: string;
  caseId: string;
  idempotencyKey: string;
  requestBody: string;
  lifecycle: 'ready' | 'sending' | 'uncertain' | 'rejected';
  uncertaintyLocked: boolean;
}

export interface ReviewCaseAssignmentClick {
  caseId: string;
  epoch: number;
  status: 'Open' | 'Resolved' | 'Dismissed';
  detailToken: string;
  detailSequence: number;
  assignmentCaseId: string;
  assignmentEpoch: number;
  assignmentToken: string;
  assignmentSequence: number;
  assignee: ReviewCaseAssignee | null;
}

export interface VerifiedReviewCaseAssignment {
  caseId: string;
  operation: 'claim' | 'release';
  assignee: ReviewCaseAssignee | null;
  recordedAt: string;
  created: boolean;
  epoch: number;
  cleanup: 'removed' | 'failed';
}

export type ReviewCaseAssignmentView =
  | { status: 'none' }
  | { status: 'blocked-authentication' }
  | { status: 'blocked-actor' }
  | { status: 'blocked-tenant' }
  | { status: 'blocked-other-actor' }
  | { status: 'invalid-storage' }
  | { status: 'pending'; attempt: ReviewCaseAssignmentAttempt };

export function useReviewCaseAssignmentAttempt(): ReviewCaseAssignmentView {
  const session = useSession();
  useSyncExternalStore(subscribe, getStorageVersion, getStorageVersion);
  return inspectReviewCaseAssignmentAttempt(session);
}

export function inspectReviewCaseAssignmentAttempt(
  session: SessionSnapshot = sessionStore.getSnapshot(),
): ReviewCaseAssignmentView {
  const stored = readStoredAttempt();
  if (stored.status === 'empty') {
    return { status: 'none' };
  }

  if (stored.status === 'invalid') {
    return { status: 'invalid-storage' };
  }

  const organizationId = developmentOrganizationId(session.token);
  if (session.token === null || organizationId === null) {
    return { status: 'blocked-authentication' };
  }

  if (organizationId !== stored.attempt.organizationId) {
    return { status: 'blocked-tenant' };
  }

  const actorId = developmentActorId(session.token);
  if (actorId === null) {
    return { status: 'blocked-actor' };
  }

  if (actorId !== stored.attempt.actorId) {
    return { status: 'blocked-other-actor' };
  }

  return { status: 'pending', attempt: stored.attempt };
}

export function reviewCaseAssignmentAction(
  session: SessionSnapshot,
  click: ReviewCaseAssignmentClick | null,
): 'claim' | 'release' | 'none' {
  if (click === null || readStoredAttempt().status !== 'empty') {
    return 'none';
  }

  const organizationId = developmentOrganizationId(session.token);
  const actorId = developmentActorId(session.token);
  if (session.token === null || organizationId === null || actorId === null) {
    return 'none';
  }

  if (session.epoch !== click.epoch || click.assignmentEpoch !== click.epoch || click.status !== 'Open') {
    return 'none';
  }

  const caseId = click.caseId.toLowerCase();
  if (!isExactGuid(caseId)
    || click.assignmentCaseId.toLowerCase() !== caseId
    || !Number.isSafeInteger(click.detailSequence)
    || !Number.isSafeInteger(click.assignmentSequence)) {
    return 'none';
  }

  const detailToken = readReviewCaseConcurrencyToken(click.detailToken);
  const assignmentToken = readReviewCaseConcurrencyToken(click.assignmentToken);
  if (detailToken === null || assignmentToken === null || detailToken !== assignmentToken) {
    return 'none';
  }

  if (readFloor !== null
    && (click.detailSequence <= readFloor.detailSequence || click.assignmentSequence <= readFloor.assignmentSequence)) {
    return 'none';
  }

  if (click.assignee === null) {
    return 'claim';
  }

  if (click.assignee.actorSource === actorSource && click.assignee.actorId === actorId) {
    return 'release';
  }

  return 'none';
}

export function beginReviewCaseAssignmentAttempt(
  operation: 'claim' | 'release',
  click: ReviewCaseAssignmentClick,
): ReviewCaseAssignmentAttempt {
  rejectDevelopmentCookie();
  if (submissionInFlight) {
    throw new ReviewCaseAssignmentSubmissionInProgressError();
  }

  const existing = readStoredAttempt();
  if (existing.status === 'invalid') {
    throw new ReviewCaseAssignmentAttemptBlockedError();
  }

  if (existing.status === 'present') {
    throw new UnresolvedReviewCaseAssignmentError();
  }

  const session = sessionStore.getSnapshot();
  requireOrganization(session);
  requireActor(session);
  if (reviewCaseAssignmentAction(session, click) !== operation) {
    throw new ReviewCaseAssignmentNotReadyError();
  }

  const idempotencyKey = crypto.randomUUID();
  if (!isExactGuid(idempotencyKey)) {
    throw new ReviewCaseAssignmentStorageError();
  }

  const attempt: ReviewCaseAssignmentAttempt = {
    schemaVersion,
    operation: operation === 'claim' ? 'ReviewCaseClaim' : 'ReviewCaseRelease',
    organizationId: developmentOrganizationId(session.token) ?? '',
    actorSource,
    actorId: developmentActorId(session.token) ?? '',
    caseId: click.caseId.toLowerCase(),
    idempotencyKey,
    requestBody: serializeReviewCaseAssignmentBody(click.assignmentToken),
    lifecycle: 'ready',
    uncertaintyLocked: false,
  };
  if (!storedBodyMatches(attempt)) {
    throw new ReviewCaseAssignmentStorageError();
  }

  persistAttempt(attempt);
  return readBack(attempt);
}

export async function submitReviewCaseAssignmentAttempt(signal?: AbortSignal): Promise<VerifiedReviewCaseAssignment> {
  rejectDevelopmentCookie();
  if (submissionInFlight) {
    throw new ReviewCaseAssignmentSubmissionInProgressError();
  }

  const session = sessionStore.getSnapshot();
  const organizationId = requireOrganization(session);
  const actorId = requireActor(session);
  const stored = readStoredAttempt();
  if (stored.status !== 'present') {
    throw new ReviewCaseAssignmentAttemptBlockedError();
  }

  if (organizationId !== stored.attempt.organizationId) {
    throw new ReviewCaseAssignmentTenantMismatchError();
  }

  if (actorId !== stored.attempt.actorId) {
    throw new ReviewCaseAssignmentActorMismatchError();
  }

  if (stored.attempt.lifecycle === 'rejected') {
    throw new ReviewCaseAssignmentRejectedError(0, null);
  }

  submissionInFlight = true;
  const epoch = session.epoch;
  const replaying = stored.attempt.lifecycle === 'sending' || stored.attempt.lifecycle === 'uncertain';
  try {
    let prepared: ReviewCaseAssignmentAttempt;
    try {
      prepared = markDispatch(stored.attempt);
    } catch (error) {
      if (error instanceof ReviewCaseAssignmentStorageError) {
        throw replaying
          ? new ReviewCaseAssignmentRetryNotSentError()
          : new ReviewCaseAssignmentCommandNotSentError();
      }

      throw error;
    }

    const path = prepared.operation === 'ReviewCaseClaim'
      ? `/api/review-cases/${prepared.caseId}/assignment`
      : `/api/review-cases/${prepared.caseId}/assignment/release`;
    let response: Response;
    try {
      response = await fetch(path, {
        method: 'POST',
        credentials: 'same-origin',
        headers: {
          Accept: 'application/json',
          'Content-Type': 'application/json',
          ...authenticationHeaders(session, 'POST'),
          'Idempotency-Key': prepared.idempotencyKey,
        },
        body: prepared.requestBody,
        signal: signal === undefined ? session.signal : AbortSignal.any([signal, session.signal]),
      });
    } catch (error) {
      rememberUnknown(prepared, epoch);
      if (isAbortError(error) || error instanceof TypeError) {
        throw new UnknownReviewCaseAssignmentOutcomeError();
      }

      throw error;
    }

    const verified = await classifyResponse(response, prepared, epoch);
    emitVerified(verified);
    return verified;
  } finally {
    submissionInFlight = false;
  }
}

export function abandonReviewCaseAssignmentAttempt(idempotencyKey: string, requestBody: string): void {
  rejectDevelopmentCookie();
  if (submissionInFlight) {
    throw new ReviewCaseAssignmentSubmissionInProgressError();
  }

  const stored = readStoredAttempt();
  if (stored.status !== 'present'
    || stored.attempt.idempotencyKey !== idempotencyKey
    || stored.attempt.requestBody !== requestBody) {
    throw new ReviewCaseAssignmentAttemptChangedError();
  }

  const session = sessionStore.getSnapshot();
  const organizationId = developmentOrganizationId(session.token);
  const actorId = developmentActorId(session.token);
  if (session.token === null || organizationId === null) {
    throw new ReviewCaseAssignmentAuthenticationError();
  }

  if (actorId === null) {
    throw new ReviewCaseAssignmentActorRequiredError();
  }

  if (organizationId !== stored.attempt.organizationId) {
    throw new ReviewCaseAssignmentTenantMismatchError();
  }

  if (actorId !== stored.attempt.actorId) {
    throw new ReviewCaseAssignmentActorMismatchError();
  }

  removeOwned(stored.attempt);
}

export function abandonUnreadableReviewCaseAssignmentAttempt(): void {
  rejectDevelopmentCookie();
  if (submissionInFlight) {
    throw new ReviewCaseAssignmentSubmissionInProgressError();
  }

  if (readStoredAttempt().status !== 'invalid') {
    throw new ReviewCaseAssignmentAttemptChangedError();
  }

  removeKey();
}

export function subscribeReviewCaseAssignmentVerified(
  listener: (result: VerifiedReviewCaseAssignment) => void,
): () => void {
  verifiedListeners.add(listener);
  return () => {
    verifiedListeners.delete(listener);
  };
}

export function resetReviewCaseAssignmentAttemptStateForTests(): void {
  submissionInFlight = false;
  readFloor = null;
  publish();
}

export function assignmentReadFloorForTests(): { detailSequence: number; assignmentSequence: number } | null {
  return readFloor === null ? null : { ...readFloor };
}

async function classifyResponse(
  response: Response,
  attempt: ReviewCaseAssignmentAttempt,
  epoch: number,
): Promise<VerifiedReviewCaseAssignment> {
  const ownership = ownAttempt(attempt, epoch);
  if (ownership !== 'owned') {
    throw ownershipError(ownership);
  }

  if (response.status !== 200 && response.status !== 201) {
    return refuse(response, attempt, epoch);
  }

  let text: string;
  try {
    text = await response.text();
  } catch {
    rememberUnknown(attempt, epoch);
    throw new UnreadableReviewCaseAssignmentError();
  }

  const afterBody = ownAttempt(attempt, epoch);
  if (afterBody !== 'owned') {
    throw ownershipError(afterBody);
  }

  let verified: Omit<VerifiedReviewCaseAssignment, 'cleanup'>;
  try {
    verified = mapVerified(parse(text), response, attempt, epoch);
  } catch (error) {
    if (error instanceof StaleSessionError
      || error instanceof ReviewCaseAssignmentAttemptChangedError
      || error instanceof ReviewCaseAssignmentActorMismatchError) {
      throw error;
    }

    rememberUnknown(attempt, epoch);
    throw new UnreadableReviewCaseAssignmentError();
  }

  const afterMap = ownAttempt(attempt, epoch);
  if (afterMap !== 'owned') {
    throw ownershipError(afterMap);
  }

  try {
    removeOwned(attempt);
    return { ...verified, cleanup: 'removed' };
  } catch (error) {
    if (error instanceof ReviewCaseAssignmentStorageCleanupError) {
      return { ...verified, cleanup: 'failed' };
    }

    throw error;
  }
}

async function refuse(
  response: Response,
  attempt: ReviewCaseAssignmentAttempt,
  epoch: number,
): Promise<never> {
  let text = '';
  let bodyRead = true;
  try {
    text = await response.text();
  } catch {
    bodyRead = false;
  }

  const ownership = ownAttempt(attempt, epoch);
  if (ownership !== 'owned') {
    throw ownershipError(ownership);
  }

  const error = toApiError(response.status, text);
  if (response.status === 409 && error.code === payloadConflictCode) {
    replaceAttempt(attempt, { ...attempt, lifecycle: 'rejected', uncertaintyLocked: attempt.uncertaintyLocked });
    throw new ReviewCaseAssignmentRejectedError(error.status, error.code);
  }

  if (response.status === 409 && error.code === concurrencyConflictCode) {
    if (attempt.uncertaintyLocked) {
      throw new ReviewCaseAssignmentConcurrencyUncheckedError();
    }

    replaceAttempt(attempt, { ...attempt, lifecycle: 'rejected', uncertaintyLocked: false });
    throw new ReviewCaseAssignmentRejectedError(error.status, error.code);
  }

  if (isControlledRejection(error, bodyRead) || isDomainConflict(error)) {
    if (attempt.uncertaintyLocked) {
      throw new ReviewCaseAssignmentUncertaintyRemainsError();
    }

    replaceAttempt(attempt, { ...attempt, lifecycle: 'rejected', uncertaintyLocked: false });
    throw new ReviewCaseAssignmentRejectedError(error.status, error.code);
  }

  rememberUnknown(attempt, epoch);
  throw new UnknownReviewCaseAssignmentOutcomeError();
}

function isControlledRejection(error: ApiError, bodyRead: boolean): boolean {
  if (error.status === 401 || error.status === 403 || error.status === 413 || error.status === 415) {
    return true;
  }

  if (!bodyRead) {
    return false;
  }

  if (error.status === 400) {
    return controlledBadRequestCodes.some((code) => error.code === code);
  }

  if (error.status === 404) {
    return error.code === 'reconciliation.review_case.not_found';
  }

  return false;
}

function isDomainConflict(error: ApiError): boolean {
  return error.status === 409 && domainConflictCodes.some((code) => error.code === code);
}

function markDispatch(attempt: ReviewCaseAssignmentAttempt): ReviewCaseAssignmentAttempt {
  if (attempt.lifecycle === 'sending' || attempt.lifecycle === 'uncertain') {
    return replaceAttempt(attempt, { ...attempt, lifecycle: 'uncertain', uncertaintyLocked: true });
  }

  return replaceAttempt(attempt, { ...attempt, lifecycle: 'sending', uncertaintyLocked: false });
}

function rememberUnknown(attempt: ReviewCaseAssignmentAttempt, epoch: number): void {
  if (ownAttempt(attempt, epoch) !== 'owned') {
    return;
  }

  try {
    replaceAttempt(attempt, { ...attempt, lifecycle: 'uncertain', uncertaintyLocked: true });
  } catch {
    // The original descriptor remains when the uncertain mark cannot be stored.
  }
}

function mapVerified(
  value: unknown,
  response: Response,
  attempt: ReviewCaseAssignmentAttempt,
  epoch: number,
): Omit<VerifiedReviewCaseAssignment, 'cleanup'> {
  const operation = attempt.operation === 'ReviewCaseClaim' ? 'claim' : 'release';
  let command: ReturnType<typeof readVerifiedReviewCaseAssignment>;
  try {
    command = readVerifiedReviewCaseAssignment(value, {
      reviewCaseId: attempt.caseId,
      operation,
      actorSource,
      actorId: attempt.actorId,
    });
  } catch (error) {
    if (error instanceof UnexpectedResponseError) {
      throw new UnreadableReviewCaseAssignmentError();
    }

    throw error;
  }

  if (response.status !== 200 && response.status !== 201) {
    throw new UnreadableReviewCaseAssignmentError();
  }

  return {
    caseId: command.reviewCaseId,
    operation: command.operation,
    assignee: command.assignee,
    recordedAt: command.recordedAt,
    created: response.status === 201,
    epoch,
  };
}

function ownAttempt(
  attempt: ReviewCaseAssignmentAttempt,
  epoch: number,
): 'owned' | 'stale' | 'changed' | 'foreign' {
  const session = sessionStore.getSnapshot();
  const organizationId = developmentOrganizationId(session.token);
  const actorId = developmentActorId(session.token);
  if (session.epoch !== epoch || organizationId !== attempt.organizationId) {
    return 'stale';
  }

  if (actorId === null || actorId !== attempt.actorId) {
    return 'foreign';
  }

  const stored = readStoredAttempt();
  if (!sameAttempt(stored, attempt)) {
    return 'changed';
  }

  return 'owned';
}

function ownershipError(ownership: 'stale' | 'changed' | 'foreign'): Error {
  if (ownership === 'stale') {
    return new StaleSessionError();
  }

  if (ownership === 'foreign') {
    return new ReviewCaseAssignmentActorMismatchError();
  }

  return new ReviewCaseAssignmentAttemptChangedError();
}

function sameAttempt(
  stored: ReturnType<typeof readStoredAttempt>,
  attempt: ReviewCaseAssignmentAttempt,
): stored is { status: 'present'; attempt: ReviewCaseAssignmentAttempt } {
  return stored.status === 'present'
    && stored.attempt.operation === attempt.operation
    && stored.attempt.organizationId === attempt.organizationId
    && stored.attempt.actorSource === attempt.actorSource
    && stored.attempt.actorId === attempt.actorId
    && stored.attempt.caseId === attempt.caseId
    && stored.attempt.idempotencyKey === attempt.idempotencyKey
    && stored.attempt.requestBody === attempt.requestBody;
}

function replaceAttempt(
  previous: ReviewCaseAssignmentAttempt,
  next: ReviewCaseAssignmentAttempt,
): ReviewCaseAssignmentAttempt {
  const stored = readStoredAttempt();
  if (!sameAttempt(stored, previous)) {
    throw new ReviewCaseAssignmentAttemptChangedError();
  }

  persistAttempt(next);
  return readBack(next);
}

function persistAttempt(attempt: ReviewCaseAssignmentAttempt): void {
  const serialized = JSON.stringify(attempt);
  try {
    sessionStorage.setItem(reviewCaseAssignmentAttemptStorageKey, serialized);
    if (sessionStorage.getItem(reviewCaseAssignmentAttemptStorageKey) !== serialized) {
      throw new ReviewCaseAssignmentStorageError();
    }
  } catch (error) {
    if (error instanceof ReviewCaseAssignmentStorageError) {
      throw error;
    }

    throw new ReviewCaseAssignmentStorageError();
  }

  readBack(attempt);
  publish();
}

function readBack(attempt: ReviewCaseAssignmentAttempt): ReviewCaseAssignmentAttempt {
  const stored = readStoredAttempt();
  if (!sameAttempt(stored, attempt)
    || stored.attempt.lifecycle !== attempt.lifecycle
    || stored.attempt.uncertaintyLocked !== attempt.uncertaintyLocked) {
    throw new ReviewCaseAssignmentStorageError();
  }

  return stored.attempt;
}

function removeOwned(attempt: ReviewCaseAssignmentAttempt): void {
  const stored = readStoredAttempt();
  if (!sameAttempt(stored, attempt)) {
    throw new ReviewCaseAssignmentAttemptChangedError();
  }

  removeKey();
}

function removeKey(): void {
  try {
    sessionStorage.removeItem(reviewCaseAssignmentAttemptStorageKey);
  } catch {
    throw new ReviewCaseAssignmentStorageCleanupError();
  }

  let remaining: string | null;
  try {
    remaining = sessionStorage.getItem(reviewCaseAssignmentAttemptStorageKey);
  } catch {
    throw new ReviewCaseAssignmentStorageCleanupError();
  }

  if (remaining !== null) {
    throw new ReviewCaseAssignmentStorageCleanupError();
  }

  readFloor = {
    detailSequence: startedReviewCaseDetailReadCount(),
    assignmentSequence: startedAssignmentReadCount(),
  };
  publish();
}

function readStoredAttempt():
  | { status: 'empty' }
  | { status: 'invalid' }
  | { status: 'present'; attempt: ReviewCaseAssignmentAttempt } {
  let raw: string | null;
  try {
    raw = sessionStorage.getItem(reviewCaseAssignmentAttemptStorageKey);
  } catch {
    return { status: 'invalid' };
  }

  if (raw === null) {
    return { status: 'empty' };
  }

  try {
    const parsed: unknown = JSON.parse(raw);
    const attempt = parseAttempt(parsed);
    if (attempt === null || !storedBodyMatches(attempt)) {
      return { status: 'invalid' };
    }

    return { status: 'present', attempt };
  } catch {
    return { status: 'invalid' };
  }
}

function parseAttempt(value: unknown): ReviewCaseAssignmentAttempt | null {
  if (!isRecord(value)
    || value.schemaVersion !== schemaVersion
    || (value.operation !== 'ReviewCaseClaim' && value.operation !== 'ReviewCaseRelease')
    || value.actorSource !== actorSource
    || typeof value.actorId !== 'string'
    || typeof value.requestBody !== 'string'
    || (value.lifecycle !== 'ready' && value.lifecycle !== 'sending' && value.lifecycle !== 'uncertain' && value.lifecycle !== 'rejected')
    || typeof value.uncertaintyLocked !== 'boolean'
    || typeof value.organizationId !== 'string'
    || !isExactGuid(value.organizationId)
    || typeof value.caseId !== 'string'
    || !isExactGuid(value.caseId)
    || typeof value.idempotencyKey !== 'string'
    || !isExactGuid(value.idempotencyKey)) {
    return null;
  }

  if (!isStoredActorId(value.actorId)) {
    return null;
  }

  return {
    schemaVersion,
    operation: value.operation,
    organizationId: value.organizationId.toLowerCase(),
    actorSource,
    actorId: value.actorId,
    caseId: value.caseId.toLowerCase(),
    idempotencyKey: value.idempotencyKey.toLowerCase(),
    requestBody: value.requestBody,
    lifecycle: value.lifecycle,
    uncertaintyLocked: value.uncertaintyLocked,
  };
}

function storedBodyMatches(attempt: ReviewCaseAssignmentAttempt): boolean {
  try {
    const parsed: unknown = JSON.parse(attempt.requestBody);
    if (!isRecord(parsed) || Object.keys(parsed).length !== 1 || typeof parsed.concurrencyToken !== 'string') {
      return false;
    }

    if (readReviewCaseConcurrencyToken(parsed.concurrencyToken) === null) {
      return false;
    }

    return attempt.requestBody === serializeReviewCaseAssignmentBody(parsed.concurrencyToken);
  } catch {
    return false;
  }
}

function isStoredActorId(value: string): boolean {
  return value.length > 0 && value.length <= 256 && trimDotNetWhiteSpace(value) === value;
}

function requireOrganization(session: SessionSnapshot): string {
  const organizationId = developmentOrganizationId(session.token);
  if (session.token === null || organizationId === null) {
    throw new ReviewCaseAssignmentAuthenticationError();
  }

  return organizationId;
}

function requireActor(session: SessionSnapshot): string {
  const actorId = developmentActorId(session.token);
  if (actorId === null) {
    throw new ReviewCaseAssignmentActorRequiredError();
  }

  return actorId;
}

function rejectDevelopmentCookie(): void {
  rejectCookieFinancialRequest(sessionStore.getSnapshot());
}

function emitVerified(result: VerifiedReviewCaseAssignment): void {
  for (const listener of [...verifiedListeners]) {
    listener(result);
  }
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

function getStorageVersion(): number {
  return storageVersion;
}

function publish(): void {
  storageVersion += 1;
  listeners.forEach((listener) => listener());
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value) && !isLosslessNumber(value);
}
