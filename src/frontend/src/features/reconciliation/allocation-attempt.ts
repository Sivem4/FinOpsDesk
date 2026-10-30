import { useSyncExternalStore } from 'react';
import { isLosslessNumber, parse } from 'lossless-json';

import { isAbortError, StaleSessionError } from '../../shared/api/errors';
import { toApiError } from '../../shared/api/problem';
import { sessionStore, type SessionSnapshot } from '../../shared/api/session-store';
import { useSession } from '../../shared/auth/session';
import {
  parseAllocationAmount,
  readMoney,
  serializeAllocationBody,
  type DecimalString,
} from '../../shared/money/money';

export const explicitAmountAttemptStorageKey = 'finopsdesk.explicit-amount-attempt';

const schemaVersion = 1;
const operationType = 'ExplicitAmount';
const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const emptyGuid = '00000000-0000-0000-0000-000000000000';
const minimumActorLength = 2;
const maximumActorLength = 256;

export class AllocationStorageError extends Error {
  constructor() {
    super('The allocation attempt could not be stored in this browser tab. The allocation was not sent.');
    this.name = 'AllocationStorageError';
  }
}

export class UnresolvedAllocationAttemptError extends Error {
  constructor() {
    super('An unresolved allocation attempt is already stored. It was not replaced.');
    this.name = 'UnresolvedAllocationAttemptError';
  }
}

export class AllocationAttemptBlockedError extends Error {
  constructor() {
    super('The stored allocation attempt cannot be sent. It was not replaced.');
    this.name = 'AllocationAttemptBlockedError';
  }
}

export class AllocationAuthenticationError extends Error {
  constructor() {
    super('An authenticated session for the stored organization is required before the allocation can be sent.');
    this.name = 'AllocationAuthenticationError';
  }
}

export class AllocationTenantMismatchError extends Error {
  constructor() {
    super('The stored allocation attempt belongs to a different organization and was not sent.');
    this.name = 'AllocationTenantMismatchError';
  }
}

export class AllocationActorRequiredError extends Error {
  constructor() {
    super('The current session has no actor, so the allocation was not sent.');
    this.name = 'AllocationActorRequiredError';
  }
}

export class AllocationSubmissionInProgressError extends Error {
  constructor() {
    super('This allocation attempt is already being sent.');
    this.name = 'AllocationSubmissionInProgressError';
  }
}

export class AllocationStorageCleanupError extends Error {
  constructor() {
    super('The allocation response was verified, but the local attempt could not be cleared. Retry the same attempt manually.');
    this.name = 'AllocationStorageCleanupError';
  }
}

export class AllocationAttemptChangedError extends Error {
  constructor() {
    super('The stored allocation attempt changed before the response was applied. The stored attempt was left unchanged.');
    this.name = 'AllocationAttemptChangedError';
  }
}

export class UnknownAllocationOutcomeError extends Error {
  constructor() {
    super('The allocation request ended before a server result was received. Cancelling the request does not prove the allocation was rolled back.');
    this.name = 'UnknownAllocationOutcomeError';
  }
}

export class UnreadableAllocationResultError extends Error {
  constructor() {
    super('The server returned an allocation result this client cannot verify. The allocation may already be stored.');
    this.name = 'UnreadableAllocationResultError';
  }
}

export interface ExplicitAmountAttempt {
  schemaVersion: 1;
  operation: 'ExplicitAmount';
  idempotencyKey: string;
  invoiceId: string;
  bankTransactionId: string;
  amount: DecimalString;
  currency: string;
  organizationId: string;
  requestBody: string;
  lifecycle: 'ready' | 'uncertain';
}

export interface VerifiedExplicitAmount {
  allocationId: string;
  invoiceId: string;
  bankTransactionId: string;
  amount: DecimalString;
  currency: string;
  replayed: boolean;
  epoch: number;
}

export type AllocationAttemptView =
  | { status: 'none' }
  | { status: 'pending'; attempt: ExplicitAmountAttempt }
  | { status: 'blocked-authentication' }
  | { status: 'blocked-tenant' }
  | { status: 'invalid-storage' };

export interface BeginExplicitAmountInput {
  invoiceId: string;
  bankTransactionId: string;
  amount: string;
  currency: string;
  organizationId: string;
}

const listeners = new Set<() => void>();
let storageVersion = 0;
let submissionInFlight = false;

export function subscribeAllocationAttempt(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

export function getAllocationAttemptVersion(): number {
  return storageVersion;
}

export function useAllocationAttempt(): AllocationAttemptView {
  const session = useSession();
  useSyncExternalStore(subscribeAllocationAttempt, getAllocationAttemptVersion, getAllocationAttemptVersion);
  return inspectAllocationAttempt(session);
}

export function inspectAllocationAttempt(session: SessionSnapshot = sessionStore.getSnapshot()): AllocationAttemptView {
  const stored = readStoredAttempt();

  if (stored.status === 'empty') {
    return { status: 'none' };
  }

  if (stored.status === 'invalid') {
    return { status: 'invalid-storage' };
  }

  const organizationId = readOrganizationId(session.token);

  if (organizationId === null) {
    return { status: 'blocked-authentication' };
  }

  if (organizationId !== stored.attempt.organizationId) {
    return { status: 'blocked-tenant' };
  }

  return { status: 'pending', attempt: stored.attempt };
}

export function beginExplicitAmountAttempt(input: BeginExplicitAmountInput): ExplicitAmountAttempt {
  if (submissionInFlight) {
    throw new AllocationSubmissionInProgressError();
  }

  const existing = readStoredAttempt();

  if (existing.status === 'invalid') {
    throw new AllocationAttemptBlockedError();
  }

  if (existing.status === 'present') {
    throw new UnresolvedAllocationAttemptError();
  }

  const session = sessionStore.getSnapshot();
  const currentOrganizationId = readOrganizationId(session.token);

  if (currentOrganizationId === null) {
    throw new AllocationAuthenticationError();
  }

  if (!hasActor(session.token)) {
    throw new AllocationActorRequiredError();
  }

  if (!sameGuid(input.organizationId, currentOrganizationId)) {
    throw new AllocationTenantMismatchError();
  }

  const invoiceId = requiredGuid(input.invoiceId, 'Invoice id');
  const bankTransactionId = requiredGuid(input.bankTransactionId, 'Bank transaction id');
  const organizationId = requiredGuid(input.organizationId, 'Organization id');
  const currency = input.currency.trim().toUpperCase();
  const amount = parseAllocationAmount(input.amount);
  const idempotencyKey = crypto.randomUUID();

  if (!isGuid(idempotencyKey)) {
    throw new AllocationStorageError();
  }

  const requestBody = serializeAllocationBody({
    invoiceId,
    bankTransactionId,
    currency,
    amount,
  });
  const attempt: ExplicitAmountAttempt = {
    schemaVersion: 1,
    operation: operationType,
    idempotencyKey,
    invoiceId,
    bankTransactionId,
    amount,
    currency,
    organizationId,
    requestBody,
    lifecycle: 'ready',
  };

  if (!requestAgrees(attempt)) {
    throw new AllocationStorageError();
  }

  persistAttempt(attempt);
  return attempt;
}

export async function submitExplicitAmountAttempt(signal?: AbortSignal): Promise<VerifiedExplicitAmount> {
  if (submissionInFlight) {
    throw new AllocationSubmissionInProgressError();
  }

  const stored = readStoredAttempt();

  if (stored.status === 'empty' || stored.status === 'invalid') {
    throw new AllocationAttemptBlockedError();
  }

  const attempt = stored.attempt;
  const session = sessionStore.getSnapshot();
  const organizationId = readOrganizationId(session.token);

  if (session.token === null || organizationId === null) {
    throw new AllocationAuthenticationError();
  }

  if (organizationId !== attempt.organizationId) {
    throw new AllocationTenantMismatchError();
  }

  if (!hasActor(session.token)) {
    throw new AllocationActorRequiredError();
  }

  const epoch = session.epoch;
  submissionInFlight = true;

  try {
    let response: Response;

    try {
      response = await fetch('/api/allocations', {
        method: 'POST',
        headers: {
          Accept: 'application/json',
          'Content-Type': 'application/json',
          Authorization: `Bearer ${session.token}`,
          'Idempotency-Key': attempt.idempotencyKey,
        },
        body: attempt.requestBody,
        signal: signal === undefined ? session.signal : AbortSignal.any([signal, session.signal]),
      });
    } catch (error) {
      markUncertain(attempt);
      throw transportOutcome(epoch, error);
    }

    assertCurrentEpoch(epoch);
    let text: string;

    try {
      text = await response.text();
    } catch {
      if (!response.ok) {
        markUncertain(attempt);
        throw toApiError(response.status, '');
      }

      assertCurrentEpoch(epoch);
      markUncertain(attempt);
      throw new UnreadableAllocationResultError();
    }

    assertCurrentEpoch(epoch);

    if (!response.ok) {
      markUncertain(attempt);
      throw toApiError(response.status, text);
    }

    let verified: VerifiedExplicitAmount;

    try {
      verified = mapVerifiedAllocation(parse(text), attempt, response.status, epoch);
      assertCurrentEpoch(epoch);
    } catch (error) {
      if (error instanceof StaleSessionError) {
        throw error;
      }

      assertCurrentEpoch(epoch);
      markUncertain(attempt);
      throw new UnreadableAllocationResultError();
    }

    clearOwnedAttempt(attempt);
    return verified;
  } finally {
    submissionInFlight = false;
  }
}

export function abandonExplicitAmountAttempt(): void {
  if (submissionInFlight) {
    throw new AllocationSubmissionInProgressError();
  }

  removeStoredAttempt();
}

export function resetAllocationAttemptStateForTests(): void {
  submissionInFlight = false;
  storageVersion += 1;
}

function persistAttempt(attempt: ExplicitAmountAttempt): void {
  const serialized = serializeStoredAttempt(attempt);

  try {
    sessionStorage.setItem(explicitAmountAttemptStorageKey, serialized);

    if (sessionStorage.getItem(explicitAmountAttemptStorageKey) !== serialized) {
      throw new AllocationStorageError();
    }
  } catch (error) {
    if (error instanceof AllocationStorageError) {
      throw error;
    }

    throw new AllocationStorageError();
  }

  const stored = readStoredAttempt();

  if (stored.status !== 'present' || stored.attempt.requestBody !== attempt.requestBody || stored.attempt.idempotencyKey !== attempt.idempotencyKey) {
    throw new AllocationStorageError();
  }

  publish();
}

function markUncertain(attempt: ExplicitAmountAttempt): void {
  const stored = readStoredAttempt();

  if (stored.status !== 'present' || stored.attempt.idempotencyKey !== attempt.idempotencyKey || stored.attempt.requestBody !== attempt.requestBody) {
    return;
  }

  if (stored.attempt.lifecycle === 'uncertain') {
    return;
  }

  try {
    persistAttempt({ ...stored.attempt, lifecycle: 'uncertain' });
  } catch {
    // The original descriptor remains. A failed lifecycle update must not delete it.
  }
}

function clearOwnedAttempt(attempt: ExplicitAmountAttempt): void {
  if (!storedAttemptOwns(attempt)) {
    throw new AllocationAttemptChangedError();
  }

  try {
    sessionStorage.removeItem(explicitAmountAttemptStorageKey);
  } catch {
    throw new AllocationStorageCleanupError();
  }

  let remaining: string | null;

  try {
    remaining = sessionStorage.getItem(explicitAmountAttemptStorageKey);
  } catch {
    throw new AllocationStorageCleanupError();
  }

  if (remaining !== null) {
    throw new AllocationStorageCleanupError();
  }

  publish();
}

function storedAttemptOwns(attempt: ExplicitAmountAttempt): boolean {
  const stored = readStoredAttempt();
  return stored.status === 'present'
    && stored.attempt.idempotencyKey === attempt.idempotencyKey
    && stored.attempt.requestBody === attempt.requestBody;
}

function removeStoredAttempt(): void {
  sessionStorage.removeItem(explicitAmountAttemptStorageKey);
  publish();
}

function publish(): void {
  storageVersion += 1;

  for (const listener of listeners) {
    listener();
  }
}

function transportOutcome(epoch: number, error: unknown): Error {
  if (sessionStore.getSnapshot().epoch !== epoch) {
    return new StaleSessionError();
  }

  if (isAbortError(error)) {
    return new UnknownAllocationOutcomeError();
  }

  return new UnknownAllocationOutcomeError();
}

function assertCurrentEpoch(epoch: number): void {
  if (sessionStore.getSnapshot().epoch !== epoch) {
    throw new StaleSessionError();
  }
}

type StoredRead =
  | { status: 'empty' }
  | { status: 'invalid' }
  | { status: 'present'; attempt: ExplicitAmountAttempt };

function readStoredAttempt(): StoredRead {
  let raw: string | null;

  try {
    raw = sessionStorage.getItem(explicitAmountAttemptStorageKey);
  } catch {
    return { status: 'invalid' };
  }

  if (raw === null) {
    return { status: 'empty' };
  }

  try {
    const parsed: unknown = JSON.parse(raw);
    const attempt = parseStoredAttempt(parsed);
    return attempt === null ? { status: 'invalid' } : { status: 'present', attempt };
  } catch {
    return { status: 'invalid' };
  }
}

function parseStoredAttempt(value: unknown): ExplicitAmountAttempt | null {
  if (!isRecord(value)) {
    return null;
  }

  if (value.schemaVersion !== schemaVersion || value.operation !== operationType || (value.lifecycle !== 'ready' && value.lifecycle !== 'uncertain')) {
    return null;
  }

  if (!isGuid(value.idempotencyKey) || !isGuid(value.invoiceId) || !isGuid(value.bankTransactionId) || !isGuid(value.organizationId)) {
    return null;
  }

  if (typeof value.currency !== 'string' || !/^[A-Z]{3}$/.test(value.currency) || typeof value.amount !== 'string' || typeof value.requestBody !== 'string') {
    return null;
  }

  let amount: DecimalString;

  try {
    amount = parseAllocationAmount(value.amount);
  } catch {
    return null;
  }

  if (amount !== value.amount) {
    return null;
  }

  const attempt: ExplicitAmountAttempt = {
    schemaVersion: 1,
    operation: operationType,
    idempotencyKey: value.idempotencyKey,
    invoiceId: value.invoiceId,
    bankTransactionId: value.bankTransactionId,
    amount,
    currency: value.currency,
    organizationId: value.organizationId.toLowerCase(),
    requestBody: value.requestBody,
    lifecycle: value.lifecycle,
  };

  if (!sameGuid(attempt.invoiceId, value.invoiceId) || !sameGuid(attempt.bankTransactionId, value.bankTransactionId) || !sameGuid(attempt.idempotencyKey, value.idempotencyKey)) {
    return null;
  }

  attempt.invoiceId = value.invoiceId;
  attempt.bankTransactionId = value.bankTransactionId;
  attempt.idempotencyKey = value.idempotencyKey;
  attempt.organizationId = value.organizationId.toLowerCase();

  return requestAgrees(attempt) ? attempt : null;
}

function requestAgrees(attempt: ExplicitAmountAttempt): boolean {
  let parsed: unknown;

  try {
    parsed = parse(attempt.requestBody);
  } catch {
    return false;
  }

  if (!isRecord(parsed)) {
    return false;
  }

  const keys = Object.keys(parsed);

  if (keys.length !== 3 || parsed.invoiceId !== attempt.invoiceId || parsed.bankTransactionId !== attempt.bankTransactionId || !isRecord(parsed.amount)) {
    return false;
  }

  const amountKeys = Object.keys(parsed.amount);

  if (amountKeys.length !== 2 || parsed.amount.currency !== attempt.currency || !isLosslessNumber(parsed.amount.amount)) {
    return false;
  }

  try {
    const money = readMoney(parsed.amount);
    return money.amount === attempt.amount && money.currency === attempt.currency;
  } catch {
    return false;
  }
}

function mapVerifiedAllocation(
  value: unknown,
  attempt: ExplicitAmountAttempt,
  status: number,
  epoch: number,
): VerifiedExplicitAmount {
  if (!isRecord(value) || typeof value.replayed !== 'boolean') {
    throw new UnreadableAllocationResultError();
  }

  if (status === 201 && value.replayed) {
    throw new UnreadableAllocationResultError();
  }

  if (status === 200 && !value.replayed) {
    throw new UnreadableAllocationResultError();
  }

  if (status !== 200 && status !== 201) {
    throw new UnreadableAllocationResultError();
  }

  if (typeof value.allocationId !== 'string' || typeof value.invoiceId !== 'string' || typeof value.bankTransactionId !== 'string') {
    throw new UnreadableAllocationResultError();
  }

  if (!isGuid(value.allocationId) || !sameGuid(value.invoiceId, attempt.invoiceId) || !sameGuid(value.bankTransactionId, attempt.bankTransactionId)) {
    throw new UnreadableAllocationResultError();
  }

  const amount = readMoney(value.amount);

  if (amount.amount !== attempt.amount || amount.currency !== attempt.currency) {
    throw new UnreadableAllocationResultError();
  }

  return {
    allocationId: value.allocationId,
    invoiceId: value.invoiceId,
    bankTransactionId: value.bankTransactionId,
    amount: amount.amount,
    currency: amount.currency,
    replayed: value.replayed,
    epoch,
  };
}

function serializeStoredAttempt(attempt: ExplicitAmountAttempt): string {
  return JSON.stringify({
    schemaVersion: attempt.schemaVersion,
    operation: attempt.operation,
    idempotencyKey: attempt.idempotencyKey,
    invoiceId: attempt.invoiceId,
    bankTransactionId: attempt.bankTransactionId,
    amount: attempt.amount,
    currency: attempt.currency,
    organizationId: attempt.organizationId,
    requestBody: attempt.requestBody,
    lifecycle: attempt.lifecycle,
  });
}

function readOrganizationId(token: string | null): string | null {
  const claim = readClaim(token, 'org_id');
  return typeof claim === 'string' && isGuid(claim) ? claim.toLowerCase() : null;
}

function hasActor(token: string | null): boolean {
  const claim = readClaim(token, 'sub');
  return typeof claim === 'string' && claim === claim.trim() && claim.length >= minimumActorLength && claim.length <= maximumActorLength;
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
    const payload: unknown = JSON.parse(decodeBase64Url(parts[1]));
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

function requiredGuid(value: string, label: string): string {
  if (!isGuid(value)) {
    throw new Error(`${label} must be a non-empty GUID.`);
  }

  return value;
}

function isGuid(value: unknown): value is string {
  return typeof value === 'string' && guidPattern.test(value) && value.toLowerCase() !== emptyGuid;
}

function sameGuid(left: string, right: string): boolean {
  return left.toLowerCase() === right.toLowerCase();
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
