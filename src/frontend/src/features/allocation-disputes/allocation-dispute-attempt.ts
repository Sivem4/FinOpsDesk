import { useSyncExternalStore } from 'react';

import { authenticationHeaders, rejectCookieFinancialRequest } from '../../shared/api/authentication';
import { ApiError, isAbortError } from '../../shared/api/errors';
import { toApiError } from '../../shared/api/problem';
import { queryClient } from '../../shared/api/query-client';
import { sessionStore, type SessionSnapshot } from '../../shared/api/session-store';
import { useSession } from '../../shared/auth/session';
import {
  isExactGuid,
  mapRegisteredAllocationDispute,
  type AllocationDisputeRecord,
} from './allocation-dispute-api';

export const allocationDisputeAttemptStorageKey = 'finopsdesk.allocation-dispute-attempt';

const schemaVersion = 1;
const operation = 'AllocationDisputeRegistration';
const actorSource = 'developmentJwt';
const maximumStatementLength = 500;
const maximumActorLength = 256;
const badRequestCodes = new Set([
  'reconciliation.allocation_dispute.invalid_request',
  'reconciliation.allocation_dispute.statement_required',
  'reconciliation.allocation_dispute.statement_too_long',
  'reconciliation.allocation_dispute.idempotency_key_required',
  'reconciliation.allocation_dispute.idempotency_key_invalid',
]);
const conflictCodes = new Set([
  'reconciliation.allocation_dispute.idempotency_payload_conflict',
  'reconciliation.allocation_dispute.allocation_not_confirmed',
  'reconciliation.allocation_dispute.concurrency_conflict',
]);

let submissionInFlight = false;
let storageVersion = 0;
let remembered: RememberedAllocationDispute | null = null;
const listeners = new Set<() => void>();

export class AllocationDisputeStorageError extends Error {
  constructor() {
    super('The allocation dispute command could not be stored.');
    this.name = 'AllocationDisputeStorageError';
  }
}

export class AllocationDisputeStorageCleanupError extends Error {
  constructor() {
    super('The recorded allocation dispute could not be cleared from this browser tab.');
    this.name = 'AllocationDisputeStorageCleanupError';
  }
}

export class AllocationDisputeAttemptBlockedError extends Error {
  constructor() {
    super('The stored allocation dispute command blocks a new command.');
    this.name = 'AllocationDisputeAttemptBlockedError';
  }
}

export class UnresolvedAllocationDisputeError extends Error {
  constructor() {
    super('An unresolved allocation dispute command is already stored.');
    this.name = 'UnresolvedAllocationDisputeError';
  }
}

export class AllocationDisputeAuthenticationError extends Error {
  constructor() {
    super('Sign in before recording an allocation dispute.');
    this.name = 'AllocationDisputeAuthenticationError';
  }
}

export class AllocationDisputeActorRequiredError extends Error {
  constructor() {
    super('This dispute command needs an operator identity.');
    this.name = 'AllocationDisputeActorRequiredError';
  }
}

export class AllocationDisputeActorMismatchError extends Error {
  constructor() {
    super('The stored dispute command belongs to another operator.');
    this.name = 'AllocationDisputeActorMismatchError';
  }
}

export class AllocationDisputeTenantMismatchError extends Error {
  constructor() {
    super('The stored dispute command belongs to another organization.');
    this.name = 'AllocationDisputeTenantMismatchError';
  }
}

export class AllocationDisputeSubmissionInProgressError extends Error {
  constructor() {
    super('An allocation dispute command is already being sent.');
    this.name = 'AllocationDisputeSubmissionInProgressError';
  }
}

export class AllocationDisputeAttemptChangedError extends Error {
  constructor() {
    super('The stored dispute command changed before the response could be applied.');
    this.name = 'AllocationDisputeAttemptChangedError';
  }
}

export class AllocationDisputeCommandNotSentError extends Error {
  constructor() {
    super('The dispute command was not sent.');
    this.name = 'AllocationDisputeCommandNotSentError';
  }
}

export class AllocationDisputeRetryNotSentError extends Error {
  constructor() {
    super('The dispute retry was not sent.');
    this.name = 'AllocationDisputeRetryNotSentError';
  }
}

export class UnknownAllocationDisputeOutcomeError extends Error {
  constructor() {
    super('The dispute command was not verified.');
    this.name = 'UnknownAllocationDisputeOutcomeError';
  }
}

export class UnreadableAllocationDisputeError extends Error {
  constructor() {
    super('The dispute response could not be verified.');
    this.name = 'UnreadableAllocationDisputeError';
  }
}

export class AllocationDisputeRejectedError extends Error {
  readonly status: number;
  readonly code: string | null;

  constructor(status: number, code: string | null) {
    super('The server refused this dispute command.');
    this.name = 'AllocationDisputeRejectedError';
    this.status = status;
    this.code = code;
  }
}

export class AllocationDisputeUncertaintyRemainsError extends Error {
  readonly status: number;

  readonly code: string | null;

  constructor(status: number, code: string | null) {
    super('A later refusal did not clear the unresolved dispute command.');
    this.name = 'AllocationDisputeUncertaintyRemainsError';
    this.status = status;
    this.code = code;
  }
}

export interface AllocationDisputeAttempt {
  schemaVersion: 1;
  operation: 'AllocationDisputeRegistration';
  organizationId: string;
  actorSource: 'developmentJwt';
  actorId: string;
  allocationId: string;
  idempotencyKey: string;
  requestBody: string;
  normalizedStatement: string;
  lifecycle: 'ready' | 'sending' | 'uncertain' | 'rejected';
  uncertaintyLocked: boolean;
}

export interface RememberedAllocationDispute {
  operation: 'AllocationDisputeRegistration';
  organizationId: string;
  actorSource: 'developmentJwt';
  actorId: string;
  allocationId: string;
  idempotencyKey: string;
  requestBody: string;
  normalizedStatement: string;
  record: AllocationDisputeRecord;
  created: boolean;
  epoch: number;
}

export interface VerifiedAllocationDisputeRegistration {
  record: AllocationDisputeRecord;
  created: boolean;
  epoch: number;
  cleanup: 'removed' | 'failed';
  allocationId: string;
  idempotencyKey: string;
  requestBody: string;
  normalizedStatement: string;
  refresh: Promise<void>;
}

export type AllocationDisputeAttemptView =
  | { status: 'none' }
  | { status: 'blocked-authentication' }
  | { status: 'blocked-actor' }
  | { status: 'blocked-tenant' }
  | { status: 'blocked-other-actor' }
  | { status: 'invalid-storage' }
  | { status: 'pending'; attempt: AllocationDisputeAttempt };

export function useAllocationDisputeAttempt(): AllocationDisputeAttemptView {
  const session = useSession();
  useSyncExternalStore(subscribe, getStorageVersion, getStorageVersion);
  return inspectAllocationDisputeAttempt(session);
}

export function useRememberedAllocationDispute(): RememberedAllocationDispute | null {
  useSession();
  useSyncExternalStore(subscribe, getStorageVersion, getStorageVersion);
  return rememberedAllocationDisputeRegistration();
}

export function inspectAllocationDisputeAttempt(
  session: SessionSnapshot = sessionStore.getSnapshot(),
): AllocationDisputeAttemptView {
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

export function rememberedAllocationDisputeRegistration(
  session: SessionSnapshot = sessionStore.getSnapshot(),
): RememberedAllocationDispute | null {
  if (remembered === null || !callerOwns(session, remembered)) {
    return null;
  }

  return remembered;
}

export function rememberedAllocationDisputeMatches(attempt: AllocationDisputeAttempt): boolean {
  return remembered !== null && sameIdentity(remembered, attempt);
}

export function normalizeAllocationDisputeStatement(value: string): string | null {
  const statement = trimDotNetWhiteSpace(value);
  if (statement.length < 1 || statement.length > maximumStatementLength) {
    return null;
  }

  return statement;
}

export function beginAllocationDisputeRegistration(
  allocationId: string,
  normalizedStatement: string,
): AllocationDisputeAttempt {
  rejectDevelopmentCookie();
  if (submissionInFlight) {
    throw new AllocationDisputeSubmissionInProgressError();
  }

  const existing = readStoredAttempt();
  if (existing.status === 'invalid') {
    throw new AllocationDisputeAttemptBlockedError();
  }

  if (existing.status === 'present') {
    throw new UnresolvedAllocationDisputeError();
  }

  const session = sessionStore.getSnapshot();
  const organizationId = requireOrganization(session);
  const actorId = requireActor(session);
  if (!isExactGuid(allocationId) || normalizeAllocationDisputeStatement(normalizedStatement) !== normalizedStatement) {
    throw new AllocationDisputeStorageError();
  }

  const idempotencyKey = crypto.randomUUID();
  if (!isExactGuid(idempotencyKey)) {
    throw new AllocationDisputeStorageError();
  }

  const attempt: AllocationDisputeAttempt = {
    schemaVersion,
    operation,
    organizationId,
    actorSource,
    actorId,
    allocationId: allocationId.toLowerCase(),
    idempotencyKey,
    requestBody: serializeStatement(normalizedStatement),
    normalizedStatement,
    lifecycle: 'ready',
    uncertaintyLocked: false,
  };
  persistAttempt(attempt);
  return readBack(attempt);
}

export async function submitAllocationDisputeAttempt(): Promise<VerifiedAllocationDisputeRegistration> {
  rejectDevelopmentCookie();
  if (submissionInFlight) {
    throw new AllocationDisputeSubmissionInProgressError();
  }

  const session = sessionStore.getSnapshot();
  const organizationId = requireOrganization(session);
  const actorId = requireActor(session);
  const stored = readStoredAttempt();
  if (stored.status !== 'present') {
    throw new AllocationDisputeAttemptBlockedError();
  }

  if (organizationId !== stored.attempt.organizationId) {
    throw new AllocationDisputeTenantMismatchError();
  }

  if (actorId !== stored.attempt.actorId) {
    throw new AllocationDisputeActorMismatchError();
  }

  if (stored.attempt.lifecycle === 'rejected') {
    throw new AllocationDisputeRejectedError(0, null);
  }

  submissionInFlight = true;
  const epoch = session.epoch;
  const replaying = stored.attempt.lifecycle === 'sending' || stored.attempt.lifecycle === 'uncertain';
  try {
    let prepared: AllocationDisputeAttempt;
    try {
      prepared = markDispatch(stored.attempt);
    } catch (error) {
      if (error instanceof AllocationDisputeStorageError) {
        throw replaying
          ? new AllocationDisputeRetryNotSentError()
          : new AllocationDisputeCommandNotSentError();
      }

      throw error;
    }

    let response: Response;
    try {
      response = await fetch(`/api/allocations/${prepared.allocationId}/disputes`, {
        method: 'POST',
        credentials: 'same-origin',
        headers: {
          Accept: 'application/json',
          'Content-Type': 'application/json',
          ...authenticationHeaders(session, 'POST'),
          'Idempotency-Key': prepared.idempotencyKey,
        },
        body: prepared.requestBody,
        signal: session.signal,
      });
    } catch (error) {
      rememberUnknown(prepared);
      if (isAbortError(error) || error instanceof TypeError) {
        throw new UnknownAllocationDisputeOutcomeError();
      }

      throw error;
    }

    return await classifyResponse(response, prepared, epoch);
  } finally {
    submissionInFlight = false;
  }
}

export function promotePersistedSendingAllocationDispute(): void {
  if (submissionInFlight) {
    return;
  }

  const stored = readStoredAttempt();
  if (stored.status !== 'present' || stored.attempt.lifecycle !== 'sending') {
    return;
  }

  if (!callerOwns(sessionStore.getSnapshot(), stored.attempt)) {
    return;
  }

  try {
    replaceAttempt(stored.attempt, { ...stored.attempt, lifecycle: 'uncertain', uncertaintyLocked: true });
  } catch {
    // The original sending record remains when the uncertain mark cannot be stored.
  }
}

export function retryAllocationDisputeDescriptorRemoval(): void {
  rejectDevelopmentCookie();
  if (submissionInFlight) {
    throw new AllocationDisputeSubmissionInProgressError();
  }

  const session = sessionStore.getSnapshot();
  requireOrganization(session);
  requireActor(session);
  if (remembered === null || !callerOwns(session, remembered)) {
    throw new AllocationDisputeAttemptChangedError();
  }

  const stored = readStoredAttempt();
  if (stored.status !== 'present' || !sameIdentity(stored.attempt, remembered)) {
    throw new AllocationDisputeAttemptChangedError();
  }

  removeOwned(stored.attempt);
}

export function abandonAllocationDisputeAttempt(idempotencyKey: string, requestBody: string): void {
  rejectDevelopmentCookie();
  const stored = readStoredAttempt();
  if (stored.status !== 'present'
    || stored.attempt.idempotencyKey !== idempotencyKey
    || stored.attempt.requestBody !== requestBody) {
    throw new AllocationDisputeAttemptChangedError();
  }

  const session = sessionStore.getSnapshot();
  if (!callerOwns(session, stored.attempt)) {
    if (developmentOrganizationId(session.token) !== stored.attempt.organizationId) {
      throw session.token === null
        ? new AllocationDisputeAuthenticationError()
        : new AllocationDisputeTenantMismatchError();
    }

    throw developmentActorId(session.token) === null
      ? new AllocationDisputeActorRequiredError()
      : new AllocationDisputeActorMismatchError();
  }

  removeOwned(stored.attempt);
  if (remembered !== null && sameIdentity(remembered, stored.attempt)) {
    remembered = null;
    publish();
  }
}

export function abandonUnreadableAllocationDisputeAttempt(): void {
  rejectDevelopmentCookie();
  if (submissionInFlight) {
    throw new AllocationDisputeSubmissionInProgressError();
  }

  if (readStoredAttempt().status !== 'invalid') {
    throw new AllocationDisputeAttemptChangedError();
  }

  removeKey();
}

export async function refreshAllocationDisputeLists(epoch: number): Promise<void> {
  if (sessionStore.getSnapshot().epoch !== epoch) {
    return;
  }

  await queryClient.invalidateQueries(
    { queryKey: ['allocation-disputes', 'list', epoch] },
    { throwOnError: true },
  );
}

function trackListRefresh(epoch: number): Promise<void> {
  const refresh = refreshAllocationDisputeLists(epoch);
  void refresh.catch(() => undefined);
  return refresh;
}

export function resetAllocationDisputeAttemptStateForTests(): void {
  submissionInFlight = false;
  remembered = null;
  publish();
}

async function classifyResponse(
  response: Response,
  attempt: AllocationDisputeAttempt,
  epoch: number,
): Promise<VerifiedAllocationDisputeRegistration> {
  if (response.status !== 200 && response.status !== 201) {
    return refuse(response, attempt);
  }

  let text: string;
  try {
    text = await response.text();
  } catch {
    rememberUnknown(attempt);
    throw new UnreadableAllocationDisputeError();
  }

  let record: AllocationDisputeRecord;
  try {
    record = mapRegisteredAllocationDispute(JSON.parse(text) as unknown, attempt.allocationId, attempt.normalizedStatement);
  } catch {
    rememberUnknown(attempt);
    throw new UnreadableAllocationDisputeError();
  }

  let cleanup: 'removed' | 'failed' = 'removed';
  if (!storageOwns(attempt)) {
    void trackListRefresh(epoch);
    throw new AllocationDisputeAttemptChangedError();
  }

  try {
    removeOwned(attempt);
  } catch (error) {
    if (error instanceof AllocationDisputeStorageCleanupError && storageOwns(attempt)) {
      cleanup = 'failed';
    } else {
      void trackListRefresh(epoch);
      throw error;
    }
  }

  remembered = {
    operation,
    organizationId: attempt.organizationId,
    actorSource,
    actorId: attempt.actorId,
    allocationId: attempt.allocationId,
    idempotencyKey: attempt.idempotencyKey,
    requestBody: attempt.requestBody,
    normalizedStatement: attempt.normalizedStatement,
    record,
    created: response.status === 201,
    epoch,
  };
  publish();
  return {
    record,
    created: response.status === 201,
    epoch,
    cleanup,
    allocationId: attempt.allocationId,
    idempotencyKey: attempt.idempotencyKey,
    requestBody: attempt.requestBody,
    normalizedStatement: attempt.normalizedStatement,
    refresh: trackListRefresh(epoch),
  };
}

async function refuse(response: Response, attempt: AllocationDisputeAttempt): Promise<never> {
  let text = '';
  let bodyRead = true;
  try {
    text = await response.text();
  } catch {
    bodyRead = false;
  }

  const error = bodyRead ? toApiError(response.status, text) : new ApiError(response.status, null, '', '');
  if (isRecognizedRefusal(error, bodyRead)) {
    if (attempt.uncertaintyLocked) {
      throw new AllocationDisputeUncertaintyRemainsError(error.status, error.code);
    }

    replaceIfOwned(attempt, { ...attempt, lifecycle: 'rejected', uncertaintyLocked: false });
    throw new AllocationDisputeRejectedError(error.status, error.code);
  }

  rememberUnknown(attempt);
  throw new UnknownAllocationDisputeOutcomeError();
}

function isRecognizedRefusal(error: ApiError, bodyRead: boolean): boolean {
  if (error.status === 401) {
    return true;
  }

  if (!bodyRead || error.code === null) {
    return false;
  }

  if (error.status === 400) {
    return badRequestCodes.has(error.code);
  }

  if (error.status === 403) {
    return error.code === 'reconciliation.actor.required';
  }

  if (error.status === 404) {
    return error.code === 'reconciliation.allocation.not_found';
  }

  if (error.status === 409) {
    return conflictCodes.has(error.code);
  }

  if (error.status === 413) {
    return error.code === 'reconciliation.allocation.request_too_large';
  }

  if (error.status === 415) {
    return error.code === 'reconciliation.allocation.unsupported_media_type';
  }

  return false;
}

function markDispatch(attempt: AllocationDisputeAttempt): AllocationDisputeAttempt {
  if (attempt.lifecycle === 'sending' || attempt.lifecycle === 'uncertain') {
    return replaceAttempt(attempt, { ...attempt, lifecycle: 'uncertain', uncertaintyLocked: true });
  }

  return replaceAttempt(attempt, { ...attempt, lifecycle: 'sending', uncertaintyLocked: false });
}

function rememberUnknown(attempt: AllocationDisputeAttempt): void {
  if (!storageOwns(attempt)) {
    return;
  }

  try {
    replaceAttempt(attempt, { ...attempt, lifecycle: 'uncertain', uncertaintyLocked: true });
  } catch {
    // The original descriptor remains when the uncertain mark cannot be stored.
  }
}

function replaceIfOwned(previous: AllocationDisputeAttempt, next: AllocationDisputeAttempt): void {
  if (!storageOwns(previous)) {
    return;
  }

  replaceAttempt(previous, next);
}

function storageOwns(attempt: AllocationDisputeAttempt): boolean {
  if (!callerOwns(sessionStore.getSnapshot(), attempt)) {
    return false;
  }

  const stored = readStoredAttempt();
  return stored.status === 'present' && sameIdentity(stored.attempt, attempt);
}

function callerOwns(
  session: SessionSnapshot,
  attempt: { organizationId: string; actorId: string },
): boolean {
  return developmentOrganizationId(session.token) === attempt.organizationId
    && developmentActorId(session.token) === attempt.actorId;
}

function sameIdentity(
  stored: {
    operation: AllocationDisputeAttempt['operation'];
    organizationId: string;
    actorSource: AllocationDisputeAttempt['actorSource'];
    actorId: string;
    allocationId: string;
    idempotencyKey: string;
    requestBody: string;
    normalizedStatement: string;
  },
  attempt: {
    operation: AllocationDisputeAttempt['operation'];
    organizationId: string;
    actorSource: AllocationDisputeAttempt['actorSource'];
    actorId: string;
    allocationId: string;
    idempotencyKey: string;
    requestBody: string;
    normalizedStatement: string;
  },
): boolean {
  return stored.operation === attempt.operation
    && stored.organizationId === attempt.organizationId
    && stored.actorSource === attempt.actorSource
    && stored.actorId === attempt.actorId
    && stored.allocationId === attempt.allocationId
    && stored.idempotencyKey === attempt.idempotencyKey
    && stored.requestBody === attempt.requestBody
    && stored.normalizedStatement === attempt.normalizedStatement;
}

function replaceAttempt(
  previous: AllocationDisputeAttempt,
  next: AllocationDisputeAttempt,
): AllocationDisputeAttempt {
  const stored = readStoredAttempt();
  if (stored.status !== 'present' || !sameIdentity(stored.attempt, previous)) {
    throw new AllocationDisputeAttemptChangedError();
  }

  persistAttempt(next);
  return readBack(next);
}

function persistAttempt(attempt: AllocationDisputeAttempt): void {
  const serialized = JSON.stringify(attempt);
  try {
    sessionStorage.setItem(allocationDisputeAttemptStorageKey, serialized);
    if (sessionStorage.getItem(allocationDisputeAttemptStorageKey) !== serialized) {
      throw new AllocationDisputeStorageError();
    }
  } catch (error) {
    if (error instanceof AllocationDisputeStorageError) {
      throw error;
    }

    throw new AllocationDisputeStorageError();
  }

  readBack(attempt);
  publish();
}

function readBack(attempt: AllocationDisputeAttempt): AllocationDisputeAttempt {
  const stored = readStoredAttempt();
  if (stored.status !== 'present'
    || !sameIdentity(stored.attempt, attempt)
    || stored.attempt.lifecycle !== attempt.lifecycle
    || stored.attempt.uncertaintyLocked !== attempt.uncertaintyLocked) {
    throw new AllocationDisputeStorageError();
  }

  return stored.attempt;
}

function removeOwned(attempt: AllocationDisputeAttempt): void {
  const stored = readStoredAttempt();
  if (stored.status !== 'present' || !sameIdentity(stored.attempt, attempt)) {
    throw new AllocationDisputeAttemptChangedError();
  }

  removeKey();
}

function removeKey(): void {
  try {
    sessionStorage.removeItem(allocationDisputeAttemptStorageKey);
  } catch {
    throw new AllocationDisputeStorageCleanupError();
  }

  let remaining: string | null;
  try {
    remaining = sessionStorage.getItem(allocationDisputeAttemptStorageKey);
  } catch {
    throw new AllocationDisputeStorageCleanupError();
  }

  if (remaining !== null) {
    throw new AllocationDisputeStorageCleanupError();
  }

  publish();
}

function readStoredAttempt():
  | { status: 'empty' }
  | { status: 'invalid' }
  | { status: 'present'; attempt: AllocationDisputeAttempt } {
  let raw: string | null;
  try {
    raw = sessionStorage.getItem(allocationDisputeAttemptStorageKey);
  } catch {
    return { status: 'invalid' };
  }

  if (raw === null) {
    return { status: 'empty' };
  }

  try {
    const parsed: unknown = JSON.parse(raw);
    const attempt = parseAttempt(parsed);
    if (attempt === null) {
      return { status: 'invalid' };
    }

    return { status: 'present', attempt };
  } catch {
    return { status: 'invalid' };
  }
}

function parseAttempt(value: unknown): AllocationDisputeAttempt | null {
  if (!isRecord(value)
    || value.schemaVersion !== schemaVersion
    || value.operation !== operation
    || value.actorSource !== actorSource
    || typeof value.actorId !== 'string'
    || typeof value.requestBody !== 'string'
    || typeof value.normalizedStatement !== 'string'
    || (value.lifecycle !== 'ready' && value.lifecycle !== 'sending' && value.lifecycle !== 'uncertain' && value.lifecycle !== 'rejected')
    || typeof value.uncertaintyLocked !== 'boolean'
    || typeof value.organizationId !== 'string'
    || !isExactGuid(value.organizationId)
    || typeof value.allocationId !== 'string'
    || !isExactGuid(value.allocationId)
    || typeof value.idempotencyKey !== 'string'
    || !isExactGuid(value.idempotencyKey)) {
    return null;
  }

  if (!isStoredActorId(value.actorId)) {
    return null;
  }

  const attempt: AllocationDisputeAttempt = {
    schemaVersion,
    operation,
    organizationId: value.organizationId.toLowerCase(),
    actorSource,
    actorId: value.actorId,
    allocationId: value.allocationId.toLowerCase(),
    idempotencyKey: value.idempotencyKey,
    requestBody: value.requestBody,
    normalizedStatement: value.normalizedStatement,
    lifecycle: value.lifecycle,
    uncertaintyLocked: value.uncertaintyLocked,
  };
  if (attempt.requestBody !== serializeStatement(attempt.normalizedStatement)
    || normalizeAllocationDisputeStatement(attempt.normalizedStatement) !== attempt.normalizedStatement) {
    return null;
  }

  return attempt;
}

function serializeStatement(statement: string): string {
  return JSON.stringify({ statement });
}

function isStoredActorId(value: string): boolean {
  return value.length > 0 && value.length <= maximumActorLength && trimDotNetWhiteSpace(value) === value;
}

function requireOrganization(session: SessionSnapshot): string {
  const organizationId = developmentOrganizationId(session.token);
  if (session.token === null || organizationId === null) {
    throw new AllocationDisputeAuthenticationError();
  }

  return organizationId;
}

function requireActor(session: SessionSnapshot): string {
  const actorId = developmentActorId(session.token);
  if (actorId === null) {
    throw new AllocationDisputeActorRequiredError();
  }

  return actorId;
}

function rejectDevelopmentCookie(): void {
  rejectCookieFinancialRequest(sessionStore.getSnapshot());
}

function developmentActorId(token: string | null): string | null {
  const claim = readClaim(token, 'sub');
  if (typeof claim !== 'string') {
    return null;
  }

  const actorId = trimDotNetWhiteSpace(claim);
  if (actorId.length === 0 || actorId.length > maximumActorLength) {
    return null;
  }

  return actorId;
}

function developmentOrganizationId(token: string | null): string | null {
  const claim = readClaim(token, 'org_id');
  if (typeof claim !== 'string' || !isExactGuid(claim)) {
    return null;
  }

  return claim.toLowerCase();
}

function readClaim(token: string | null, name: string): unknown {
  if (token === null) {
    return null;
  }

  const parts = token.split('.');
  if (parts.length !== 3 || parts.some((part) => part.length === 0)) {
    return null;
  }

  try {
    const payload: unknown = JSON.parse(decodeBase64Url(parts[1] ?? ''));
    return isRecord(payload) ? payload[name] : null;
  } catch {
    return null;
  }
}

function decodeBase64Url(value: string): string {
  const base64 = value.replace(/-/g, '+').replace(/_/g, '/');
  const padded = base64.padEnd(base64.length + ((4 - (base64.length % 4)) % 4), '=');
  const binary = atob(padded);
  const bytes = Uint8Array.from(binary, (character) => character.charCodeAt(0));
  return new TextDecoder().decode(bytes);
}

function trimDotNetWhiteSpace(value: string): string {
  let start = 0;
  let end = value.length;
  while (start < end && isDotNetWhiteSpace(value.charCodeAt(start))) {
    start += 1;
  }

  while (end > start && isDotNetWhiteSpace(value.charCodeAt(end - 1))) {
    end -= 1;
  }

  return value.slice(start, end);
}

function isDotNetWhiteSpace(code: number): boolean {
  return code === 0x20
    || (code >= 0x09 && code <= 0x0d)
    || code === 0x85
    || code === 0xa0
    || code === 0x1680
    || (code >= 0x2000 && code <= 0x200a)
    || code === 0x2028
    || code === 0x2029
    || code === 0x202f
    || code === 0x205f
    || code === 0x3000;
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
  for (const listener of listeners) {
    listener();
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
