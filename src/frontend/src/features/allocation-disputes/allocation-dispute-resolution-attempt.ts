import { useSyncExternalStore } from 'react';

import { authenticationHeaders, rejectCookieFinancialRequest } from '../../shared/api/authentication';
import { ApiError, isAbortError, StaleSessionError, UnexpectedResponseError } from '../../shared/api/errors';
import { toApiError } from '../../shared/api/problem';
import { queryClient } from '../../shared/api/query-client';
import { sessionStore, type SessionSnapshot } from '../../shared/api/session-store';
import { useSession } from '../../shared/auth/session';
import {
  allocationDisputeResolutionQueryKey,
  fetchAllocationDisputeResolution,
  isExactGuid,
  mapAllocationDisputeResolutionDecision,
  mapAllocationDisputeResolutionState,
  normalizeAllocationDisputeResolutionReason,
  type AllocationDisputeResolutionDecision,
  type AllocationDisputeResolutionOutcome,
  type AllocationDisputeResolutionState,
} from './allocation-dispute-api';

export const allocationDisputeResolutionAttemptStorageKey = 'finopsdesk.allocation-dispute-resolution-attempt';

const schemaVersion = 1;
const operation = 'AllocationDisputeResolution';
const actorSource = 'developmentJwt';
const maximumActorLength = 256;
const badRequestCodes = new Set([
  'reconciliation.allocation_dispute.resolution.invalid_request',
  'reconciliation.allocation_dispute.resolution.outcome_required',
  'reconciliation.allocation_dispute.resolution.reason_required',
  'reconciliation.allocation_dispute.resolution.reason_too_long',
  'reconciliation.allocation_dispute.resolution.idempotency_key_required',
  'reconciliation.allocation_dispute.resolution.idempotency_key_invalid',
]);
const conflictCodes = new Set([
  'reconciliation.allocation_dispute.resolution.idempotency_payload_conflict',
  'reconciliation.allocation_dispute.resolution.already_resolved',
  'reconciliation.allocation_dispute.resolution.concurrency_conflict',
]);

interface ResolutionBoundary {
  serial: number;
  confirmed: AllocationDisputeResolutionDecision | null;
  nullObservedSerial: number | null;
}

let submissionInFlight = false;
let storageVersion = 0;
let remembered: RememberedAllocationDisputeResolution | null = null;
const listeners = new Set<() => void>();
const boundaries = new Map<string, ResolutionBoundary>();

export class AllocationDisputeResolutionStorageError extends Error {
  constructor() {
    super('The allocation dispute decision command could not be stored.');
    this.name = 'AllocationDisputeResolutionStorageError';
  }
}

export class AllocationDisputeResolutionVerifiedTrackingError extends Error {
  constructor() {
    super('The decision is stored. Local tracking could not be removed.');
    this.name = 'AllocationDisputeResolutionVerifiedTrackingError';
  }
}

export class AllocationDisputeResolutionStorageCleanupError extends Error {
  constructor() {
    super('The recorded allocation dispute decision could not be cleared from this browser tab.');
    this.name = 'AllocationDisputeResolutionStorageCleanupError';
  }
}

export class AllocationDisputeResolutionCommandNotSentError extends Error {
  constructor() {
    super('The allocation dispute decision was not sent.');
    this.name = 'AllocationDisputeResolutionCommandNotSentError';
  }
}

export class AllocationDisputeResolutionRetryNotSentError extends Error {
  constructor() {
    super('The same allocation dispute decision was not sent again.');
    this.name = 'AllocationDisputeResolutionRetryNotSentError';
  }
}

export class AllocationDisputeResolutionAuthenticationError extends Error {
  constructor() {
    super('The allocation dispute decision needs the current development session.');
    this.name = 'AllocationDisputeResolutionAuthenticationError';
  }
}

export class AllocationDisputeResolutionActorRequiredError extends Error {
  constructor() {
    super('The allocation dispute decision needs an operator identity.');
    this.name = 'AllocationDisputeResolutionActorRequiredError';
  }
}

export class AllocationDisputeResolutionActorMismatchError extends Error {
  constructor() {
    super('The stored allocation dispute decision belongs to another operator.');
    this.name = 'AllocationDisputeResolutionActorMismatchError';
  }
}

export class AllocationDisputeResolutionTenantMismatchError extends Error {
  constructor() {
    super('The stored allocation dispute decision belongs to another organization.');
    this.name = 'AllocationDisputeResolutionTenantMismatchError';
  }
}

export class AllocationDisputeResolutionAttemptBlockedError extends Error {
  constructor() {
    super('The stored allocation dispute decision command cannot be sent.');
    this.name = 'AllocationDisputeResolutionAttemptBlockedError';
  }
}

export class AllocationDisputeResolutionAttemptChangedError extends Error {
  constructor() {
    super('The stored allocation dispute decision command changed.');
    this.name = 'AllocationDisputeResolutionAttemptChangedError';
  }
}

export class AllocationDisputeResolutionSubmissionInProgressError extends Error {
  constructor() {
    super('This allocation dispute decision command is still being sent.');
    this.name = 'AllocationDisputeResolutionSubmissionInProgressError';
  }
}

export class AllocationDisputeResolutionNotFreshError extends Error {
  constructor() {
    super('A new allocation dispute decision waits for a current read of no stored decision.');
    this.name = 'AllocationDisputeResolutionNotFreshError';
  }
}

export class UnresolvedAllocationDisputeResolutionError extends Error {
  constructor() {
    super('A stored allocation dispute decision command is still unresolved.');
    this.name = 'UnresolvedAllocationDisputeResolutionError';
  }
}

export class UnknownAllocationDisputeResolutionOutcomeError extends Error {
  constructor() {
    super('The allocation dispute decision ended before a verified result was received.');
    this.name = 'UnknownAllocationDisputeResolutionOutcomeError';
  }
}

export class UnreadableAllocationDisputeResolutionError extends Error {
  constructor() {
    super('The allocation dispute decision response could not be verified.');
    this.name = 'UnreadableAllocationDisputeResolutionError';
  }
}

export class AllocationDisputeResolutionRejectedError extends Error {
  readonly status: number;

  readonly code: string | null;

  constructor(status: number, code: string | null) {
    super('The allocation dispute decision was refused.');
    this.name = 'AllocationDisputeResolutionRejectedError';
    this.status = status;
    this.code = code;
  }
}

export class AllocationDisputeResolutionUncertaintyRemainsError extends Error {
  readonly status: number;

  readonly code: string | null;

  constructor(status: number, code: string | null) {
    super('The earlier allocation dispute decision is still unresolved.');
    this.name = 'AllocationDisputeResolutionUncertaintyRemainsError';
    this.status = status;
    this.code = code;
  }
}

export interface AllocationDisputeResolutionAttempt {
  schemaVersion: 1;
  operation: 'AllocationDisputeResolution';
  organizationId: string;
  actorSource: 'developmentJwt';
  actorId: string;
  disputeId: string;
  idempotencyKey: string;
  requestBody: string;
  outcome: AllocationDisputeResolutionOutcome;
  normalizedReason: string;
  lifecycle: 'ready' | 'sending' | 'uncertain' | 'rejected';
  uncertaintyLocked: boolean;
}

export interface RememberedAllocationDisputeResolution {
  operation: 'AllocationDisputeResolution';
  organizationId: string;
  actorSource: 'developmentJwt';
  actorId: string;
  disputeId: string;
  idempotencyKey: string;
  requestBody: string;
  outcome: AllocationDisputeResolutionOutcome;
  normalizedReason: string;
  decision: AllocationDisputeResolutionDecision;
  created: boolean;
  epoch: number;
}

export interface VerifiedAllocationDisputeResolution {
  decision: AllocationDisputeResolutionDecision;
  created: boolean;
  epoch: number;
  cleanup: 'removed' | 'failed';
  disputeId: string;
  idempotencyKey: string;
  requestBody: string;
  outcome: AllocationDisputeResolutionOutcome;
  normalizedReason: string;
  refresh: Promise<void>;
}

export type AllocationDisputeResolutionAttemptView =
  | { status: 'none' }
  | { status: 'blocked-authentication' }
  | { status: 'blocked-actor' }
  | { status: 'blocked-tenant' }
  | { status: 'blocked-other-actor' }
  | { status: 'invalid-storage' }
  | { status: 'pending'; attempt: AllocationDisputeResolutionAttempt };

export interface ResolutionBoundarySnapshot {
  serial: number;
  confirmed: AllocationDisputeResolutionDecision | null;
  nullObservedSerial: number | null;
}

export function useAllocationDisputeResolutionAttempt(): AllocationDisputeResolutionAttemptView {
  const session = useSession();
  useSyncExternalStore(subscribe, getStorageVersion, getStorageVersion);
  return inspectAllocationDisputeResolutionAttempt(session);
}

export function useRememberedAllocationDisputeResolution(): RememberedAllocationDisputeResolution | null {
  useSession();
  useSyncExternalStore(subscribe, getStorageVersion, getStorageVersion);
  return rememberedAllocationDisputeResolution();
}

export function rememberedAllocationDisputeResolution(
  session: SessionSnapshot = sessionStore.getSnapshot(),
): RememberedAllocationDisputeResolution | null {
  if (remembered === null || !callerOwns(session, remembered)) {
    return null;
  }

  return remembered;
}

export function rememberedResolutionMatches(attempt: AllocationDisputeResolutionAttempt): boolean {
  return remembered !== null && sameIdentity(remembered, attempt);
}

export function useConfirmedAllocationDisputeResolution(
  epoch: number,
  disputeId: string | null,
): AllocationDisputeResolutionDecision | null {
  useSyncExternalStore(subscribe, getStorageVersion, getStorageVersion);
  if (disputeId === null) {
    return null;
  }

  return confirmedAllocationDisputeResolution(epoch, disputeId);
}

export function inspectAllocationDisputeResolutionAttempt(
  session: SessionSnapshot = sessionStore.getSnapshot(),
): AllocationDisputeResolutionAttemptView {
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

export function confirmedAllocationDisputeResolution(
  epoch: number,
  disputeId: string,
): AllocationDisputeResolutionDecision | null {
  return boundary(epoch, disputeId).confirmed;
}

export function resolutionBoundaryForTests(epoch: number, disputeId: string): ResolutionBoundarySnapshot {
  const current = boundary(epoch, disputeId);
  return {
    serial: current.serial,
    confirmed: current.confirmed,
    nullObservedSerial: current.nullObservedSerial,
  };
}

export function resolutionAttemptAllowed(epoch: number, disputeId: string): boolean {
  const session = sessionStore.getSnapshot();
  if (session.epoch !== epoch || session.token === null) {
    return false;
  }

  if (developmentOrganizationId(session.token) === null || developmentActorId(session.token) === null) {
    return false;
  }

  if (!isExactGuid(disputeId) || readStoredAttempt().status !== 'empty') {
    return false;
  }

  const current = boundary(epoch, disputeId);
  if (current.confirmed !== null || current.nullObservedSerial !== current.serial) {
    return false;
  }

  const queryKey = allocationDisputeResolutionQueryKey(epoch, disputeId);
  const queryState = queryClient.getQueryState<AllocationDisputeResolutionState>(queryKey);
  const data = queryClient.getQueryData<AllocationDisputeResolutionState>(queryKey);
  return queryState?.status === 'success'
    && queryState.fetchStatus === 'idle'
    && data?.decision === null;
}

export async function loadAllocationDisputeResolution(
  disputeId: string,
  epoch: number,
  signal: AbortSignal,
): Promise<AllocationDisputeResolutionState> {
  rejectDevelopmentCookie();
  if (!isExactGuid(disputeId)) {
    throw new UnexpectedResponseError('The allocation dispute decision could not be read.');
  }

  const requestedId = disputeId.toLowerCase();
  const startedSerial = boundary(epoch, requestedId).serial;
  let document: unknown;
  try {
    document = await fetchAllocationDisputeResolution(requestedId, signal);
  } catch (error) {
    if (sessionStore.getSnapshot().epoch !== epoch) {
      throw new StaleSessionError();
    }

    throw error;
  }

  if (sessionStore.getSnapshot().epoch !== epoch) {
    throw new StaleSessionError();
  }

  const state = mapAllocationDisputeResolutionState(document, requestedId);
  const current = boundary(epoch, requestedId);
  if (current.serial !== startedSerial) {
    if (current.confirmed !== null) {
      return { disputeId: current.confirmed.disputeId, decision: current.confirmed };
    }

    throw new UnexpectedResponseError('The allocation dispute decision could not be read.');
  }

  if (state.decision === null) {
    if (current.confirmed !== null) {
      return { disputeId: current.confirmed.disputeId, decision: current.confirmed };
    }

    current.nullObservedSerial = startedSerial;
    publish();
  }

  return state;
}

export async function submitAllocationDisputeResolution(
  disputeId: string,
  outcome: AllocationDisputeResolutionOutcome,
  reason: string,
): Promise<VerifiedAllocationDisputeResolution> {
  rejectDevelopmentCookie();
  if (submissionInFlight) {
    throw new AllocationDisputeResolutionSubmissionInProgressError();
  }

  const session = sessionStore.getSnapshot();
  const epoch = session.epoch;
  if (!resolutionAttemptAllowed(epoch, disputeId)) {
    throw new AllocationDisputeResolutionNotFreshError();
  }

  const normalizedReason = normalizeAllocationDisputeResolutionReason(reason);
  if (normalizedReason === null || (outcome !== 'upheld' && outcome !== 'not_upheld') || !isExactGuid(disputeId)) {
    throw new AllocationDisputeResolutionStorageError();
  }

  const organizationId = requireOrganization(session);
  const actorId = requireActor(session);
  const idempotencyKey = crypto.randomUUID();
  if (!isExactGuid(idempotencyKey)) {
    throw new AllocationDisputeResolutionStorageError();
  }

  const attempt: AllocationDisputeResolutionAttempt = {
    schemaVersion,
    operation,
    organizationId,
    actorSource,
    actorId,
    disputeId: disputeId.toLowerCase(),
    idempotencyKey,
    requestBody: serializeResolution(outcome, normalizedReason),
    outcome,
    normalizedReason,
    lifecycle: 'ready',
    uncertaintyLocked: false,
  };
  submissionInFlight = true;
  try {
    let prepared: AllocationDisputeResolutionAttempt;
    try {
      persistAttempt(attempt);
      prepared = markDispatch(readBack(attempt));
    } catch (error) {
      if (error instanceof AllocationDisputeResolutionStorageError) {
        throw new AllocationDisputeResolutionCommandNotSentError();
      }

      throw error;
    }

    return await postPrepared(prepared, epoch);
  } finally {
    submissionInFlight = false;
  }
}

export async function replayAllocationDisputeResolutionAttempt(): Promise<VerifiedAllocationDisputeResolution> {
  rejectDevelopmentCookie();
  if (submissionInFlight) {
    throw new AllocationDisputeResolutionSubmissionInProgressError();
  }

  const session = sessionStore.getSnapshot();
  const organizationId = requireOrganization(session);
  const actorId = requireActor(session);
  const stored = readStoredAttempt();
  if (stored.status !== 'present') {
    throw new AllocationDisputeResolutionAttemptBlockedError();
  }

  if (organizationId !== stored.attempt.organizationId) {
    throw new AllocationDisputeResolutionTenantMismatchError();
  }

  if (actorId !== stored.attempt.actorId) {
    throw new AllocationDisputeResolutionActorMismatchError();
  }

  if (rememberedResolutionMatches(stored.attempt)) {
    throw new AllocationDisputeResolutionVerifiedTrackingError();
  }

  if (stored.attempt.lifecycle === 'rejected') {
    throw new AllocationDisputeResolutionRejectedError(0, null);
  }

  submissionInFlight = true;
  const epoch = session.epoch;
  const replaying = stored.attempt.lifecycle === 'sending' || stored.attempt.lifecycle === 'uncertain';
  try {
    let prepared: AllocationDisputeResolutionAttempt;
    try {
      prepared = markDispatch(stored.attempt);
    } catch (error) {
      if (error instanceof AllocationDisputeResolutionStorageError) {
        throw replaying
          ? new AllocationDisputeResolutionRetryNotSentError()
          : new AllocationDisputeResolutionCommandNotSentError();
      }

      throw error;
    }

    return await postPrepared(prepared, epoch);
  } finally {
    submissionInFlight = false;
  }
}

export function promotePersistedSendingAllocationDisputeResolution(): void {
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

export function retryAllocationDisputeResolutionDescriptorRemoval(): void {
  rejectDevelopmentCookie();
  if (submissionInFlight) {
    throw new AllocationDisputeResolutionSubmissionInProgressError();
  }

  const session = sessionStore.getSnapshot();
  requireOrganization(session);
  requireActor(session);
  if (remembered === null || !callerOwns(session, remembered)) {
    throw new AllocationDisputeResolutionAttemptChangedError();
  }

  const stored = readStoredAttempt();
  if (stored.status !== 'present' || !sameIdentity(stored.attempt, remembered)) {
    throw new AllocationDisputeResolutionAttemptChangedError();
  }

  removeOwned(stored.attempt);
}

export async function abandonAllocationDisputeResolutionAttempt(
  idempotencyKey: string,
  requestBody: string,
): Promise<void> {
  rejectDevelopmentCookie();
  const stored = readStoredAttempt();
  if (stored.status !== 'present'
    || stored.attempt.idempotencyKey !== idempotencyKey
    || stored.attempt.requestBody !== requestBody) {
    throw new AllocationDisputeResolutionAttemptChangedError();
  }

  const session = sessionStore.getSnapshot();
  if (!callerOwns(session, stored.attempt)) {
    if (developmentOrganizationId(session.token) !== stored.attempt.organizationId) {
      throw session.token === null
        ? new AllocationDisputeResolutionAuthenticationError()
        : new AllocationDisputeResolutionTenantMismatchError();
    }

    throw developmentActorId(session.token) === null
      ? new AllocationDisputeResolutionActorRequiredError()
      : new AllocationDisputeResolutionActorMismatchError();
  }

  const disputeId = stored.attempt.disputeId;
  const epoch = session.epoch;
  removeOwned(stored.attempt);
  if (remembered !== null && sameIdentity(remembered, stored.attempt)) {
    remembered = null;
    publish();
  }

  raiseObservationBoundary(epoch, disputeId);
  await queryClient.cancelQueries({
    queryKey: allocationDisputeResolutionQueryKey(epoch, disputeId),
    exact: true,
  });
  if (sessionStore.getSnapshot().epoch === epoch) {
    await queryClient.refetchQueries(
      { queryKey: allocationDisputeResolutionQueryKey(epoch, disputeId), exact: true },
      { throwOnError: false },
    );
  }
}

export function abandonUnreadableAllocationDisputeResolutionAttempt(): void {
  rejectDevelopmentCookie();
  if (submissionInFlight) {
    throw new AllocationDisputeResolutionSubmissionInProgressError();
  }

  if (readStoredAttempt().status !== 'invalid') {
    throw new AllocationDisputeResolutionAttemptChangedError();
  }

  removeKey();
}

export async function refreshAllocationDisputeResolution(epoch: number, disputeId: string): Promise<void> {
  if (sessionStore.getSnapshot().epoch !== epoch) {
    return;
  }

  await queryClient.fetchQuery({
    queryKey: allocationDisputeResolutionQueryKey(epoch, disputeId),
    queryFn: ({ signal }) => loadAllocationDisputeResolution(disputeId, epoch, signal),
    staleTime: 0,
  });
}

export function resetAllocationDisputeResolutionAttemptStateForTests(): void {
  submissionInFlight = false;
  remembered = null;
  boundaries.clear();
  publish();
}

async function postPrepared(
  prepared: AllocationDisputeResolutionAttempt,
  epoch: number,
): Promise<VerifiedAllocationDisputeResolution> {
  const session = sessionStore.getSnapshot();
  let response: Response;
  try {
    response = await fetch(`/api/allocation-disputes/${prepared.disputeId}/resolution`, {
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
      throw new UnknownAllocationDisputeResolutionOutcomeError();
    }

    throw error;
  }

  return classifyResponse(response, prepared, epoch);
}

async function classifyResponse(
  response: Response,
  attempt: AllocationDisputeResolutionAttempt,
  epoch: number,
): Promise<VerifiedAllocationDisputeResolution> {
  if (response.status !== 200 && response.status !== 201) {
    return refuse(response, attempt);
  }

  let text: string;
  try {
    text = await response.text();
  } catch {
    rememberUnknown(attempt);
    throw new UnreadableAllocationDisputeResolutionError();
  }

  let decision: AllocationDisputeResolutionDecision;
  try {
    decision = mapAllocationDisputeResolutionDecision(JSON.parse(text) as unknown, attempt.disputeId);
  } catch {
    rememberUnknown(attempt);
    throw new UnreadableAllocationDisputeResolutionError();
  }

  if (decision.outcome !== attempt.outcome || decision.reason !== attempt.normalizedReason) {
    rememberUnknown(attempt);
    throw new UnreadableAllocationDisputeResolutionError();
  }

  if (!storageOwns(attempt)) {
    throw new AllocationDisputeResolutionAttemptChangedError();
  }

  const queryKey = allocationDisputeResolutionQueryKey(epoch, attempt.disputeId);
  await queryClient.cancelQueries({ queryKey, exact: true });
  raiseConfirmed(epoch, attempt.disputeId, decision);

  let cleanup: 'removed' | 'failed' = 'removed';
  try {
    if (storageOwns(attempt)) {
      removeOwned(attempt);
    }
  } catch (error) {
    if (error instanceof AllocationDisputeResolutionStorageCleanupError && storageOwns(attempt)) {
      cleanup = 'failed';
    } else if (error instanceof AllocationDisputeResolutionAttemptChangedError) {
      cleanup = 'removed';
    } else {
      throw error;
    }
  }

  if (sessionStore.getSnapshot().epoch === epoch) {
    queryClient.setQueryData<AllocationDisputeResolutionState>(queryKey, {
      disputeId: decision.disputeId,
      decision,
    });
  }

  remembered = {
    operation,
    organizationId: attempt.organizationId,
    actorSource,
    actorId: attempt.actorId,
    disputeId: attempt.disputeId,
    idempotencyKey: attempt.idempotencyKey,
    requestBody: attempt.requestBody,
    outcome: attempt.outcome,
    normalizedReason: attempt.normalizedReason,
    decision,
    created: response.status === 201,
    epoch,
  };
  publish();
  return {
    decision,
    created: response.status === 201,
    epoch,
    cleanup,
    disputeId: attempt.disputeId,
    idempotencyKey: attempt.idempotencyKey,
    requestBody: attempt.requestBody,
    outcome: attempt.outcome,
    normalizedReason: attempt.normalizedReason,
    refresh: trackResolutionRefresh(epoch, attempt.disputeId),
  };
}

async function refuse(response: Response, attempt: AllocationDisputeResolutionAttempt): Promise<never> {
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
      throw new AllocationDisputeResolutionUncertaintyRemainsError(error.status, error.code);
    }

    replaceIfOwned(attempt, { ...attempt, lifecycle: 'rejected', uncertaintyLocked: false });
    throw new AllocationDisputeResolutionRejectedError(error.status, error.code);
  }

  rememberUnknown(attempt);
  throw new UnknownAllocationDisputeResolutionOutcomeError();
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
    return error.code === 'reconciliation.allocation_dispute.not_found';
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

function trackResolutionRefresh(epoch: number, disputeId: string): Promise<void> {
  const refresh = refreshAllocationDisputeResolution(epoch, disputeId);
  void refresh.catch(() => undefined);
  return refresh;
}

function raiseConfirmed(
  epoch: number,
  disputeId: string,
  decision: AllocationDisputeResolutionDecision,
): void {
  const current = boundary(epoch, disputeId);
  current.serial += 1;
  current.confirmed = decision;
  publish();
}

function raiseObservationBoundary(epoch: number, disputeId: string): void {
  const current = boundary(epoch, disputeId);
  current.serial += 1;
  publish();
}

function boundary(epoch: number, disputeId: string): ResolutionBoundary {
  const key = `${epoch}:${disputeId.toLowerCase()}`;
  const existing = boundaries.get(key);
  if (existing !== undefined) {
    return existing;
  }

  const created: ResolutionBoundary = { serial: 0, confirmed: null, nullObservedSerial: null };
  boundaries.set(key, created);
  return created;
}

function markDispatch(attempt: AllocationDisputeResolutionAttempt): AllocationDisputeResolutionAttempt {
  if (attempt.lifecycle === 'sending' || attempt.lifecycle === 'uncertain') {
    return replaceAttempt(attempt, { ...attempt, lifecycle: 'uncertain', uncertaintyLocked: true });
  }

  return replaceAttempt(attempt, { ...attempt, lifecycle: 'sending', uncertaintyLocked: false });
}

function rememberUnknown(attempt: AllocationDisputeResolutionAttempt): void {
  if (!storageOwns(attempt)) {
    return;
  }

  try {
    replaceAttempt(attempt, { ...attempt, lifecycle: 'uncertain', uncertaintyLocked: true });
  } catch {
    // The original descriptor remains when the uncertain mark cannot be stored.
  }
}

function replaceIfOwned(
  previous: AllocationDisputeResolutionAttempt,
  next: AllocationDisputeResolutionAttempt,
): void {
  if (!storageOwns(previous)) {
    return;
  }

  replaceAttempt(previous, next);
}

function storageOwns(attempt: AllocationDisputeResolutionAttempt): boolean {
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
    operation: AllocationDisputeResolutionAttempt['operation'];
    organizationId: string;
    actorSource: AllocationDisputeResolutionAttempt['actorSource'];
    actorId: string;
    disputeId: string;
    idempotencyKey: string;
    requestBody: string;
    outcome: AllocationDisputeResolutionOutcome;
    normalizedReason: string;
  },
  attempt: {
    operation: AllocationDisputeResolutionAttempt['operation'];
    organizationId: string;
    actorSource: AllocationDisputeResolutionAttempt['actorSource'];
    actorId: string;
    disputeId: string;
    idempotencyKey: string;
    requestBody: string;
    outcome: AllocationDisputeResolutionOutcome;
    normalizedReason: string;
  },
): boolean {
  return stored.operation === attempt.operation
    && stored.organizationId === attempt.organizationId
    && stored.actorSource === attempt.actorSource
    && stored.actorId === attempt.actorId
    && stored.disputeId === attempt.disputeId
    && stored.idempotencyKey === attempt.idempotencyKey
    && stored.requestBody === attempt.requestBody
    && stored.outcome === attempt.outcome
    && stored.normalizedReason === attempt.normalizedReason;
}

function replaceAttempt(
  previous: AllocationDisputeResolutionAttempt,
  next: AllocationDisputeResolutionAttempt,
): AllocationDisputeResolutionAttempt {
  const stored = readStoredAttempt();
  if (stored.status !== 'present' || !sameIdentity(stored.attempt, previous)) {
    throw new AllocationDisputeResolutionAttemptChangedError();
  }

  persistAttempt(next);
  return readBack(next);
}

function persistAttempt(attempt: AllocationDisputeResolutionAttempt): void {
  const serialized = JSON.stringify(attempt);
  try {
    sessionStorage.setItem(allocationDisputeResolutionAttemptStorageKey, serialized);
    if (sessionStorage.getItem(allocationDisputeResolutionAttemptStorageKey) !== serialized) {
      throw new AllocationDisputeResolutionStorageError();
    }
  } catch (error) {
    if (error instanceof AllocationDisputeResolutionStorageError) {
      throw error;
    }

    throw new AllocationDisputeResolutionStorageError();
  }

  readBack(attempt);
  publish();
}

function readBack(attempt: AllocationDisputeResolutionAttempt): AllocationDisputeResolutionAttempt {
  const stored = readStoredAttempt();
  if (stored.status !== 'present'
    || !sameIdentity(stored.attempt, attempt)
    || stored.attempt.lifecycle !== attempt.lifecycle
    || stored.attempt.uncertaintyLocked !== attempt.uncertaintyLocked) {
    throw new AllocationDisputeResolutionStorageError();
  }

  return stored.attempt;
}

function removeOwned(attempt: AllocationDisputeResolutionAttempt): void {
  const stored = readStoredAttempt();
  if (stored.status !== 'present' || !sameIdentity(stored.attempt, attempt)) {
    throw new AllocationDisputeResolutionAttemptChangedError();
  }

  removeKey();
}

function removeKey(): void {
  try {
    sessionStorage.removeItem(allocationDisputeResolutionAttemptStorageKey);
  } catch {
    throw new AllocationDisputeResolutionStorageCleanupError();
  }

  let remaining: string | null;
  try {
    remaining = sessionStorage.getItem(allocationDisputeResolutionAttemptStorageKey);
  } catch {
    throw new AllocationDisputeResolutionStorageCleanupError();
  }

  if (remaining !== null) {
    throw new AllocationDisputeResolutionStorageCleanupError();
  }

  publish();
}

function readStoredAttempt():
  | { status: 'empty' }
  | { status: 'invalid' }
  | { status: 'present'; attempt: AllocationDisputeResolutionAttempt } {
  let raw: string | null;
  try {
    raw = sessionStorage.getItem(allocationDisputeResolutionAttemptStorageKey);
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

function parseAttempt(value: unknown): AllocationDisputeResolutionAttempt | null {
  if (!isRecord(value)
    || value.schemaVersion !== schemaVersion
    || value.operation !== operation
    || value.actorSource !== actorSource
    || typeof value.actorId !== 'string'
    || typeof value.requestBody !== 'string'
    || typeof value.normalizedReason !== 'string'
    || (value.outcome !== 'upheld' && value.outcome !== 'not_upheld')
    || (value.lifecycle !== 'ready' && value.lifecycle !== 'sending' && value.lifecycle !== 'uncertain' && value.lifecycle !== 'rejected')
    || typeof value.uncertaintyLocked !== 'boolean'
    || typeof value.organizationId !== 'string'
    || !isExactGuid(value.organizationId)
    || typeof value.disputeId !== 'string'
    || !isExactGuid(value.disputeId)
    || typeof value.idempotencyKey !== 'string'
    || !isExactGuid(value.idempotencyKey)) {
    return null;
  }

  if (!isStoredActorId(value.actorId) || normalizeAllocationDisputeResolutionReason(value.normalizedReason) !== value.normalizedReason) {
    return null;
  }

  const attempt: AllocationDisputeResolutionAttempt = {
    schemaVersion,
    operation,
    organizationId: value.organizationId.toLowerCase(),
    actorSource,
    actorId: value.actorId,
    disputeId: value.disputeId.toLowerCase(),
    idempotencyKey: value.idempotencyKey,
    requestBody: value.requestBody,
    outcome: value.outcome,
    normalizedReason: value.normalizedReason,
    lifecycle: value.lifecycle,
    uncertaintyLocked: value.uncertaintyLocked,
  };
  if (attempt.requestBody !== serializeResolution(attempt.outcome, attempt.normalizedReason)) {
    return null;
  }

  return attempt;
}

function serializeResolution(outcome: AllocationDisputeResolutionOutcome, reason: string): string {
  return JSON.stringify({ outcome, reason });
}

function isStoredActorId(value: string): boolean {
  return value.length > 0 && value.length <= maximumActorLength && trimDotNetWhiteSpace(value) === value;
}

function requireOrganization(session: SessionSnapshot): string {
  const organizationId = developmentOrganizationId(session.token);
  if (session.token === null || organizationId === null) {
    throw new AllocationDisputeResolutionAuthenticationError();
  }

  return organizationId;
}

function requireActor(session: SessionSnapshot): string {
  const actorId = developmentActorId(session.token);
  if (actorId === null) {
    throw new AllocationDisputeResolutionActorRequiredError();
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
