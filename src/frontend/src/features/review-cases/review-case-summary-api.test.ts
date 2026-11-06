import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { ApiError, UnexpectedResponseError } from '../../shared/api/errors';
import { queryClient } from '../../shared/api/query-client';
import { sessionStore, type CookieSessionFields } from '../../shared/api/session-store';
import { server } from '../../test/server';
import {
  clearSummaryCountsForAccessDenial,
  getReviewCaseSummary,
  type ReviewCaseSummary,
} from './review-case-summary-api';

afterEach(() => {
  vi.unstubAllEnvs();
  vi.unstubAllGlobals();
});

describe('review case summary API', () => {
  it('reads six zeros and ignores an extra field', async () => {
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    let search = 'unset';
    server.use(http.get('/api/review-cases/summary', ({ request }) => {
      search = new URL(request.url).search;
      return text(`${counts('0', '0', '0', '0', '0', '0').slice(0, -1)},"extra":true}`);
    }));

    const summary = await getReviewCaseSummary(new AbortController().signal);

    expect(search).toBe('');
    expect(summary).toEqual(view('0', '0', '0', '0', '0', '0'));
  });

  it('keeps an integer above Number.MAX_SAFE_INTEGER', async () => {
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    const above = '9007199254740993';
    server.use(http.get('/api/review-cases/summary', () => text(counts(above, above, '0', '0', above, '0'))));

    const summary = await getReviewCaseSummary(new AbortController().signal);

    expect(summary.totalCount).toBe(above);
    expect(summary.openCompetingMatchCount).toBe(above);
    expect(summary.totalCount).not.toBe(String(Number(above)));
  });

  it('keeps int64 max and rejects one past it', async () => {
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    const limit = '9223372036854775807';
    server.use(http.get('/api/review-cases/summary', () => text(counts(limit, limit, '0', '0', limit, '0'))));

    const summary = await getReviewCaseSummary(new AbortController().signal);

    expect(summary).toEqual(view(limit, limit, '0', '0', limit, '0'));
    expect(summary.totalCount).not.toBe(String(Number(limit)));
    server.use(http.get('/api/review-cases/summary', () => text(counts('9223372036854775808', '0', '0', '0', '0', '0'))));
    await expect(getReviewCaseSummary(new AbortController().signal)).rejects.toBeInstanceOf(UnexpectedResponseError);
  });

  it.each([
    ['a missing field', counts('1', '1', '0', '0', '1', '0').replace(',"openNeedsInvestigationCount":0', '')],
    ['null', counts('1', '1', '0', '0', '1', 'null')],
    ['a string', counts('1', '1', '0', '0', '"1"', '0')],
    ['a negative', counts('-1', '0', '0', '0', '0', '0')],
    ['a fraction', counts('1.5', '0', '0', '0', '0', '0')],
    ['an exponent', counts('1e2', '0', '0', '0', '0', '0')],
    ['a broken status identity', counts('3', '1', '1', '0', '1', '0')],
    ['a broken reason identity', counts('2', '2', '0', '0', '1', '0')],
  ])('rejects %s', async (_name, body) => {
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    server.use(http.get('/api/review-cases/summary', () => text(body)));

    await expect(getReviewCaseSummary(new AbortController().signal)).rejects.toBeInstanceOf(UnexpectedResponseError);
  });

  it('fetches a cookie summary with review-cases.read and without audit.read', async () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    sessionStore.replaceCookieSession(cookieSession(['review-cases.read']), true);
    let calls = 0;
    server.use(http.get('/api/review-cases/summary', ({ request }) => {
      calls += 1;
      expect(new URL(request.url).search).toBe('');
      return text(counts('1', '1', '0', '0', '1', '0'));
    }));

    const summary = await getReviewCaseSummary(new AbortController().signal);

    expect(summary.openCount).toBe('1');
    expect(calls).toBe(1);
  });

  it('does not fetch a cookie summary without review-cases.read', async () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    sessionStore.replaceCookieSession(cookieSession(['audit.read']), true);
    const fetchSpy = vi.spyOn(globalThis, 'fetch');

    await expect(getReviewCaseSummary(new AbortController().signal)).rejects.toMatchObject({ status: 403 });
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it('clears counts only for the current denial and keeps a newer success', () => {
    const key = ['review-cases', 'summary', 4] as const;
    const original = view('4', '3', '1', '0', '2', '1');
    const replacement = view('9', '4', '3', '2', '3', '1');
    const denial = new ApiError(403, null, 'Forbidden', '');
    const other = new ApiError(403, null, 'Forbidden', '');
    queryClient.setQueryData(key, original);
    const query = queryClient.getQueryCache().find({ queryKey: key, exact: true });
    expect(query).toBeDefined();
    query?.setState({ status: 'error', error: denial });

    clearSummaryCountsForAccessDenial(queryClient, key, other);
    expect(query?.state.data).toEqual(original);

    clearSummaryCountsForAccessDenial(queryClient, key, denial);
    expect(query?.state.data).toBeUndefined();
    expect(query?.state.error).toBe(denial);

    clearSummaryCountsForAccessDenial(queryClient, key, denial);
    expect(query?.state.error).toBe(denial);

    query?.setState({ status: 'success', data: replacement, error: null });
    clearSummaryCountsForAccessDenial(queryClient, key, denial);
    expect(query?.state.data).toEqual(replacement);
    expect(query?.state.status).toBe('success');
  });
});

function counts(
  total: string,
  open: string,
  resolved: string,
  dismissed: string,
  competing: string,
  needs: string,
): string {
  return `{"totalCount":${total},"openCount":${open},"resolvedCount":${resolved},"dismissedCount":${dismissed},"openCompetingMatchCount":${competing},"openNeedsInvestigationCount":${needs}}`;
}

function view(
  total: string,
  open: string,
  resolved: string,
  dismissed: string,
  competing: string,
  needs: string,
): ReviewCaseSummary {
  return {
    totalCount: total,
    openCount: open,
    resolvedCount: resolved,
    dismissedCount: dismissed,
    openCompetingMatchCount: competing,
    openNeedsInvestigationCount: needs,
  };
}

function text(body: string) {
  return HttpResponse.text(body, { headers: { 'Content-Type': 'application/json' } });
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
