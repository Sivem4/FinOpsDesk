import { hashKey, type QueryCacheNotifyEvent } from '@tanstack/react-query';
import { parse } from 'lossless-json';
import { useSyncExternalStore } from 'react';

import { authenticationHeaders, rejectCookieFinancialRequest } from '../../shared/api/authentication';
import { ApiError, isAbortError, StaleSessionError, UnexpectedResponseError } from '../../shared/api/errors';
import { toApiError } from '../../shared/api/problem';
import { queryClient } from '../../shared/api/query-client';
import { sessionStore, type SessionSnapshot } from '../../shared/api/session-store';
import { useSession } from '../../shared/auth/session';
import { getReversalContext, type ReversalContext } from '../allocations/reversal-api';
import { invalidateVerifiedReversal } from '../allocations/reversal-completion';
import type { VerifiedReversal } from '../allocations/reversal-attempt';
import { compareDecimalAmounts, readMoney, type DecimalString } from '../../shared/money/money';
import {
  allocationDisputeDetailQueryKey,
  allocationDisputeIdsMatch,
  allocationDisputeResolutionQueryKey,
  fetchAllocationDisputeResolution,
  isAllocationDisputeTimestamp,
  isExactGuid,
  mapAllocationDisputeResolutionState,
  normalizeAllocationDisputeResolutionReason,
  type AllocationDisputeRecord,
  type AllocationDisputeResolutionState,
} from './allocation-dispute-api';

export const allocationDisputeCorrectionAttemptStorageKey = 'finopsdesk.allocation-dispute-correction-attempt';

const schemaVersion = 1;
const operation = 'AllocationDisputeCorrection';
const actorSource = 'developmentJwt';
const maximumActorLength = 256;
const badRequestCodes = new Set([
  'reconciliation.allocation_dispute.correction.invalid_request',
  'reconciliation.allocation_dispute.correction.reason_required',
  'reconciliation.allocation_dispute.correction.reason_too_long',
  'reconciliation.allocation_dispute.correction.idempotency_key_required',
  'reconciliation.allocation_dispute.correction.idempotency_key_invalid',
]);
const conflictCodes = new Set([
  'reconciliation.allocation_dispute.correction.idempotency_payload_conflict',
  'reconciliation.allocation_dispute.correction.decision_required',
  'reconciliation.allocation_dispute.correction.not_upheld',
  'reconciliation.allocation_dispute.correction.already_corrected',
  'reconciliation.allocation_dispute.correction.integrity_conflict',
  'reconciliation.allocation_dispute.correction.concurrency_conflict',
  'reconciliation.concurrency_conflict',
  'reconciliation.allocation.already_reversed',
  'reconciliation.allocation.reversal_reason_too_long',
  'reconciliation.allocation.reversal_target_mismatch',
  'reconciliation.invoice.reversal_exceeds_allocated',
  'reconciliation.bank_transaction.reversal_exceeds_allocated',
]);

export interface AllocationDisputeCorrectionAttempt {
  schemaVersion: 1;
  operation: 'AllocationDisputeCorrection';
  organizationId: string;
  actorSource: 'developmentJwt';
  actorId: string;
  disputeId: string;
  allocationId: string;
  invoiceId: string;
  bankTransactionId: string;
  amount: DecimalString;
  currency: string;
  idempotencyKey: string;
  requestBody: string;
  normalizedReason: string;
  lifecycle: 'ready' | 'sending' | 'uncertain' | 'rejected';
  uncertaintyLocked: boolean;
}

export interface VerifiedAllocationDisputeCorrection {
  disputeId: string;
  allocationId: string;
  invoiceId: string;
  bankTransactionId: string;
  amount: DecimalString;
  currency: string;
  reason: string;
  reversedAt: string;
  actorSource: 'developmentJwt' | 'productionCookie';
  actorId: string;
  replayed: boolean;
  epoch: number;
  cleanup: 'removed' | 'failed';
  idempotencyKey: string;
  requestBody: string;
  refresh: Promise<void>;
}

export type AllocationDisputeCorrectionAttemptView =
  | { status: 'none' }
  | { status: 'invalid-storage' }
  | { status: 'blocked-authentication' }
  | { status: 'blocked-tenant' }
  | { status: 'blocked-actor' }
  | { status: 'blocked-other-actor' }
  | { status: 'pending'; attempt: AllocationDisputeCorrectionAttempt };

interface ReadBoundary {
  serial: number;
  observedSerial: number | null;
}

interface CorrectionReads {
  decision: ReadBoundary;
  correction: ReadBoundary;
  contextRead: ReadBoundary;
  upheld: boolean;
  correctionNull: boolean;
  contextValue: ReversalContext | null;
}

let storageVersion = 0;
let submissionInFlight = false;
let activeView: CorrectionView | null = null;
let remembered: RememberedCorrection | null = null;
const listeners = new Set<() => void>();
const boundaries = new Map<string, CorrectionReads>();
const floors = new Map<string, ReadFloor>();
const confirmedAt = new Map<string, number>();
const fetchObservations = new Map<string, FetchObservation>();

queryClient.getQueryCache().subscribe((event) => {
  noteQueryEvent(event);
});

export class AllocationDisputeCorrectionStorageError extends Error {
  constructor() {
    super('The correction attempt could not be stored.');
    this.name = 'AllocationDisputeCorrectionStorageError';
  }
}

export class AllocationDisputeCorrectionCommandNotSentError extends Error {
  constructor() {
    super('The correction command was not sent.');
    this.name = 'AllocationDisputeCorrectionCommandNotSentError';
  }
}

export class AllocationDisputeCorrectionNotFreshError extends Error {
  constructor() {
    super('The correction needs a fresh upheld decision, an empty correction, and a confirmed allocation.');
    this.name = 'AllocationDisputeCorrectionNotFreshError';
  }
}

export class AllocationDisputeCorrectionAttemptBlockedError extends Error {
  constructor() {
    super('The stored correction command blocks a new command.');
    this.name = 'AllocationDisputeCorrectionAttemptBlockedError';
  }
}

export class UnknownAllocationDisputeCorrectionOutcomeError extends Error {
  constructor() {
    super('The correction result is unknown.');
    this.name = 'UnknownAllocationDisputeCorrectionOutcomeError';
  }
}

export class UnreadableAllocationDisputeCorrectionError extends Error {
  constructor() {
    super('The correction response could not be bound to the stored attempt.');
    this.name = 'UnreadableAllocationDisputeCorrectionError';
  }
}

export class AllocationDisputeCorrectionRejectedError extends Error {
  constructor(readonly status: number, readonly code: string | null) {
    super('The correction was refused.');
    this.name = 'AllocationDisputeCorrectionRejectedError';
  }
}

export class AllocationDisputeCorrectionUncertaintyRemainsError extends Error {
  constructor(readonly status: number, readonly code: string | null) {
    super('A later refusal did not clear the unresolved correction.');
    this.name = 'AllocationDisputeCorrectionUncertaintyRemainsError';
  }
}

export class AllocationDisputeCorrectionAttemptChangedError extends Error {
  constructor() {
    super('The stored correction attempt changed.');
    this.name = 'AllocationDisputeCorrectionAttemptChangedError';
  }
}

export class AllocationDisputeCorrectionStorageCleanupError extends Error {
  constructor() {
    super('The verified correction could not be removed from local tracking.');
    this.name = 'AllocationDisputeCorrectionStorageCleanupError';
  }
}

export class AllocationDisputeCorrectionSubmissionInProgressError extends Error {
  constructor() {
    super('A correction command is already being sent.');
    this.name = 'AllocationDisputeCorrectionSubmissionInProgressError';
  }
}

export class AllocationDisputeCorrectionVerifiedTrackingError extends Error {
  constructor() {
    super('The verified correction is still tracked locally.');
    this.name = 'AllocationDisputeCorrectionVerifiedTrackingError';
  }
}

interface CorrectionView {
  epoch: number;
  visit: number;
  disputeId: string;
  allocationId: string;
}

interface RememberedCorrection {
  organizationId: string;
  actorId: string;
  disputeId: string;
  allocationId: string;
  idempotencyKey: string;
  requestBody: string;
}

interface ReadFloor {
  detail: number;
  decision: number;
  correction: number;
  context: number;
  detailFetch: number;
  decisionFetch: number;
  correctionFetch: number;
  contextFetch: number;
}

interface FetchObservation {
  starts: number;
  activeStart: number | null;
  successStart: number;
}

export interface StoredAllocationDisputeCorrection {
  disputeId: string;
  allocationId: string;
  invoiceId: string;
  bankTransactionId: string;
  amount: DecimalString;
  currency: string;
  reason: string;
  actorSource: 'developmentJwt' | 'productionCookie';
  actorId: string;
  reversedAt: string;
}

export interface AllocationDisputeCorrectionState {
  disputeId: string;
  correction: StoredAllocationDisputeCorrection | null;
}

export function useAllocationDisputeCorrectionAttempt(): AllocationDisputeCorrectionAttemptView {
  useSession();
  useSyncExternalStore(subscribe, () => storageVersion, () => storageVersion);
  return inspect();
}

function inspect(): AllocationDisputeCorrectionAttemptView {
  const stored = readStored();
  if (stored.status === 'empty') {
    return { status: 'none' };
  }

  if (stored.status === 'invalid') {
    return { status: 'invalid-storage' };
  }

  const session = sessionStore.getSnapshot();
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

export function correctionVerifiedTrackingMatches(attempt: AllocationDisputeCorrectionAttempt): boolean {
  return rememberedMatches(attempt);
}

export function bindCorrectionCommandView(
  epoch: number,
  visit: number,
  disputeId: string,
  allocationId: string,
): void {
  activeView = {
    epoch,
    visit,
    disputeId,
    allocationId,
  };
}

export function correctionAttemptAllowed(epoch: number, disputeId: string): boolean {
  const session = sessionStore.getSnapshot();
  if (submissionInFlight || session.epoch !== epoch || session.token === null || readStored().status !== 'empty') {
    return false;
  }

  if (developmentOrganizationId(session.token) === null || developmentActorId(session.token) === null) {
    return false;
  }

  if (!isExactGuid(disputeId) || activeView === null || activeView.epoch !== epoch || activeView.disputeId !== disputeId) {
    return false;
  }

  if (confirmedBlocks(epoch, disputeId)) {
    return false;
  }

  const allocationId = activeView.allocationId;
  const current = readFloor(epoch, disputeId);
  const detail = freshQuery<AllocationDisputeRecord>(
    allocationDisputeDetailQueryKey(epoch, disputeId),
    current.detail,
    current.detailFetch,
    (record) => allocationDisputeIdsMatch(record.disputeId, disputeId)
      && allocationDisputeIdsMatch(record.allocationId, allocationId),
  );
  const decision = freshQuery<AllocationDisputeResolutionState>(
    allocationDisputeResolutionQueryKey(epoch, disputeId),
    current.decision,
    current.decisionFetch,
    (state) => allocationDisputeIdsMatch(state.disputeId, disputeId) && state.decision?.outcome === 'upheld',
  );
  const correction = freshQuery<AllocationDisputeCorrectionState>(
    allocationDisputeCorrectionQueryKey(epoch, disputeId),
    current.correction,
    current.correctionFetch,
    (state) => allocationDisputeIdsMatch(state.disputeId, disputeId) && state.correction === null,
  );
  const context = freshQuery<ReversalContext>(
    reversalContextQueryKey(epoch, allocationId, disputeId),
    current.context,
    current.contextFetch,
    (value) => value.status === 'confirmed' && allocationDisputeIdsMatch(value.allocationId, allocationId),
  );
  return detail && decision && correction && context;
}

export function allocationDisputeCorrectionQueryKey(epoch: number, disputeId: string) {
  return ['allocation-disputes', 'correction', epoch, disputeId] as const;
}

export function reversalContextQueryKey(epoch: number, allocationId: string, disputeId: string) {
  return ['allocations', 'reversal-context', epoch, allocationId, disputeId] as const;
}

function freshQuery<T>(
  key: readonly unknown[],
  floor: number,
  requiredFetch: number,
  accept: (data: T) => boolean,
): boolean {
  const state = queryClient.getQueryState<T>(key);
  const data = queryClient.getQueryData<T>(key);
  if (state?.status !== 'success' || state.fetchStatus !== 'idle' || data === undefined || !accept(data)) {
    return false;
  }

  if (requiredFetch === 0) {
    return state.dataUpdatedAt > floor;
  }

  return fetchObservation(hashKey(key)).successStart >= requiredFetch;
}

export async function loadCorrectionObservation(
  disputeId: string,
  epoch: number,
  signal: AbortSignal,
): Promise<AllocationDisputeCorrectionState> {
  rejectDevelopmentCookie();
  const started = boundary(epoch, disputeId).correction.serial;
  const document = await apiGetCorrection(disputeId, signal);
  if (sessionStore.getSnapshot().epoch !== epoch) {
    throw new StaleSessionError();
  }

  const current = boundary(epoch, disputeId);
  const state = mapCorrectionState(document, disputeId);
  if (current.correction.serial === started) {
    current.correction.observedSerial = started;
    current.correctionNull = state.correction === null;
    queryClient.setQueryData(allocationDisputeCorrectionQueryKey(epoch, disputeId), state);
    releaseConfirmedWhenStored(epoch, disputeId);
    publish();
  }

  return state;
}

export async function loadDecisionObservation(
  disputeId: string,
  epoch: number,
  signal: AbortSignal,
): Promise<AllocationDisputeResolutionState> {
  rejectDevelopmentCookie();
  const requestedId = disputeId.toLowerCase();
  const started = boundary(epoch, requestedId).decision.serial;
  const document = await fetchAllocationDisputeResolution(requestedId, signal);
  if (sessionStore.getSnapshot().epoch !== epoch) {
    throw new StaleSessionError();
  }

  const state = mapAllocationDisputeResolutionState(document, requestedId);
  const current = boundary(epoch, requestedId);
  if (current.decision.serial === started) {
    current.decision.observedSerial = started;
    current.upheld = state.decision?.outcome === 'upheld';
    queryClient.setQueryData(allocationDisputeResolutionQueryKey(epoch, requestedId), state);
    publish();
  }

  return state;
}

export async function loadContextObservation(
  allocationId: string,
  disputeId: string,
  epoch: number,
  signal: AbortSignal,
): Promise<ReversalContext> {
  rejectDevelopmentCookie();
  const started = boundary(epoch, disputeId).contextRead.serial;
  const context = await getReversalContext(allocationId, signal);
  if (sessionStore.getSnapshot().epoch !== epoch) {
    throw new StaleSessionError();
  }

  const current = boundary(epoch, disputeId);
  if (current.contextRead.serial === started) {
    current.contextRead.observedSerial = started;
    current.contextValue = context;
    queryClient.setQueryData(reversalContextQueryKey(epoch, allocationId, disputeId), context);
    publish();
  }

  return context;
}

export async function submitAllocationDisputeCorrection(
  disputeId: string,
  reason: string,
): Promise<VerifiedAllocationDisputeCorrection> {
  rejectDevelopmentCookie();
  if (submissionInFlight) {
    throw new AllocationDisputeCorrectionSubmissionInProgressError();
  }

  const session = sessionStore.getSnapshot();
  const epoch = session.epoch;
  if (!correctionAttemptAllowed(epoch, disputeId)) {
    throw new AllocationDisputeCorrectionNotFreshError();
  }

  const normalizedReason = normalizeAllocationDisputeResolutionReason(reason);
  const allocationId = activeView?.allocationId ?? '';
  const context = queryClient.getQueryData<ReversalContext>(reversalContextQueryKey(epoch, allocationId, disputeId));
  if (normalizedReason === null || context === undefined || context.status !== 'confirmed' || !allocationDisputeIdsMatch(context.allocationId, allocationId)) {
    throw new AllocationDisputeCorrectionNotFreshError();
  }

  const attempt: AllocationDisputeCorrectionAttempt = {
    schemaVersion,
    operation,
    organizationId: requireOrganization(session),
    actorSource,
    actorId: requireActor(session),
    disputeId,
    allocationId: context.allocationId.toLowerCase(),
    invoiceId: context.invoiceId.toLowerCase(),
    bankTransactionId: context.bankTransactionId.toLowerCase(),
    amount: context.amount,
    currency: context.currency,
    idempotencyKey: crypto.randomUUID(),
    requestBody: JSON.stringify({ reason: normalizedReason }),
    normalizedReason,
    lifecycle: 'ready',
    uncertaintyLocked: false,
  };
  submissionInFlight = true;
  try {
    let prepared: AllocationDisputeCorrectionAttempt;
    try {
      writeAttempt(attempt);
      prepared = markDispatch(attempt);
    } catch (error) {
      if (error instanceof AllocationDisputeCorrectionStorageError) {
        throw new AllocationDisputeCorrectionCommandNotSentError();
      }

      throw error;
    }

    return await postPrepared(prepared, epoch);
  } finally {
    submissionInFlight = false;
  }
}

export async function replayAllocationDisputeCorrectionAttempt(): Promise<VerifiedAllocationDisputeCorrection> {
  rejectDevelopmentCookie();
  if (submissionInFlight) {
    throw new AllocationDisputeCorrectionSubmissionInProgressError();
  }

  const stored = readStored();
  if (stored.status !== 'present') {
    throw new AllocationDisputeCorrectionAttemptBlockedError();
  }

  const session = sessionStore.getSnapshot();
  if (!callerOwns(session, stored.attempt)) {
    throw new AllocationDisputeCorrectionAttemptBlockedError();
  }

  if (rememberedMatches(stored.attempt)) {
    throw new AllocationDisputeCorrectionVerifiedTrackingError();
  }

  if (stored.attempt.lifecycle === 'rejected') {
    throw new AllocationDisputeCorrectionRejectedError(0, null);
  }

  submissionInFlight = true;
  try {
    return await postPrepared(markDispatch(stored.attempt), session.epoch);
  } finally {
    submissionInFlight = false;
  }
}

export function promotePersistedSendingAllocationDisputeCorrection(): void {
  if (submissionInFlight) {
    return;
  }

  const stored = readStored();
  if (stored.status !== 'present' || stored.attempt.lifecycle !== 'sending') {
    return;
  }

  try {
    writeAttempt({ ...stored.attempt, lifecycle: 'uncertain', uncertaintyLocked: true });
  } catch {
    // The original sending record remains when the uncertain mark cannot be stored.
  }
}

export async function abandonAllocationDisputeCorrectionAttempt(
  idempotencyKey: string,
  requestBody: string,
): Promise<void> {
  const stored = readStored();
  if (stored.status !== 'present'
    || stored.attempt.idempotencyKey !== idempotencyKey
    || stored.attempt.requestBody !== requestBody) {
    throw new AllocationDisputeCorrectionAttemptChangedError();
  }

  if (rememberedMatches(stored.attempt) || !callerOwns(sessionStore.getSnapshot(), stored.attempt)) {
    throw new AllocationDisputeCorrectionAttemptChangedError();
  }

  const epoch = sessionStore.getSnapshot().epoch;
  removeOwned(stored.attempt);
  raiseCorrectionFloor(epoch, stored.attempt.disputeId, stored.attempt.allocationId);
}

export async function reloadVisibleCorrectionReads(
  epoch: number,
  disputeId: string,
  allocationId: string,
): Promise<void> {
  if (sessionStore.getSnapshot().epoch !== epoch || activeView === null) {
    return;
  }

  if (activeView.epoch !== epoch
    || !allocationDisputeIdsMatch(activeView.disputeId, disputeId)
    || !allocationDisputeIdsMatch(activeView.allocationId, allocationId)) {
    return;
  }

  const keys = [
    allocationDisputeDetailQueryKey(epoch, activeView.disputeId),
    allocationDisputeResolutionQueryKey(epoch, activeView.disputeId),
    allocationDisputeCorrectionQueryKey(epoch, activeView.disputeId),
    reversalContextQueryKey(epoch, activeView.allocationId, activeView.disputeId),
  ];
  await Promise.all(keys.map((queryKey) => queryClient.cancelQueries({ queryKey, exact: true })));
  await Promise.all(keys.map((queryKey) => queryClient.refetchQueries({ queryKey, exact: true })));
}

export function retryAllocationDisputeCorrectionDescriptorRemoval(): void {
  if (submissionInFlight) {
    throw new AllocationDisputeCorrectionSubmissionInProgressError();
  }

  const stored = readStored();
  const session = sessionStore.getSnapshot();
  if (stored.status !== 'present' || remembered === null || !rememberedMatches(stored.attempt) || !callerOwns(session, stored.attempt)) {
    throw new AllocationDisputeCorrectionAttemptChangedError();
  }

  const epoch = session.epoch;
  removeOwned(stored.attempt);
  remembered = null;
  raiseCorrectionFloor(epoch, stored.attempt.disputeId, stored.attempt.allocationId);
  publish();
}

function raiseCorrectionFloor(epoch: number, disputeId: string, allocationId: string): void {
  const current = boundary(epoch, disputeId);
  current.decision.serial += 1;
  current.decision.observedSerial = null;
  current.correction.serial += 1;
  current.correction.observedSerial = null;
  current.contextRead.serial += 1;
  current.contextRead.observedSerial = null;
  current.upheld = false;
  current.correctionNull = false;
  current.contextValue = null;
  const floor = readFloor(epoch, disputeId);
  floor.detail = observedAt(allocationDisputeDetailQueryKey(epoch, disputeId));
  floor.decision = observedAt(allocationDisputeResolutionQueryKey(epoch, disputeId));
  floor.correction = observedAt(allocationDisputeCorrectionQueryKey(epoch, disputeId));
  floor.context = observedAt(reversalContextQueryKey(epoch, allocationId, disputeId));
  floor.detailFetch = nextFetch(allocationDisputeDetailQueryKey(epoch, disputeId));
  floor.decisionFetch = nextFetch(allocationDisputeResolutionQueryKey(epoch, disputeId));
  floor.correctionFetch = nextFetch(allocationDisputeCorrectionQueryKey(epoch, disputeId));
  floor.contextFetch = nextFetch(reversalContextQueryKey(epoch, allocationId, disputeId));
  publish();
}

function nextFetch(key: readonly unknown[]): number {
  return fetchObservation(hashKey(key)).starts + 1;
}

function noteQueryEvent(event: QueryCacheNotifyEvent): void {
  if (event.type !== 'updated') {
    return;
  }

  const current = fetchObservation(event.query.queryHash);
  if (event.action.type === 'fetch') {
    current.starts += 1;
    current.activeStart = current.starts;
    return;
  }

  if (event.action.type === 'success' && event.action.manual !== true && current.activeStart !== null) {
    current.successStart = current.activeStart;
    current.activeStart = null;
  }
}

function fetchObservation(hash: string): FetchObservation {
  const existing = fetchObservations.get(hash);
  if (existing !== undefined) {
    return existing;
  }

  const created: FetchObservation = { starts: 0, activeStart: null, successStart: 0 };
  fetchObservations.set(hash, created);
  return created;
}

function observedAt(key: readonly unknown[]): number {
  return queryClient.getQueryState(key)?.dataUpdatedAt ?? Date.now();
}

export function resetAllocationDisputeCorrectionAttemptStateForTests(): void {
  submissionInFlight = false;
  activeView = null;
  remembered = null;
  boundaries.clear();
  floors.clear();
  confirmedAt.clear();
  fetchObservations.clear();
  sessionStorage.removeItem(allocationDisputeCorrectionAttemptStorageKey);
  publish();
}

async function postPrepared(
  prepared: AllocationDisputeCorrectionAttempt,
  epoch: number,
): Promise<VerifiedAllocationDisputeCorrection> {
  let response: Response;
  try {
    response = await fetch(`/api/allocation-disputes/${prepared.disputeId}/correction`, {
      method: 'POST',
      credentials: 'same-origin',
      headers: {
        Accept: 'application/json',
        'Content-Type': 'application/json',
        ...authenticationHeaders(sessionStore.getSnapshot(), 'POST'),
        'Idempotency-Key': prepared.idempotencyKey,
      },
      body: prepared.requestBody,
      signal: sessionStore.getSnapshot().signal,
    });
  } catch (error) {
    rememberUnknown(prepared);
    if (isAbortError(error) || error instanceof TypeError) {
      throw new UnknownAllocationDisputeCorrectionOutcomeError();
    }

    throw error;
  }

  if (response.status !== 200) {
    return refuse(response, prepared);
  }

  let text: string;
  try {
    text = await response.text();
  } catch {
    rememberUnknown(prepared);
    throw new UnreadableAllocationDisputeCorrectionError();
  }

  let verified: VerifiedAllocationDisputeCorrection;
  try {
    verified = bindResponse(parse(text) as unknown, prepared, epoch);
  } catch {
    rememberUnknown(prepared);
    throw new UnreadableAllocationDisputeCorrectionError();
  }

  markConfirmed(epoch, prepared.disputeId);
  let cleanup: 'removed' | 'failed' = 'removed';
  try {
    removeOwned(prepared);
  } catch (error) {
    if (error instanceof AllocationDisputeCorrectionStorageCleanupError && storageOwns(prepared)) {
      cleanup = 'failed';
      remembered = {
        organizationId: prepared.organizationId,
        actorId: prepared.actorId,
        disputeId: prepared.disputeId,
        allocationId: prepared.allocationId,
        idempotencyKey: prepared.idempotencyKey,
        requestBody: prepared.requestBody,
      };
    } else if (error instanceof AllocationDisputeCorrectionAttemptChangedError) {
      cleanup = 'removed';
    } else {
      throw error;
    }
  }

  const result = {
    ...verified,
    cleanup,
    refresh: sessionStore.getSnapshot().epoch === epoch
      ? refreshVerifiedCorrection(verified)
      : Promise.resolve(),
  };
  return result;
}

export async function rereadAllocationDisputeCorrection(epoch: number, disputeId: string): Promise<void> {
  if (sessionStore.getSnapshot().epoch !== epoch) {
    return;
  }

  try {
    await queryClient.fetchQuery({
      queryKey: allocationDisputeCorrectionQueryKey(epoch, disputeId),
      queryFn: ({ signal }) => loadCorrectionObservation(disputeId, epoch, signal),
    });
  } catch (error) {
    if (isQueryCancellation(error)) {
      return;
    }

    throw error;
  }
}

function isQueryCancellation(error: unknown): boolean {
  return error instanceof Error && (error.name === 'CancelledError' || error.message === 'CancelledError');
}

export async function refreshVerifiedCorrection(result: VerifiedAllocationDisputeCorrection): Promise<void> {
  if (sessionStore.getSnapshot().epoch === result.epoch) {
    invalidateVerifiedReversal(asReversal(result));
  }

  await rereadAllocationDisputeCorrection(result.epoch, result.disputeId);
}

function asReversal(result: VerifiedAllocationDisputeCorrection): VerifiedReversal {
  return {
    allocationId: result.allocationId,
    invoiceId: result.invoiceId,
    bankTransactionId: result.bankTransactionId,
    amount: result.amount,
    currency: result.currency,
    reason: result.reason,
    reversedAt: result.reversedAt,
    replayed: result.replayed,
    epoch: result.epoch,
  };
}

function bindResponse(
  body: unknown,
  attempt: AllocationDisputeCorrectionAttempt,
  epoch: number,
): VerifiedAllocationDisputeCorrection {
  if (!isRecord(body)) {
    throw new UnexpectedResponseError('The correction response could not be read.');
  }

  const money = readMoney(body.amount);
  const disputeId = readGuid(body.disputeId);
  const allocationId = readGuid(body.allocationId);
  const invoiceId = readGuid(body.invoiceId);
  const bankTransactionId = readGuid(body.bankTransactionId);
  const actorSource = body.actorSource === 'developmentJwt' || body.actorSource === 'productionCookie'
    ? body.actorSource
    : null;
  if (body.status !== 'reversed'
    || actorSource === null
    || typeof body.actorId !== 'string'
    || body.actorId.length < 1
    || body.actorId.length > maximumActorLength
    || body.actorId.trim() !== body.actorId
    || typeof body.reason !== 'string'
    || typeof body.replayed !== 'boolean'
    || !isAllocationDisputeTimestamp(body.reversedAt)
    || !allocationDisputeIdsMatch(disputeId, attempt.disputeId)
    || !allocationDisputeIdsMatch(allocationId, attempt.allocationId)
    || !allocationDisputeIdsMatch(invoiceId, attempt.invoiceId)
    || !allocationDisputeIdsMatch(bankTransactionId, attempt.bankTransactionId)
    || body.reason !== attempt.normalizedReason
    || money.currency !== attempt.currency
    || compareDecimalAmounts(money.amount, attempt.amount) !== 0) {
    throw new UnexpectedResponseError('The correction response could not be read.');
  }

  return {
    disputeId: attempt.disputeId,
    allocationId: attempt.allocationId,
    invoiceId: attempt.invoiceId,
    bankTransactionId: attempt.bankTransactionId,
    amount: money.amount,
    currency: money.currency,
    reason: body.reason,
    reversedAt: body.reversedAt,
    actorSource,
    actorId: body.actorId,
    replayed: body.replayed,
    epoch,
    cleanup: 'removed',
    idempotencyKey: attempt.idempotencyKey,
    requestBody: attempt.requestBody,
    refresh: Promise.resolve(),
  };
}

async function refuse(response: Response, attempt: AllocationDisputeCorrectionAttempt): Promise<never> {
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
      throw new AllocationDisputeCorrectionUncertaintyRemainsError(error.status, error.code);
    }

    const stored = readStored();
    if (stored.status === 'present' && stored.attempt.idempotencyKey === attempt.idempotencyKey) {
      writeAttempt({ ...stored.attempt, lifecycle: 'rejected', uncertaintyLocked: false });
    }

    throw new AllocationDisputeCorrectionRejectedError(error.status, error.code);
  }

  rememberUnknown(attempt);
  throw new UnknownAllocationDisputeCorrectionOutcomeError();
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

  return false;
}

function rememberUnknown(attempt: AllocationDisputeCorrectionAttempt): void {
  const stored = readStored();
  if (stored.status !== 'present' || stored.attempt.idempotencyKey !== attempt.idempotencyKey) {
    return;
  }

  try {
    writeAttempt({ ...stored.attempt, lifecycle: 'uncertain', uncertaintyLocked: true });
  } catch {
    // The original descriptor remains when the uncertain mark cannot be stored.
  }
}

function markDispatch(attempt: AllocationDisputeCorrectionAttempt): AllocationDisputeCorrectionAttempt {
  const next: AllocationDisputeCorrectionAttempt = attempt.lifecycle === 'sending' || attempt.lifecycle === 'uncertain'
    ? { ...attempt, lifecycle: 'uncertain', uncertaintyLocked: true }
    : { ...attempt, lifecycle: 'sending', uncertaintyLocked: false };
  writeAttempt(next);
  return next;
}

function boundary(epoch: number, disputeId: string): CorrectionReads {
  const key = `${epoch}:${disputeId.toLowerCase()}`;
  const existing = boundaries.get(key);
  if (existing !== undefined) {
    return existing;
  }

  const created: CorrectionReads = {
    decision: { serial: 0, observedSerial: null },
    correction: { serial: 0, observedSerial: null },
    contextRead: { serial: 0, observedSerial: null },
    upheld: false,
    correctionNull: false,
    contextValue: null,
  };
  boundaries.set(key, created);
  return created;
}

async function apiGetCorrection(disputeId: string, signal: AbortSignal): Promise<unknown> {
  const response = await fetch(`/api/allocation-disputes/${disputeId}/correction`, {
    headers: {
      Accept: 'application/json',
      ...authenticationHeaders(sessionStore.getSnapshot(), 'GET'),
    },
    signal,
  });
  if (!response.ok) {
    throw toApiError(response.status, await response.text());
  }

  return parse(await response.text());
}

function mapCorrectionState(body: unknown, disputeId: string): AllocationDisputeCorrectionState {
  if (!isRecord(body) || !allocationDisputeIdsMatch(readGuid(body.disputeId), disputeId)) {
    throw new UnexpectedResponseError('The correction could not be read.');
  }

  if (body.correction === null) {
    return { disputeId, correction: null };
  }

  if (!isRecord(body.correction)) {
    throw new UnexpectedResponseError('The correction could not be read.');
  }

  const money = readMoney(body.correction.amount);
  const storedDisputeId = readGuid(body.correction.disputeId);
  const allocationId = readGuid(body.correction.allocationId);
  const invoiceId = readGuid(body.correction.invoiceId);
  const bankTransactionId = readGuid(body.correction.bankTransactionId);
  const actorSource = body.correction.actorSource === 'developmentJwt' || body.correction.actorSource === 'productionCookie'
    ? body.correction.actorSource
    : null;
  if (!allocationDisputeIdsMatch(storedDisputeId, disputeId)
    || actorSource === null
    || typeof body.correction.actorId !== 'string'
    || body.correction.actorId.length < 1
    || body.correction.actorId.length > maximumActorLength
    || body.correction.actorId.trim() !== body.correction.actorId
    || typeof body.correction.reason !== 'string'
    || body.correction.reason.length < 1
    || body.correction.reason.length > 500
    || !isAllocationDisputeTimestamp(body.correction.reversedAt)) {
    throw new UnexpectedResponseError('The correction could not be read.');
  }

  return {
    disputeId,
    correction: {
      disputeId,
      allocationId,
      invoiceId,
      bankTransactionId,
      amount: money.amount,
      currency: money.currency,
      reason: body.correction.reason,
      actorSource,
      actorId: body.correction.actorId,
      reversedAt: body.correction.reversedAt,
    },
  };
}

function storageOwns(attempt: AllocationDisputeCorrectionAttempt): boolean {
  const stored = readStored();
  return stored.status === 'present'
    && stored.attempt.idempotencyKey === attempt.idempotencyKey
    && stored.attempt.requestBody === attempt.requestBody;
}

function callerOwns(session: SessionSnapshot, attempt: { organizationId: string; actorId: string }): boolean {
  return developmentOrganizationId(session.token) === attempt.organizationId
    && developmentActorId(session.token) === attempt.actorId;
}

function rememberedMatches(attempt: AllocationDisputeCorrectionAttempt): boolean {
  return remembered !== null
    && remembered.organizationId === attempt.organizationId
    && remembered.actorId === attempt.actorId
    && remembered.disputeId === attempt.disputeId
    && remembered.allocationId === attempt.allocationId
    && remembered.idempotencyKey === attempt.idempotencyKey
    && remembered.requestBody === attempt.requestBody;
}

function markConfirmed(epoch: number, disputeId: string): void {
  confirmedAt.set(
    `${epoch}:${disputeId}`,
    queryClient.getQueryState(allocationDisputeCorrectionQueryKey(epoch, disputeId))?.dataUpdatedAt ?? Date.now(),
  );
}

function confirmedBlocks(epoch: number, disputeId: string): boolean {
  return confirmedAt.has(`${epoch}:${disputeId}`);
}

function releaseConfirmedWhenStored(epoch: number, disputeId: string): void {
  const marker = confirmedAt.get(`${epoch}:${disputeId}`);
  if (marker === undefined) {
    return;
  }

  const state = queryClient.getQueryState<AllocationDisputeCorrectionState>(allocationDisputeCorrectionQueryKey(epoch, disputeId));
  const data = queryClient.getQueryData<AllocationDisputeCorrectionState>(allocationDisputeCorrectionQueryKey(epoch, disputeId));
  if (state?.status === 'success'
    && state.fetchStatus === 'idle'
    && state.dataUpdatedAt > marker
    && data?.correction !== null
    && data?.correction !== undefined) {
    confirmedAt.delete(`${epoch}:${disputeId}`);
  }
}

function readFloor(epoch: number, disputeId: string): ReadFloor {
  const key = `${epoch}:${disputeId}`;
  const existing = floors.get(key);
  if (existing !== undefined) {
    return existing;
  }

  const created: ReadFloor = {
    detail: 0,
    decision: 0,
    correction: 0,
    context: 0,
    detailFetch: 0,
    decisionFetch: 0,
    correctionFetch: 0,
    contextFetch: 0,
  };
  floors.set(key, created);
  return created;
}

function writeAttempt(attempt: AllocationDisputeCorrectionAttempt): void {
  const serialized = JSON.stringify(attempt);
  try {
    sessionStorage.setItem(allocationDisputeCorrectionAttemptStorageKey, serialized);
  } catch {
    throw new AllocationDisputeCorrectionStorageError();
  }

  if (sessionStorage.getItem(allocationDisputeCorrectionAttemptStorageKey) !== serialized) {
    throw new AllocationDisputeCorrectionStorageError();
  }

  publish();
}

function removeOwned(attempt: AllocationDisputeCorrectionAttempt): void {
  const stored = readStored();
  if (stored.status !== 'present' || stored.attempt.idempotencyKey !== attempt.idempotencyKey) {
    throw new AllocationDisputeCorrectionAttemptChangedError();
  }

  try {
    sessionStorage.removeItem(allocationDisputeCorrectionAttemptStorageKey);
  } catch {
    throw new AllocationDisputeCorrectionStorageCleanupError();
  }

  if (sessionStorage.getItem(allocationDisputeCorrectionAttemptStorageKey) !== null) {
    throw new AllocationDisputeCorrectionStorageCleanupError();
  }

  publish();
}

function readStored(): { status: 'empty' } | { status: 'invalid' } | { status: 'present'; attempt: AllocationDisputeCorrectionAttempt } {
  const raw = sessionStorage.getItem(allocationDisputeCorrectionAttemptStorageKey);
  if (raw === null) {
    return { status: 'empty' };
  }

  try {
    const value: unknown = JSON.parse(raw);
    if (!isAttempt(value)) {
      return { status: 'invalid' };
    }

    return { status: 'present', attempt: value };
  } catch {
    return { status: 'invalid' };
  }
}

function isAttempt(value: unknown): value is AllocationDisputeCorrectionAttempt {
  if (!isRecord(value)) {
    return false;
  }

  return value.schemaVersion === schemaVersion
    && value.operation === operation
    && value.actorSource === actorSource
    && typeof value.organizationId === 'string'
    && typeof value.actorId === 'string'
    && typeof value.disputeId === 'string'
    && typeof value.allocationId === 'string'
    && typeof value.invoiceId === 'string'
    && typeof value.bankTransactionId === 'string'
    && typeof value.amount === 'string'
    && typeof value.currency === 'string'
    && typeof value.idempotencyKey === 'string'
    && typeof value.requestBody === 'string'
    && typeof value.normalizedReason === 'string'
    && (value.lifecycle === 'ready' || value.lifecycle === 'sending' || value.lifecycle === 'uncertain' || value.lifecycle === 'rejected')
    && typeof value.uncertaintyLocked === 'boolean';
}

function requireOrganization(session: SessionSnapshot): string {
  const organizationId = developmentOrganizationId(session.token);
  if (organizationId === null) {
    throw new AllocationDisputeCorrectionNotFreshError();
  }

  return organizationId;
}

function requireActor(session: SessionSnapshot): string {
  const actorId = developmentActorId(session.token);
  if (actorId === null) {
    throw new AllocationDisputeCorrectionNotFreshError();
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

  const actorId = claim.trim();
  if (actorId.length === 0 || actorId.length > maximumActorLength) {
    return null;
  }

  return actorId;
}

function developmentOrganizationId(token: string | null): string | null {
  const claim = readClaim(token, 'org_id');
  return typeof claim === 'string' && isExactGuid(claim) ? claim.toLowerCase() : null;
}

function readClaim(token: string | null, name: string): unknown {
  if (token === null) {
    return null;
  }

  const parts = token.split('.');
  if (parts.length !== 3) {
    return null;
  }

  try {
    const payload: unknown = JSON.parse(atob(parts[1].replace(/-/g, '+').replace(/_/g, '/')));
    return isRecord(payload) ? payload[name] : null;
  } catch {
    return null;
  }
}

function readGuid(value: unknown): string {
  if (typeof value !== 'string' || !isExactGuid(value)) {
    throw new UnexpectedResponseError('The correction response could not be read.');
  }

  return value.toLowerCase();
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

function publish(): void {
  storageVersion += 1;
  for (const listener of listeners) {
    listener();
  }
}
