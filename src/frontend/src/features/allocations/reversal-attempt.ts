import { parse } from 'lossless-json';
import { useSyncExternalStore } from 'react';

import { authenticationHeaders } from '../../shared/api/authentication';
import { isAbortError, StaleSessionError } from '../../shared/api/errors';
import { toApiError } from '../../shared/api/problem';
import { sessionStore, type SessionSnapshot } from '../../shared/api/session-store';
import { browserAuthMode } from '../../shared/auth/auth-mode';
import { useSession } from '../../shared/auth/session';
import { compareDecimalAmounts, readMoney, readMoneyAmount, type DecimalString } from '../../shared/money/money';
import { readAllocationOrganizationId } from '../reconciliation/allocation-attempt';
import { normalizeReversalReason } from './reversal-reason';

export const reversalAttemptStorageKey = 'finopsdesk.reversal-attempt';

const schemaVersion = 1;
const operationType = 'Reversal';
const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const emptyGuid = '00000000-0000-0000-0000-000000000000';
const timestampPattern = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$/;
const minimumActorLength = 2;
const maximumActorLength = 256;

let submissionInFlight = false;
let storageVersion = 0;
const listeners = new Set<() => void>();

export class ReversalStorageError extends Error {
  constructor() {
    super('The reversal attempt could not be stored.');
    this.name = 'ReversalStorageError';
  }
}

export class ReversalStorageCleanupError extends Error {
  constructor() {
    super('The verified reversal could not be cleared from this browser tab.');
    this.name = 'ReversalStorageCleanupError';
  }
}

export class ReversalAttemptBlockedError extends Error {
  constructor() {
    super('A stored reversal attempt blocks a new reversal.');
    this.name = 'ReversalAttemptBlockedError';
  }
}

export class UnresolvedReversalAttemptError extends Error {
  constructor() {
    super('An unresolved reversal attempt is already stored.');
    this.name = 'UnresolvedReversalAttemptError';
  }
}

export class ReversalAuthenticationError extends Error {
  constructor() {
    super('Sign in before reversing an allocation.');
    this.name = 'ReversalAuthenticationError';
  }
}

export class ReversalActorRequiredError extends Error {
  constructor() {
    super('This reversal needs an operator identity.');
    this.name = 'ReversalActorRequiredError';
  }
}

export class ReversalTenantMismatchError extends Error {
  constructor() {
    super('The stored reversal belongs to another organization.');
    this.name = 'ReversalTenantMismatchError';
  }
}

export class ReversalSubmissionInProgressError extends Error {
  constructor() {
    super('A reversal request is already being sent.');
    this.name = 'ReversalSubmissionInProgressError';
  }
}

export class UnknownReversalOutcomeError extends Error {
  constructor() {
    super('The reversal was not verified.');
    this.name = 'UnknownReversalOutcomeError';
  }
}

export class UnreadableReversalResultError extends Error {
  constructor() {
    super('The reversal response could not be verified.');
    this.name = 'UnreadableReversalResultError';
  }
}

export class ReversalAttemptChangedError extends Error {
  constructor() {
    super('The stored reversal changed before it could be cleared.');
    this.name = 'ReversalAttemptChangedError';
  }
}

export class ReversalReasonError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'ReversalReasonError';
  }
}

export class ReversalPreflightError extends Error {
  readonly kind: 'reversed';

  constructor() {
    super('This allocation is already reversed.');
    this.name = 'ReversalPreflightError';
    this.kind = 'reversed';
  }
}

export interface ReversalAttempt {
  schemaVersion: 1;
  operation: 'Reversal';
  allocationId: string;
  invoiceId: string;
  bankTransactionId: string;
  organizationId: string;
  idempotencyKey: string;
  reason: string;
  requestBody: string;
  amount: DecimalString;
  currency: string;
  lifecycle: 'ready' | 'uncertain';
  createdAt: string;
}

export interface BeginReversalInput {
  allocationId: string;
  invoiceId: string;
  bankTransactionId: string;
  amount: DecimalString;
  currency: string;
  reason: string;
}

export interface VerifiedReversal {
  allocationId: string;
  invoiceId: string;
  bankTransactionId: string;
  amount: DecimalString;
  currency: string;
  reason: string;
  reversedAt: string;
  replayed: boolean;
  epoch: number;
}

export type ReversalAttemptView =
  | { status: 'none' }
  | { status: 'blocked-authentication' }
  | { status: 'blocked-tenant' }
  | { status: 'invalid-storage' }
  | { status: 'pending'; attempt: ReversalAttempt };

export function useReversalAttempt(): ReversalAttemptView {
  const session = useSession();
  useSyncExternalStore(subscribe, getReversalAttemptVersion, getReversalAttemptVersion);
  return inspectReversalAttempt(session);
}

export function inspectReversalAttempt(session: SessionSnapshot = sessionStore.getSnapshot()): ReversalAttemptView {
  const stored = readStoredAttempt();

  if (stored.status === 'empty') {
    return { status: 'none' };
  }

  if (stored.status === 'invalid') {
    return { status: 'invalid-storage' };
  }

  const organizationId = readAllocationOrganizationId(session.token);

  if (organizationId === null) {
    return { status: 'blocked-authentication' };
  }

  if (organizationId !== stored.attempt.organizationId) {
    return { status: 'blocked-tenant' };
  }

  return { status: 'pending', attempt: stored.attempt };
}

export function beginReversalAttempt(input: BeginReversalInput): ReversalAttempt {
  if (submissionInFlight) {
    throw new ReversalSubmissionInProgressError();
  }

  const existing = readStoredAttempt();

  if (existing.status === 'invalid') {
    throw new ReversalAttemptBlockedError();
  }

  if (existing.status === 'present') {
    throw new UnresolvedReversalAttemptError();
  }

  const session = sessionStore.getSnapshot();
  const organizationId = readAllocationOrganizationId(session.token);

  if (session.token === null || organizationId === null) {
    throw new ReversalAuthenticationError();
  }

  if (!hasActor(session.token)) {
    throw new ReversalActorRequiredError();
  }

  const decision = normalizeReversalReason(input.reason);

  if (!decision.ok) {
    throw new ReversalReasonError(decision.message);
  }

  const allocationId = requiredGuid(input.allocationId);
  const invoiceId = requiredGuid(input.invoiceId);
  const bankTransactionId = requiredGuid(input.bankTransactionId);
  const currency = input.currency.trim().toUpperCase();

  if (!/^[A-Z]{3}$/.test(currency)) {
    throw new ReversalStorageError();
  }

  const amount = readMoneyAmount(input.amount);

  if (amount !== input.amount || compareDecimalAmounts(amount, readMoneyAmount('0')) !== 1) {
    throw new ReversalStorageError();
  }

  const idempotencyKey = crypto.randomUUID();

  if (!isGuid(idempotencyKey)) {
    throw new ReversalStorageError();
  }

  const requestBody = JSON.stringify({ reason: decision.reason });
  const attempt: ReversalAttempt = {
    schemaVersion: 1,
    operation: operationType,
    allocationId,
    invoiceId,
    bankTransactionId,
    organizationId,
    idempotencyKey,
    reason: decision.reason,
    requestBody,
    amount,
    currency,
    lifecycle: 'ready',
    createdAt: new Date().toISOString(),
  };

  if (!requestAgrees(attempt)) {
    throw new ReversalStorageError();
  }

  persistAttempt(attempt);
  return readBack(attempt);
}

export async function submitReversalAttempt(signal?: AbortSignal): Promise<VerifiedReversal> {
  if (browserAuthMode() === 'cookie') {
    throw new ReversalAuthenticationError();
  }

  if (submissionInFlight) {
    throw new ReversalSubmissionInProgressError();
  }

  const stored = readStoredAttempt();

  if (stored.status !== 'present') {
    throw new ReversalAttemptBlockedError();
  }

  const session = sessionStore.getSnapshot();
  const organizationId = readAllocationOrganizationId(session.token);

  if (session.token === null || organizationId === null) {
    throw new ReversalAuthenticationError();
  }

  if (organizationId !== stored.attempt.organizationId) {
    throw new ReversalTenantMismatchError();
  }

  if (!hasActor(session.token)) {
    throw new ReversalActorRequiredError();
  }

  const epoch = session.epoch;
  submissionInFlight = true;

  try {
    const attempt = durableUncertain(stored.attempt);
    let response: Response;

    try {
      response = await fetch(`/api/allocations/${attempt.allocationId}/reverse`, {
        method: 'POST',
        credentials: 'same-origin',
        headers: {
          Accept: 'application/json',
          'Content-Type': 'application/json',
          ...authenticationHeaders(session, 'POST'),
          'Idempotency-Key': attempt.idempotencyKey,
        },
        body: attempt.requestBody,
        signal: signal === undefined ? session.signal : AbortSignal.any([signal, session.signal]),
      });
    } catch (error) {
      throw transportOutcome(epoch, error);
    }

    assertCurrentEpoch(epoch);
    let text: string;

    try {
      text = await response.text();
    } catch {
      assertCurrentEpoch(epoch);
      throw response.ok ? new UnreadableReversalResultError() : toApiError(response.status, '');
    }

    assertCurrentEpoch(epoch);

    if (!response.ok) {
      throw toApiError(response.status, text);
    }

    let verified: VerifiedReversal;

    try {
      verified = mapVerifiedReversal(parse(text), attempt, epoch);
      assertCurrentEpoch(epoch);
    } catch (error) {
      if (error instanceof StaleSessionError) {
        throw error;
      }

      assertCurrentEpoch(epoch);
      throw new UnreadableReversalResultError();
    }

    clearOwnedAttempt(attempt);
    return verified;
  } finally {
    submissionInFlight = false;
  }
}

export function abandonReversalAttempt(idempotencyKey: string): void {
  if (submissionInFlight) {
    throw new ReversalSubmissionInProgressError();
  }

  const stored = readStoredAttempt();

  if (stored.status !== 'present' || stored.attempt.idempotencyKey !== idempotencyKey) {
    throw new ReversalAttemptChangedError();
  }

  const organizationId = readAllocationOrganizationId(sessionStore.getSnapshot().token);

  if (organizationId === null) {
    throw new ReversalAuthenticationError();
  }

  if (organizationId !== stored.attempt.organizationId) {
    throw new ReversalTenantMismatchError();
  }

  try {
    sessionStorage.removeItem(reversalAttemptStorageKey);
  } catch {
    throw new ReversalStorageCleanupError();
  }

  let remaining: string | null;

  try {
    remaining = sessionStorage.getItem(reversalAttemptStorageKey);
  } catch {
    throw new ReversalStorageCleanupError();
  }

  if (remaining !== null) {
    throw new ReversalStorageCleanupError();
  }

  publish();
}

export function resetReversalAttemptStateForTests(): void {
  submissionInFlight = false;
  storageVersion += 1;
}

function durableUncertain(attempt: ReversalAttempt): ReversalAttempt {
  if (attempt.lifecycle === 'uncertain') {
    return attempt;
  }

  const uncertain: ReversalAttempt = { ...attempt, lifecycle: 'uncertain' };
  persistAttempt(uncertain);
  return readBack(uncertain);
}

function readBack(attempt: ReversalAttempt): ReversalAttempt {
  const stored = readStoredAttempt();

  if (
    stored.status !== 'present'
    || stored.attempt.idempotencyKey !== attempt.idempotencyKey
    || stored.attempt.requestBody !== attempt.requestBody
    || stored.attempt.lifecycle !== attempt.lifecycle
    || stored.attempt.reason !== attempt.reason
  ) {
    throw new ReversalStorageError();
  }

  return stored.attempt;
}

function persistAttempt(attempt: ReversalAttempt): void {
  const serialized = JSON.stringify(attempt);

  try {
    sessionStorage.setItem(reversalAttemptStorageKey, serialized);

    if (sessionStorage.getItem(reversalAttemptStorageKey) !== serialized) {
      throw new ReversalStorageError();
    }
  } catch (error) {
    if (error instanceof ReversalStorageError) {
      throw error;
    }

    throw new ReversalStorageError();
  }

  const stored = readStoredAttempt();

  if (
    stored.status !== 'present'
    || stored.attempt.requestBody !== attempt.requestBody
    || stored.attempt.idempotencyKey !== attempt.idempotencyKey
  ) {
    throw new ReversalStorageError();
  }

  publish();
}

function clearOwnedAttempt(attempt: ReversalAttempt): void {
  const stored = readStoredAttempt();

  if (
    stored.status !== 'present'
    || stored.attempt.idempotencyKey !== attempt.idempotencyKey
    || stored.attempt.requestBody !== attempt.requestBody
  ) {
    throw new ReversalAttemptChangedError();
  }

  try {
    sessionStorage.removeItem(reversalAttemptStorageKey);
  } catch {
    throw new ReversalStorageCleanupError();
  }

  let remaining: string | null;

  try {
    remaining = sessionStorage.getItem(reversalAttemptStorageKey);
  } catch {
    throw new ReversalStorageCleanupError();
  }

  if (remaining !== null) {
    throw new ReversalStorageCleanupError();
  }

  publish();
}

type StoredRead =
  | { status: 'empty' }
  | { status: 'invalid' }
  | { status: 'present'; attempt: ReversalAttempt };

function readStoredAttempt(): StoredRead {
  let raw: string | null;

  try {
    raw = sessionStorage.getItem(reversalAttemptStorageKey);
  } catch {
    return { status: 'invalid' };
  }

  if (raw === null) {
    return { status: 'empty' };
  }

  try {
    const attempt = parseStoredAttempt(JSON.parse(raw) as unknown);
    return attempt === null ? { status: 'invalid' } : { status: 'present', attempt };
  } catch {
    return { status: 'invalid' };
  }
}

function parseStoredAttempt(value: unknown): ReversalAttempt | null {
  if (!isRecord(value)) {
    return null;
  }

  if (value.schemaVersion !== schemaVersion || value.operation !== operationType) {
    return null;
  }

  if (value.lifecycle !== 'ready' && value.lifecycle !== 'uncertain') {
    return null;
  }

  if (!isGuid(value.allocationId) || !isGuid(value.invoiceId) || !isGuid(value.bankTransactionId) || !isGuid(value.organizationId) || !isGuid(value.idempotencyKey)) {
    return null;
  }

  if (typeof value.reason !== 'string' || typeof value.requestBody !== 'string' || typeof value.currency !== 'string' || typeof value.amount !== 'string' || typeof value.createdAt !== 'string') {
    return null;
  }

  if (!/^[A-Z]{3}$/.test(value.currency) || value.createdAt.length === 0 || Number.isNaN(Date.parse(value.createdAt))) {
    return null;
  }

  const decision = normalizeReversalReason(value.reason);

  if (!decision.ok || decision.reason !== value.reason) {
    return null;
  }

  let amount: DecimalString;

  try {
    amount = readMoneyAmount(value.amount);
  } catch {
    return null;
  }

  if (amount !== value.amount || compareDecimalAmounts(amount, readMoneyAmount('0')) !== 1) {
    return null;
  }

  const attempt: ReversalAttempt = {
    schemaVersion: 1,
    operation: operationType,
    allocationId: value.allocationId,
    invoiceId: value.invoiceId,
    bankTransactionId: value.bankTransactionId,
    organizationId: value.organizationId.toLowerCase(),
    idempotencyKey: value.idempotencyKey,
    reason: value.reason,
    requestBody: value.requestBody,
    amount,
    currency: value.currency,
    lifecycle: value.lifecycle,
    createdAt: value.createdAt,
  };

  return requestAgrees(attempt) ? attempt : null;
}

function requestAgrees(attempt: ReversalAttempt): boolean {
  let parsed: unknown;

  try {
    parsed = parse(attempt.requestBody);
  } catch {
    return false;
  }

  if (!isRecord(parsed) || Object.keys(parsed).length !== 1 || parsed.reason !== attempt.reason) {
    return false;
  }

  return true;
}

function mapVerifiedReversal(value: unknown, attempt: ReversalAttempt, epoch: number): VerifiedReversal {
  if (!isRecord(value) || value.status !== 'reversed' || typeof value.replayed !== 'boolean') {
    throw new UnreadableReversalResultError();
  }

  if (!isGuid(value.allocationId) || !sameGuid(value.allocationId, attempt.allocationId)) {
    throw new UnreadableReversalResultError();
  }

  let amount: DecimalString;
  let currency: string;

  try {
    const money = readMoney(value.amount);
    amount = money.amount;
    currency = money.currency;
  } catch {
    throw new UnreadableReversalResultError();
  }

  if (currency !== attempt.currency || compareDecimalAmounts(amount, attempt.amount) !== 0) {
    throw new UnreadableReversalResultError();
  }

  if (typeof value.reason !== 'string' || value.reason !== attempt.reason) {
    throw new UnreadableReversalResultError();
  }

  if (typeof value.reversedAt !== 'string' || !timestampPattern.test(value.reversedAt) || Number.isNaN(Date.parse(value.reversedAt))) {
    throw new UnreadableReversalResultError();
  }

  return {
    allocationId: value.allocationId,
    invoiceId: attempt.invoiceId,
    bankTransactionId: attempt.bankTransactionId,
    amount,
    currency: attempt.currency,
    reason: value.reason,
    reversedAt: value.reversedAt,
    replayed: value.replayed,
    epoch,
  };
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

function getReversalAttemptVersion(): number {
  return storageVersion;
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
    return new UnknownReversalOutcomeError();
  }

  return new UnknownReversalOutcomeError();
}

function assertCurrentEpoch(epoch: number): void {
  if (sessionStore.getSnapshot().epoch !== epoch) {
    throw new StaleSessionError();
  }
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

function requiredGuid(value: string): string {
  if (!isGuid(value)) {
    throw new ReversalStorageError();
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
