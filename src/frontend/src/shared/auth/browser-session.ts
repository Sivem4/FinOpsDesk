import { isAbortError } from '../api/errors';
import {
  sessionStore,
  type BrowserOrganization,
  type CookieSessionFields,
  type SessionSnapshot,
} from '../api/session-store';

export const financialReadCapabilities = [
  'invoices.read',
  'bank-transactions.read',
  'reconciliation.read',
  'allocations.read',
  'audit.read',
  'review-cases.read',
] as const;

export const financialWriteCapabilities = [
  'reconciliation.confirm',
  'allocations.create',
  'allocations.reverse',
] as const;

const recognizedCapabilities = [
  ...financialReadCapabilities,
  ...financialWriteCapabilities,
] as const;

export const screenCapabilities = [
  'invoices.read',
  'bank-transactions.read',
  'reconciliation.read',
  'allocations.read',
  'review-cases.read',
] as const;

export const sessionContractHeader = 'X-FinOpsDesk-Session-Contract';

export type BrowserSessionLoad =
  | { kind: 'anonymous' | 'restricted' | 'workspace'; session: CookieSessionFields }
  | { kind: 'unavailable' }
  | { kind: 'invalid' }
  | { kind: 'network' }
  | { kind: 'http'; status: number };

const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const emptyGuid = '00000000-0000-0000-0000-000000000000';
const maximumOrganizationNameLength = 200;

let sessionReadGeneration = 0;

export function beginSessionRead(): number {
  sessionReadGeneration += 1;
  return sessionReadGeneration;
}

export function isCurrentSessionRead(readId: number): boolean {
  return readId === sessionReadGeneration;
}

export function resetSessionReadsForTests(): void {
  sessionReadGeneration = 0;
}

export async function loadBrowserSession(signal: AbortSignal): Promise<BrowserSessionLoad> {
  let response: Response;

  try {
    response = await fetch('/api/session', {
      method: 'GET',
      credentials: 'same-origin',
      cache: 'no-store',
      headers: {
        Accept: 'application/json',
        [sessionContractHeader]: '2',
      },
      signal,
    });
  } catch (error) {
    if (isAbortError(error)) {
      throw error;
    }

    return { kind: 'network' };
  }

  if (response.status === 503) {
    return { kind: 'unavailable' };
  }

  if (!response.ok) {
    return { kind: 'http', status: response.status };
  }

  let body: unknown;

  try {
    body = await response.json();
  } catch {
    return { kind: 'invalid' };
  }

  return parseBrowserSession(body);
}

export function parseBrowserSession(body: unknown): BrowserSessionLoad {
  if (!isRecord(body) || typeof body.authenticated !== 'boolean' || typeof body.financialAccess !== 'boolean') {
    return { kind: 'invalid' };
  }

  const capabilities = readCapabilities(body.permissions, body.financialAccess, body.authenticated);

  if (capabilities === 'invalid') {
    return { kind: 'invalid' };
  }

  if (typeof body.csrfToken !== 'string' || body.csrfToken.length === 0) {
    return { kind: 'invalid' };
  }

  const organizations = readOrganizations(body.organizations);

  if (organizations === 'invalid') {
    return { kind: 'invalid' };
  }

  const activeOrganization = readActiveOrganization(body.activeOrganization, organizations);

  if (activeOrganization === 'invalid') {
    return { kind: 'invalid' };
  }

  if (body.authenticated) {
    if (
      typeof body.issuer !== 'string'
      || body.issuer.length === 0
      || typeof body.subject !== 'string'
      || body.subject.length === 0
    ) {
      return { kind: 'invalid' };
    }

    if (!isDisplayName(body.displayName)) {
      return { kind: 'invalid' };
    }

    const expiresAt = readExpiresAt(body.expiresAt);

    if (expiresAt === 'invalid') {
      return { kind: 'invalid' };
    }

    return {
      kind: capabilities.mode === 'verified' ? 'workspace' : 'restricted',
      session: {
        authenticated: true,
        csrfToken: body.csrfToken,
        issuer: body.issuer,
        subject: body.subject,
        displayName: body.displayName === null || body.displayName.length === 0 ? null : body.displayName,
        expiresAt,
        organizations,
        activeOrganization,
        authorization: capabilities.mode,
        capabilities: capabilities.values,
      },
    };
  }

  if (
    body.issuer !== null
    || body.subject !== null
    || body.displayName !== null
    || body.expiresAt !== null
    || organizations.length !== 0
    || activeOrganization !== null
  ) {
    return { kind: 'invalid' };
  }

  return {
    kind: 'anonymous',
    session: {
      authenticated: false,
      csrfToken: body.csrfToken,
      issuer: null,
      subject: null,
      displayName: null,
      expiresAt: null,
      organizations: [],
      activeOrganization: null,
      authorization: 'anonymous',
      capabilities: [],
    },
  };
}

export function commitBrowserSession(
  observedEpoch: number,
  observedRead: number,
  next: CookieSessionFields,
): 'applied' | 'stale' {
  const current = sessionStore.getSnapshot();

  if (current.epoch !== observedEpoch || !isCurrentSessionRead(observedRead)) {
    return 'stale';
  }

  sessionStore.replaceCookieSession(next, !sameBrowserIdentity(current, next));
  return 'applied';
}

export function readProblemCode(body: string): string | null {
  try {
    const parsed: unknown = JSON.parse(body);

    if (!isRecord(parsed) || typeof parsed.code !== 'string') {
      return null;
    }

    return parsed.code;
  } catch {
    return null;
  }
}

export function sameBrowserIdentity(current: SessionSnapshot, next: CookieSessionFields): boolean {
  return current.authenticated === next.authenticated
    && current.issuer === next.issuer
    && current.subject === next.subject
    && activeOrganizationId(current) === activeOrganizationId(next)
    && sameCapabilities(current.capabilities, next.capabilities);
}

function sameCapabilities(current: readonly string[], next: readonly string[]): boolean {
  if (current.length !== next.length) {
    return false;
  }

  for (let index = 0; index < current.length; index += 1) {
    if (current[index] !== next[index]) {
      return false;
    }
  }

  return true;
}

function readCapabilities(
  permissions: unknown,
  financialAccess: boolean,
  authenticated: boolean,
): { mode: 'anonymous' | 'restricted' | 'verified'; values: readonly string[] } | 'invalid' {
  const recognized = recognizeCapabilities(permissions);

  if (recognized === 'invalid') {
    return 'invalid';
  }

  if (!authenticated) {
    if (financialAccess || (recognized !== 'absent' && recognized.values.length > 0)) {
      return 'invalid';
    }

    return { mode: 'anonymous', values: [] };
  }

  if (recognized === 'absent') {
    if (financialAccess) {
      return 'invalid';
    }

    return { mode: 'restricted', values: [] };
  }

  const reads = recognized.reads;

  if (!financialAccess) {
    if (reads.length > 0) {
      return 'invalid';
    }

    return { mode: 'restricted', values: [] };
  }

  if (reads.length === 0) {
    return { mode: 'restricted', values: [] };
  }

  return { mode: 'verified', values: recognized.values };
}

function recognizeCapabilities(permissions: unknown): { reads: readonly string[]; values: readonly string[] } | 'absent' | 'invalid' {
  if (permissions === undefined) {
    return 'absent';
  }

  if (!Array.isArray(permissions)) {
    return 'invalid';
  }

  const accepted = new Set<string>();

  for (const entry of permissions) {
    if (typeof entry !== 'string') {
      return 'invalid';
    }

    if ((recognizedCapabilities as readonly string[]).includes(entry)) {
      accepted.add(entry);
    }
  }

  const values = recognizedCapabilities.filter((capability) => accepted.has(capability));
  return {
    reads: values.filter((capability) => (financialReadCapabilities as readonly string[]).includes(capability)),
    values,
  };
}

function activeOrganizationId(session: { activeOrganization: BrowserOrganization | null }): string | null {
  return session.activeOrganization?.organizationId ?? null;
}

function readOrganizations(value: unknown): BrowserOrganization[] | 'invalid' {
  if (!Array.isArray(value)) {
    return 'invalid';
  }

  const organizations: BrowserOrganization[] = [];
  const seen = new Set<string>();

  for (const entry of value) {
    const organization = readOrganization(entry);

    if (organization === null || seen.has(organization.organizationId)) {
      return 'invalid';
    }

    seen.add(organization.organizationId);
    organizations.push(organization);
  }

  return organizations;
}

function readActiveOrganization(
  value: unknown,
  organizations: BrowserOrganization[],
): BrowserOrganization | null | 'invalid' {
  if (value === null) {
    return null;
  }

  const active = readOrganization(value);

  if (active === null) {
    return 'invalid';
  }

  const choice = organizations.find((organization) => organization.organizationId === active.organizationId);

  if (choice === undefined || choice.displayName !== active.displayName) {
    return 'invalid';
  }

  return choice;
}

function readOrganization(value: unknown): BrowserOrganization | null {
  if (!isRecord(value)) {
    return null;
  }

  const organizationId = canonicalGuid(value.organizationId);

  if (
    organizationId === null
    || typeof value.displayName !== 'string'
    || value.displayName.length === 0
    || value.displayName.length > maximumOrganizationNameLength
  ) {
    return null;
  }

  return { organizationId, displayName: value.displayName };
}

function canonicalGuid(value: unknown): string | null {
  if (typeof value !== 'string' || !guidPattern.test(value)) {
    return null;
  }

  const canonical = value.toLowerCase();
  return canonical === emptyGuid ? null : canonical;
}

function readExpiresAt(value: unknown): number | null | 'invalid' {
  if (value === null) {
    return null;
  }

  if (typeof value !== 'string') {
    return 'invalid';
  }

  const parsed = Date.parse(value);

  if (Number.isNaN(parsed)) {
    return 'invalid';
  }

  return Math.floor(parsed / 1000);
}

function isDisplayName(value: unknown): value is string | null {
  return value === null || (typeof value === 'string' && value.length <= 256);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
