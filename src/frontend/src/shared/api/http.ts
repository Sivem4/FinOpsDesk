import { parse } from 'lossless-json';

import { isAbortError, NetworkError, StaleSessionError, UnexpectedResponseError } from './errors';
import { toApiError } from './problem';
import { sessionStore } from './session-store';

export async function apiGet(path: string, signal: AbortSignal): Promise<unknown> {
  const session = sessionStore.getSnapshot();

  if (session.token === null) {
    throw toApiError(401, '');
  }

  const epoch = session.epoch;
  let response: Response;

  try {
    response = await fetch(path, {
      method: 'GET',
      headers: {
        Accept: 'application/json',
        Authorization: `Bearer ${session.token}`,
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
