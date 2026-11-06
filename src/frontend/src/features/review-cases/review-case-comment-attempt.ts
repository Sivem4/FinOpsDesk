import { parse, isLosslessNumber } from 'lossless-json';
import { useSyncExternalStore } from 'react';

import { authenticationHeaders, rejectCookieFinancialRequest } from '../../shared/api/authentication';
import { ApiError, isAbortError, StaleSessionError } from '../../shared/api/errors';
import { toApiError } from '../../shared/api/problem';
import { sessionStore, type SessionSnapshot } from '../../shared/api/session-store';
import { useSession } from '../../shared/auth/session';
import { readReviewCaseComment, type ReviewCaseCommentRecord } from './review-case-comment-api';
import { serializeReviewCaseCommentBody } from './review-case-comment-request';
import {
  developmentActorId,
  developmentOrganizationId,
  isExactGuid,
} from './review-case-open-request';

export const reviewCaseCommentAttemptStorageKey = 'finopsdesk.review-case-comment-attempt';

const schemaVersion = 1;
const operationType = 'ReviewCaseComment';
const controlledBadRequestCodes = [
  'reconciliation.review_case_comment.invalid_request',
  'reconciliation.review_case_comment.idempotency_key_required',
  'reconciliation.review_case_comment.idempotency_key_invalid',
] as const;

let submissionInFlight = false;
let storageVersion = 0;
const listeners = new Set<() => void>();

export class ReviewCaseCommentStorageError extends Error {
  constructor() {
    super('The review case comment could not be stored.');
    this.name = 'ReviewCaseCommentStorageError';
  }
}

export class ReviewCaseCommentStorageCleanupError extends Error {
  constructor() {
    super('The verified comment could not be cleared from this browser tab.');
    this.name = 'ReviewCaseCommentStorageCleanupError';
  }
}

export class ReviewCaseCommentAttemptBlockedError extends Error {
  constructor() {
    super('The stored review case comment blocks a new comment.');
    this.name = 'ReviewCaseCommentAttemptBlockedError';
  }
}

export class UnresolvedReviewCaseCommentError extends Error {
  constructor() {
    super('An unresolved review case comment is already stored.');
    this.name = 'UnresolvedReviewCaseCommentError';
  }
}

export class ReviewCaseCommentAuthenticationError extends Error {
  constructor() {
    super('Sign in before adding a review case comment.');
    this.name = 'ReviewCaseCommentAuthenticationError';
  }
}

export class ReviewCaseCommentActorRequiredError extends Error {
  constructor() {
    super('This comment needs an operator identity.');
    this.name = 'ReviewCaseCommentActorRequiredError';
  }
}

export class ReviewCaseCommentTenantMismatchError extends Error {
  constructor() {
    super('The stored comment belongs to another organization.');
    this.name = 'ReviewCaseCommentTenantMismatchError';
  }
}

export class ReviewCaseCommentSubmissionInProgressError extends Error {
  constructor() {
    super('A review case comment is already being sent.');
    this.name = 'ReviewCaseCommentSubmissionInProgressError';
  }
}

export class ReviewCaseCommentAttemptChangedError extends Error {
  constructor() {
    super('The stored comment changed before the response could be applied.');
    this.name = 'ReviewCaseCommentAttemptChangedError';
  }
}

export class UnknownReviewCaseCommentOutcomeError extends Error {
  constructor() {
    super('The comment was not verified.');
    this.name = 'UnknownReviewCaseCommentOutcomeError';
  }
}

export class UnreadableReviewCaseCommentError extends Error {
  constructor() {
    super('The comment response could not be verified.');
    this.name = 'UnreadableReviewCaseCommentError';
  }
}

export class ReviewCaseCommentPayloadConflictError extends Error {
  constructor() {
    super('This idempotency key was already used for a different comment.');
    this.name = 'ReviewCaseCommentPayloadConflictError';
  }
}

export class ReviewCaseCommentRejectedError extends Error {
  readonly status: number;
  readonly code: string | null;

  constructor(status: number, code: string | null) {
    super('The server refused this comment before saving it.');
    this.name = 'ReviewCaseCommentRejectedError';
    this.status = status;
    this.code = code;
  }
}

export class ReviewCaseCommentUncertaintyRemainsError extends Error {
  constructor() {
    super('A later refusal did not clear the unresolved comment.');
    this.name = 'ReviewCaseCommentUncertaintyRemainsError';
  }
}

export interface ReviewCaseCommentAttempt {
  schemaVersion: 1;
  operation: 'ReviewCaseComment';
  organizationId: string;
  caseId: string;
  idempotencyKey: string;
  requestBody: string;
  normalizedText: string;
  lifecycle: 'ready' | 'sending' | 'uncertain' | 'rejected';
  uncertaintyLocked: boolean;
}

export interface VerifiedReviewCaseComment {
  caseId: string;
  id: string;
  text: string;
  actorId: string;
  recordedAt: string;
  created: boolean;
  epoch: number;
  cleanup: 'removed' | 'failed';
}

export type ReviewCaseCommentView =
  | { status: 'none' }
  | { status: 'blocked-authentication' }
  | { status: 'blocked-tenant' }
  | { status: 'invalid-storage' }
  | { status: 'pending'; attempt: ReviewCaseCommentAttempt };

export function useReviewCaseCommentAttempt(): ReviewCaseCommentView {
  const session = useSession();
  useSyncExternalStore(subscribe, getStorageVersion, getStorageVersion);
  return inspectReviewCaseCommentAttempt(session);
}

export function inspectReviewCaseCommentAttempt(
  session: SessionSnapshot = sessionStore.getSnapshot(),
): ReviewCaseCommentView {
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

  return { status: 'pending', attempt: stored.attempt };
}

export function beginReviewCaseCommentAttempt(caseId: string, normalizedText: string): ReviewCaseCommentAttempt {
  rejectDevelopmentCookie();
  if (submissionInFlight) {
    throw new ReviewCaseCommentSubmissionInProgressError();
  }

  const existing = readStoredAttempt();
  if (existing.status === 'invalid') {
    throw new ReviewCaseCommentAttemptBlockedError();
  }

  if (existing.status === 'present') {
    throw new UnresolvedReviewCaseCommentError();
  }

  const session = sessionStore.getSnapshot();
  const organizationId = requireOrganization(session);
  requireActor(session);
  if (!isExactGuid(caseId)) {
    throw new ReviewCaseCommentStorageError();
  }

  const idempotencyKey = crypto.randomUUID();
  if (!isExactGuid(idempotencyKey)) {
    throw new ReviewCaseCommentStorageError();
  }

  const attempt: ReviewCaseCommentAttempt = {
    schemaVersion,
    operation: operationType,
    organizationId,
    caseId,
    idempotencyKey,
    requestBody: serializeReviewCaseCommentBody(normalizedText),
    normalizedText,
    lifecycle: 'ready',
    uncertaintyLocked: false,
  };
  if (attempt.requestBody !== serializeReviewCaseCommentBody(attempt.normalizedText)) {
    throw new ReviewCaseCommentStorageError();
  }

  persistAttempt(attempt);
  return readBack(attempt);
}

export async function submitReviewCaseCommentAttempt(signal?: AbortSignal): Promise<VerifiedReviewCaseComment> {
  rejectDevelopmentCookie();
  if (submissionInFlight) {
    throw new ReviewCaseCommentSubmissionInProgressError();
  }

  const session = sessionStore.getSnapshot();
  const organizationId = requireOrganization(session);
  requireActor(session);
  const stored = readStoredAttempt();
  if (stored.status !== 'present') {
    throw new ReviewCaseCommentAttemptBlockedError();
  }

  if (organizationId !== stored.attempt.organizationId) {
    throw new ReviewCaseCommentTenantMismatchError();
  }

  if (stored.attempt.lifecycle === 'rejected') {
    throw new ReviewCaseCommentRejectedError(0, null);
  }

  submissionInFlight = true;
  const epoch = session.epoch;
  try {
    const prepared = markDispatch(stored.attempt);
    let response: Response;
    try {
      response = await fetch(`/api/review-cases/${prepared.caseId}/comments`, {
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
        throw new UnknownReviewCaseCommentOutcomeError();
      }

      throw error;
    }

    return await classifyResponse(response, prepared, epoch);
  } finally {
    submissionInFlight = false;
  }
}

export function abandonReviewCaseCommentAttempt(idempotencyKey: string, requestBody: string): void {
  rejectDevelopmentCookie();
  if (submissionInFlight) {
    throw new ReviewCaseCommentSubmissionInProgressError();
  }

  const stored = readStoredAttempt();
  if (stored.status !== 'present'
    || stored.attempt.idempotencyKey !== idempotencyKey
    || stored.attempt.requestBody !== requestBody) {
    throw new ReviewCaseCommentAttemptChangedError();
  }

  const organizationId = developmentOrganizationId(sessionStore.getSnapshot().token);
  if (organizationId === null) {
    throw new ReviewCaseCommentAuthenticationError();
  }

  if (organizationId !== stored.attempt.organizationId) {
    throw new ReviewCaseCommentTenantMismatchError();
  }

  removeOwned(stored.attempt);
}

export function abandonUnreadableReviewCaseCommentAttempt(): void {
  rejectDevelopmentCookie();
  if (submissionInFlight) {
    throw new ReviewCaseCommentSubmissionInProgressError();
  }

  if (readStoredAttempt().status !== 'invalid') {
    throw new ReviewCaseCommentAttemptChangedError();
  }

  removeKey();
}

export function resetReviewCaseCommentAttemptStateForTests(): void {
  submissionInFlight = false;
  storageVersion += 1;
}

async function classifyResponse(
  response: Response,
  attempt: ReviewCaseCommentAttempt,
  epoch: number,
): Promise<VerifiedReviewCaseComment> {
  const ownership = ownAttempt(attempt, epoch);
  if (ownership !== 'owned') {
    throw ownership === 'stale' ? new StaleSessionError() : new ReviewCaseCommentAttemptChangedError();
  }

  if (response.status !== 200 && response.status !== 201) {
    return refuse(response, attempt, epoch);
  }

  let text: string;
  try {
    text = await response.text();
  } catch {
    rememberUnknown(attempt, epoch);
    throw new UnreadableReviewCaseCommentError();
  }

  if (ownAttempt(attempt, epoch) !== 'owned') {
    throw ownAttempt(attempt, epoch) === 'stale' ? new StaleSessionError() : new ReviewCaseCommentAttemptChangedError();
  }

  let verified: Omit<VerifiedReviewCaseComment, 'cleanup'>;
  try {
    verified = mapVerified(parse(text), response, attempt, epoch);
  } catch (error) {
    if (error instanceof StaleSessionError || error instanceof ReviewCaseCommentAttemptChangedError) {
      throw error;
    }

    rememberUnknown(attempt, epoch);
    throw new UnreadableReviewCaseCommentError();
  }

  if (ownAttempt(attempt, epoch) !== 'owned') {
    throw ownAttempt(attempt, epoch) === 'stale' ? new StaleSessionError() : new ReviewCaseCommentAttemptChangedError();
  }

  try {
    removeOwned(attempt);
    return { ...verified, cleanup: 'removed' };
  } catch (error) {
    if (error instanceof ReviewCaseCommentStorageCleanupError) {
      return { ...verified, cleanup: 'failed' };
    }

    throw error;
  }
}

async function refuse(response: Response, attempt: ReviewCaseCommentAttempt, epoch: number): Promise<never> {
  let text = '';
  let bodyRead = true;
  try {
    text = await response.text();
  } catch {
    bodyRead = false;
  }

  if (ownAttempt(attempt, epoch) !== 'owned') {
    throw ownAttempt(attempt, epoch) === 'stale' ? new StaleSessionError() : new ReviewCaseCommentAttemptChangedError();
  }

  const error = toApiError(response.status, text);
  if (error.code === 'reconciliation.review_case_comment.idempotency_payload_conflict') {
    throw new ReviewCaseCommentPayloadConflictError();
  }

  if (isControlledRejection(error, bodyRead)) {
    if (attempt.uncertaintyLocked) {
      throw new ReviewCaseCommentUncertaintyRemainsError();
    }

    replaceAttempt(attempt, { ...attempt, lifecycle: 'rejected', uncertaintyLocked: false });
    throw new ReviewCaseCommentRejectedError(error.status, error.code);
  }

  if (response.status >= 500 || error.code === 'reconciliation.review_case_comment.concurrency_conflict' || text === '') {
    rememberUnknown(attempt, epoch);
    throw new UnknownReviewCaseCommentOutcomeError();
  }

  if (attempt.uncertaintyLocked) {
    throw new ReviewCaseCommentUncertaintyRemainsError();
  }

  rememberUnknown(attempt, epoch);
  throw new UnknownReviewCaseCommentOutcomeError();
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

function markDispatch(attempt: ReviewCaseCommentAttempt): ReviewCaseCommentAttempt {
  if (attempt.lifecycle === 'sending' || attempt.lifecycle === 'uncertain') {
    return replaceAttempt(attempt, { ...attempt, lifecycle: 'uncertain', uncertaintyLocked: true });
  }

  return replaceAttempt(attempt, { ...attempt, lifecycle: 'sending', uncertaintyLocked: false });
}

function rememberUnknown(attempt: ReviewCaseCommentAttempt, epoch: number): void {
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
  attempt: ReviewCaseCommentAttempt,
  epoch: number,
): Omit<VerifiedReviewCaseComment, 'cleanup'> {
  let comment: ReviewCaseCommentRecord;
  try {
    comment = readReviewCaseComment(value);
  } catch {
    throw new UnreadableReviewCaseCommentError();
  }

  if (comment.text !== attempt.normalizedText || (response.status !== 200 && response.status !== 201)) {
    throw new UnreadableReviewCaseCommentError();
  }

  return {
    caseId: attempt.caseId,
    id: comment.id,
    text: comment.text,
    actorId: comment.actorId,
    recordedAt: comment.recordedAt,
    created: response.status === 201,
    epoch,
  };
}

function ownAttempt(attempt: ReviewCaseCommentAttempt, epoch: number): 'owned' | 'stale' | 'changed' {
  const session = sessionStore.getSnapshot();
  const organizationId = developmentOrganizationId(session.token);
  if (session.epoch !== epoch || organizationId !== attempt.organizationId) {
    return 'stale';
  }

  const stored = readStoredAttempt();
  if (!sameAttempt(stored, attempt)) {
    return 'changed';
  }

  return 'owned';
}

function sameAttempt(
  stored: ReturnType<typeof readStoredAttempt>,
  attempt: ReviewCaseCommentAttempt,
): stored is { status: 'present'; attempt: ReviewCaseCommentAttempt } {
  return stored.status === 'present'
    && stored.attempt.operation === attempt.operation
    && stored.attempt.organizationId === attempt.organizationId
    && stored.attempt.caseId === attempt.caseId
    && stored.attempt.idempotencyKey === attempt.idempotencyKey
    && stored.attempt.requestBody === attempt.requestBody
    && stored.attempt.normalizedText === attempt.normalizedText;
}

function replaceAttempt(previous: ReviewCaseCommentAttempt, next: ReviewCaseCommentAttempt): ReviewCaseCommentAttempt {
  const stored = readStoredAttempt();
  if (!sameAttempt(stored, previous)) {
    throw new ReviewCaseCommentAttemptChangedError();
  }

  persistAttempt(next);
  return readBack(next);
}

function persistAttempt(attempt: ReviewCaseCommentAttempt): void {
  const serialized = JSON.stringify(attempt);
  try {
    sessionStorage.setItem(reviewCaseCommentAttemptStorageKey, serialized);
    if (sessionStorage.getItem(reviewCaseCommentAttemptStorageKey) !== serialized) {
      throw new ReviewCaseCommentStorageError();
    }
  } catch (error) {
    if (error instanceof ReviewCaseCommentStorageError) {
      throw error;
    }

    throw new ReviewCaseCommentStorageError();
  }

  readBack(attempt);
  publish();
}

function readBack(attempt: ReviewCaseCommentAttempt): ReviewCaseCommentAttempt {
  const stored = readStoredAttempt();
  if (!sameAttempt(stored, attempt)
    || stored.attempt.lifecycle !== attempt.lifecycle
    || stored.attempt.uncertaintyLocked !== attempt.uncertaintyLocked) {
    throw new ReviewCaseCommentStorageError();
  }

  return stored.attempt;
}

function removeOwned(attempt: ReviewCaseCommentAttempt): void {
  const stored = readStoredAttempt();
  if (!sameAttempt(stored, attempt)) {
    throw new ReviewCaseCommentAttemptChangedError();
  }

  removeKey();
}

function removeKey(): void {
  try {
    sessionStorage.removeItem(reviewCaseCommentAttemptStorageKey);
  } catch {
    throw new ReviewCaseCommentStorageCleanupError();
  }

  let remaining: string | null;
  try {
    remaining = sessionStorage.getItem(reviewCaseCommentAttemptStorageKey);
  } catch {
    throw new ReviewCaseCommentStorageCleanupError();
  }

  if (remaining !== null) {
    throw new ReviewCaseCommentStorageCleanupError();
  }

  publish();
}

function readStoredAttempt():
  | { status: 'empty' }
  | { status: 'invalid' }
  | { status: 'present'; attempt: ReviewCaseCommentAttempt } {
  let raw: string | null;
  try {
    raw = sessionStorage.getItem(reviewCaseCommentAttemptStorageKey);
  } catch {
    return { status: 'invalid' };
  }

  if (raw === null) {
    return { status: 'empty' };
  }

  try {
    const parsed: unknown = JSON.parse(raw);
    const attempt = parseAttempt(parsed);
    if (attempt === null || attempt.requestBody !== serializeReviewCaseCommentBody(attempt.normalizedText)) {
      return { status: 'invalid' };
    }

    return { status: 'present', attempt };
  } catch {
    return { status: 'invalid' };
  }
}

function parseAttempt(value: unknown): ReviewCaseCommentAttempt | null {
  if (!isRecord(value)
    || value.schemaVersion !== schemaVersion
    || value.operation !== operationType
    || typeof value.requestBody !== 'string'
    || typeof value.normalizedText !== 'string'
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

  let text: string;
  try {
    text = readReviewCaseComment({
      id: '11111111-1111-4111-8111-111111111111',
      text: value.normalizedText,
      actorId: 'stored',
      recordedAt: '2026-11-06T00:00:00Z',
    }).text;
  } catch {
    return null;
  }

  if (text !== value.normalizedText) {
    return null;
  }

  return {
    schemaVersion,
    operation: operationType,
    organizationId: value.organizationId,
    caseId: value.caseId,
    idempotencyKey: value.idempotencyKey,
    requestBody: value.requestBody,
    normalizedText: text,
    lifecycle: value.lifecycle,
    uncertaintyLocked: value.uncertaintyLocked,
  };
}

function requireOrganization(session: SessionSnapshot): string {
  const organizationId = developmentOrganizationId(session.token);
  if (session.token === null || organizationId === null) {
    throw new ReviewCaseCommentAuthenticationError();
  }

  return organizationId;
}

function requireActor(session: SessionSnapshot): void {
  if (developmentActorId(session.token) === null) {
    throw new ReviewCaseCommentActorRequiredError();
  }
}

function rejectDevelopmentCookie(): void {
  rejectCookieFinancialRequest(sessionStore.getSnapshot());
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
