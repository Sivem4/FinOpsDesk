import { parse, isLosslessNumber } from 'lossless-json';
import { useSyncExternalStore } from 'react';

import { authenticationHeaders, rejectCookieFinancialRequest } from '../../shared/api/authentication';
import { ApiError, isAbortError, StaleSessionError, UnexpectedResponseError } from '../../shared/api/errors';
import { toApiError } from '../../shared/api/problem';
import { sessionStore, type SessionSnapshot } from '../../shared/api/session-store';
import { useSession } from '../../shared/auth/session';
import {
  developmentActorId,
  developmentOrganizationId,
  isDotNetNullOrWhiteSpace,
  isExactGuid,
  serializeReviewCaseOpenBody,
  trimDotNetWhiteSpace,
  type NormalizedReviewCaseOpen,
  type ReviewCaseOpenReason,
  type ReviewCaseOpenSource,
} from './review-case-open-request';

export const reviewCaseOpenAttemptStorageKey = 'finopsdesk.review-case-open-attempt';

const schemaVersion = 1;
const operationType = 'ReviewCaseOpen';
const timestampPattern = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.\d+)?(Z|[+-](\d{2}):(\d{2}))$/;
const outcomes = ['AllocationRecorded', 'NoActionRequired', 'HandledOutside'] as const;
const statuses = ['Open', 'Resolved', 'Dismissed'] as const;

let submissionInFlight = false;
let storageVersion = 0;
const listeners = new Set<() => void>();

export class ReviewCaseOpenStorageError extends Error {
  constructor() {
    super('The review case opening could not be stored.');
    this.name = 'ReviewCaseOpenStorageError';
  }
}

export class ReviewCaseOpenStorageCleanupError extends Error {
  constructor() {
    super('The verified opening could not be cleared from this browser tab.');
    this.name = 'ReviewCaseOpenStorageCleanupError';
  }
}

export class ReviewCaseOpenAttemptBlockedError extends Error {
  constructor() {
    super('The stored review case opening blocks a new opening.');
    this.name = 'ReviewCaseOpenAttemptBlockedError';
  }
}

export class UnresolvedReviewCaseOpenError extends Error {
  constructor() {
    super('An unresolved review case opening is already stored.');
    this.name = 'UnresolvedReviewCaseOpenError';
  }
}

export class ReviewCaseOpenAuthenticationError extends Error {
  constructor() {
    super('Sign in before opening a review case.');
    this.name = 'ReviewCaseOpenAuthenticationError';
  }
}

export class ReviewCaseOpenActorRequiredError extends Error {
  constructor() {
    super('This opening needs an operator identity.');
    this.name = 'ReviewCaseOpenActorRequiredError';
  }
}

export class ReviewCaseOpenTenantMismatchError extends Error {
  constructor() {
    super('The stored opening belongs to another organization.');
    this.name = 'ReviewCaseOpenTenantMismatchError';
  }
}

export class ReviewCaseOpenSubmissionInProgressError extends Error {
  constructor() {
    super('A review case opening is already being sent.');
    this.name = 'ReviewCaseOpenSubmissionInProgressError';
  }
}

export class ReviewCaseOpenAttemptChangedError extends Error {
  constructor() {
    super('The stored opening changed before the response could be applied.');
    this.name = 'ReviewCaseOpenAttemptChangedError';
  }
}

export class UnknownReviewCaseOpenOutcomeError extends Error {
  constructor() {
    super('The opening was not verified.');
    this.name = 'UnknownReviewCaseOpenOutcomeError';
  }
}

export class UnreadableReviewCaseOpenError extends Error {
  constructor() {
    super('The opening response could not be verified.');
    this.name = 'UnreadableReviewCaseOpenError';
  }
}

export class ReviewCaseOpenPayloadConflictError extends Error {
  constructor() {
    super('This idempotency key was already used for a different opening.');
    this.name = 'ReviewCaseOpenPayloadConflictError';
  }
}

export class ReviewCaseOpenRejectedError extends Error {
  readonly status: number;
  readonly code: string | null;

  constructor(status: number, code: string | null) {
    super('The server refused this opening before saving it.');
    this.name = 'ReviewCaseOpenRejectedError';
    this.status = status;
    this.code = code;
  }
}

export class ReviewCaseOpenUncertaintyRemainsError extends Error {
  constructor() {
    super('A later refusal did not clear the unresolved opening.');
    this.name = 'ReviewCaseOpenUncertaintyRemainsError';
  }
}

export interface ReviewCaseOpenAttempt extends NormalizedReviewCaseOpen {
  schemaVersion: 1;
  operation: 'ReviewCaseOpen';
  organizationId: string;
  idempotencyKey: string;
  requestBody: string;
  lifecycle: 'ready' | 'sending' | 'uncertain' | 'rejected';
  uncertaintyLocked: boolean;
}

export interface VerifiedReviewCaseOpen {
  reviewCaseId: string;
  status: 'Open' | 'Resolved' | 'Dismissed';
  created: boolean;
  reason: ReviewCaseOpenReason;
  invoiceId: string | null;
  bankTransactionId: string | null;
  sourceLabel: ReviewCaseOpenSource | null;
  note: string | null;
  outcome: (typeof outcomes)[number] | null;
  resolutionNote: string | null;
  allocationId: string | null;
  openedAt: string;
  closedAt: string | null;
  epoch: number;
  cleanup: 'removed' | 'failed';
}

export type ReviewCaseOpenView =
  | { status: 'none' }
  | { status: 'blocked-authentication' }
  | { status: 'blocked-tenant' }
  | { status: 'invalid-storage' }
  | { status: 'pending'; attempt: ReviewCaseOpenAttempt };

export function useReviewCaseOpenAttempt(): ReviewCaseOpenView {
  const session = useSession();
  useSyncExternalStore(subscribe, getStorageVersion, getStorageVersion);
  return inspectReviewCaseOpenAttempt(session);
}

export function inspectReviewCaseOpenAttempt(session: SessionSnapshot = sessionStore.getSnapshot()): ReviewCaseOpenView {
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

export function beginReviewCaseOpenAttempt(value: NormalizedReviewCaseOpen): ReviewCaseOpenAttempt {
  rejectDevelopmentCookie();
  if (submissionInFlight) {
    throw new ReviewCaseOpenSubmissionInProgressError();
  }

  const existing = readStoredAttempt();
  if (existing.status === 'invalid') {
    throw new ReviewCaseOpenAttemptBlockedError();
  }

  if (existing.status === 'present') {
    throw new UnresolvedReviewCaseOpenError();
  }

  const session = sessionStore.getSnapshot();
  const organizationId = requireOrganization(session);
  requireActor(session);
  const idempotencyKey = crypto.randomUUID();
  if (!isExactGuid(idempotencyKey)) {
    throw new ReviewCaseOpenStorageError();
  }

  const attempt: ReviewCaseOpenAttempt = {
    schemaVersion,
    operation: operationType,
    organizationId,
    idempotencyKey,
    requestBody: serializeReviewCaseOpenBody(value),
    reason: value.reason,
    invoiceId: value.invoiceId,
    bankTransactionId: value.bankTransactionId,
    sourceLabel: value.sourceLabel,
    note: value.note,
    lifecycle: 'ready',
    uncertaintyLocked: false,
  };
  if (attempt.requestBody !== serializeReviewCaseOpenBody(attempt)) {
    throw new ReviewCaseOpenStorageError();
  }

  persistAttempt(attempt);
  return readBack(attempt);
}

export async function submitReviewCaseOpenAttempt(signal?: AbortSignal): Promise<VerifiedReviewCaseOpen> {
  rejectDevelopmentCookie();
  if (submissionInFlight) {
    throw new ReviewCaseOpenSubmissionInProgressError();
  }

  const session = sessionStore.getSnapshot();
  const organizationId = requireOrganization(session);
  requireActor(session);
  const stored = readStoredAttempt();
  if (stored.status !== 'present') {
    throw new ReviewCaseOpenAttemptBlockedError();
  }

  if (organizationId !== stored.attempt.organizationId) {
    throw new ReviewCaseOpenTenantMismatchError();
  }

  if (stored.attempt.lifecycle === 'rejected') {
    throw new ReviewCaseOpenRejectedError(0, null);
  }

  submissionInFlight = true;
  const epoch = session.epoch;
  try {
    const prepared = markDispatch(stored.attempt);
    let response: Response;
    try {
      response = await fetch('/api/review-cases', {
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
        throw new UnknownReviewCaseOpenOutcomeError();
      }

      throw error;
    }

    return await classifyResponse(response, prepared, epoch);
  } finally {
    submissionInFlight = false;
  }
}

export function abandonReviewCaseOpenAttempt(idempotencyKey: string, requestBody: string): void {
  rejectDevelopmentCookie();
  if (submissionInFlight) {
    throw new ReviewCaseOpenSubmissionInProgressError();
  }

  const stored = readStoredAttempt();
  if (stored.status !== 'present'
    || stored.attempt.idempotencyKey !== idempotencyKey
    || stored.attempt.requestBody !== requestBody) {
    throw new ReviewCaseOpenAttemptChangedError();
  }

  const organizationId = developmentOrganizationId(sessionStore.getSnapshot().token);
  if (organizationId === null) {
    throw new ReviewCaseOpenAuthenticationError();
  }

  if (organizationId !== stored.attempt.organizationId) {
    throw new ReviewCaseOpenTenantMismatchError();
  }

  removeOwned(stored.attempt);
}

export function abandonUnreadableReviewCaseOpenAttempt(): void {
  rejectDevelopmentCookie();
  if (submissionInFlight) {
    throw new ReviewCaseOpenSubmissionInProgressError();
  }

  if (readStoredAttempt().status !== 'invalid') {
    throw new ReviewCaseOpenAttemptChangedError();
  }

  removeKey();
}

export function resetReviewCaseOpenAttemptStateForTests(): void {
  submissionInFlight = false;
  storageVersion += 1;
}

async function classifyResponse(response: Response, attempt: ReviewCaseOpenAttempt, epoch: number): Promise<VerifiedReviewCaseOpen> {
  const ownership = ownAttempt(attempt, epoch);
  if (ownership !== 'owned') {
    throw ownership === 'stale' ? new StaleSessionError() : new ReviewCaseOpenAttemptChangedError();
  }

  if (response.status !== 200 && response.status !== 201) {
    return refuse(response, attempt, epoch);
  }

  let text: string;
  try {
    text = await response.text();
  } catch {
    rememberUnknown(attempt, epoch);
    throw new UnreadableReviewCaseOpenError();
  }

  if (ownAttempt(attempt, epoch) !== 'owned') {
    throw ownAttempt(attempt, epoch) === 'stale' ? new StaleSessionError() : new ReviewCaseOpenAttemptChangedError();
  }

  let verified: Omit<VerifiedReviewCaseOpen, 'cleanup'>;
  try {
    verified = mapVerified(parse(text), response, attempt, epoch);
  } catch (error) {
    if (error instanceof StaleSessionError || error instanceof ReviewCaseOpenAttemptChangedError) {
      throw error;
    }

    rememberUnknown(attempt, epoch);
    throw new UnreadableReviewCaseOpenError();
  }

  if (ownAttempt(attempt, epoch) !== 'owned') {
    throw ownAttempt(attempt, epoch) === 'stale' ? new StaleSessionError() : new ReviewCaseOpenAttemptChangedError();
  }

  try {
    removeOwned(attempt);
    return { ...verified, cleanup: 'removed' };
  } catch (error) {
    if (error instanceof ReviewCaseOpenStorageCleanupError) {
      return { ...verified, cleanup: 'failed' };
    }

    throw error;
  }
}

async function refuse(response: Response, attempt: ReviewCaseOpenAttempt, epoch: number): Promise<never> {
  let text = '';
  let bodyRead = true;
  try {
    text = await response.text();
  } catch {
    bodyRead = false;
  }

  if (ownAttempt(attempt, epoch) !== 'owned') {
    throw ownAttempt(attempt, epoch) === 'stale' ? new StaleSessionError() : new ReviewCaseOpenAttemptChangedError();
  }

  const error = toApiError(response.status, text);
  if (error.code === 'reconciliation.review_case.idempotency_payload_conflict') {
    throw new ReviewCaseOpenPayloadConflictError();
  }

  if (isControlledRejection(error, bodyRead)) {
    if (attempt.uncertaintyLocked) {
      throw new ReviewCaseOpenUncertaintyRemainsError();
    }

    replaceAttempt(attempt, { ...attempt, lifecycle: 'rejected', uncertaintyLocked: false });
    throw new ReviewCaseOpenRejectedError(error.status, error.code);
  }

  if (response.status >= 500 || error.code === 'reconciliation.review_case.concurrency_conflict' || text === '') {
    rememberUnknown(attempt, epoch);
    throw new UnknownReviewCaseOpenOutcomeError();
  }

  if (attempt.uncertaintyLocked) {
    throw new ReviewCaseOpenUncertaintyRemainsError();
  }

  rememberUnknown(attempt, epoch);
  throw new UnknownReviewCaseOpenOutcomeError();
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
      || error.code === 'reconciliation.review_case.idempotency_key_invalid';
  }

  if (error.status === 404) {
    return error.code === 'reconciliation.invoice.not_found'
      || error.code === 'reconciliation.bank_transaction.not_found';
  }

  if (error.status === 409) {
    return error.code === 'reconciliation.review_case.open_request_conflict'
      || error.code === 'reconciliation.review_case.subject_conflict';
  }

  return false;
}

function markDispatch(attempt: ReviewCaseOpenAttempt): ReviewCaseOpenAttempt {
  if (attempt.lifecycle === 'sending' || attempt.lifecycle === 'uncertain') {
    return replaceAttempt(attempt, { ...attempt, lifecycle: 'uncertain', uncertaintyLocked: true });
  }

  return replaceAttempt(attempt, { ...attempt, lifecycle: 'sending', uncertaintyLocked: false });
}

function rememberUnknown(attempt: ReviewCaseOpenAttempt, epoch: number): void {
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
  attempt: ReviewCaseOpenAttempt,
  epoch: number,
): Omit<VerifiedReviewCaseOpen, 'cleanup'> {
  if (!isRecord(value)) {
    throw new UnreadableReviewCaseOpenError();
  }

  const reviewCaseId = readGuid(value.reviewCaseId);
  const reason = readEnum(value.reason, ['CompetingMatch', 'NeedsInvestigation'] as const);
  const status = readEnum(value.status, statuses);
  const invoiceId = readNullableGuid(value.invoiceId);
  const bankTransactionId = readNullableGuid(value.bankTransactionId);
  const sourceLabel = readNullableEnum(value.sourceLabel, ['manual', 'exact-1', 'smart-1'] as const);
  const note = readNullableText(value.note);
  const outcome = readNullableEnum(value.outcome, outcomes);
  const resolutionNote = readNullableText(value.resolutionNote);
  const allocationId = readNullableGuid(value.allocationId);
  const openedAt = readRequiredTimestamp(value.openedAt);
  const closedAt = readNullableTimestamp(value.closedAt);
  const concurrencyToken = value.concurrencyToken;
  if (reviewCaseId === null
    || reason === null
    || status === null
    || typeof concurrencyToken !== 'string'
    || isDotNetNullOrWhiteSpace(concurrencyToken)
    || reason !== attempt.reason
    || invoiceId !== attempt.invoiceId
    || bankTransactionId !== attempt.bankTransactionId
    || sourceLabel !== attempt.sourceLabel
    || note !== attempt.note) {
    throw new UnreadableReviewCaseOpenError();
  }

  if (!statusShape(status, outcome, resolutionNote, allocationId, closedAt)) {
    throw new UnreadableReviewCaseOpenError();
  }

  if (response.status === 201) {
    if (status !== 'Open' || !locationMatches(response, reviewCaseId)) {
      throw new UnreadableReviewCaseOpenError();
    }
  } else if (response.status !== 200) {
    throw new UnreadableReviewCaseOpenError();
  }

  return {
    reviewCaseId,
    status,
    created: response.status === 201,
    reason,
    invoiceId,
    bankTransactionId,
    sourceLabel,
    note,
    outcome,
    resolutionNote,
    allocationId,
    openedAt,
    closedAt,
    epoch,
  };
}

function statusShape(
  status: 'Open' | 'Resolved' | 'Dismissed',
  outcome: (typeof outcomes)[number] | null,
  resolutionNote: string | null,
  allocationId: string | null,
  closedAt: string | null,
): boolean {
  if (resolutionNote === undefined) {
    return false;
  }

  if (status === 'Open') {
    return outcome === null && allocationId === null && closedAt === null && resolutionNote === null;
  }

  if (closedAt === null) {
    return false;
  }

  if (status === 'Dismissed') {
    return outcome === null && allocationId === null;
  }

  if (outcome === null) {
    return false;
  }

  return outcome === 'AllocationRecorded' ? allocationId !== null : allocationId === null;
}

function locationMatches(response: Response, reviewCaseId: string): boolean {
  const location = response.headers.get('Location');
  if (location === null) {
    return false;
  }

  try {
    const path = new URL(location, 'http://localhost').pathname;
    return path.toLowerCase() === `/api/review-cases/${reviewCaseId.toLowerCase()}`;
  } catch {
    return false;
  }
}

function ownAttempt(attempt: ReviewCaseOpenAttempt, epoch: number): 'owned' | 'stale' | 'changed' {
  const session = sessionStore.getSnapshot();
  const organizationId = developmentOrganizationId(session.token);
  if (session.epoch !== epoch || organizationId !== attempt.organizationId) {
    return 'stale';
  }

  const stored = readStoredAttempt();
  if (stored.status !== 'present'
    || stored.attempt.idempotencyKey !== attempt.idempotencyKey
    || stored.attempt.requestBody !== attempt.requestBody) {
    return 'changed';
  }

  return 'owned';
}

function replaceAttempt(previous: ReviewCaseOpenAttempt, next: ReviewCaseOpenAttempt): ReviewCaseOpenAttempt {
  const stored = readStoredAttempt();
  if (stored.status !== 'present'
    || stored.attempt.idempotencyKey !== previous.idempotencyKey
    || stored.attempt.requestBody !== previous.requestBody) {
    throw new ReviewCaseOpenAttemptChangedError();
  }

  persistAttempt(next);
  return readBack(next);
}

function persistAttempt(attempt: ReviewCaseOpenAttempt): void {
  const serialized = JSON.stringify(attempt);
  try {
    sessionStorage.setItem(reviewCaseOpenAttemptStorageKey, serialized);
    if (sessionStorage.getItem(reviewCaseOpenAttemptStorageKey) !== serialized) {
      throw new ReviewCaseOpenStorageError();
    }
  } catch (error) {
    if (error instanceof ReviewCaseOpenStorageError) {
      throw error;
    }

    throw new ReviewCaseOpenStorageError();
  }

  readBack(attempt);
  publish();
}

function readBack(attempt: ReviewCaseOpenAttempt): ReviewCaseOpenAttempt {
  const stored = readStoredAttempt();
  if (stored.status !== 'present'
    || stored.attempt.idempotencyKey !== attempt.idempotencyKey
    || stored.attempt.requestBody !== attempt.requestBody
    || stored.attempt.lifecycle !== attempt.lifecycle
    || stored.attempt.uncertaintyLocked !== attempt.uncertaintyLocked) {
    throw new ReviewCaseOpenStorageError();
  }

  return stored.attempt;
}

function removeOwned(attempt: ReviewCaseOpenAttempt): void {
  const stored = readStoredAttempt();
  if (stored.status !== 'present'
    || stored.attempt.idempotencyKey !== attempt.idempotencyKey
    || stored.attempt.requestBody !== attempt.requestBody) {
    throw new ReviewCaseOpenAttemptChangedError();
  }

  removeKey();
}

function removeKey(): void {
  try {
    sessionStorage.removeItem(reviewCaseOpenAttemptStorageKey);
  } catch {
    throw new ReviewCaseOpenStorageCleanupError();
  }

  let remaining: string | null;
  try {
    remaining = sessionStorage.getItem(reviewCaseOpenAttemptStorageKey);
  } catch {
    throw new ReviewCaseOpenStorageCleanupError();
  }

  if (remaining !== null) {
    throw new ReviewCaseOpenStorageCleanupError();
  }

  publish();
}

function readStoredAttempt(): { status: 'empty' } | { status: 'invalid' } | { status: 'present'; attempt: ReviewCaseOpenAttempt } {
  let raw: string | null;
  try {
    raw = sessionStorage.getItem(reviewCaseOpenAttemptStorageKey);
  } catch {
    return { status: 'invalid' };
  }

  if (raw === null) {
    return { status: 'empty' };
  }

  try {
    const parsed: unknown = JSON.parse(raw);
    const attempt = parseAttempt(parsed);
    if (attempt === null || attempt.requestBody !== serializeReviewCaseOpenBody(attempt)) {
      return { status: 'invalid' };
    }

    return { status: 'present', attempt };
  } catch {
    return { status: 'invalid' };
  }
}

function parseAttempt(value: unknown): ReviewCaseOpenAttempt | null {
  if (!isRecord(value)
    || value.schemaVersion !== schemaVersion
    || value.operation !== operationType
    || typeof value.requestBody !== 'string'
    || (value.lifecycle !== 'ready' && value.lifecycle !== 'sending' && value.lifecycle !== 'uncertain' && value.lifecycle !== 'rejected')
    || typeof value.uncertaintyLocked !== 'boolean'
    || typeof value.organizationId !== 'string'
    || !isExactGuid(value.organizationId)
    || typeof value.idempotencyKey !== 'string'
    || !isExactGuid(value.idempotencyKey)) {
    return null;
  }

  const reason = readEnum(value.reason, ['CompetingMatch', 'NeedsInvestigation'] as const);
  const invoiceId = value.invoiceId === null ? null : readGuid(value.invoiceId);
  const bankTransactionId = value.bankTransactionId === null ? null : readGuid(value.bankTransactionId);
  const sourceLabel = value.sourceLabel === null ? null : readEnum(value.sourceLabel, ['manual', 'exact-1', 'smart-1'] as const);
  const note = value.note === null ? null : readStoredNote(value.note);
  if (reason === null || (value.invoiceId !== null && invoiceId === null) || (value.bankTransactionId !== null && bankTransactionId === null)) {
    return null;
  }

  if ((value.sourceLabel !== null && sourceLabel === null) || (value.note !== null && note === null)) {
    return null;
  }

  const invoiceNamed = invoiceId !== null;
  const bankNamed = bankTransactionId !== null;
  if (reason === 'CompetingMatch' && (!invoiceNamed || !bankNamed)) {
    return null;
  }

  if (reason === 'NeedsInvestigation' && invoiceNamed === bankNamed) {
    return null;
  }

  return {
    schemaVersion,
    operation: operationType,
    organizationId: value.organizationId,
    idempotencyKey: value.idempotencyKey,
    requestBody: value.requestBody,
    reason,
    invoiceId,
    bankTransactionId,
    sourceLabel,
    note,
    lifecycle: value.lifecycle,
    uncertaintyLocked: value.uncertaintyLocked,
  };
}

function readStoredNote(value: unknown): string | null {
  if (typeof value !== 'string' || value.length === 0 || value.length > 500 || trimDotNetWhiteSpace(value) !== value) {
    return null;
  }

  return value;
}

function requireOrganization(session: SessionSnapshot): string {
  const organizationId = developmentOrganizationId(session.token);
  if (session.token === null || organizationId === null) {
    throw new ReviewCaseOpenAuthenticationError();
  }

  return organizationId;
}

function requireActor(session: SessionSnapshot): void {
  if (developmentActorId(session.token) === null) {
    throw new ReviewCaseOpenActorRequiredError();
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

function readEnum<T extends string>(value: unknown, allowed: readonly T[]): T | null {
  return typeof value === 'string' && allowed.includes(value as T) ? value as T : null;
}

function readNullableEnum<T extends string>(value: unknown, allowed: readonly T[]): T | null {
  if (value === null) {
    return null;
  }

  const parsed = readEnum(value, allowed);
  if (parsed === null) {
    throw new UnexpectedResponseError('The review case response could not be read.');
  }

  return parsed;
}

function readNullableText(value: unknown): string | null {
  if (value === null) {
    return null;
  }

  if (typeof value !== 'string') {
    throw new UnexpectedResponseError('The review case response could not be read.');
  }

  return value;
}

function readNullableGuid(value: unknown): string | null {
  if (value === null) {
    return null;
  }

  const parsed = readGuid(value);
  if (parsed === null) {
    throw new UnexpectedResponseError('The review case response could not be read.');
  }

  return parsed;
}

function readGuid(value: unknown): string | null {
  return typeof value === 'string' && isExactGuid(value) ? value.toLowerCase() : null;
}

function readRequiredTimestamp(value: unknown): string {
  if (!isValidTimestamp(value)) {
    throw new UnreadableReviewCaseOpenError();
  }

  return value;
}

function readNullableTimestamp(value: unknown): string | null {
  if (value === null) {
    return null;
  }

  if (!isValidTimestamp(value)) {
    throw new UnreadableReviewCaseOpenError();
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
