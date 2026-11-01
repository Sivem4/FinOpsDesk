import { parse } from 'lossless-json';

import { authenticationHeaders, rejectCookieFinancialRequest } from './authentication';
import { isAbortError, NetworkError, StaleSessionError, UnexpectedResponseError } from './errors';
import { toApiError } from './problem';
import { sessionStore } from './session-store';

export async function apiGet(path: string, signal: AbortSignal): Promise<unknown> {
  const session = sessionStore.getSnapshot();
  rejectCookieFinancialRequest(session);

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
