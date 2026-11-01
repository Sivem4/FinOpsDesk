import type { QueryClient } from '@tanstack/react-query';

import { queryClient } from './query-client';

export type SessionReason = 'expired' | 'unauthorized' | 'forbidden' | null;

export interface CookieSessionFields {
  authenticated: boolean;
  csrfToken: string | null;
  issuer: string | null;
  subject: string | null;
  displayName: string | null;
  expiresAt: number | null;
}

export interface SessionSnapshot extends CookieSessionFields {
  epoch: number;
  token: string | null;
  reason: SessionReason;
  signal: AbortSignal;
  financialAccess: boolean;
}

type Listener = () => void;

export class SessionStore {
  private controller = new AbortController();

  private listeners = new Set<Listener>();

  private snapshot: SessionSnapshot;

  constructor(private readonly queries: QueryClient) {
    this.snapshot = this.cleared(0, this.controller.signal);
  }

  subscribe = (listener: Listener): (() => void) => {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  };

  getSnapshot = (): SessionSnapshot => this.snapshot;

  rotate(token: string | null, expiresAt: number | null, reason: SessionReason): void {
    const previous = this.controller;
    this.controller = new AbortController();
    this.snapshot = {
      ...this.cleared(this.snapshot.epoch + 1, this.controller.signal),
      token,
      expiresAt,
      reason,
    };
    previous.abort();
    this.publish();
    void this.queries.cancelQueries();
    this.queries.clear();
  }

  replaceCookieSession(next: CookieSessionFields, rotateEpoch: boolean): void {
    if (!rotateEpoch) {
      this.snapshot = {
        ...this.snapshot,
        ...next,
        token: null,
        reason: null,
        financialAccess: false,
      };
      this.publish();
      return;
    }

    const previous = this.controller;
    this.controller = new AbortController();
    this.snapshot = {
      ...this.cleared(this.snapshot.epoch + 1, this.controller.signal),
      ...next,
      token: null,
      reason: null,
      financialAccess: false,
    };
    previous.abort();
    this.publish();
    void this.queries.cancelQueries();
    this.queries.clear();
  }

  resetForTests(): void {
    const previous = this.controller;
    this.controller = new AbortController();
    this.snapshot = this.cleared(0, this.controller.signal);
    previous.abort();
    this.publish();
    this.queries.clear();
  }

  private cleared(epoch: number, signal: AbortSignal): SessionSnapshot {
    return {
      epoch,
      token: null,
      expiresAt: null,
      reason: null,
      signal,
      csrfToken: null,
      authenticated: false,
      financialAccess: false,
      issuer: null,
      subject: null,
      displayName: null,
    };
  }

  private publish(): void {
    for (const listener of this.listeners) {
      listener();
    }
  }
}

export const sessionStore = new SessionStore(queryClient);
