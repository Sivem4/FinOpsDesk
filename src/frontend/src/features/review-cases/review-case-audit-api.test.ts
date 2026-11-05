import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { StaleSessionError, UnexpectedResponseError } from '../../shared/api/errors';
import { sessionStore, type CookieSessionFields } from '../../shared/api/session-store';
import { server } from '../../test/server';
import { listReviewCaseAudit } from './review-case-audit-api';

const caseId = '11111111-1111-4111-8111-111111111111';
const invoiceId = '22222222-2222-4222-8222-222222222222';
const bankId = '33333333-3333-4333-8333-333333333333';
const allocationId = '44444444-4444-4444-8444-444444444444';
const openedId = '55555555-5555-4555-8555-555555555555';
const resolvedId = '66666666-6666-4666-8666-666666666666';
const dismissedId = '77777777-7777-4777-8777-777777777777';

afterEach(() => {
  vi.unstubAllEnvs();
  vi.unstubAllGlobals();
});

describe('review case audit API', () => {
  it('reads a development history through the shared client', async () => {
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    const urls: string[] = [];
    server.use(http.get('/api/review-cases/:id/audit', ({ request }) => {
      urls.push(request.url);
      return text(auditDocument([openedEntry(), resolvedEntry(), dismissedEntry()], 3));
    }));

    const history = await listReviewCaseAudit(caseId, new AbortController().signal);

    expect(urls).toHaveLength(1);
    expect(new URL(urls[0] ?? 'http://localhost/missing').searchParams.get('page')).toBe('1');
    expect(new URL(urls[0] ?? 'http://localhost/missing').searchParams.get('pageSize')).toBe('50');
    expect(history.items.map((entry) => entry.action)).toEqual(['Opened', 'Resolved', 'Dismissed']);
    expect(history.items[0]).toMatchObject({
      actorId: 'actor with spaces',
      reason: 'CompetingMatch',
      invoiceId,
      bankTransactionId: bankId,
      sourceLabel: 'manual',
      note: 'opening note',
      outcome: null,
      allocationId: null,
      resolutionNote: null,
    });
    expect(history.items[1]).toMatchObject({
      outcome: 'AllocationRecorded',
      allocationId,
      resolutionNote: 'already recorded',
      reason: null,
      note: null,
    });
    expect(history.items[2]).toMatchObject({
      resolutionNote: null,
      outcome: null,
      allocationId: null,
      reason: null,
    });
    expect(history.totalCount).toBe(3);
  });

  it('reads a cookie history only when audit.read is verified', async () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    sessionStore.replaceCookieSession(cookieSession(['audit.read']), true);
    server.use(http.get('/api/review-cases/:id/audit', () => text(auditDocument([dismissedEntry()], 0))));

    const history = await listReviewCaseAudit(caseId, new AbortController().signal);

    expect(history.totalCount).toBe(0);
    expect(history.items).toHaveLength(1);
    expect(history.items[0]?.action).toBe('Dismissed');
    expect(history.items[0]?.actorId).toBe('closing-actor');
  });

  it('does not fetch when the cookie caller lacks audit.read', async () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    sessionStore.replaceCookieSession(cookieSession(['review-cases.read']), true);
    const fetchSpy = vi.spyOn(globalThis, 'fetch');

    await expect(listReviewCaseAudit(caseId, new AbortController().signal)).rejects.toMatchObject({ status: 403 });
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it('keeps a returned terminal entry when the older count is zero', async () => {
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    server.use(http.get('/api/review-cases/:id/audit', () => text(auditDocument([dismissedEntry()], 0))));

    const history = await listReviewCaseAudit(caseId, new AbortController().signal);

    expect(history.totalCount).toBe(0);
    expect(history.items[0]?.actorId).toBe('closing-actor');
    expect(history.items[0]?.reason).toBeNull();
    expect(history.items[0]?.note).toBeNull();
  });

  it('rejects an actor, an enum, and an action shape that the contract does not allow', async () => {
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    const documents = [
      auditDocument([entry({ actorId: '  padded' })]),
      auditDocument([entry({ actorId: '   ' })]),
      auditDocument([entry({ actorId: 'a'.repeat(257) })]),
      auditDocument([entry({ action: 'Closed' })]),
      auditDocument([entry({ reason: 'Other' })]),
      auditDocument([entry({ invoiceId: null })]),
      auditDocument([entry({ outcome: 'AllocationRecorded', allocationId })]),
      auditDocument([resolvedEntry({ allocationId: null })]),
      auditDocument([entry({ note: ` ${'n'.repeat(10)}` })]),
    ];
    let index = 0;
    server.use(http.get('/api/review-cases/:id/audit', () => text(documents[index] ?? '{}')));

    for (let attempt = 0; attempt < documents.length; attempt += 1) {
      index = attempt;
      await expect(listReviewCaseAudit(caseId, new AbortController().signal)).rejects.toBeInstanceOf(UnexpectedResponseError);
    }
  });

  it('preserves a byte order mark at either end of the actor and notes', async () => {
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    const actorId = '\uFEFFopening-actor\uFEFF';
    const note = '\uFEFFkept opening note\uFEFF';
    const resolutionNote = '\uFEFFkept closing note\uFEFF';
    server.use(http.get('/api/review-cases/:id/audit', () => text(auditDocument([
      entry({ actorId, note }),
      entry({
        id: dismissedId,
        action: 'Dismissed',
        actorId: '\uFEFFclosing-actor\uFEFF',
        recordedAt: '2026-11-05T10:00:00Z',
        reason: null,
        invoiceId: null,
        bankTransactionId: null,
        sourceLabel: null,
        note: null,
        resolutionNote,
      }),
    ]))));

    const history = await listReviewCaseAudit(caseId, new AbortController().signal);

    expect(history.items[0]?.actorId).toBe(actorId);
    expect(history.items[0]?.note).toBe(note);
    expect(history.items[1]?.actorId).toBe('\uFEFFclosing-actor\uFEFF');
    expect(history.items[1]?.resolutionNote).toBe(resolutionNote);
  });

  it('preserves an actor id that contains an internal space', async () => {
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    server.use(http.get('/api/review-cases/:id/audit', () => text(auditDocument([
      entry({ actorId: 'actor with spaces' }),
    ]))));

    const history = await listReviewCaseAudit(caseId, new AbortController().signal);

    expect(history.items[0]?.actorId).toBe('actor with spaces');
  });

  it('rejects a cookie history that finishes after the organization changes', async () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    sessionStore.replaceCookieSession(cookieSession(['audit.read']), true);
    let resolveFetch: (response: Response) => void = () => {
      throw new Error('The fetch was not started.');
    };
    vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>((resolve) => {
      resolveFetch = resolve;
    })));

    const pending = listReviewCaseAudit(caseId, new AbortController().signal);
    sessionStore.replaceCookieSession({
      ...cookieSession(['audit.read']),
      activeOrganization: {
        organizationId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
        displayName: 'South Ledger',
      },
    }, true);
    resolveFetch(new Response(auditDocument([openedEntry()]), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    }));

    await expect(pending).rejects.toBeInstanceOf(StaleSessionError);
  });
});

function text(body: string) {
  return HttpResponse.text(body, { headers: { 'Content-Type': 'application/json' } });
}

function auditDocument(items: Record<string, unknown>[], totalCount = items.length): string {
  return JSON.stringify({
    items,
    page: 1,
    pageSize: 50,
    totalCount,
  });
}

function openedEntry(): Record<string, unknown> {
  return entry({
    id: openedId,
    action: 'Opened',
    actorId: 'actor with spaces',
    recordedAt: '2026-11-05T08:00:00Z',
    reason: 'CompetingMatch',
    invoiceId,
    bankTransactionId: bankId,
    sourceLabel: 'manual',
    note: 'opening note',
  });
}

function resolvedEntry(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return entry({
    id: resolvedId,
    action: 'Resolved',
    actorId: 'resolver',
    recordedAt: '2026-11-05T09:00:00Z',
    reason: null,
    invoiceId: null,
    bankTransactionId: null,
    sourceLabel: null,
    note: null,
    outcome: 'AllocationRecorded',
    allocationId,
    resolutionNote: 'already recorded',
    ...overrides,
  });
}

function dismissedEntry(): Record<string, unknown> {
  return entry({
    id: dismissedId,
    action: 'Dismissed',
    actorId: 'closing-actor',
    recordedAt: '2026-11-05T10:00:00Z',
    reason: null,
    invoiceId: null,
    bankTransactionId: null,
    sourceLabel: null,
    note: null,
    resolutionNote: null,
  });
}

function entry(overrides: Record<string, unknown>): Record<string, unknown> {
  return {
    id: openedId,
    action: 'Opened',
    actorId: 'opening-actor',
    recordedAt: '2026-11-05T08:00:00Z',
    reason: 'CompetingMatch',
    invoiceId,
    bankTransactionId: bankId,
    sourceLabel: null,
    note: null,
    outcome: null,
    allocationId: null,
    resolutionNote: null,
    ...overrides,
  };
}

function cookieSession(capabilities: readonly string[]): CookieSessionFields {
  return {
    authenticated: true,
    csrfToken: 'csrf-session',
    issuer: 'https://issuer.example',
    subject: 'oidc-operator',
    displayName: 'Ada Operator',
    expiresAt: null,
    organizations: [],
    activeOrganization: {
      organizationId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
      displayName: 'North Ledger',
    },
    authorization: 'verified',
    capabilities,
  };
}
