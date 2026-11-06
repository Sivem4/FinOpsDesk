import { parse, isLosslessNumber } from 'lossless-json';
import { useSyncExternalStore } from 'react';

import { authenticationHeaders, rejectCookieFinancialRequest } from '../../shared/api/authentication';
import { ApiError, isAbortError, StaleSessionError } from '../../shared/api/errors';
import { toApiError } from '../../shared/api/problem';
import { sessionStore, type SessionSnapshot } from '../../shared/api/session-store';
import { useSession } from '../../shared/auth/session';
import {
  readReviewCaseConcurrencyToken,
  startedReviewCaseDetailReadCount,
  type ReviewCaseCommandContext,
} from './review-case-api';
import {
  serializeReviewCaseDecisionBody,
  type NormalizedReviewCaseDecision,
  type ReviewCaseDecisionOperation,
  type ReviewCaseDecisionOutcome,
} from './review-case-decision-request';
import {
  developmentActorId,
  developmentOrganizationId,
  isDotNetNullOrWhiteSpace,
  isExactGuid,
  trimDotNetWhiteSpace,
} from './review-case-open-request';

export const reviewCaseDecisionAttemptStorageKey = 'finopsdesk.review-case-decision-attempt';

const schemaVersion = 1;
const timestampPattern = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.\d+)?(Z|[+-](\d{2}):(\d{2}))$/;
const outcomes = ['AllocationRecorded', 'NoActionRequired', 'HandledOutside'] as const;
const operations = ['ReviewCaseResolve', 'ReviewCaseDismiss'] as const;

let submissionInFlight = false;
let freshReadAfter = 0;
let storageVersion = 0;
const listeners = new Set<() => void>();

export class ReviewCaseDecisionStorageError extends Error {
  constructor() {
    super('The review case decision could not be stored.');
    this.name = 'ReviewCaseDecisionStorageError';
  }
}

export class ReviewCaseDecisionStorageCleanupError extends Error {
  constructor() {
    super('The verified decision could not be cleared from this browser tab.');
    this.name = 'ReviewCaseDecisionStorageCleanupError';
  }
}

export class ReviewCaseDecisionAttemptBlockedError extends Error {
  constructor() {
    super('The stored review case decision blocks a new decision.');
    this.name = 'ReviewCaseDecisionAttemptBlockedError';
  }
}

export class UnresolvedReviewCaseDecisionError extends Error {
  constructor() {
    super('An unresolved review case decision is already stored.');
    this.name = 'UnresolvedReviewCaseDecisionError';
  }
}

export class ReviewCaseDecisionAuthenticationError extends Error {
  constructor() {
    super('Sign in before deciding a review case.');
    this.name = 'ReviewCaseDecisionAuthenticationError';
  }
}

export class ReviewCaseDecisionActorRequiredError extends Error {
  constructor() {
    super('This decision needs an operator identity.');
    this.name = 'ReviewCaseDecisionActorRequiredError';
  }
}

export class ReviewCaseDecisionTenantMismatchError extends Error {
  constructor() {
    super('The stored decision belongs to another organization.');
    this.name = 'ReviewCaseDecisionTenantMismatchError';
  }
}

export class ReviewCaseDecisionSubmissionInProgressError extends Error {
  constructor() {
    super('A review case decision is already being sent.');
    this.name = 'ReviewCaseDecisionSubmissionInProgressError';
  }
}

export class ReviewCaseDecisionAttemptChangedError extends Error {
  constructor() {
    super('The stored decision changed before the response could be applied.');
    this.name = 'ReviewCaseDecisionAttemptChangedError';
  }
}

export class UnknownReviewCaseDecisionOutcomeError extends Error {
  constructor() {
    super('The decision was not verified.');
    this.name = 'UnknownReviewCaseDecisionOutcomeError';
  }
}

export class UnreadableReviewCaseDecisionError extends Error {
  constructor() {
    super('The decision response could not be verified.');
    this.name = 'UnreadableReviewCaseDecisionError';
  }
}

export class ReviewCaseDecisionRefusedError extends Error {
  readonly status: number;
  readonly code: string | null;

  constructor(status: number, code: string | null) {
    super('The server refused this decision before saving it.');
    this.name = 'ReviewCaseDecisionRefusedError';
    this.status = status;
    this.code = code;
  }
}

export class ReviewCaseDecisionConflictError extends Error {
  readonly code: 'reconciliation.review_case.concurrency_conflict' | 'reconciliation.review_case.terminal_conflict';

  constructor(code: ReviewCaseDecisionConflictError['code']) {
    super(code === 'reconciliation.review_case.terminal_conflict'
      ? 'The review case already has a different terminal decision.'
      : 'The review case changed before this decision was saved.');
    this.name = 'ReviewCaseDecisionConflictError';
    this.code = code;
  }
}

export class ReviewCaseDecisionUncertaintyRemainsError extends Error {
  constructor() {
    super('A later refusal did not clear the unresolved decision.');
    this.name = 'ReviewCaseDecisionUncertaintyRemainsError';
  }
}

export interface ReviewCaseDecisionAttempt extends NormalizedReviewCaseDecision {
  schemaVersion: 1;
  requestBody: string;
  organizationId: string;
  lifecycle: 'ready' | 'sending' | 'uncertain' | 'refused';
  uncertaintyLocked: boolean;
}

export interface VerifiedReviewCaseDecision {
  reviewCaseId: string;
  operation: ReviewCaseDecisionOperation;
  status: 'Resolved' | 'Dismissed';
  outcome: ReviewCaseDecisionOutcome | null;
  allocationId: string | null;
  resolutionNote: string | null;
  epoch: number;
  cleanup: 'removed' | 'failed';
}

export type ReviewCaseDecisionView =
  | { status: 'none' }
  | { status: 'blocked-authentication' }
  | { status: 'blocked-tenant' }
  | { status: 'invalid-storage' }
  | { status: 'pending'; attempt: ReviewCaseDecisionAttempt };

export function useReviewCaseDecisionAttempt(): ReviewCaseDecisionView {
  const session = useSession();
  useSyncExternalStore(subscribe, getStorageVersion, getStorageVersion);
  return inspectReviewCaseDecisionAttempt(session);
}

export function useDecisionReadBoundary(): number {
  return useSyncExternalStore(subscribe, getFreshReadAfter, getFreshReadAfter);
}

export function inspectReviewCaseDecisionAttempt(
  session: SessionSnapshot = sessionStore.getSnapshot(),
): ReviewCaseDecisionView {
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

export function decisionCommandAllowsDraft(
  command: ReviewCaseCommandContext | null,
  epoch: number,
  caseId: string,
): boolean {
  return command !== null
    && command.epoch === epoch
    && command.caseId === caseId
    && command.readSequence > freshReadAfter;
}

export function beginReviewCaseDecisionAttempt(value: NormalizedReviewCaseDecision): ReviewCaseDecisionAttempt {
  rejectDevelopmentCookie();
  if (submissionInFlight) {
    throw new ReviewCaseDecisionSubmissionInProgressError();
  }

  const existing = readStoredAttempt();
  if (existing.status === 'invalid') {
    throw new ReviewCaseDecisionAttemptBlockedError();
  }

  if (existing.status === 'present') {
    throw new UnresolvedReviewCaseDecisionError();
  }

  const session = sessionStore.getSnapshot();
  const organizationId = requireOrganization(session);
  requireActor(session);
  const attempt = createAttempt(organizationId, value);
  persistAttempt(attempt);
  return readBack(attempt);
}

export async function submitReviewCaseDecisionAttempt(signal?: AbortSignal): Promise<VerifiedReviewCaseDecision> {
  rejectDevelopmentCookie();
  if (submissionInFlight) {
    throw new ReviewCaseDecisionSubmissionInProgressError();
  }

  const session = sessionStore.getSnapshot();
  const organizationId = requireOrganization(session);
  requireActor(session);
  const stored = readStoredAttempt();
  if (stored.status !== 'present') {
    throw new ReviewCaseDecisionAttemptBlockedError();
  }

  if (organizationId !== stored.attempt.organizationId) {
    throw new ReviewCaseDecisionTenantMismatchError();
  }

  if (stored.attempt.lifecycle === 'refused') {
    throw new ReviewCaseDecisionRefusedError(0, null);
  }

  submissionInFlight = true;
  const epoch = session.epoch;
  try {
    const prepared = markDispatch(stored.attempt);
    let response: Response;
    try {
      response = await fetch(decisionUrl(prepared), {
        method: 'POST',
        credentials: 'same-origin',
        headers: {
          Accept: 'application/json',
          'Content-Type': 'application/json',
          ...authenticationHeaders(session, 'POST'),
        },
        body: prepared.requestBody,
        signal: signal === undefined ? session.signal : AbortSignal.any([signal, session.signal]),
      });
    } catch (error) {
      rememberUnknown(prepared, epoch);
      if (isAbortError(error) || error instanceof TypeError) {
        throw new UnknownReviewCaseDecisionOutcomeError();
      }

      throw error;
    }

    return await classifyResponse(response, prepared, epoch);
  } finally {
    submissionInFlight = false;
  }
}

export function abandonReviewCaseDecisionAttempt(operation: ReviewCaseDecisionOperation, caseId: string, requestBody: string): void {
  rejectDevelopmentCookie();
  if (submissionInFlight) {
    throw new ReviewCaseDecisionSubmissionInProgressError();
  }

  const stored = readStoredAttempt();
  if (stored.status !== 'present'
    || stored.attempt.operation !== operation
    || stored.attempt.caseId !== caseId
    || stored.attempt.requestBody !== requestBody) {
    throw new ReviewCaseDecisionAttemptChangedError();
  }

  const organizationId = developmentOrganizationId(sessionStore.getSnapshot().token);
  if (organizationId === null) {
    throw new ReviewCaseDecisionAuthenticationError();
  }

  if (organizationId !== stored.attempt.organizationId) {
    throw new ReviewCaseDecisionTenantMismatchError();
  }

  removeOwned(stored.attempt);
  raiseDecisionReadBoundary();
}

export function abandonUnreadableReviewCaseDecisionAttempt(): void {
  rejectDevelopmentCookie();
  if (submissionInFlight) {
    throw new ReviewCaseDecisionSubmissionInProgressError();
  }

  if (readStoredAttempt().status !== 'invalid') {
    throw new ReviewCaseDecisionAttemptChangedError();
  }

  removeKey();
  raiseDecisionReadBoundary();
}

export function resetReviewCaseDecisionAttemptStateForTests(): void {
  submissionInFlight = false;
  freshReadAfter = 0;
  storageVersion += 1;
}

function createAttempt(organizationId: string, value: NormalizedReviewCaseDecision): ReviewCaseDecisionAttempt {
  const attempt: ReviewCaseDecisionAttempt = {
    schemaVersion,
    operation: value.operation,
    organizationId,
    caseId: value.caseId,
    requestBody: serializeReviewCaseDecisionBody(value),
    outcome: value.outcome,
    allocationId: value.allocationId,
    resolutionNote: value.resolutionNote,
    concurrencyToken: value.concurrencyToken,
    lifecycle: 'ready',
    uncertaintyLocked: false,
  };
  if (attempt.requestBody !== serializeReviewCaseDecisionBody(attempt)) {
    throw new ReviewCaseDecisionStorageError();
  }

  return attempt;
}

async function classifyResponse(
  response: Response,
  attempt: ReviewCaseDecisionAttempt,
  epoch: number,
): Promise<VerifiedReviewCaseDecision> {
  const ownership = ownAttempt(attempt, epoch);
  if (ownership !== 'owned') {
    throw ownership === 'stale' ? new StaleSessionError() : new ReviewCaseDecisionAttemptChangedError();
  }

  if (response.status !== 200) {
    return refuse(response, attempt, epoch);
  }

  let text: string;
  try {
    text = await response.text();
  } catch {
    rememberUnknown(attempt, epoch);
    throw new UnreadableReviewCaseDecisionError();
  }

  if (ownAttempt(attempt, epoch) !== 'owned') {
    throw ownAttempt(attempt, epoch) === 'stale' ? new StaleSessionError() : new ReviewCaseDecisionAttemptChangedError();
  }

  let verified: Omit<VerifiedReviewCaseDecision, 'cleanup'>;
  try {
    verified = mapVerified(parse(text), attempt, epoch);
  } catch (error) {
    if (error instanceof StaleSessionError || error instanceof ReviewCaseDecisionAttemptChangedError) {
      throw error;
    }

    rememberUnknown(attempt, epoch);
    throw new UnreadableReviewCaseDecisionError();
  }

  if (ownAttempt(attempt, epoch) !== 'owned') {
    throw ownAttempt(attempt, epoch) === 'stale' ? new StaleSessionError() : new ReviewCaseDecisionAttemptChangedError();
  }

  try {
    removeOwned(attempt);
    return { ...verified, cleanup: 'removed' };
  } catch (error) {
    if (error instanceof ReviewCaseDecisionStorageCleanupError) {
      return { ...verified, cleanup: 'failed' };
    }

    throw error;
  }
}

async function refuse(response: Response, attempt: ReviewCaseDecisionAttempt, epoch: number): Promise<never> {
  let text = '';
  let bodyRead = true;
  try {
    text = await response.text();
  } catch {
    bodyRead = false;
  }

  if (ownAttempt(attempt, epoch) !== 'owned') {
    throw ownAttempt(attempt, epoch) === 'stale' ? new StaleSessionError() : new ReviewCaseDecisionAttemptChangedError();
  }

  const error = toApiError(response.status, text);
  const conflict = conflictCode(error, bodyRead);
  if (conflict !== null) {
    if (attempt.uncertaintyLocked) {
      throw new ReviewCaseDecisionUncertaintyRemainsError();
    }

    throw new ReviewCaseDecisionConflictError(conflict);
  }

  if (isControlledRejection(error, bodyRead)) {
    if (attempt.uncertaintyLocked) {
      throw new ReviewCaseDecisionUncertaintyRemainsError();
    }

    replaceAttempt(attempt, { ...attempt, lifecycle: 'refused', uncertaintyLocked: false });
    throw new ReviewCaseDecisionRefusedError(error.status, error.code);
  }

  rememberUnknown(attempt, epoch);
  if (attempt.uncertaintyLocked) {
    throw new ReviewCaseDecisionUncertaintyRemainsError();
  }

  throw new UnknownReviewCaseDecisionOutcomeError();
}

function conflictCode(
  error: ApiError,
  bodyRead: boolean,
): ReviewCaseDecisionConflictError['code'] | null {
  if (!bodyRead || error.status !== 409) {
    return null;
  }

  if (error.code === 'reconciliation.review_case.concurrency_conflict'
    || error.code === 'reconciliation.review_case.terminal_conflict') {
    return error.code;
  }

  return null;
}

function isControlledRejection(error: ApiError, bodyRead: boolean): boolean {
  if (error.status === 401 || error.status === 403 || error.status === 413 || error.status === 415) {
    return true;
  }

  if (!bodyRead) {
    return false;
  }

  if (error.status === 400) {
    return error.code === 'reconciliation.review_case.invalid_request'
      || error.code === 'reconciliation.review_case.invalid_concurrency_token'
      || error.code === 'reconciliation.review_case.allocation_required'
      || error.code === 'reconciliation.review_case.allocation_forbidden';
  }

  if (error.status === 404) {
    return error.code === 'reconciliation.review_case.not_found';
  }

  if (error.status === 409) {
    return error.code === 'reconciliation.review_case.allocation_mismatch';
  }

  return false;
}

function markDispatch(attempt: ReviewCaseDecisionAttempt): ReviewCaseDecisionAttempt {
  if (attempt.lifecycle === 'sending' || attempt.lifecycle === 'uncertain') {
    return replaceAttempt(attempt, { ...attempt, lifecycle: 'uncertain', uncertaintyLocked: true });
  }

  return replaceAttempt(attempt, { ...attempt, lifecycle: 'sending', uncertaintyLocked: false });
}

function rememberUnknown(attempt: ReviewCaseDecisionAttempt, epoch: number): void {
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
  attempt: ReviewCaseDecisionAttempt,
  epoch: number,
): Omit<VerifiedReviewCaseDecision, 'cleanup'> {
  if (!isRecord(value)) {
    throw new UnreadableReviewCaseDecisionError();
  }

  const reviewCaseId = readGuid(value.reviewCaseId);
  const status = readEnum(value.status, ['Open', 'Resolved', 'Dismissed'] as const);
  const outcome = readNullableEnum(value.outcome, outcomes);
  const allocationId = readNullableGuid(value.allocationId);
  const resolutionNote = readNullableText(value.resolutionNote);
  const openedAt = readRequiredTimestamp(value.openedAt);
  const closedAt = readNullableTimestamp(value.closedAt);
  const concurrencyToken = readReviewCaseConcurrencyToken(value.concurrencyToken);
  if (reviewCaseId === null
    || status === null
    || concurrencyToken === null
    || reviewCaseId !== attempt.caseId
    || resolutionNote !== attempt.resolutionNote
    || !readOptionalCaseShape(value)) {
    throw new UnreadableReviewCaseDecisionError();
  }

  if (attempt.operation === 'ReviewCaseResolve') {
    if (status !== 'Resolved'
      || outcome !== attempt.outcome
      || allocationId !== attempt.allocationId
      || closedAt === null
      || openedAt.length === 0) {
      throw new UnreadableReviewCaseDecisionError();
    }
  } else if (status !== 'Dismissed' || outcome !== null || allocationId !== null || closedAt === null) {
    throw new UnreadableReviewCaseDecisionError();
  }

  return {
    reviewCaseId,
    operation: attempt.operation,
    status,
    outcome,
    allocationId,
    resolutionNote,
    epoch,
  };
}

function readOptionalCaseShape(value: Record<string, unknown>): boolean {
  const reason = readEnum(value.reason, ['CompetingMatch', 'NeedsInvestigation'] as const);
  const sourceLabel = value.sourceLabel === null
    ? null
    : readEnum(value.sourceLabel, ['manual', 'exact-1', 'smart-1'] as const);
  return reason !== null && (value.sourceLabel === null || sourceLabel !== null);
}

function ownAttempt(attempt: ReviewCaseDecisionAttempt, epoch: number): 'owned' | 'stale' | 'changed' {
  const session = sessionStore.getSnapshot();
  const organizationId = developmentOrganizationId(session.token);
  if (session.epoch !== epoch || organizationId !== attempt.organizationId) {
    return 'stale';
  }

  const stored = readStoredAttempt();
  if (stored.status !== 'present'
    || stored.attempt.operation !== attempt.operation
    || stored.attempt.caseId !== attempt.caseId
    || stored.attempt.requestBody !== attempt.requestBody) {
    return 'changed';
  }

  return 'owned';
}

function replaceAttempt(previous: ReviewCaseDecisionAttempt, next: ReviewCaseDecisionAttempt): ReviewCaseDecisionAttempt {
  const stored = readStoredAttempt();
  if (stored.status !== 'present'
    || stored.attempt.operation !== previous.operation
    || stored.attempt.caseId !== previous.caseId
    || stored.attempt.requestBody !== previous.requestBody) {
    throw new ReviewCaseDecisionAttemptChangedError();
  }

  persistAttempt(next);
  return readBack(next);
}

function persistAttempt(attempt: ReviewCaseDecisionAttempt): void {
  const serialized = JSON.stringify(attempt);
  try {
    sessionStorage.setItem(reviewCaseDecisionAttemptStorageKey, serialized);
    if (sessionStorage.getItem(reviewCaseDecisionAttemptStorageKey) !== serialized) {
      throw new ReviewCaseDecisionStorageError();
    }
  } catch (error) {
    if (error instanceof ReviewCaseDecisionStorageError) {
      throw error;
    }

    throw new ReviewCaseDecisionStorageError();
  }

  readBack(attempt);
  publish();
}

function readBack(attempt: ReviewCaseDecisionAttempt): ReviewCaseDecisionAttempt {
  const stored = readStoredAttempt();
  if (stored.status !== 'present'
    || stored.attempt.operation !== attempt.operation
    || stored.attempt.caseId !== attempt.caseId
    || stored.attempt.requestBody !== attempt.requestBody
    || stored.attempt.lifecycle !== attempt.lifecycle
    || stored.attempt.uncertaintyLocked !== attempt.uncertaintyLocked) {
    throw new ReviewCaseDecisionStorageError();
  }

  return stored.attempt;
}

function removeOwned(attempt: ReviewCaseDecisionAttempt): void {
  const stored = readStoredAttempt();
  if (stored.status !== 'present'
    || stored.attempt.operation !== attempt.operation
    || stored.attempt.caseId !== attempt.caseId
    || stored.attempt.requestBody !== attempt.requestBody) {
    throw new ReviewCaseDecisionAttemptChangedError();
  }

  removeKey();
}

function removeKey(): void {
  try {
    sessionStorage.removeItem(reviewCaseDecisionAttemptStorageKey);
  } catch {
    throw new ReviewCaseDecisionStorageCleanupError();
  }

  let remaining: string | null;
  try {
    remaining = sessionStorage.getItem(reviewCaseDecisionAttemptStorageKey);
  } catch {
    throw new ReviewCaseDecisionStorageCleanupError();
  }

  if (remaining !== null) {
    throw new ReviewCaseDecisionStorageCleanupError();
  }

  publish();
}

function raiseDecisionReadBoundary(): void {
  freshReadAfter = startedReviewCaseDetailReadCount();
  publish();
}

function readStoredAttempt():
  | { status: 'empty' }
  | { status: 'invalid' }
  | { status: 'present'; attempt: ReviewCaseDecisionAttempt } {
  let raw: string | null;
  try {
    raw = sessionStorage.getItem(reviewCaseDecisionAttemptStorageKey);
  } catch {
    return { status: 'invalid' };
  }

  if (raw === null) {
    return { status: 'empty' };
  }

  try {
    const parsed: unknown = JSON.parse(raw);
    const attempt = parseAttempt(parsed);
    if (attempt === null || attempt.requestBody !== serializeReviewCaseDecisionBody(attempt)) {
      return { status: 'invalid' };
    }

    return { status: 'present', attempt };
  } catch {
    return { status: 'invalid' };
  }
}

function parseAttempt(value: unknown): ReviewCaseDecisionAttempt | null {
  if (!isRecord(value)
    || value.schemaVersion !== schemaVersion
    || !operations.includes(value.operation as ReviewCaseDecisionOperation)
    || typeof value.requestBody !== 'string'
    || (value.lifecycle !== 'ready' && value.lifecycle !== 'sending' && value.lifecycle !== 'uncertain' && value.lifecycle !== 'refused')
    || typeof value.uncertaintyLocked !== 'boolean'
    || typeof value.organizationId !== 'string'
    || !isExactGuid(value.organizationId)
    || typeof value.caseId !== 'string'
    || !isExactGuid(value.caseId)
    || typeof value.concurrencyToken !== 'string'
    || readReviewCaseConcurrencyToken(value.concurrencyToken) !== value.concurrencyToken) {
    return null;
  }

  const concurrencyToken = value.concurrencyToken;

  const operation = value.operation as ReviewCaseDecisionOperation;
  const outcome = value.outcome === null ? null : readEnum(value.outcome, outcomes);
  const allocationId = value.allocationId === null ? null : readGuid(value.allocationId);
  const resolutionNote = readStoredNote(value.resolutionNote);
  if (value.outcome !== null && outcome === null) {
    return null;
  }

  if (value.allocationId !== null && allocationId === null) {
    return null;
  }

  if (resolutionNote === undefined) {
    return null;
  }

  if (operation === 'ReviewCaseDismiss') {
    if (outcome !== null || allocationId !== null) {
      return null;
    }
  } else if (outcome === null || (outcome === 'AllocationRecorded') !== (allocationId !== null)) {
    return null;
  }

  return {
    schemaVersion,
    operation,
    organizationId: value.organizationId,
    caseId: value.caseId,
    requestBody: value.requestBody,
    outcome,
    allocationId,
    resolutionNote,
    concurrencyToken,
    lifecycle: value.lifecycle,
    uncertaintyLocked: value.uncertaintyLocked,
  };
}

function readStoredNote(value: unknown): string | null | undefined {
  if (value === null) {
    return null;
  }

  if (typeof value !== 'string' || isDotNetNullOrWhiteSpace(value) || value !== trimDotNetWhiteSpace(value) || value.length > 500) {
    return undefined;
  }

  return value;
}

function decisionUrl(attempt: ReviewCaseDecisionAttempt): string {
  const action = attempt.operation === 'ReviewCaseResolve' ? 'resolve' : 'dismiss';
  return `/api/review-cases/${attempt.caseId}/${action}`;
}

function requireOrganization(session: SessionSnapshot): string {
  const organizationId = developmentOrganizationId(session.token);
  if (session.token === null || organizationId === null) {
    throw new ReviewCaseDecisionAuthenticationError();
  }

  return organizationId;
}

function requireActor(session: SessionSnapshot): void {
  if (developmentActorId(session.token) === null) {
    throw new ReviewCaseDecisionActorRequiredError();
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

function getFreshReadAfter(): number {
  return freshReadAfter;
}

function publish(): void {
  storageVersion += 1;
  listeners.forEach((listener) => listener());
}

function readEnum<T extends string>(value: unknown, allowed: readonly T[]): T | null {
  return typeof value === 'string' && allowed.includes(value as T) ? value as T : null;
}

function readNullableEnum<T extends string>(value: unknown, allowed: readonly T[]): T | null {
  if (value === null) {
    return null;
  }

  const parsed = readEnum(value, allowed);
  if (parsed === null) {
    throw new UnreadableReviewCaseDecisionError();
  }

  return parsed;
}

function readNullableText(value: unknown): string | null {
  if (value === null) {
    return null;
  }

  if (typeof value !== 'string') {
    throw new UnreadableReviewCaseDecisionError();
  }

  return value;
}

function readNullableGuid(value: unknown): string | null {
  if (value === null) {
    return null;
  }

  const parsed = readGuid(value);
  if (parsed === null) {
    throw new UnreadableReviewCaseDecisionError();
  }

  return parsed;
}

function readGuid(value: unknown): string | null {
  return typeof value === 'string' && isExactGuid(value) ? value.toLowerCase() : null;
}

function readRequiredTimestamp(value: unknown): string {
  if (!isValidTimestamp(value)) {
    throw new UnreadableReviewCaseDecisionError();
  }

  return value;
}

function readNullableTimestamp(value: unknown): string | null {
  if (value === null) {
    return null;
  }

  if (!isValidTimestamp(value)) {
    throw new UnreadableReviewCaseDecisionError();
  }

  return value;
}

function isValidTimestamp(value: unknown): value is string {
  if (typeof value !== 'string') {
    return false;
  }

  const match = timestampPattern.exec(value);
  if (match === null) {
    return false;
  }

  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  const hour = Number(match[4]);
  const minute = Number(match[5]);
  const second = Number(match[6]);
  if (hour > 23 || minute > 59 || second > 59 || !isRealCalendarDay(year, month, day)) {
    return false;
  }

  if (match[7] === 'Z') {
    return true;
  }

  const offsetHour = Number(match[8]);
  const offsetMinute = Number(match[9]);
  return offsetHour <= 23 && offsetMinute <= 59;
}

function isRealCalendarDay(year: number, month: number, day: number): boolean {
  if (month < 1 || month > 12 || day < 1) {
    return false;
  }

  const lengths = [31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
  const length = month === 2 && isLeapYear(year) ? 29 : lengths[month - 1];
  return day <= length;
}

function isLeapYear(year: number): boolean {
  return year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value) && !isLosslessNumber(value);
}
