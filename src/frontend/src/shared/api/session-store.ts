import type { QueryClient } from '@tanstack/react-query';

import { queryClient } from './query-client';

export type SessionReason = 'expired' | 'unauthorized' | 'forbidden' | null;

export interface SessionSnapshot {
  epoch: number;
  token: string | null;
  expiresAt: number | null;
  reason: SessionReason;
  signal: AbortSignal;
}

type Listener = () => void;

export class SessionStore {
  private controller = new AbortController();

  private listeners = new Set<Listener>();

  private snapshot: SessionSnapshot;

  constructor(private readonly queries: QueryClient) {
    this.snapshot = {
      epoch: 0,
      token: null,
      expiresAt: null,
      reason: null,
      signal: this.controller.signal,
    };
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
      epoch: this.snapshot.epoch + 1,
      token,
      expiresAt,
      reason,
      signal: this.controller.signal,
    };
    previous.abort();
    this.publish();
    void this.queries.cancelQueries();
    this.queries.clear();
  }

  resetForTests(): void {
    const previous = this.controller;
    this.controller = new AbortController();
    this.snapshot = {
      epoch: 0,
      token: null,
      expiresAt: null,
      reason: null,
      signal: this.controller.signal,
    };
    previous.abort();
    this.publish();
    this.queries.clear();
  }

  private publish(): void {
    for (const listener of this.listeners) {
      listener();
    }
  }
}

export const sessionStore = new SessionStore(queryClient);
