import { sessionStore, type SessionSnapshot } from './session-store';

export const cookieRecoveryKeys = {
  ConfirmExactMatch: 'finopsdesk.cookie-recovery.confirm',
  CreateAllocation: 'finopsdesk.cookie-recovery.allocation',
  ReverseAllocation: 'finopsdesk.cookie-recovery.reversal',
} as const;

export type CookieRecoveryOperation = keyof typeof cookieRecoveryKeys;

export class CookieRecoveryStorageError extends Error {
  constructor() {
    super('The recovery record could not be stored. The financial request was not sent.');
    this.name = 'CookieRecoveryStorageError';
  }
}

export class UnresolvedCookieAttemptError extends Error {
  constructor() {
    super('An unresolved financial attempt is already stored. A new request was not sent.');
    this.name = 'UnresolvedCookieAttemptError';
  }
}

export class CookieRecoveryCleanupError extends Error {
  constructor() {
    super('The server result was received, but this browser could not clear the local recovery record. The original key was not replaced, and the operation was not rolled back.');
    this.name = 'CookieRecoveryCleanupError';
  }
}

export type CookieRecoveryDisposition = 'dispatched' | 'uncertain' | 'rejected';

export interface CookieRecoveryRecord {
  schemaVersion: 2;
  operation: CookieRecoveryOperation;
  issuer: string;
  subject: string;
  organizationId: string;
  idempotencyKey: string;
  requestBody: string;
  fingerprint: string;
  disposition: CookieRecoveryDisposition;
  context: Readonly<Record<string, string>>;
}

export type CookieRecoveryView =
  | { status: 'none' }
  | { status: 'hidden' }
  | { status: 'invalid' }
  | { status: 'recoverable'; record: CookieRecoveryRecord };

const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const listeners = new Set<() => void>();
let storageVersion = 0;

export function subscribeCookieRecovery(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

export function cookieRecoveryVersion(): number {
  return storageVersion;
}

function publish(): void {
  storageVersion += 1;
  for (const listener of listeners) {
    listener();
  }
}

export function fingerprintBody(body: string): string {
  let hash = 0xcbf29ce484222325n;
  const prime = 0x100000001b3n;
  const mask = 0xffffffffffffffffn;
  for (let index = 0; index < body.length; index += 1) {
    hash ^= BigInt(body.charCodeAt(index));
    hash = (hash * prime) & mask;
  }

  return `${body.length.toString(16)}:${hash.toString(16)}`;
}

export function persistDispatchedRecovery(
  input: Omit<CookieRecoveryRecord, 'schemaVersion' | 'fingerprint'>,
): CookieRecoveryRecord {
  const record = signedRecord(input);
  const serialized = JSON.stringify(record);
  const key = cookieRecoveryKeys[record.operation];
  let existing: string | null;

  try {
    existing = sessionStorage.getItem(key);
  } catch {
    throw new CookieRecoveryStorageError();
  }

  if (existing !== null) {
    if (existing === serialized || sameStoredAttempt(existing, record)) {
      return recordFromRaw(existing, record.operation) ?? record;
    }

    throw new UnresolvedCookieAttemptError();
  }

  try {
    sessionStorage.setItem(key, serialized);
    if (sessionStorage.getItem(key) !== serialized) {
      throw new CookieRecoveryStorageError();
    }
  } catch (error) {
    if (error instanceof CookieRecoveryStorageError) {
      throw error;
    }

    throw new CookieRecoveryStorageError();
  }

  publish();
  return record;
}

export function readRecovery(operation: CookieRecoveryOperation, session: SessionSnapshot): CookieRecoveryView {
  const key = cookieRecoveryKeys[operation];
  let raw: string | null;

  try {
    raw = sessionStorage.getItem(key);
  } catch {
    return { status: 'invalid' };
  }

  if (raw === null) {
    return { status: 'none' };
  }

  const record = parseRecord(raw, operation);
  if (record === null) {
    return { status: 'invalid' };
  }

  if (!sameIdentity(record, session)) {
    return { status: 'hidden' };
  }

  if (record.fingerprint !== fingerprintBody(identityText(record))) {
    return { status: 'invalid' };
  }

  return { status: 'recoverable', record };
}

export function removeRecovery(operation: CookieRecoveryOperation, idempotencyKey: string): void {
  const stored = readRecovery(operation, sessionStore.getSnapshot());
  if (stored.status !== 'recoverable' || stored.record.idempotencyKey !== idempotencyKey) {
    return;
  }

  sessionStorage.removeItem(cookieRecoveryKeys[operation]);
  publish();
}

export function completeCookieRecovery(operation: CookieRecoveryOperation, idempotencyKey: string): void {
  const key = cookieRecoveryKeys[operation];
  let raw: string | null;
  try {
    raw = sessionStorage.getItem(key);
  } catch {
    throw new CookieRecoveryCleanupError();
  }

  const record = raw === null ? null : parseRecord(raw, operation);
  const session = sessionStore.getSnapshot();
  if (record === null || record.idempotencyKey !== idempotencyKey.toLowerCase() || !sameIdentity(record, session)) {
    throw new CookieRecoveryCleanupError();
  }

  try {
    sessionStorage.removeItem(key);
    if (sessionStorage.getItem(key) !== null) {
      throw new CookieRecoveryCleanupError();
    }
  } catch (error) {
    if (error instanceof CookieRecoveryCleanupError) {
      throw error;
    }

    throw new CookieRecoveryCleanupError();
  }

  publish();
}

export function markRecoveryDisposition(
  operation: CookieRecoveryOperation,
  idempotencyKey: string,
  disposition: Exclude<CookieRecoveryDisposition, 'dispatched'>,
): void {
  const stored = readRecovery(operation, sessionStore.getSnapshot());
  if (stored.status !== 'recoverable' || stored.record.idempotencyKey !== idempotencyKey) {
    throw new CookieRecoveryStorageError();
  }

  if (stored.record.disposition === 'uncertain' && disposition === 'rejected') {
    return;
  }

  if (stored.record.disposition === disposition) {
    return;
  }

  const next = signedRecord({ ...stored.record, disposition });
  const serialized = JSON.stringify(next);
  const key = cookieRecoveryKeys[operation];
  try {
    sessionStorage.setItem(key, serialized);
    if (sessionStorage.getItem(key) !== serialized) {
      throw new CookieRecoveryStorageError();
    }
  } catch (error) {
    if (error instanceof CookieRecoveryStorageError) {
      throw error;
    }

    throw new CookieRecoveryStorageError();
  }

  publish();
}

export function dismissRejectedRecovery(operation: CookieRecoveryOperation): boolean {
  const stored = readRecovery(operation, sessionStore.getSnapshot());
  if (stored.status !== 'recoverable' || stored.record.disposition !== 'rejected') {
    return false;
  }

  sessionStorage.removeItem(cookieRecoveryKeys[operation]);
  publish();
  return true;
}

function sameIdentity(record: CookieRecoveryRecord, session: SessionSnapshot): boolean {
  return session.authenticated
    && session.issuer === record.issuer
    && session.subject === record.subject
    && session.activeOrganization?.organizationId.toLowerCase() === record.organizationId;
}

function parseRecord(raw: string, operation: CookieRecoveryOperation): CookieRecoveryRecord | null {
  let value: unknown;

  try {
    value = JSON.parse(raw);
  } catch {
    return null;
  }

  if (!isRecord(value) || value.schemaVersion !== 2 || value.operation !== operation) {
    return null;
  }

  if (typeof value.issuer !== 'string' || value.issuer.length === 0 || typeof value.subject !== 'string' || value.subject.length === 0) {
    return null;
  }

  if (typeof value.organizationId !== 'string' || !guidPattern.test(value.organizationId)) {
    return null;
  }

  if (typeof value.idempotencyKey !== 'string' || !guidPattern.test(value.idempotencyKey)) {
    return null;
  }

  if (typeof value.requestBody !== 'string' || value.requestBody.length === 0 || typeof value.fingerprint !== 'string') {
    return null;
  }

  if (value.disposition !== 'dispatched' && value.disposition !== 'uncertain' && value.disposition !== 'rejected') {
    return null;
  }

  if (!isRecord(value.context)) {
    return null;
  }

  const context: Record<string, string> = {};
  for (const [name, entry] of Object.entries(value.context)) {
    if (typeof entry !== 'string') {
      return null;
    }

    context[name] = entry;
  }

  return {
    schemaVersion: 2,
    operation,
    issuer: value.issuer,
    subject: value.subject,
    organizationId: value.organizationId.toLowerCase(),
    idempotencyKey: value.idempotencyKey.toLowerCase(),
    requestBody: value.requestBody,
    fingerprint: value.fingerprint,
    disposition: value.disposition,
    context,
  };
}

function signedRecord(input: Omit<CookieRecoveryRecord, 'schemaVersion' | 'fingerprint'>): CookieRecoveryRecord {
  const draft: CookieRecoveryRecord = {
    schemaVersion: 2,
    ...input,
    fingerprint: '',
  };
  return {
    ...draft,
    fingerprint: fingerprintBody(identityText(draft)),
  };
}

function identityText(record: CookieRecoveryRecord): string {
  return [
    String(record.schemaVersion),
    record.operation,
    record.issuer,
    record.subject,
    record.organizationId,
    record.idempotencyKey,
    record.disposition,
    canonicalContext(record.context),
    record.requestBody,
  ].join('\n');
}

function canonicalContext(context: Readonly<Record<string, string>>): string {
  return Object.keys(context).sort().map((key) => `${key}=${context[key]}`).join('&');
}

function sameStoredAttempt(raw: string, record: CookieRecoveryRecord): boolean {
  const parsed = parseRecord(raw, record.operation);
  if (parsed === null) {
    return false;
  }

  return parsed.issuer === record.issuer
    && parsed.subject === record.subject
    && parsed.organizationId === record.organizationId
    && parsed.idempotencyKey === record.idempotencyKey
    && parsed.requestBody === record.requestBody
    && canonicalContext(parsed.context) === canonicalContext(record.context);
}

function recordFromRaw(raw: string, operation: CookieRecoveryOperation): CookieRecoveryRecord | null {
  return parseRecord(raw, operation);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
