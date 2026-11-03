import { parse } from 'lossless-json';

import { browserAuthMode } from '../auth/auth-mode';
import { financialWriteCapabilities } from '../auth/browser-session';
import { compareDecimalAmounts, readMoney, readMoneyAmount } from '../money/money';
import { sessionStore, type SessionSnapshot } from './session-store';
import {
  completeCookieRecovery,
  CookieRecoveryCleanupError,
  markRecoveryDisposition,
  persistDispatchedRecovery,
  readRecovery,
  UnresolvedCookieAttemptError,
  type CookieRecoveryOperation,
  type CookieRecoveryRecord,
} from './cookie-recovery';

export class CookieWriteBlockedError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'CookieWriteBlockedError';
  }
}

export const cookieUncertainMessage = 'The outcome is uncertain. A lost response or an HTTP 5xx response does not show that the server rolled the financial change back.';
export const cookieRevokedMessage = 'This attempt cannot be sent again until the write permission returns. The earlier request was not rolled back, and its idempotency key stays the same.';
export const cookieStaleMessage = 'The session changed after the request started. The response was not applied here, and the earlier attempt was not rolled back.';
export const cookieCleanupMessage = 'The server confirmed this operation. This browser could not remove the local recovery record. The original key was not replaced, and the operation was not rolled back.';
export const cookieRejectedFinalMessage = 'This attempt was rejected before the financial change was saved. Discard it before starting another one.';
export const cookieUncertainRetryMessage = 'This retry was refused. The earlier attempt stays unresolved, its key was not replaced, and it was not rolled back.';

const capabilityForOperation = {
  ConfirmExactMatch: 'reconciliation.confirm',
  CreateAllocation: 'allocations.create',
  ReverseAllocation: 'allocations.reverse',
} as const satisfies Record<CookieRecoveryOperation, (typeof financialWriteCapabilities)[number]>;

const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const emptyGuid = '00000000-0000-0000-0000-000000000000';
const timestampPattern = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$/;
const reserved = new Set<CookieRecoveryOperation>();

export interface CookieSecurityOrigin {
  epoch: number;
  readiness: number;
  issuer: string;
  subject: string;
  organizationId: string;
  capability: (typeof financialWriteCapabilities)[number];
}

export type CookieDispatchResult =
  | { kind: 'confirmed'; origin: CookieSecurityOrigin; cleanup: 'removed' | 'failed'; text: string }
  | { kind: 'rejected'; origin: CookieSecurityOrigin; status: number; text: string }
  | { kind: 'uncertain'; origin: CookieSecurityOrigin; text: string }
  | { kind: 'stale'; text: string }
  | { kind: 'blocked'; text: string };

export function resetCookieFinancialPostForTests(): void {
  reserved.clear();
}

export async function runCookieFinancialMutation(input: {
  operation: CookieRecoveryOperation;
  requestBody: string;
  context: Readonly<Record<string, string>>;
  idempotencyKey?: string;
  signal?: AbortSignal;
}): Promise<CookieDispatchResult> {
  if (!reserve(input.operation)) {
    return { kind: 'blocked', text: 'This financial attempt is already running. Another request was not sent.' };
  }

  try {
    let prepared: CookieRecoveryRecord;
    try {
      prepared = prepareCookieFinancialPost(input.operation, input.requestBody, input.context, input.idempotencyKey);
    } catch (error) {
      return blockedResult(error);
    }

    try {
      return await dispatchStored(prepared, input.signal ?? new AbortController().signal, 'initial');
    } catch {
      rememberUncertain(prepared);
      return { kind: 'uncertain', origin: captureOrigin(sessionStore.getSnapshot(), input.operation), text: cookieUncertainMessage };
    }
  } finally {
    reserved.delete(input.operation);
  }
}

export async function retryCookieFinancialMutation(
  operation: CookieRecoveryOperation,
  signal?: AbortSignal,
): Promise<CookieDispatchResult> {
  if (!reserve(operation)) {
    return { kind: 'blocked', text: 'This financial attempt is already running. Another request was not sent.' };
  }

  try {
    let storedRecord: CookieRecoveryRecord;
    try {
      const session = requireWritableSession(operation);
      const stored = readRecovery(operation, session);
      if (stored.status !== 'recoverable') {
        throw new CookieWriteBlockedError('The stored attempt is not available for this identity. The financial request was not sent.');
      }

      if (stored.record.disposition === 'dispatched') {
        markRecoveryDisposition(operation, stored.record.idempotencyKey, 'uncertain');
      }

      const current = readRecovery(operation, session);
      if (current.status !== 'recoverable') {
        throw new CookieWriteBlockedError('The recovery record could not be updated. The retry was not sent.');
      }

      assertOperationIdentity(current.record);
      storedRecord = current.record;
    } catch (error) {
      return blockedResult(error);
    }

    try {
      return await dispatchStored(storedRecord, signal ?? sessionStore.getSnapshot().signal, 'retry');
    } catch {
      rememberUncertain(storedRecord);
      return { kind: 'uncertain', origin: captureOrigin(sessionStore.getSnapshot(), operation), text: cookieUncertainMessage };
    }
  } finally {
    reserved.delete(operation);
  }
}

export function prepareCookieFinancialPost(
  operation: CookieRecoveryOperation,
  requestBody: string,
  context: Readonly<Record<string, string>>,
  idempotencyKey: string = crypto.randomUUID(),
): CookieRecoveryRecord {
  const session = requireWritableSession(operation);
  if (!guidPattern.test(idempotencyKey) || idempotencyKey.toLowerCase() === emptyGuid) {
    throw new CookieWriteBlockedError('The financial request was not sent.');
  }

  const record: CookieRecoveryRecord = {
    schemaVersion: 2,
    operation,
    issuer: session.issuer ?? '',
    subject: session.subject ?? '',
    organizationId: (session.activeOrganization?.organizationId ?? '').toLowerCase(),
    idempotencyKey: idempotencyKey.toLowerCase(),
    requestBody,
    fingerprint: '',
    disposition: 'dispatched',
    context,
  };
  assertOperationIdentity(record);
  return persistDispatchedRecovery(record);
}

export function originStillCurrent(origin: CookieSecurityOrigin): boolean {
  return sameOrigin(origin, sessionStore.getSnapshot());
}

async function dispatchStored(
  record: CookieRecoveryRecord,
  signal: AbortSignal,
  mode: 'initial' | 'retry',
): Promise<CookieDispatchResult> {
  const session = requireWritableSession(record.operation);
  const stored = readRecovery(record.operation, session);
  if (stored.status !== 'recoverable' || !sameAttempt(stored.record, record)) {
    throw new CookieWriteBlockedError('The stored attempt is not available for this identity. The financial request was not sent.');
  }

  assertOperationIdentity(stored.record);
  const origin = captureOrigin(session, record.operation);
  let response: Response;

  try {
    response = await fetch(routeFor(stored.record), {
      method: 'POST',
      credentials: 'same-origin',
      headers: {
        Accept: 'application/json',
        'Content-Type': 'application/json',
        'X-CSRF-Token': session.csrfToken ?? '',
        'Idempotency-Key': stored.record.idempotencyKey,
      },
      body: stored.record.requestBody,
      signal: AbortSignal.any([signal, session.signal]),
    });
  } catch {
    rememberUncertain(record);
    if (!originStillCurrent(origin)) {
      return { kind: 'stale', text: cookieStaleMessage };
    }

    return { kind: 'uncertain', origin, text: cookieUncertainMessage };
  }

  let text: string;
  try {
    text = await response.text();
  } catch {
    rememberUncertain(record);
    if (!originStillCurrent(origin)) {
      return { kind: 'stale', text: cookieStaleMessage };
    }

    return { kind: 'uncertain', origin, text: cookieUncertainMessage };
  }

  if (!originStillCurrent(origin)) {
    return { kind: 'stale', text: cookieStaleMessage };
  }

  if (!response.ok) {
    return classifyFailure(stored.record, origin, response.status, text, mode);
  }

  if (!successAgrees(stored.record, response.status, text)) {
    rememberUncertain(stored.record);
    return { kind: 'uncertain', origin, text: cookieUncertainMessage };
  }

  if (!originStillCurrent(origin)) {
    rememberUncertain(stored.record);
    return { kind: 'stale', text: cookieStaleMessage };
  }

  try {
    completeCookieRecovery(stored.record.operation, stored.record.idempotencyKey);
  } catch (error) {
    if (error instanceof CookieRecoveryCleanupError || error instanceof Error) {
      return { kind: 'confirmed', origin, cleanup: 'failed', text: cookieCleanupMessage };
    }

    return { kind: 'confirmed', origin, cleanup: 'failed', text: cookieCleanupMessage };
  }

  if (!originStillCurrent(origin)) {
    return { kind: 'stale', text: cookieStaleMessage };
  }

  return { kind: 'confirmed', origin, cleanup: 'removed', text: '' };
}

function classifyFailure(
  record: CookieRecoveryRecord,
  origin: CookieSecurityOrigin,
  status: number,
  body: string,
  mode: 'initial' | 'retry',
): CookieDispatchResult {
  const code = readProblemCode(body);
  const firstResponse = mode === 'initial' && record.disposition !== 'uncertain';
  if (status < 500 && firstResponse && provenPreCommit(status, code)) {
    try {
      markRecoveryDisposition(record.operation, record.idempotencyKey, 'rejected');
    } catch {
      rememberUncertain(record);
    }

    return { kind: 'rejected', origin, status, text: cookieRejectedFinalMessage };
  }

  rememberUncertain(record);
  if (status >= 500) {
    return { kind: 'uncertain', origin, text: cookieUncertainMessage };
  }

  if (mode === 'retry' || record.disposition === 'uncertain') {
    return { kind: 'uncertain', origin, text: cookieUncertainRetryMessage };
  }

  return { kind: 'uncertain', origin, text: cookieUncertainMessage };
}

function provenPreCommit(status: number, code: string | null): boolean {
  if (status === 401 || status === 403) {
    return true;
  }

  if (code === null) {
    return false;
  }

  if (code.includes('idempotency_payload_conflict')) {
    return false;
  }

  return provenPreCommitCodes.has(code);
}

function readProblemCode(body: string): string | null {
  try {
    const value: unknown = JSON.parse(body);
    if (isRecord(value) && typeof value.code === 'string') {
      return value.code;
    }
  } catch {
    return null;
  }

  return null;
}

function rememberUncertain(record: CookieRecoveryRecord): void {
  try {
    markRecoveryDisposition(record.operation, record.idempotencyKey, 'uncertain');
  } catch {
    // The original descriptor remains. A failed lifecycle update must not delete it.
  }
}

const provenPreCommitCodes = new Set<string>([
  'auth.csrf.invalid',
  'reconciliation.confirmation.invalid_request',
  'reconciliation.confirmation.idempotency_key_invalid',
  'reconciliation.confirmation.idempotency_key_required',
  'reconciliation.allocation.invalid_request',
  'reconciliation.allocation.invalid_amount',
  'reconciliation.allocation.reversal_invalid_request',
  'reconciliation.allocation.unsupported_media_type',
  'reconciliation.invoice.not_found',
  'reconciliation.bank_transaction.not_found',
  'reconciliation.allocation.not_found',
  'reconciliation.invoice.allocation_exceeds_outstanding',
  'reconciliation.bank_transaction.allocation_exceeds_unapplied',
  'reconciliation.concurrency_conflict',
  'reconciliation.allocation.already_reversed',
  'reconciliation.allocation.reversal_reason_too_long',
  'reconciliation.invoice.reversal_exceeds_allocated',
  'reconciliation.bank_transaction.reversal_exceeds_allocated',
]);

function reserve(operation: CookieRecoveryOperation): boolean {
  if (reserved.has(operation)) {
    return false;
  }

  reserved.add(operation);
  return true;
}

function blockedResult(error: unknown): CookieDispatchResult {
  if (error instanceof UnresolvedCookieAttemptError || error instanceof CookieWriteBlockedError) {
    return { kind: 'blocked', text: error.message };
  }

  if (error instanceof Error && error.name === 'CookieRecoveryStorageError') {
    return { kind: 'blocked', text: error.message };
  }

  return { kind: 'blocked', text: 'The financial request was not sent.' };
}

function requireWritableSession(operation: CookieRecoveryOperation): SessionSnapshot {
  const session = sessionStore.getSnapshot();
  const capability = capabilityForOperation[operation];
  if (browserAuthMode() !== 'cookie' || session.authorization !== 'verified' || session.issuer === null || session.subject === null || session.activeOrganization === null) {
    throw new CookieWriteBlockedError('A verified organization session is required. The financial request was not sent.');
  }

  if (!session.capabilities.includes(capability) || session.csrfToken === null || session.csrfToken.length === 0) {
    throw new CookieWriteBlockedError('The current session cannot send this financial request.');
  }

  return session;
}

function captureOrigin(session: SessionSnapshot, operation: CookieRecoveryOperation): CookieSecurityOrigin {
  return {
    epoch: session.epoch,
    readiness: session.readiness,
    issuer: session.issuer ?? '',
    subject: session.subject ?? '',
    organizationId: (session.activeOrganization?.organizationId ?? '').toLowerCase(),
    capability: capabilityForOperation[operation],
  };
}

function sameOrigin(origin: CookieSecurityOrigin, session: SessionSnapshot): boolean {
  return session.authorization === 'verified'
    && session.epoch === origin.epoch
    && session.readiness === origin.readiness
    && session.issuer === origin.issuer
    && session.subject === origin.subject
    && session.activeOrganization?.organizationId.toLowerCase() === origin.organizationId
    && session.capabilities.includes(origin.capability);
}

function sameAttempt(stored: CookieRecoveryRecord, requested: CookieRecoveryRecord): boolean {
  return stored.operation === requested.operation
    && stored.idempotencyKey === requested.idempotencyKey
    && stored.requestBody === requested.requestBody
    && stored.issuer === requested.issuer
    && stored.subject === requested.subject
    && stored.organizationId === requested.organizationId
    && canonical(stored.context) === canonical(requested.context);
}

function routeFor(record: CookieRecoveryRecord): string {
  if (record.operation === 'ConfirmExactMatch') {
    return '/api/matches/confirm';
  }

  if (record.operation === 'CreateAllocation') {
    return '/api/allocations';
  }

  return `/api/allocations/${requiredGuid(record.context.allocationId)}/reverse`;
}

function assertOperationIdentity(record: CookieRecoveryRecord): void {
  if (record.schemaVersion !== 2) {
    throw new CookieWriteBlockedError('The stored attempt is not available for this identity. The financial request was not sent.');
  }

  if (record.operation === 'ConfirmExactMatch') {
    const body = parseJson(record.requestBody);
    if (!sameGuid(bodyString(body, 'invoiceId'), record.context.invoiceId) || !sameGuid(bodyString(body, 'bankTransactionId'), record.context.bankTransactionId)) {
      throw new CookieWriteBlockedError('The stored attempt is not available for this identity. The financial request was not sent.');
    }

    return;
  }

  if (record.operation === 'CreateAllocation') {
    const body = parse(record.requestBody);
    if (!isRecord(body) || !sameGuid(bodyString(body, 'invoiceId'), record.context.invoiceId) || !sameGuid(bodyString(body, 'bankTransactionId'), record.context.bankTransactionId)) {
      throw new CookieWriteBlockedError('The stored attempt is not available for this identity. The financial request was not sent.');
    }

    const amount = readMoney(body.amount);
    if (amount.amount !== record.context.amount || amount.currency !== record.context.currency) {
      throw new CookieWriteBlockedError('The stored attempt is not available for this identity. The financial request was not sent.');
    }

    return;
  }

  requiredGuid(record.context.allocationId);
  requiredGuid(record.context.invoiceId);
  requiredGuid(record.context.bankTransactionId);
  const body = parseJson(record.requestBody);
  if (Object.keys(body).length !== 1 || body.reason !== record.context.reason || typeof record.context.reason !== 'string' || record.context.reason.trim().length < 2) {
    throw new CookieWriteBlockedError('The stored attempt is not available for this identity. The financial request was not sent.');
  }
}

function successAgrees(record: CookieRecoveryRecord, status: number, text: string): boolean {
  let value: unknown;
  try {
    value = parse(text);
  } catch {
    return false;
  }

  if (!isRecord(value) || typeof value.replayed !== 'boolean') {
    return false;
  }

  if (record.operation === 'ReverseAllocation') {
    return reversalAgrees(record, status, value);
  }

  if (!((status === 201 && value.replayed === false) || (status === 200 && value.replayed === true))) {
    return false;
  }

  if (!isGuid(textGuid(value.allocationId)) || !sameGuid(textGuid(value.invoiceId), record.context.invoiceId) || !sameGuid(textGuid(value.bankTransactionId), record.context.bankTransactionId)) {
    return false;
  }

  let amount: ReturnType<typeof readMoney>;
  try {
    amount = readMoney(value.amount);
  } catch {
    return false;
  }

  if (compareDecimalAmounts(amount.amount, readMoneyAmount('0')) !== 1) {
    return false;
  }

  if (record.operation === 'CreateAllocation' && (amount.amount !== record.context.amount || amount.currency !== record.context.currency)) {
    return false;
  }

  return true;
}

function reversalAgrees(record: CookieRecoveryRecord, status: number, value: Record<string, unknown>): boolean {
  if (status !== 200 || value.status !== 'reversed' || typeof value.replayed !== 'boolean') {
    return false;
  }

  if (!sameGuid(textGuid(value.allocationId), record.context.allocationId) || value.reason !== record.context.reason) {
    return false;
  }

  if (typeof value.reversedAt !== 'string' || !timestampPattern.test(value.reversedAt) || Number.isNaN(Date.parse(value.reversedAt))) {
    return false;
  }

  try {
    const amount = readMoney(value.amount);
    return amount.amount === record.context.amount && amount.currency === record.context.currency;
  } catch {
    return false;
  }
}

function parseJson(body: string): Record<string, unknown> {
  const value: unknown = JSON.parse(body);
  if (!isRecord(value)) {
    throw new CookieWriteBlockedError('The stored attempt is not available for this identity. The financial request was not sent.');
  }

  return value;
}

function bodyString(value: Record<string, unknown>, name: string): string {
  const entry = value[name];
  if (typeof entry !== 'string') {
    throw new CookieWriteBlockedError('The stored attempt is not available for this identity. The financial request was not sent.');
  }

  return entry;
}

function textGuid(value: unknown): string {
  return typeof value === 'string' ? value : '';
}

function requiredGuid(value: string | undefined): string {
  if (value === undefined || !isGuid(value)) {
    throw new CookieWriteBlockedError('The stored attempt is not available for this identity. The financial request was not sent.');
  }

  return value.toLowerCase();
}

function isGuid(value: string): boolean {
  return guidPattern.test(value) && value.toLowerCase() !== emptyGuid;
}

function sameGuid(left: string, right: string | undefined): boolean {
  return right !== undefined && left.toLowerCase() === right.toLowerCase() && isGuid(left);
}

function canonical(context: Readonly<Record<string, string>>): string {
  return Object.keys(context).sort().map((key) => `${key}=${context[key]}`).join('&');
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
