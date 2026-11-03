import { parse } from 'lossless-json';

import { browserAuthMode } from '../auth/auth-mode';
import { authenticationHeaders } from './authentication';
import { isAbortError, NetworkError, StaleSessionError, UnexpectedResponseError } from './errors';
import { noteFinancialForbidden, noteFinancialUnauthorized } from './financial-status';
import { toApiError } from './problem';
import { sessionStore } from './session-store';

export async function apiGet(path: string, signal: AbortSignal): Promise<unknown> {
  if (browserAuthMode() === 'cookie') {
    return readCookieFinancial(path, signal);
  }

  const session = sessionStore.getSnapshot();

  if (session.token === null) {
    throw toApiError(401, '');
  }

  const epoch = session.epoch;
  let response: Response;

  try {
    response = await fetch(path, {
      method: 'GET',
      credentials: 'same-origin',
      headers: {
        Accept: 'application/json',
        ...authenticationHeaders(session, 'GET'),
      },
      signal: AbortSignal.any([signal, session.signal]),
    });
  } catch (error) {
    if (isAbortError(error)) {
      throw error;
    }

    throw new NetworkError();
  }

  assertCurrentEpoch(epoch);
  const body = await response.text();
  assertCurrentEpoch(epoch);

  if (!response.ok) {
    throw toApiError(response.status, body);
  }

  try {
    return parse(body);
  } catch {
    throw new UnexpectedResponseError();
  }
}

function assertCurrentEpoch(epoch: number): void {
  if (sessionStore.getSnapshot().epoch !== epoch) {
    throw new StaleSessionError();
  }
}

async function readCookieFinancial(path: string, signal: AbortSignal): Promise<unknown> {
  const session = sessionStore.getSnapshot();
  const capability = capabilityForPath(path);

  if (
    capability === null
    || session.authorization !== 'verified'
    || !session.capabilities.includes(capability)
  ) {
    throw toApiError(session.authenticated ? 403 : 401, '');
  }

  const epoch = session.epoch;
  const readiness = session.readiness;
  let response: Response;

  try {
    response = await fetch(path, {
      method: 'GET',
      credentials: 'same-origin',
      headers: { Accept: 'application/json' },
      signal: AbortSignal.any([signal, session.signal]),
    });
  } catch (error) {
    if (isAbortError(error)) {
      throw error;
    }

    throw new NetworkError();
  }

  assertReadable(epoch, readiness);
  const body = await response.text();
  assertReadable(epoch, readiness);

  if (response.status === 401) {
    noteFinancialUnauthorized();
    throw toApiError(response.status, body);
  }

  if (response.status === 403) {
    noteFinancialForbidden();
    throw toApiError(response.status, body);
  }

  if (!response.ok) {
    const error = toApiError(response.status, body);

    if (response.status === 503 && error.code === 'auth.authorization.unavailable') {
      sessionStore.markAuthorizationUnavailable();
    }

    throw error;
  }

  try {
    return parse(body);
  } catch {
    throw new UnexpectedResponseError();
  }
}

function assertReadable(epoch: number, readiness: number): void {
  const current = sessionStore.getSnapshot();

  if (
    current.epoch !== epoch
    || current.readiness !== readiness
    || current.authorization !== 'verified'
  ) {
    throw new StaleSessionError();
  }
}

function capabilityForPath(path: string): string | null {
  const pathname = path.split('?')[0] ?? path;

  if (pathname === '/api/invoices') {
    return 'invoices.read';
  }

  if (pathname === '/api/bank-transactions') {
    return 'bank-transactions.read';
  }

  if (pathname === '/api/matches' || pathname === '/api/matches/smart') {
    return 'reconciliation.read';
  }

  if (
    pathname === '/api/allocations'
    || pathname === '/api/allocations/context'
    || /^\/api\/allocations\/[^/]+\/reversal-context$/.test(pathname)
  ) {
    return 'allocations.read';
  }

  if (/^\/api\/allocations\/[^/]+\/audit$/.test(pathname)) {
    return 'audit.read';
  }

  return null;
}
