import { useSyncExternalStore } from 'react';

import { sessionStore, type SessionReason, type SessionSnapshot } from '../api/session-store';

export function useSession(): SessionSnapshot {
  return useSyncExternalStore(sessionStore.subscribe, sessionStore.getSnapshot, sessionStore.getSnapshot);
}

export function setAccessToken(token: string, expiresAt: number): void {
  sessionStore.rotate(token, expiresAt, null);
}

export function endSession(reason: SessionReason): void {
  sessionStore.rotate(null, null, reason);
}
