import { isAbortError } from '../api/errors';
import {
  sessionStore,
  type CookieSessionFields,
  type SessionSnapshot,
} from '../api/session-store';

export type BrowserSessionLoad =
  | { kind: 'anonymous' | 'restricted'; session: CookieSessionFields }
  | { kind: 'invalid' }
  | { kind: 'network' }
  | { kind: 'http'; status: number };

export async function loadBrowserSession(signal: AbortSignal): Promise<BrowserSessionLoad> {
  let response: Response;

  try {
    response = await fetch('/api/session', {
      method: 'GET',
      credentials: 'same-origin',
      cache: 'no-store',
      headers: { Accept: 'application/json' },
      signal,
    });
  } catch (error) {
    if (isAbortError(error)) {
      throw error;
    }

    return { kind: 'network' };
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
  if (!isRecord(body) || typeof body.authenticated !== 'boolean' || body.financialAccess !== false) {
    return { kind: 'invalid' };
  }

  if (typeof body.csrfToken !== 'string' || body.csrfToken.length === 0) {
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
      kind: 'restricted',
      session: {
        authenticated: true,
        csrfToken: body.csrfToken,
        issuer: body.issuer,
        subject: body.subject,
        displayName: body.displayName === null || body.displayName.length === 0 ? null : body.displayName,
        expiresAt,
      },
    };
  }

  if (body.issuer !== null || body.subject !== null || body.displayName !== null || body.expiresAt !== null) {
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
    },
  };
}

export function commitBrowserSession(observedEpoch: number, next: CookieSessionFields): 'applied' | 'stale' {
  const current = sessionStore.getSnapshot();

  if (current.epoch !== observedEpoch) {
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

function sameBrowserIdentity(current: SessionSnapshot, next: CookieSessionFields): boolean {
  return current.authenticated === next.authenticated
    && current.issuer === next.issuer
    && current.subject === next.subject;
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
