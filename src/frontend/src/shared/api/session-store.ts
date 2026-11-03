import type { QueryClient } from '@tanstack/react-query';

import { queryClient } from './query-client';

export type SessionReason = 'expired' | 'unauthorized' | 'forbidden' | null;

export interface BrowserOrganization {
  organizationId: string;
  displayName: string;
}

export type SessionAuthorization = 'anonymous' | 'restricted' | 'verified' | 'unavailable';

export interface CookieSessionFields {
  authenticated: boolean;
  csrfToken: string | null;
  issuer: string | null;
  subject: string | null;
  displayName: string | null;
  expiresAt: number | null;
  organizations: BrowserOrganization[];
  activeOrganization: BrowserOrganization | null;
  authorization: SessionAuthorization;
  capabilities: readonly string[];
}

export interface SessionSnapshot extends CookieSessionFields {
  epoch: number;
  readiness: number;
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
    this.snapshot = this.cleared(0, 0, this.controller.signal);
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
      ...this.cleared(this.snapshot.epoch + 1, this.snapshot.readiness + 1, this.controller.signal),
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
    const leavingUnavailable = this.snapshot.authorization === 'unavailable';
    const financialAccess = next.authorization === 'verified' && next.capabilities.length > 0;
    const readiness = rotateEpoch || leavingUnavailable
      ? this.snapshot.readiness + 1
      : this.snapshot.readiness;

    if (!rotateEpoch && !leavingUnavailable) {
      this.snapshot = {
        ...this.snapshot,
        ...next,
        readiness,
        token: null,
        reason: null,
        financialAccess,
      };
      this.publish();
      return;
    }

    const previous = this.controller;
    this.controller = new AbortController();
    this.snapshot = {
      ...this.cleared(
        rotateEpoch ? this.snapshot.epoch + 1 : this.snapshot.epoch,
        readiness,
        this.controller.signal,
      ),
      ...next,
      token: null,
      reason: null,
      financialAccess,
    };
    previous.abort();
    this.publish();
    void this.queries.cancelQueries();
    this.queries.clear();
  }

  markAuthorizationUnavailable(): void {
    if (!this.snapshot.authenticated || this.snapshot.authorization === 'unavailable') {
      return;
    }

    const previous = this.controller;
    this.controller = new AbortController();
    this.snapshot = {
      ...this.snapshot,
      authorization: 'unavailable',
      financialAccess: false,
      readiness: this.snapshot.readiness + 1,
      signal: this.controller.signal,
    };
    previous.abort();
    this.publish();
    // Drop in-flight reads and the financial cache. The epoch stays put, and the
    // authenticated cookie identity, including the last verified capabilities, stays
    // on the snapshot so recovery can compare it. Readiness is what makes the next
    // successful session start fresh query keys.
    void this.queries.cancelQueries();
    this.queries.clear();
  }

  resetForTests(): void {
    const previous = this.controller;
    this.controller = new AbortController();
    this.snapshot = this.cleared(0, 0, this.controller.signal);
    previous.abort();
    this.publish();
    this.queries.clear();
  }

  private cleared(epoch: number, readiness: number, signal: AbortSignal): SessionSnapshot {
    return {
      epoch,
      readiness,
      token: null,
      expiresAt: null,
      reason: null,
      signal,
      csrfToken: null,
      authenticated: false,
      financialAccess: false,
      authorization: 'anonymous',
      capabilities: [],
      issuer: null,
      subject: null,
      displayName: null,
      organizations: [],
      activeOrganization: null,
    };
  }

  private publish(): void {
    for (const listener of this.listeners) {
      listener();
    }
  }
}

export const sessionStore = new SessionStore(queryClient);
