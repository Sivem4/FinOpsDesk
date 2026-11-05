import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { StaleSessionError, UnexpectedResponseError } from '../../shared/api/errors';
import { sessionStore, type CookieSessionFields } from '../../shared/api/session-store';
import { server } from '../../test/server';
import { getReviewCase, listReviewCases } from './review-case-api';

const caseId = '11111111-1111-4111-8111-111111111111';
const invoiceId = '22222222-2222-4222-8222-222222222222';
const bankId = '33333333-3333-4333-8333-333333333333';
const secretToken = 'concurrency-token-must-stay-hidden';

afterEach(() => {
  vi.unstubAllEnvs();
  vi.unstubAllGlobals();
});

describe('review case API', () => {
  it('reads a development list and detail through the shared client', async () => {
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    server.use(
      http.get('/api/review-cases', ({ request }) => {
        expect(new URL(request.url).searchParams.has('selected')).toBe(false);
        return text(listDocument());
      }),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
    );

    const list = await listReviewCases(1, 50, 'Open', invoiceId, null, new AbortController().signal);
    const detail = await getReviewCase(caseId, new AbortController().signal);

    expect(list.items[0]?.reviewCaseId).toBe(caseId);
    expect(list.items[0]?.status).toBe('Open');
    expect(detail.invoice).toEqual({ found: true, amount: '0', currency: 'PLN' });
    expect(detail.bankTransaction).toEqual({ found: true, amount: '0.0001', currency: 'EUR' });
    expect(JSON.stringify(detail)).not.toContain(secretToken);
  });

  it('reads a cookie list and detail only when review-cases.read is verified', async () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    sessionStore.replaceCookieSession(cookieSession(['review-cases.read']), true);
    server.use(
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
    );

    const list = await listReviewCases(2, 25, null, null, bankId, new AbortController().signal);
    const detail = await getReviewCase(caseId, new AbortController().signal);

    expect(list.page).toBe(1);
    expect(detail.reviewCase.note).toBe('watch the zero balance');
    expect(detail.invoice.amount).toBe('0');
  });

  it('does not fetch when the cookie caller lacks review-cases.read', async () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    sessionStore.replaceCookieSession(cookieSession(['invoices.read']), true);
    const fetchSpy = vi.spyOn(globalThis, 'fetch');

    await expect(listReviewCases(1, 50, null, null, null, new AbortController().signal)).rejects.toMatchObject({ status: 403 });
    await expect(getReviewCase(caseId, new AbortController().signal)).rejects.toMatchObject({ status: 403 });
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it('rejects a found flag that does not match the money', async () => {
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    server.use(http.get('/api/review-cases/:id', () => text(detailDocument(false, '{"amount":0,"currency":"PLN"}'))));

    await expect(getReviewCase(caseId, new AbortController().signal)).rejects.toBeInstanceOf(UnexpectedResponseError);
  });

  it('rejects a cookie detail that finishes after the organization changes', async () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    sessionStore.replaceCookieSession(cookieSession(['review-cases.read']), true);
    let resolveFetch: (response: Response) => void = () => {
      throw new Error('The fetch was not started.');
    };
    vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>((resolve) => {
      resolveFetch = resolve;
    })));

    const pending = getReviewCase(caseId, new AbortController().signal);
    sessionStore.replaceCookieSession({
      ...cookieSession(['review-cases.read']),
      activeOrganization: {
        organizationId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
        displayName: 'South Ledger',
      },
    }, true);
    resolveFetch(new Response(detailDocument(), { status: 200, headers: { 'Content-Type': 'application/json' } }));

    await expect(pending).rejects.toBeInstanceOf(StaleSessionError);
  });

  it('rejects a development detail that finishes after the token is replaced', async () => {
    sessionStore.rotate('token-a', 4_000_000_000, null);
    let resolveFetch: (response: Response) => void = () => {
      throw new Error('The fetch was not started.');
    };
    vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>((resolve) => {
      resolveFetch = resolve;
    })));

    const pending = getReviewCase(caseId, new AbortController().signal);
    sessionStore.rotate('token-b', 4_000_000_000, null);
    resolveFetch(new Response(detailDocument(), { status: 200, headers: { 'Content-Type': 'application/json' } }));

    await expect(pending).rejects.toBeInstanceOf(StaleSessionError);
  });
});

function text(body: string) {
  return HttpResponse.text(body, { headers: { 'Content-Type': 'application/json' } });
}

function listDocument(): string {
  return `{"items":[${caseDocument()}],"page":1,"pageSize":50,"totalCount":1}`;
}

function detailDocument(invoiceFound = true, invoiceOutstanding: string | null = '{"amount":0,"currency":"PLN"}'): string {
  const money = invoiceOutstanding === null ? 'null' : invoiceOutstanding;
  return `{"reviewCase":${caseDocument()},"linkedFinancials":{"invoiceFound":${invoiceFound},"invoiceOutstanding":${money},"bankTransactionFound":true,"bankTransactionUnapplied":{"amount":0.0001,"currency":"EUR"}}}`;
}

function caseDocument(): string {
  return `{"reviewCaseId":"${caseId}","reason":"CompetingMatch","status":"Open","invoiceId":"${invoiceId}","bankTransactionId":"${bankId}","sourceLabel":"manual","note":"watch the zero balance","outcome":null,"resolutionNote":null,"allocationId":null,"openedAt":"2026-11-05T08:00:00Z","closedAt":null,"concurrencyToken":"${secretToken}"}`;
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
