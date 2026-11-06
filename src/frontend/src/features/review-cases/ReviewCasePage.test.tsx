import { QueryClientProvider } from '@tanstack/react-query';
import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { BrowserRouter, Link, MemoryRouter, Route, RouterProvider, Routes, createMemoryRouter } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { ApiError } from '../../shared/api/errors';
import { queryClient } from '../../shared/api/query-client';
import { sessionStore, type CookieSessionFields } from '../../shared/api/session-store';
import { server } from '../../test/server';
import { fakeJwt } from '../../test/jwt';
import { resetReviewCaseDetailReadsForTests } from './review-case-api';
import {
  resetReviewCaseDecisionAttemptStateForTests,
  reviewCaseDecisionAttemptStorageKey,
} from './review-case-decision-attempt';
import { ReviewCaseCommentsPanel } from './ReviewCaseCommentPanel';
import { reviewCaseCommentLastPage } from './review-case-comment-api';
import {
  resetReviewCaseCommentAttemptStateForTests,
  reviewCaseCommentAttemptStorageKey,
} from './review-case-comment-attempt';
import { ReviewCasePage } from './ReviewCasePage';
import { clearSummaryCountsForAccessDenial } from './review-case-summary-api';
import { reviewCaseOpenAttemptStorageKey } from './review-case-open-attempt';

const caseId = '11111111-1111-4111-8111-111111111111';
const invoiceId = '22222222-2222-4222-8222-222222222222';
const bankId = '33333333-3333-4333-8333-333333333333';
const allocationId = '44444444-4444-4444-8444-444444444444';
const secretToken = 'concurrency-token-must-stay-hidden';

afterEach(() => {
  vi.unstubAllEnvs();
  sessionStorage.removeItem(reviewCaseOpenAttemptStorageKey);
  sessionStorage.removeItem(reviewCaseDecisionAttemptStorageKey);
  sessionStorage.removeItem(reviewCaseCommentAttemptStorageKey);
  resetReviewCaseDecisionAttemptStateForTests();
  resetReviewCaseCommentAttemptStateForTests();
  resetReviewCaseDetailReadsForTests();
});

describe('review case page', () => {
  it('shows an opening form for a development token and keeps reads free of POST', async () => {
    const user = userEvent.setup();
    let posts = 0;
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(listDocument())),
      http.post('/api/review-cases', () => {
        posts += 1;
        return new HttpResponse(null, { status: 500 });
      }),
    );
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    renderPage('/review-cases');

    expect(await screen.findByRole('button', { name: 'Open review case' })).toBeInTheDocument();
    expect(screen.getByText(/You can open one case for this organization/)).toBeInTheDocument();
    await user.click(screen.getByLabelText('Status'));
    await user.click(screen.getByRole('option', { name: 'Open' }));
    await user.click(screen.getByRole('button', { name: 'Apply filters' }));
    expect(posts).toBe(0);
  });

  it('does not show opening or recovery in a cookie session', async () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    sessionStorage.setItem(reviewCaseOpenAttemptStorageKey, '{"schemaVersion":1}');
    sessionStorage.setItem(reviewCaseDecisionAttemptStorageKey, '{"schemaVersion":1,"operation":"ReviewCaseResolve"}');
    let posts = 0;
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(listDocument())),
      http.post('/api/review-cases', () => {
        posts += 1;
        return new HttpResponse(null, { status: 500 });
      }),
      http.post('/api/review-cases/:id/resolve', () => {
        posts += 1;
        return new HttpResponse(null, { status: 500 });
      }),
      http.post('/api/review-cases/:id/dismiss', () => {
        posts += 1;
        return new HttpResponse(null, { status: 500 });
      }),
    );
    sessionStore.replaceCookieSession(cookieSession(['review-cases.read']), true);
    renderPage('/review-cases');

    expect(await screen.findByText(/This screen only reads them/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Open review case' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Send the same opening again' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Resolve' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Dismiss' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Send the same decision again' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Send the same claim again' })).not.toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Review case assignment recovery' })).not.toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Review case assignment' })).not.toBeInTheDocument();
    expect(posts).toBe(0);
    expect(sessionStorage.getItem(reviewCaseOpenAttemptStorageKey)).toBe('{"schemaVersion":1}');
    expect(sessionStorage.getItem(reviewCaseDecisionAttemptStorageKey)).toBe('{"schemaVersion":1,"operation":"ReviewCaseResolve"}');
  });

  it('keeps the confirmation when the later read fails and retries only that read', async () => {
    const user = userEvent.setup();
    const openedId = '55555555-5555-4555-8555-555555555555';
    let posts = 0;
    let reads = 0;
    let summaryReads = 0;
    queryClient.clear();
    server.use(
      http.get('/api/review-cases/summary', () => {
        summaryReads += 1;
        return text(summaryDocument());
      }),
      http.get('/api/review-cases', () => {
        reads += 1;
        if (reads === 1) {
          return text(listDocument());
        }
        return HttpResponse.json({ title: 'Unavailable' }, { status: 500 });
      }),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
      http.post('/api/review-cases', () => {
        posts += 1;
        return HttpResponse.json(openedCase(openedId, invoiceId), {
          status: 201,
          headers: { Location: `/api/review-cases/${openedId}` },
        });
      }),
    );
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    const router = renderReviewCases();
    await user.type(await screen.findByLabelText('Opening invoice id'), invoiceId);
    await user.click(screen.getByRole('button', { name: 'Open review case' }));

    expect(await screen.findByText('Review case opened.')).toBeInTheDocument();
    expect(await screen.findByRole('button', { name: 'Refresh displayed data' })).toBeInTheDocument();
    expect(screen.getByText('The opening is confirmed. The review case lists could not be refreshed.')).toBeInTheDocument();
    expect(posts).toBe(1);
    await waitFor(() => expect(summaryReads).toBeGreaterThan(1));
    expect(sessionStorage.getItem(reviewCaseOpenAttemptStorageKey)).toBeNull();
    expect(router.state.location.search).not.toContain('selected=');

    const readsAtFailure = reads;
    server.use(http.get('/api/review-cases', () => {
      reads += 1;
      return text(listDocument());
    }));
    await user.click(screen.getByRole('button', { name: 'Refresh displayed data' }));
    await waitFor(() => expect(router.state.location.search).toContain(`selected=${openedId}`));
    expect(reads).toBeGreaterThan(readsAtFailure);
    expect(posts).toBe(1);
    expect(sessionStorage.getItem(reviewCaseOpenAttemptStorageKey)).toBeNull();
    expect(screen.getByText('Review case opened.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Refresh displayed data' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Send the same opening again' })).not.toBeInTheDocument();
  }, 15000);

  it('keeps filters changed while an opening or its refresh is still waiting', async () => {
    const user = userEvent.setup();
    const openedId = '55555555-5555-4555-8555-555555555555';
    const postGate = deferred<void>();
    const refreshGate = deferred<void>();
    const refreshHold = { armed: false, held: false };
    let posts = 0;
    let lists = 0;
    queryClient.clear();
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', async () => {
        lists += 1;
        if (refreshHold.armed) {
          refreshHold.armed = false;
          refreshHold.held = true;
          await refreshGate.promise;
        }
        return text(listDocument());
      }),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
      http.post('/api/review-cases', async () => {
        posts += 1;
        await postGate.promise;
        return HttpResponse.json(openedCase(openedId, invoiceId), {
          status: 201,
          headers: { Location: `/api/review-cases/${openedId}` },
        });
      }),
    );
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    const router = renderReviewCases();
    await user.type(await screen.findByLabelText('Opening invoice id'), invoiceId);
    await user.click(screen.getByRole('button', { name: 'Open review case' }));
    await waitFor(() => expect(posts).toBe(1));
    const listsBeforeFilter = lists;
    await chooseStatus(user, 'Open');
    await user.click(screen.getByRole('button', { name: 'Apply filters' }));
    expect(router.state.location.search).toContain('status=Open');
    await waitFor(() => expect(lists).toBeGreaterThan(listsBeforeFilter));
    refreshHold.armed = true;
    postGate.resolve();
    expect(await screen.findByText('Review case opened.')).toBeInTheDocument();
    await waitFor(() => expect(refreshHold.held).toBe(true));
    await user.type(screen.getByLabelText('Invoice id'), invoiceId);
    await user.click(screen.getByRole('button', { name: 'Apply filters' }));
    expect(router.state.location.search).toContain(`invoiceId=${invoiceId}`);
    expect(router.state.location.search).toContain('status=Open');
    expect(router.state.location.search).not.toContain('selected=');
    refreshGate.resolve();
    await flush();
    const retry = screen.queryByRole('button', { name: 'Refresh displayed data' });
    if (retry !== null) {
      await user.click(retry);
    }
    await waitFor(() => expect(router.state.location.search).toContain(`selected=${openedId}`));
    expect(router.state.location.search).toContain('status=Open');
    expect(router.state.location.search).toContain(`invoiceId=${invoiceId}`);
    expect(posts).toBe(1);
    expect(sessionStorage.getItem(reviewCaseOpenAttemptStorageKey)).toBeNull();
  }, 15000);

  it('lets the later opening win when refreshes finish out of order', async () => {
    const user = userEvent.setup();
    const firstOpenedId = '55555555-5555-4555-8555-555555555555';
    const secondOpenedId = '66666666-6666-4666-8666-666666666666';
    const gates: Array<ReturnType<typeof deferred<void>>> = [];
    let posts = 0;
    queryClient.clear();
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', async () => {
        if (gates.length === 0 && posts === 0) {
          return text(listDocument());
        }
        const gate = deferred<void>();
        gates.push(gate);
        await gate.promise;
        return text(listDocument());
      }),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
      http.post('/api/review-cases', async ({ request }) => {
        posts += 1;
        const body = await request.json() as { invoiceId?: string };
        const openedId = posts === 1 ? firstOpenedId : secondOpenedId;
        return HttpResponse.json(openedCase(openedId, body.invoiceId ?? invoiceId), {
          status: 201,
          headers: { Location: `/api/review-cases/${openedId}` },
        });
      }),
    );
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    const router = renderReviewCases();
    await user.type(await screen.findByLabelText('Opening invoice id'), invoiceId);
    await user.click(screen.getByRole('button', { name: 'Open review case' }));
    expect(await screen.findByText('Review case opened.')).toBeInTheDocument();
    await waitFor(() => expect(gates).toHaveLength(1));
    await user.clear(screen.getByLabelText('Opening invoice id'));
    await user.type(screen.getByLabelText('Opening invoice id'), bankId);
    await user.click(screen.getByRole('button', { name: 'Open review case' }));
    expect(await screen.findByText(`Case ${secondOpenedId}`)).toBeInTheDocument();
    await waitFor(() => expect(gates.length).toBeGreaterThanOrEqual(2));
    const firstRefresh = gates[0];
    const secondRefresh = gates[gates.length - 1];
    if (firstRefresh === undefined || secondRefresh === undefined) {
      throw new Error('The opening refreshes were not started.');
    }
    secondRefresh.resolve();
    await waitFor(() => expect(router.state.location.search).toContain(`selected=${secondOpenedId}`));
    firstRefresh.resolve();
    await flush();
    expect(router.state.location.search).toContain(`selected=${secondOpenedId}`);
    expect(router.state.location.search).not.toContain(`selected=${firstOpenedId}`);
    expect(posts).toBe(2);
    gates.forEach((gate) => gate.resolve());
  }, 15000);

  it('does not apply a manual refresh after the session changes', async () => {
    const user = userEvent.setup();
    const openedId = '55555555-5555-4555-8555-555555555555';
    const refreshGate = deferred<void>();
    let posts = 0;
    let reads = 0;
    let holdRead = false;
    queryClient.clear();
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', async () => {
        reads += 1;
        if (reads === 1) {
          return text(listDocument());
        }
        if (holdRead) {
          await refreshGate.promise;
          return text(listDocument());
        }
        return HttpResponse.json({ title: 'Unavailable' }, { status: 500 });
      }),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
      http.post('/api/review-cases', () => {
        posts += 1;
        return HttpResponse.json(openedCase(openedId, invoiceId), {
          status: 201,
          headers: { Location: `/api/review-cases/${openedId}` },
        });
      }),
    );
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    const router = renderReviewCases();
    await user.type(await screen.findByLabelText('Opening invoice id'), invoiceId);
    await user.click(screen.getByRole('button', { name: 'Open review case' }));
    expect(await screen.findByRole('button', { name: 'Refresh displayed data' })).toBeInTheDocument();
    expect(router.state.location.search).not.toContain('selected=');
    const readsAtFailure = reads;
    holdRead = true;
    await user.click(screen.getByRole('button', { name: 'Refresh displayed data' }));
    await waitFor(() => expect(reads).toBeGreaterThan(readsAtFailure));
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    refreshGate.resolve();
    await flush();
    expect(screen.getByText('Review case opened.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Refresh displayed data' })).toBeInTheDocument();
    expect(router.state.location.search).not.toContain('selected=');
    expect(posts).toBe(1);
    expect(sessionStorage.getItem(reviewCaseOpenAttemptStorageKey)).toBeNull();
  }, 15000);

  describe('browser router', () => {
    const openedId = '55555555-5555-4555-8555-555555555555';

    afterEach(() => {
      window.history.pushState({}, '', '/');
    });

    it('does not return to the review case after leaving during the opening', async () => {
      const user = userEvent.setup();
      const postGate = deferred<void>();
      let posts = 0;
      queryClient.clear();
      server.use(
        http.get('/api/review-cases/summary', () => text(summaryDocument())),
        http.get('/api/review-cases', () => text(listDocument())),
        http.get('/api/review-cases/:id', () => text(detailDocument())),
        http.post('/api/review-cases', async () => {
          posts += 1;
          await postGate.promise;
          return HttpResponse.json(openedCase(openedId, invoiceId), {
            status: 201,
            headers: { Location: `/api/review-cases/${openedId}` },
          });
        }),
      );
      sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
      renderBrowserWorkspace();
      await user.type(await screen.findByLabelText('Opening invoice id'), invoiceId);
      await user.click(screen.getByRole('button', { name: 'Open review case' }));
      await waitFor(() => expect(posts).toBe(1));
      await user.click(screen.getByRole('link', { name: 'Invoices' }));
      expect(await screen.findByRole('heading', { name: 'Invoices' })).toBeInTheDocument();
      postGate.resolve();
      await waitFor(() => expect(sessionStorage.getItem(reviewCaseOpenAttemptStorageKey)).toBeNull());
      expect(window.location.pathname).toBe('/invoices');
      expect(window.location.search).not.toContain('selected=');
      expect(posts).toBe(1);
    }, 15000);

    it('does not apply an opening that was left and reopened before the response', async () => {
      const user = userEvent.setup();
      const postGate = deferred<void>();
      let posts = 0;
      queryClient.clear();
      server.use(
        http.get('/api/review-cases/summary', () => text(summaryDocument())),
        http.get('/api/review-cases', () => text(listDocument())),
        http.get('/api/review-cases/:id', () => text(detailDocument())),
        http.post('/api/review-cases', async () => {
          posts += 1;
          await postGate.promise;
          return HttpResponse.json(openedCase(openedId, invoiceId), {
            status: 201,
            headers: { Location: `/api/review-cases/${openedId}` },
          });
        }),
      );
      sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
      renderBrowserWorkspace();
      await user.type(await screen.findByLabelText('Opening invoice id'), invoiceId);
      await user.click(screen.getByRole('button', { name: 'Open review case' }));
      await waitFor(() => expect(posts).toBe(1));
      await user.click(screen.getByRole('link', { name: 'Invoices' }));
      expect(await screen.findByRole('heading', { name: 'Invoices' })).toBeInTheDocument();
      await user.click(screen.getByRole('link', { name: 'Review cases' }));
      expect(await screen.findByText('This opening has no verified result. Sending it again uses the same request.')).toBeInTheDocument();
      postGate.resolve();
      await waitFor(() => expect(sessionStorage.getItem(reviewCaseOpenAttemptStorageKey)).toBeNull());
      expect(window.location.pathname).toBe('/review-cases');
      expect(window.location.search).not.toContain('selected=');
      expect(posts).toBe(1);
      expect(screen.queryByText('Review case opened.')).not.toBeInTheDocument();
    }, 15000);

    it('does not apply a refresh that finishes after the panel unmounts', async () => {
      const user = userEvent.setup();
      const refreshGate = deferred<void>();
      let posts = 0;
      let lists = 0;
      queryClient.clear();
      server.use(
        http.get('/api/review-cases/summary', () => text(summaryDocument())),
        http.get('/api/review-cases', async () => {
          lists += 1;
          if (lists > 1) {
            await refreshGate.promise;
          }
          return text(listDocument());
        }),
        http.get('/api/review-cases/:id', () => text(detailDocument())),
        http.post('/api/review-cases', () => {
          posts += 1;
          return HttpResponse.json(openedCase(openedId, invoiceId), {
            status: 201,
            headers: { Location: `/api/review-cases/${openedId}` },
          });
        }),
      );
      sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
      renderBrowserWorkspace();
      await user.type(await screen.findByLabelText('Opening invoice id'), invoiceId);
      await user.click(screen.getByRole('button', { name: 'Open review case' }));
      expect(await screen.findByText('Review case opened.')).toBeInTheDocument();
      await waitFor(() => expect(lists).toBeGreaterThan(1));
      expect(window.location.search).not.toContain('selected=');
      await user.click(screen.getByRole('link', { name: 'Invoices' }));
      expect(await screen.findByRole('heading', { name: 'Invoices' })).toBeInTheDocument();
      refreshGate.resolve();
      await flush();
      expect(window.location.pathname).toBe('/invoices');
      expect(window.location.search).not.toContain('selected=');
      expect(posts).toBe(1);
    }, 15000);

    it('does not apply a manual refresh that finishes after the panel unmounts', async () => {
      const user = userEvent.setup();
      const refreshGate = deferred<void>();
      let posts = 0;
      let lists = 0;
      let holdRead = false;
      queryClient.clear();
      server.use(
        http.get('/api/review-cases/summary', () => text(summaryDocument())),
        http.get('/api/review-cases', async () => {
          lists += 1;
          if (lists === 1) {
            return text(listDocument());
          }
          if (holdRead) {
            await refreshGate.promise;
            return text(listDocument());
          }
          return HttpResponse.json({ title: 'Unavailable' }, { status: 500 });
        }),
        http.get('/api/review-cases/:id', () => text(detailDocument())),
        http.post('/api/review-cases', () => {
          posts += 1;
          return HttpResponse.json(openedCase(openedId, invoiceId), {
            status: 201,
            headers: { Location: `/api/review-cases/${openedId}` },
          });
        }),
      );
      sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
      renderBrowserWorkspace();
      await user.type(await screen.findByLabelText('Opening invoice id'), invoiceId);
      await user.click(screen.getByRole('button', { name: 'Open review case' }));
      expect(await screen.findByRole('button', { name: 'Refresh displayed data' })).toBeInTheDocument();
      expect(window.location.search).not.toContain('selected=');
      const listsAtFailure = lists;
      holdRead = true;
      await user.click(screen.getByRole('button', { name: 'Refresh displayed data' }));
      await waitFor(() => expect(lists).toBeGreaterThan(listsAtFailure));
      await user.click(screen.getByRole('link', { name: 'Invoices' }));
      expect(await screen.findByRole('heading', { name: 'Invoices' })).toBeInTheDocument();
      refreshGate.resolve();
      await flush();
      expect(window.location.pathname).toBe('/invoices');
      expect(window.location.search).not.toContain('selected=');
      expect(posts).toBe(1);
      expect(sessionStorage.getItem(reviewCaseOpenAttemptStorageKey)).toBeNull();
    }, 15000);
  });

  it('shows a development list and detail without sending the selected id as a list filter', async () => {
    const user = userEvent.setup();
    const listUrls: string[] = [];
    let posts = 0;
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', ({ request }) => {
        listUrls.push(request.url);
        return text(listDocument());
      }),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
      http.post('/api/review-cases', () => {
        posts += 1;
        return new HttpResponse(null, { status: 500 });
      }),
    );

    sessionStore.rotate('memory-token', 4_000_000_000, null);
    renderPage(`/review-cases?selected=${caseId}&page=1&pageSize=50`);

    expect(await screen.findByRole('button', { name: `Show review case ${caseId}` })).toBeInTheDocument();
    expect(await screen.findByText('Invoice outstanding 0.00 PLN')).toBeInTheDocument();
    expect(screen.getByText('Bank transaction unapplied 0.0001 EUR')).toBeInTheDocument();
    expect(screen.getByText(/not one atomic snapshot/)).toBeInTheDocument();
    expect(screen.queryByText(secretToken)).not.toBeInTheDocument();
    expect(listUrls.every((url) => !new URL(url).searchParams.has('selected'))).toBe(true);
    expect(posts).toBe(0);

    await user.click(screen.getByLabelText('Status'));
    await user.click(screen.getByRole('option', { name: 'Open' }));
    await user.click(screen.getByRole('button', { name: 'Apply filters' }));
    await waitFor(() => expect(listUrls.some((url) => new URL(url).searchParams.get('status') === 'Open')).toBe(true));
    expect(listUrls.every((url) => !new URL(url).searchParams.has('selected'))).toBe(true);
    expect(posts).toBe(0);
  });

  it('shows the same read in a verified cookie session', async () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    sessionStore.replaceCookieSession(cookieSession(['review-cases.read']), true);
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
    );

    renderPage(`/review-cases?selected=${caseId}`);

    expect(await screen.findByText('Invoice outstanding 0.00 PLN')).toBeInTheDocument();
    expect(screen.getByText('Bank transaction unapplied 0.0001 EUR')).toBeInTheDocument();
    expect(screen.queryByText(secretToken)).not.toBeInTheDocument();
  });

  it('does not fetch when the cookie session lacks review-cases.read', async () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    sessionStore.replaceCookieSession(cookieSession(['invoices.read']), true);
    let calls = 0;
    server.use(http.get('/api/review-cases', () => {
      calls += 1;
      return text(listDocument());
    }));

    renderPage('/review-cases');

    expect(await screen.findByText('Review cases are not available for this session.')).toBeInTheDocument();
    expect(calls).toBe(0);
  });

  it('shows an empty organization, a not-found detail, and a detail error without retained amounts', async () => {
    const user = userEvent.setup();
    let detailCalls = 0;
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text('{"items":[],"page":1,"pageSize":50,"totalCount":0}')),
      http.get('/api/review-cases/:id', () => {
        detailCalls += 1;
        if (detailCalls === 1) {
          return text(detailDocument());
        }
        return HttpResponse.json(
          { title: 'Inconsistent', code: 'reconciliation.review_case.inconsistent_linked_money', detail: '12345.67 PLN' },
          { status: 500 },
        );
      }),
    );
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    const empty = renderPage('/review-cases');
    expect(await screen.findByText('No review cases are in this organization yet.')).toBeInTheDocument();
    empty.unmount();

    renderPage(`/review-cases?selected=${caseId}`);
    expect(await screen.findByText('Invoice outstanding 0.00 PLN')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Refresh selected case' }));
    expect(await screen.findByText('The linked balances could not be read. No amounts from that response are shown.')).toBeInTheDocument();
    expect(screen.queryByText('0.00 PLN')).not.toBeInTheDocument();
    expect(screen.queryByText('12345.67 PLN')).not.toBeInTheDocument();
  });

  it('reports a missing review case and an empty filter', async () => {
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', ({ request }) => {
        const status = new URL(request.url).searchParams.get('status');
        if (status === 'Resolved') {
          return text('{"items":[],"page":1,"pageSize":50,"totalCount":0}');
        }
        return text(listDocument());
      }),
      http.get('/api/review-cases/:id', () => HttpResponse.json(
        { title: 'Missing', code: 'reconciliation.review_case.not_found' },
        { status: 404 },
      )),
    );
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    renderPage(`/review-cases?selected=${caseId}&status=Resolved`);

    expect(await screen.findByText('No review cases match these filters.')).toBeInTheDocument();
    expect(await screen.findByText('That review case was not found.')).toBeInTheDocument();
    expect(screen.queryByText('0.00 PLN')).not.toBeInTheDocument();
  });

  it('hides the previous detail after access is lost and after a late organization change', async () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    sessionStore.replaceCookieSession(cookieSession(['review-cases.read']), true);
    const gate = deferred<void>();
    let detailCalls = 0;
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', async () => {
        detailCalls += 1;
        if (detailCalls === 1) {
          await gate.promise;
          return text(detailDocument('9999.99'));
        }
        return text(detailDocument('1.00'));
      }),
    );

    renderPage(`/review-cases?selected=${caseId}`);
    expect(await screen.findByText('Loading the selected review case')).toBeInTheDocument();

    sessionStore.replaceCookieSession(cookieSession([]), true);
    gate.resolve();
    expect(await screen.findByText('Review cases are not available for this session.')).toBeInTheDocument();
    expect(screen.queryByText('9999.99 PLN')).not.toBeInTheDocument();
    expect(screen.queryByText('1.00 PLN')).not.toBeInTheDocument();
  });

  it('keeps the filter form aligned with list requests after back and forward', async () => {
    const user = userEvent.setup();
    const listUrls: string[] = [];
    server.use(http.get('/api/review-cases', ({ request }) => {
      listUrls.push(request.url);
      return text(listDocument());
    }));
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    const router = createMemoryRouter(
      [{ path: '/review-cases', element: <ReviewCasePage /> }],
      { initialEntries: ['/review-cases'] },
    );
    render(
      <QueryClientProvider client={queryClient}>
        <RouterProvider router={router} />
      </QueryClientProvider>,
    );

    await screen.findByRole('button', { name: `Show review case ${caseId}` });
    await chooseStatus(user, 'Open');
    await user.type(screen.getByLabelText('Invoice id'), invoiceId);
    await user.type(screen.getByLabelText('Bank transaction id'), bankId);
    await user.click(screen.getByRole('button', { name: 'Apply filters' }));
    await waitFor(() => expect(listQuery(listUrls.at(-1))).toEqual({
      status: 'Open',
      invoiceId,
      bankTransactionId: bankId,
      selected: false,
    }));

    await chooseStatus(user, 'Resolved');
    await user.click(screen.getByRole('button', { name: 'Apply filters' }));
    await waitFor(() => expect(listQuery(listUrls.at(-1)).status).toBe('Resolved'));

    await router.navigate(-1);
    await waitFor(() => expect(listQuery(listUrls.at(-1)).status).toBe('Open'));
    expect(screen.getByRole('combobox', { name: 'Status' })).toHaveTextContent('Open');
    expect(screen.getByLabelText('Invoice id')).toHaveValue(invoiceId);
    expect(screen.getByLabelText('Bank transaction id')).toHaveValue(bankId);
    expect(listQuery(listUrls.at(-1))).toEqual({
      status: 'Open',
      invoiceId,
      bankTransactionId: bankId,
      selected: false,
    });

    await router.navigate(1);
    await waitFor(() => expect(listQuery(listUrls.at(-1)).status).toBe('Resolved'));
    expect(screen.getByRole('combobox', { name: 'Status' })).toHaveTextContent('Resolved');
    expect(screen.getByLabelText('Invoice id')).toHaveValue(invoiceId);
    expect(screen.getByLabelText('Bank transaction id')).toHaveValue(bankId);
    expect(listQuery(listUrls.at(-1))).toEqual({
      status: 'Resolved',
      invoiceId,
      bankTransactionId: bankId,
      selected: false,
    });

    const requestsBeforeSelection = listUrls.length;
    await user.type(screen.getByLabelText('Invoice id'), 'x');
    await user.click(screen.getByRole('button', { name: `Show review case ${caseId}` }));
    expect(screen.getByLabelText('Invoice id')).toHaveValue(`${invoiceId}x`);
    expect(listUrls).toHaveLength(requestsBeforeSelection);
    expect(listQuery(listUrls.at(-1)).invoiceId).toBe(invoiceId);
    expect(listUrls.every((url) => !new URL(url).searchParams.has('selected'))).toBe(true);
  });

  it('offers update access when a development read is forbidden', async () => {
    const user = userEvent.setup();
    server.use(http.get('/api/review-cases', () => HttpResponse.json({ title: 'Forbidden' }, { status: 403 })));
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    renderPage('/review-cases');

    await user.click(await screen.findByRole('button', { name: 'Update access' }));
    expect(sessionStore.getSnapshot().token).toBeNull();
  });

  it('shows a resolved allocation and a dismissed closing note without writing', async () => {
    let posts = 0;
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', ({ params }) => text(params.id === caseId
        ? resolvedDetail()
        : dismissedDetail())),
      http.post('/api/review-cases', () => {
        posts += 1;
        return new HttpResponse(null, { status: 500 });
      }),
      http.post('/api/review-cases/:id/resolve', () => {
        posts += 1;
        return new HttpResponse(null, { status: 500 });
      }),
      http.post('/api/review-cases/:id/dismiss', () => {
        posts += 1;
        return new HttpResponse(null, { status: 500 });
      }),
    );
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    const resolved = renderPage(`/review-cases?selected=${caseId}`);

    expect(await screen.findByText(`Case ${caseId}`)).toBeInTheDocument();
    expect(screen.getByText('Source exact-1')).toBeInTheDocument();
    expect(screen.getByText('Opening note recorded from the matcher')).toBeInTheDocument();
    expect(screen.getByText('Resolution note allocation was already recorded')).toBeInTheDocument();
    expect(screen.getByText('Outcome AllocationRecorded')).toBeInTheDocument();
    expect(screen.getByText(`Allocation ${allocationId}`)).toBeInTheDocument();
    expect(screen.getByText('Opened 2026-11-05T09:00:00Z')).toBeInTheDocument();
    expect(screen.getByText('Closed 2026-11-05T09:30:00Z')).toBeInTheDocument();
    expect(screen.getByText(`Invoice id ${invoiceId}`)).toBeInTheDocument();
    expect(screen.getByText(`Bank transaction id ${bankId}`)).toBeInTheDocument();
    expect(screen.getByText('Invoice outstanding 0.00 PLN')).toBeInTheDocument();
    expect(screen.getByText('Bank transaction unapplied 0.0001 EUR')).toBeInTheDocument();
    expect(screen.queryByText(secretToken)).not.toBeInTheDocument();
    expect(posts).toBe(0);
    resolved.unmount();

    renderPage(`/review-cases?selected=${dismissedCaseId}`);
    expect(await screen.findByText('Resolution note closed without an allocation')).toBeInTheDocument();
    expect(screen.getByText('NeedsInvestigation · Dismissed')).toBeInTheDocument();
    expect(screen.getByText('Outcome none')).toBeInTheDocument();
    expect(screen.getByText('Allocation none')).toBeInTheDocument();
    expect(screen.getByText('Opening note none')).toBeInTheDocument();
    expect(screen.getByText('Closed 2026-11-05T10:00:00Z')).toBeInTheDocument();
    expect(screen.getByText('Invoice id: this case does not name one.')).toBeInTheDocument();
    expect(screen.getByText('Invoice: this case does not name one.')).toBeInTheDocument();
    expect(screen.getByText(`Bank transaction id ${bankId}`)).toBeInTheDocument();
    expect(screen.getByText('Bank transaction: the named document was not found.')).toBeInTheDocument();
    expect(screen.queryByText(secretToken)).not.toBeInTheDocument();
    expect(screen.queryByText('0.00 PLN')).not.toBeInTheDocument();
    expect(posts).toBe(0);
  });

  it('does not open an invalid address', () => {
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    renderPage('/review-cases?page=0&selected=not-a-guid');
    expect(screen.getByText('This review case address can’t be opened. Open Review cases again from the menu.')).toBeInTheDocument();
  });

  it('loads stored history only after View audit and keeps the returned order', async () => {
    const user = userEvent.setup();
    const auditUrls: string[] = [];
    let posts = 0;
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
      http.get('/api/review-cases/:id/audit', ({ request }) => {
        auditUrls.push(request.url);
        return text(auditDocument([
          auditEntry({
            id: dismissedAuditId,
            action: 'Dismissed',
            actorId: 'later-closer',
            recordedAt: '2026-11-05T08:00:00Z',
            reason: null,
            invoiceId: null,
            bankTransactionId: null,
            sourceLabel: null,
            note: null,
          }),
          auditEntry({ id: openedAuditId, action: 'Opened', actorId: 'actor with spaces', recordedAt: '2026-11-05T09:00:00Z' }),
        ]));
      }),
      http.post('/api/review-cases', () => {
        posts += 1;
        return new HttpResponse(null, { status: 500 });
      }),
      http.post('/api/review-cases/:id/resolve', () => {
        posts += 1;
        return new HttpResponse(null, { status: 500 });
      }),
      http.post('/api/review-cases/:id/dismiss', () => {
        posts += 1;
        return new HttpResponse(null, { status: 500 });
      }),
    );
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    renderPage(`/review-cases?selected=${caseId}`);

    expect(await screen.findByText(`Case ${caseId}`)).toBeInTheDocument();
    expect(screen.getByText('Invoice outstanding 0.00 PLN')).toBeInTheDocument();
    expect(auditUrls).toHaveLength(0);
    expect(posts).toBe(0);

    await user.click(screen.getByRole('button', { name: 'View audit' }));
    const actor = await screen.findByText('Actor actor with spaces');
    const closer = screen.getByText('Actor later-closer');
    expect(closer.compareDocumentPosition(actor) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(screen.getByText('Reason CompetingMatch')).toBeInTheDocument();
    expect(screen.getByText('Stored history is separate from the current case and from the balances above.')).toBeInTheDocument();
    expect(screen.queryByText('The opening entry is not in the stored history.')).not.toBeInTheDocument();
    expect(new URL(auditUrls[0] ?? 'http://localhost/missing').searchParams.get('page')).toBe('1');
    expect(new URL(auditUrls[0] ?? 'http://localhost/missing').searchParams.get('pageSize')).toBe('50');
    expect(posts).toBe(0);
    expect(screen.getByLabelText('Status')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Hide audit' }));
    expect(screen.queryByText('Actor actor with spaces')).not.toBeInTheDocument();
  });

  it('shows a closing entry when the count is zero and does not invent the opening entry', async () => {
    const user = userEvent.setup();
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
      http.get('/api/review-cases/:id/audit', () => text(auditDocument([
        auditEntry({
          action: 'Dismissed',
          actorId: 'historical-closer',
          reason: null,
          invoiceId: null,
          bankTransactionId: null,
          sourceLabel: null,
          note: null,
          resolutionNote: null,
        }),
      ], 0))),
    );
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    renderPage(`/review-cases?selected=${caseId}`);
    await user.click(await screen.findByRole('button', { name: 'View audit' }));

    const history = await screen.findByRole('region', { name: 'Stored history' });
    expect(within(history).getByText('Actor historical-closer')).toBeInTheDocument();
    expect(within(history).getByText('Dismissed')).toBeInTheDocument();
    expect(within(history).getByText('Resolution note none')).toBeInTheDocument();
    expect(screen.getByText('The opening entry is not in the stored history.')).toBeInTheDocument();
    expect(screen.queryByText('No stored history was found for this case. Earlier actions may not have been recorded.')).not.toBeInTheDocument();
    expect(screen.queryByText('Actor opening-actor')).not.toBeInTheDocument();
    expect(screen.getByText('Invoice outstanding 0.00 PLN')).toBeInTheDocument();
  });

  it('shows an empty history without claiming that nothing happened', async () => {
    const user = userEvent.setup();
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
      http.get('/api/review-cases/:id/audit', () => text(auditDocument([], 0))),
    );
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    renderPage(`/review-cases?selected=${caseId}`);
    await user.click(await screen.findByRole('button', { name: 'View audit' }));

    expect(await screen.findByText('No stored history was found for this case. Earlier actions may not have been recorded.')).toBeInTheDocument();
    expect(screen.queryByText('Actor')).not.toBeInTheDocument();
  });

  it('does not show audit or request it for a cookie session without audit.read', async () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    sessionStore.replaceCookieSession(cookieSession(['review-cases.read']), true);
    let auditCalls = 0;
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
      http.get('/api/review-cases/:id/audit', () => {
        auditCalls += 1;
        return text(auditDocument([auditEntry()]));
      }),
    );

    renderPage(`/review-cases?selected=${caseId}`);

    expect(await screen.findByText(`Case ${caseId}`)).toBeInTheDocument();
    expect(screen.getByRole('region', { name: 'Organization summary' })).toBeInTheDocument();
    expect(screen.getByText('All cases 1')).toBeInTheDocument();
    expect(screen.getByText('Open 1')).toBeInTheDocument();
    expect(screen.getByText('Resolved 0')).toBeInTheDocument();
    expect(screen.getByText('Dismissed 0')).toBeInTheDocument();
    expect(screen.getByText('Open competing matches 1')).toBeInTheDocument();
    expect(screen.getByText('Open investigations 0')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'View audit' })).not.toBeInTheDocument();
    expect(screen.queryByText('Actor opening-actor')).not.toBeInTheDocument();
    expect(auditCalls).toBe(0);
  });

  it('reads cookie history when audit.read is verified', async () => {
    const user = userEvent.setup();
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    sessionStore.replaceCookieSession(cookieSession(['review-cases.read', 'audit.read']), true);
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
      http.get('/api/review-cases/:id/audit', () => text(auditDocument([
        auditEntry({ action: 'Resolved', actorId: 'cookie-resolver', reason: null, invoiceId: null, bankTransactionId: null, sourceLabel: null, note: null, outcome: 'NoActionRequired', resolutionNote: 'handled in the book' }),
      ]))),
    );

    renderPage(`/review-cases?selected=${caseId}`);
    await user.click(await screen.findByRole('button', { name: 'View audit' }));

    const history = await screen.findByRole('region', { name: 'Stored history' });
    expect(within(history).getByText('Actor cookie-resolver')).toBeInTheDocument();
    expect(within(history).getByText('Outcome NoActionRequired')).toBeInTheDocument();
    expect(within(history).getByText('Allocation none')).toBeInTheDocument();
    expect(within(history).getByText('Resolution note handled in the book')).toBeInTheDocument();
    expect(screen.queryByText('Ada Operator')).not.toBeInTheDocument();
  });

  it('drops a late audit response when another case is selected', async () => {
    const user = userEvent.setup();
    const gate = deferred<void>();
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(`{"items":[${caseDocument()},${caseDocument({ reviewCaseId: otherCaseId, note: 'second case' })}],"page":1,"pageSize":50,"totalCount":2}`)),
      http.get('/api/review-cases/:id', ({ params }) => text(detailFrom(caseDocument({
        reviewCaseId: String(params.id),
        note: params.id === otherCaseId ? 'second case' : 'watch the zero balance',
      })))),
      http.get('/api/review-cases/:id/audit', async () => {
        await gate.promise;
        return text(auditDocument([
          auditEntry({ actorId: 'actor-from-first-case' }),
        ]));
      }),
    );
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    renderPage(`/review-cases?selected=${caseId}`);
    await user.click(await screen.findByRole('button', { name: 'View audit' }));
    expect(await screen.findByText('Loading stored history')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: `Show review case ${otherCaseId}` }));
    gate.resolve();
    expect(await screen.findByText(`Case ${otherCaseId}`)).toBeInTheDocument();
    expect(screen.queryByText('Actor actor-from-first-case')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Hide audit' })).not.toBeInTheDocument();
  });

  it('hides history when verified authorization becomes unavailable', async () => {
    const user = userEvent.setup();
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    sessionStore.replaceCookieSession(cookieSession(['review-cases.read', 'audit.read']), true);
    let auditCalls = 0;
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
      http.get('/api/review-cases/:id/audit', () => {
        auditCalls += 1;
        return text(auditDocument([auditEntry({ actorId: 'actor-kept' })]));
      }),
    );
    renderPage(`/review-cases?selected=${caseId}`);
    await user.click(await screen.findByRole('button', { name: 'View audit' }));
    expect(await screen.findByText('Actor actor-kept')).toBeInTheDocument();
    const callsBeforeLoss = auditCalls;

    sessionStore.markAuthorizationUnavailable();

    expect(await screen.findByText('Review cases are not available for this session.')).toBeInTheDocument();
    expect(screen.queryByText('Actor actor-kept')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'View audit' })).not.toBeInTheDocument();
    await flush();
    expect(auditCalls).toBe(callsBeforeLoss);
  });

  it('keeps the cookie session when stored history stays forbidden', async () => {
    const user = userEvent.setup();
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    sessionStore.replaceCookieSession(cookieSession(['review-cases.read', 'audit.read']), true);
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
      http.get('/api/review-cases/:id/audit', () => HttpResponse.json({ title: 'Forbidden' }, { status: 403 })),
    );
    renderPage(`/review-cases?selected=${caseId}`);
    await user.click(await screen.findByRole('button', { name: 'View audit' }));

    expect(await screen.findByText('Stored history is not available for this organization.')).toBeInTheDocument();
    expect(screen.getByText(`Case ${caseId}`)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Update access' })).not.toBeInTheDocument();
    expect(sessionStore.getSnapshot().authenticated).toBe(true);
    expect(sessionStore.getSnapshot().authorization).toBe('verified');
    expect(sessionStore.getSnapshot().csrfToken).toBe('csrf-session');
    expect(sessionStore.getSnapshot().activeOrganization?.displayName).toBe('North Ledger');

    await user.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByText('Stored history is not available for this organization.')).toBeInTheDocument();
    expect(sessionStore.getSnapshot().csrfToken).toBe('csrf-session');
    expect(screen.getByText('Invoice outstanding 0.00 PLN')).toBeInTheDocument();
  });

  it('keeps the development token and the case detail when stored history is forbidden', async () => {
    const user = userEvent.setup();
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
      http.get('/api/review-cases/:id/audit', () => HttpResponse.json({ title: 'Forbidden' }, { status: 403 })),
    );
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    renderPage(`/review-cases?selected=${caseId}`);
    await user.click(await screen.findByRole('button', { name: 'View audit' }));

    expect(await screen.findByText('You don’t have access to this stored history.')).toBeInTheDocument();
    expect(screen.getByText(`Case ${caseId}`)).toBeInTheDocument();
    expect(screen.getByText('Invoice outstanding 0.00 PLN')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Update access' })).not.toBeInTheDocument();
    expect(sessionStore.getSnapshot().token).toBe('memory-token');
  });

  it('does not keep a previous history visible after the next read cannot be read', async () => {
    const user = userEvent.setup();
    let auditCalls = 0;
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
      http.get('/api/review-cases/:id/audit', () => {
        auditCalls += 1;
        if (auditCalls === 1) {
          return text(auditDocument([auditEntry({ actorId: 'actor-once' })]));
        }
        return text('{"items":[{"action":"Nope"}]}');
      }),
    );
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    renderPage(`/review-cases?selected=${caseId}`);
    await user.click(await screen.findByRole('button', { name: 'View audit' }));
    expect(await screen.findByText('Actor actor-once')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Hide audit' }));
    await user.click(screen.getByRole('button', { name: 'View audit' }));

    expect(await screen.findByText('We couldn’t read the stored history.')).toBeInTheDocument();
    expect(screen.queryByText('Actor actor-once')).not.toBeInTheDocument();
    expect(screen.getByText(`Case ${caseId}`)).toBeInTheDocument();
  });

  it('keeps summary counts independent of filters, paging, and selection', async () => {
    const user = userEvent.setup();
    const summaryUrls: string[] = [];
    server.use(
      http.get('/api/review-cases/summary', ({ request }) => {
        summaryUrls.push(request.url);
        return text(summaryDocument('7', '3', '2', '2', '1', '2'));
      }),
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
    );
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    renderPage(`/review-cases?page=2&pageSize=25&status=Resolved&selected=${caseId}&invoiceId=${invoiceId}`);

    expect(await screen.findByText('All cases 7')).toBeInTheDocument();
    expect(screen.getByText('Open 3')).toBeInTheDocument();
    expect(screen.getByText('Resolved 2')).toBeInTheDocument();
    expect(screen.getByText('Dismissed 2')).toBeInTheDocument();
    expect(screen.getByText('Open competing matches 1')).toBeInTheDocument();
    expect(screen.getByText('Open investigations 2')).toBeInTheDocument();
    expect(summaryUrls.every((url) => new URL(url).search === '')).toBe(true);
    const summaryReads = summaryUrls.length;

    await user.click(screen.getByLabelText('Status'));
    await user.click(screen.getByRole('option', { name: 'Open' }));
    await user.click(screen.getByRole('button', { name: 'Apply filters' }));
    await flush();

    expect(summaryUrls).toHaveLength(summaryReads);
    expect(screen.getByText('All cases 7')).toBeInTheDocument();
  });

  it('reads the summary when the list address is invalid', async () => {
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(listDocument())),
    );
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    renderPage('/review-cases?page=0');

    expect(await screen.findByText('All cases 1')).toBeInTheDocument();
    expect(screen.getByText(/review case address/)).toBeInTheDocument();
  });

  it('refreshes only the organization summary', async () => {
    const user = userEvent.setup();
    let summaryReads = 0;
    let listReads = 0;
    server.use(
      http.get('/api/review-cases/summary', () => {
        summaryReads += 1;
        return text(summaryDocument());
      }),
      http.get('/api/review-cases', () => {
        listReads += 1;
        return text(listDocument());
      }),
    );
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    renderPage('/review-cases');
    expect(await screen.findByText('All cases 1')).toBeInTheDocument();
    const lists = listReads;
    const summaries = summaryReads;

    await user.click(screen.getByRole('button', { name: 'Refresh organization summary' }));
    await waitFor(() => expect(summaryReads).toBeGreaterThan(summaries));
    expect(listReads).toBe(lists);
  });

  it('keeps the list when the summary cannot be read', async () => {
    server.use(
      http.get('/api/review-cases/summary', () => HttpResponse.json({ title: 'Broken' }, { status: 500 })),
      http.get('/api/review-cases', () => text(listDocument())),
    );
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    renderPage('/review-cases');

    expect(await screen.findByText('watch the zero balance')).toBeInTheDocument();
    expect(await screen.findByText('The organization summary could not be loaded.')).toBeInTheDocument();
    expect(screen.queryByText(/All cases/)).not.toBeInTheDocument();
    expect(screen.queryByText('This organization has no persisted review cases.')).not.toBeInTheDocument();
  });

  it('keeps the last counts with a warning after a transient summary error', async () => {
    const user = userEvent.setup();
    let failed = false;
    server.use(
      http.get('/api/review-cases/summary', () => failed
        ? HttpResponse.json({ title: 'Unavailable' }, { status: 500 })
        : text(summaryDocument('4', '3', '1', '0', '2', '1'))),
      http.get('/api/review-cases', () => text(listDocument())),
    );
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    renderPage('/review-cases');
    expect(await screen.findByText('All cases 4')).toBeInTheDocument();

    failed = true;
    await user.click(screen.getByRole('button', { name: 'Refresh organization summary' }));

    expect(await screen.findByText('The organization summary could not be refreshed. The counts shown are from the last successful read.')).toBeInTheDocument();
    expect(screen.getByText('All cases 4')).toBeInTheDocument();
    expect(screen.getByText('Open investigations 1')).toBeInTheDocument();
  });

  it('hides counts after 403 until a later success, including a 500 in between', async () => {
    const user = userEvent.setup();
    let mode: 'ok' | 'denied' | 'failed' | 'restored' = 'ok';
    server.use(
      http.get('/api/review-cases/summary', () => {
        if (mode === 'denied') {
          return HttpResponse.json({ title: 'Forbidden' }, { status: 403 });
        }
        if (mode === 'failed') {
          return HttpResponse.json({ title: 'Unavailable' }, { status: 500 });
        }
        if (mode === 'restored') {
          return text(summaryDocument('9', '4', '3', '2', '3', '1'));
        }
        return text(summaryDocument('4', '3', '1', '0', '2', '1'));
      }),
      http.get('/api/review-cases', () => text(listDocument())),
    );
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    renderPage('/review-cases');
    expect(await screen.findByText('All cases 4')).toBeInTheDocument();

    mode = 'denied';
    await user.click(screen.getByRole('button', { name: 'Refresh organization summary' }));
    const summary = await screen.findByRole('region', { name: 'Organization summary' });
    expect(await within(summary).findByText('You don’t have access to the organization summary. Check your access details.')).toBeInTheDocument();
    expect(screen.queryByText('All cases 4')).not.toBeInTheDocument();

    mode = 'failed';
    await user.click(within(summary).getByRole('button', { name: 'Refresh organization summary' }));
    await flush();
    expect(screen.queryByText('All cases 4')).not.toBeInTheDocument();
    expect(screen.queryByText('All cases 9')).not.toBeInTheDocument();
    expect(within(summary).getByText('You don’t have access to the organization summary. Check your access details.')).toBeInTheDocument();

    mode = 'restored';
    await user.click(within(summary).getByRole('button', { name: 'Refresh organization summary' }));
    expect(await screen.findByText('All cases 9')).toBeInTheDocument();
    expect(screen.queryByText('You don’t have access to the organization summary. Check your access details.')).not.toBeInTheDocument();

    clearSummaryCountsForAccessDenial(
      queryClient,
      ['review-cases', 'summary', sessionStore.getSnapshot().epoch],
      new ApiError(403, null, 'Forbidden', ''),
    );
    expect(screen.getByText('All cases 9')).toBeInTheDocument();
  });

  it('hides counts after 401 and keeps them hidden while the denial is retried', async () => {
    const user = userEvent.setup();
    let denied = false;
    server.use(
      http.get('/api/review-cases/summary', () => denied
        ? HttpResponse.json({ title: 'Unauthorized' }, { status: 401 })
        : text(summaryDocument('4', '3', '1', '0', '2', '1'))),
      http.get('/api/review-cases', () => text(listDocument())),
    );
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    renderPage('/review-cases');
    expect(await screen.findByText('All cases 4')).toBeInTheDocument();

    denied = true;
    await user.click(screen.getByRole('button', { name: 'Refresh organization summary' }));
    const summary = await screen.findByRole('region', { name: 'Organization summary' });
    expect(await within(summary).findByText('Your access has expired or is no longer valid. Update it to continue.')).toBeInTheDocument();
    expect(screen.queryByText('All cases 4')).not.toBeInTheDocument();
    expect(sessionStore.getSnapshot().token).not.toBeNull();

    await user.click(within(summary).getByRole('button', { name: 'Refresh organization summary' }));
    await flush();
    expect(screen.queryByText('All cases 4')).not.toBeInTheDocument();
    expect(sessionStore.getSnapshot().token).not.toBeNull();
  });

  it('hides summary counts when review-cases.read is removed and does not fetch again', async () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    let summaryReads = 0;
    server.use(
      http.get('/api/review-cases/summary', () => {
        summaryReads += 1;
        return text(summaryDocument());
      }),
      http.get('/api/review-cases', () => text(listDocument())),
    );
    sessionStore.replaceCookieSession(cookieSession(['review-cases.read']), true);
    renderPage('/review-cases');
    expect(await screen.findByText('All cases 1')).toBeInTheDocument();
    const reads = summaryReads;

    sessionStore.replaceCookieSession(cookieSession(['invoices.read']), false);
    await waitFor(() => expect(screen.queryByRole('region', { name: 'Organization summary' })).not.toBeInTheDocument());
    expect(screen.queryByText('All cases 1')).not.toBeInTheDocument();
    await flush();
    expect(summaryReads).toBe(reads);
  });

  it('hides the previous summary when cookie readiness changes', async () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    let reads = 0;
    let releaseSecond: ((value: ReturnType<typeof text>) => void) | undefined;
    server.use(
      http.get('/api/review-cases/summary', () => {
        reads += 1;
        if (reads === 1) {
          return text(summaryDocument('4', '4', '0', '0', '0', '4'));
        }
        return new Promise<HttpResponse<string>>((resolve) => {
          releaseSecond = resolve;
        });
      }),
      http.get('/api/review-cases', () => text(listDocument())),
    );
    sessionStore.replaceCookieSession(cookieSession(['review-cases.read']), true);
    renderPage('/review-cases');
    expect(await screen.findByText('All cases 4')).toBeInTheDocument();

    sessionStore.replaceCookieSession(cookieSession(['review-cases.read']), true);
    await waitFor(() => expect(screen.queryByText('All cases 4')).not.toBeInTheDocument());
    expect(await screen.findByText('Loading the organization summary')).toBeInTheDocument();
    releaseSecond?.(text(summaryDocument('6', '1', '2', '3', '1', '0')));
    expect(await screen.findByText('All cases 6')).toBeInTheDocument();
  });

  it('hides the previous summary after the organization epoch changes', async () => {
    let releaseSecond: ((value: HttpResponse<string>) => void) | undefined;
    let reads = 0;
    server.use(
      http.get('/api/review-cases/summary', () => {
        reads += 1;
        if (reads === 1) {
          return text(summaryDocument('4', '3', '1', '0', '2', '1'));
        }
        return new Promise<HttpResponse<string>>((resolve) => {
          releaseSecond = resolve;
        });
      }),
      http.get('/api/review-cases', () => text(listDocument())),
    );
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    renderPage('/review-cases');
    expect(await screen.findByText('All cases 4')).toBeInTheDocument();

    sessionStore.rotate('memory-token', 4_000_000_000, null);
    await waitFor(() => expect(screen.queryByText('All cases 4')).not.toBeInTheDocument());
    expect(await screen.findByText('Loading the organization summary')).toBeInTheDocument();
    releaseSecond?.(text(summaryDocument('2', '2', '0', '0', '0', '2')));
    expect(await screen.findByText('All cases 2')).toBeInTheDocument();
  });

  it('selects a confirmed opening before a held summary read settles', async () => {
    const user = userEvent.setup();
    const openedId = '55555555-5555-4555-8555-555555555555';
    let posts = 0;
    let summaryReads = 0;
    let releaseSummary: ((value: HttpResponse<string>) => void) | undefined;
    server.use(
      http.get('/api/review-cases/summary', () => {
        summaryReads += 1;
        if (summaryReads === 1) {
          return text(summaryDocument());
        }
        return new Promise<HttpResponse<string>>((resolve) => {
          releaseSummary = resolve;
        });
      }),
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
      http.post('/api/review-cases', () => {
        posts += 1;
        return HttpResponse.json(openedCase(openedId, invoiceId), {
          status: 201,
          headers: { Location: `/api/review-cases/${openedId}` },
        });
      }),
    );
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    const router = renderReviewCases();
    expect(await screen.findByText('All cases 1')).toBeInTheDocument();
    await user.type(screen.getByLabelText('Opening invoice id'), invoiceId);
    await user.click(screen.getByRole('button', { name: 'Open review case' }));

    expect(await screen.findByText('Review case opened.')).toBeInTheDocument();
    await waitFor(() => expect(router.state.location.search).toContain(`selected=${openedId}`));
    expect(summaryReads).toBeGreaterThan(1);
    expect(releaseSummary).toBeTypeOf('function');
    expect(posts).toBe(1);
    expect(sessionStorage.getItem(reviewCaseOpenAttemptStorageKey)).toBeNull();

    releaseSummary?.(HttpResponse.text('{"title":"Unavailable"}', {
      status: 500,
      headers: { 'Content-Type': 'application/json' },
    }));
    expect(await screen.findByText('The organization summary could not be refreshed. The counts shown are from the last successful read.')).toBeInTheDocument();
    expect(posts).toBe(1);
    expect(sessionStorage.getItem(reviewCaseOpenAttemptStorageKey)).toBeNull();
    expect(screen.getByText('Review case opened.')).toBeInTheDocument();
  });
});

describe('review case decision page', () => {
  const commandToken = 'AQIDBAUGBwg=';

  afterEach(() => {
    window.history.pushState({}, '', '/');
  });
  const refreshedToken = 'AAAAAAAAB+M=';
  const organizationId = '11111111-1111-1111-1111-111111111111';

  it('keeps the draft token when the selected case is refreshed', async () => {
    const user = userEvent.setup();
    queryClient.clear();
    const bodies: string[] = [];
    let reads = 0;
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', () => {
        reads += 1;
        return text(detailFrom(caseDocument({ concurrencyToken: reads === 1 ? commandToken : refreshedToken })));
      }),
      http.post(`/api/review-cases/${caseId}/resolve`, async ({ request }) => {
        bodies.push(await request.text());
        return HttpResponse.json(decisionResponse('Resolved', 'NoActionRequired', null));
      }),
    );
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    renderPage(`/review-cases?selected=${caseId}`);

    await user.click(await screen.findByRole('button', { name: 'Resolve' }));
    await user.type(screen.getByLabelText('Decision resolution note'), 'kept');
    await user.click(screen.getByRole('button', { name: 'Refresh selected case' }));
    expect(await screen.findByText('Invoice outstanding 0.00 PLN')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Resolve review case' }));

    await screen.findByText('The review case matches this decision.');
    expect(JSON.parse(bodies[0] ?? '{}')).toMatchObject({
      outcome: 'NoActionRequired',
      concurrencyToken: commandToken,
      resolutionNote: 'kept',
    });
    expect(bodies[0]).not.toContain(refreshedToken);
    expect(window.location.search).not.toContain('selected=');
  });

  it('drops an unsent draft when another case is selected and does not rewrite a stored decision', async () => {
    const user = userEvent.setup();
    queryClient.clear();
    const otherToken = 'AQIDBAUGBwg=';
    const stored = JSON.stringify({
      schemaVersion: 1,
      operation: 'ReviewCaseDismiss',
      organizationId,
      caseId,
      outcome: null,
      allocationId: null,
      resolutionNote: 'stored',
      concurrencyToken: commandToken,
      requestBody: JSON.stringify({ resolutionNote: 'stored', concurrencyToken: commandToken }),
      lifecycle: 'uncertain',
      uncertaintyLocked: true,
    });
    sessionStorage.setItem(reviewCaseDecisionAttemptStorageKey, stored);
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(`{"items":[${caseDocument({ concurrencyToken: commandToken })},${caseDocument({ reviewCaseId: otherCaseId, concurrencyToken: otherToken })}],"page":1,"pageSize":50,"totalCount":2}`)),
      http.get('/api/review-cases/:id', ({ params }) => text(detailFrom(caseDocument({
        reviewCaseId: String(params.id),
        concurrencyToken: params.id === otherCaseId ? otherToken : commandToken,
      })))),
    );
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    renderPage(`/review-cases?selected=${caseId}`);

    expect(await screen.findByText('This decision has no verified result. Sending it again uses the same request.')).toBeInTheDocument();
    await user.click(await screen.findByRole('button', { name: `Show review case ${otherCaseId}` }));
    expect(await screen.findByText(`Case ${otherCaseId}`)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Resolve review case' })).not.toBeInTheDocument();
    expect(sessionStorage.getItem(reviewCaseDecisionAttemptStorageKey)).toBe(stored);
  });

  it('drops an unsent draft when the session epoch changes', async () => {
    const user = userEvent.setup();
    queryClient.clear();
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', () => text(detailFrom(caseDocument({ concurrencyToken: commandToken })))),
    );
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    renderPage(`/review-cases?selected=${caseId}`);
    await user.click(await screen.findByRole('button', { name: 'Resolve' }));
    expect(screen.getByRole('button', { name: 'Resolve review case' })).toBeInTheDocument();

    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);

    expect(await screen.findByRole('button', { name: 'Resolve' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Resolve review case' })).not.toBeInTheDocument();
  });

  it('does not unlock a decision from a read that started before local tracking was removed', async () => {
    const user = userEvent.setup();
    queryClient.clear();
    const pending: Array<(value: Response) => void> = [];
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', () => new Promise((resolve) => {
        pending.push(resolve);
      })),
    );
    sessionStorage.setItem(reviewCaseDecisionAttemptStorageKey, JSON.stringify({
      schemaVersion: 1,
      operation: 'ReviewCaseResolve',
      organizationId,
      caseId,
      outcome: 'NoActionRequired',
      allocationId: null,
      resolutionNote: null,
      concurrencyToken: commandToken,
      requestBody: JSON.stringify({ outcome: 'NoActionRequired', concurrencyToken: commandToken }),
      lifecycle: 'sending',
      uncertaintyLocked: false,
    }));
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    renderPage(`/review-cases?selected=${caseId}`);
    expect(await screen.findByRole('button', { name: 'Remove local tracking' })).toBeInTheDocument();
    expect(pending).toHaveLength(1);

    await user.click(screen.getByRole('button', { name: 'Remove local tracking' }));
    await user.click(screen.getByRole('button', { name: 'Confirm removal of local tracking' }));
    await waitFor(() => expect(pending.length).toBeGreaterThanOrEqual(2));
    pending[0]?.(text(detailFrom(caseDocument({ concurrencyToken: commandToken }))));
    await flush();
    expect(screen.queryByRole('button', { name: 'Resolve' })).not.toBeInTheDocument();

    pending[1]?.(text(detailFrom(caseDocument({ concurrencyToken: refreshedToken }))));
    expect(await screen.findByRole('button', { name: 'Resolve' })).toBeInTheDocument();
  });

  it('keeps a confirmed decision when the refresh fails and retries only reads', async () => {
    const user = userEvent.setup();
    queryClient.clear();
    let posts = 0;
    let detailReads = 0;
    let summaryReads = 0;
    server.use(
      http.get('/api/review-cases/summary', () => {
        summaryReads += 1;
        return text(summaryDocument());
      }),
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', () => {
        detailReads += 1;
        if (detailReads > 1) {
          return new HttpResponse(null, { status: 500 });
        }
        return text(detailFrom(caseDocument({ concurrencyToken: commandToken })));
      }),
      http.post(`/api/review-cases/${caseId}/dismiss`, () => {
        posts += 1;
        return HttpResponse.json(decisionResponse('Dismissed', null, null));
      }),
    );
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    renderPage(`/review-cases?selected=${caseId}`);
    await user.click(await screen.findByRole('button', { name: 'Dismiss' }));
    await user.click(screen.getByRole('button', { name: 'Dismiss review case' }));

    expect(await screen.findByText('The review case matches this decision.')).toBeInTheDocument();
    await waitFor(() => expect(summaryReads).toBeGreaterThan(1));
    expect(await screen.findByRole('button', { name: 'Refresh displayed data' })).toBeInTheDocument();
    const readsAfterFailure = detailReads;
    await user.click(screen.getByRole('button', { name: 'Refresh displayed data' }));
    await waitFor(() => expect(detailReads).toBeGreaterThan(readsAfterFailure));
    expect(posts).toBe(1);
    expect(screen.getByText('The review case matches this decision.')).toBeInTheDocument();
  });

  it('refreshes the summary after a verified resolve', async () => {
    const user = userEvent.setup();
    let posts = 0;
    let summaryReads = 0;
    server.use(
      http.get('/api/review-cases/summary', () => {
        summaryReads += 1;
        return text(summaryDocument());
      }),
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', () => text(detailFrom(caseDocument({ concurrencyToken: commandToken })))),
      http.post(`/api/review-cases/${caseId}/resolve`, () => {
        posts += 1;
        return HttpResponse.json(decisionResponse('Resolved', 'NoActionRequired', null));
      }),
    );
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    renderPage(`/review-cases?selected=${caseId}`);
    await user.click(await screen.findByRole('button', { name: 'Resolve' }));
    await user.type(screen.getByLabelText('Decision resolution note'), 'kept');
    await user.click(screen.getByRole('button', { name: 'Resolve review case' }));

    expect(await screen.findByText('The review case matches this decision.')).toBeInTheDocument();
    await waitFor(() => expect(summaryReads).toBeGreaterThan(1));
    expect(posts).toBe(1);
  });

  it('does not apply a late decision to a page the operator has left', async () => {
    const user = userEvent.setup();
    queryClient.clear();
    const gate = deferred<void>();
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', () => text(detailFrom(caseDocument({ concurrencyToken: commandToken })))),
      http.post(`/api/review-cases/${caseId}/dismiss`, async () => {
        await gate.promise;
        return HttpResponse.json(decisionResponse('Dismissed', null, null));
      }),
    );
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    renderBrowserWorkspace();
    await user.click(await screen.findByRole('button', { name: `Show review case ${caseId}` }));
    await user.click(await screen.findByRole('button', { name: 'Dismiss' }));
    await user.click(screen.getByRole('button', { name: 'Dismiss review case' }));
    await user.click(screen.getByRole('link', { name: 'Invoices' }));
    expect(await screen.findByRole('heading', { name: 'Invoices' })).toBeInTheDocument();

    gate.resolve();
    await flush();
    expect(screen.getByRole('heading', { name: 'Invoices' })).toBeInTheDocument();
    expect(screen.queryByText('The review case matches this decision.')).not.toBeInTheDocument();
    expect(sessionStorage.getItem(reviewCaseDecisionAttemptStorageKey)).toBeNull();
  });
});

function decisionResponse(status: string, outcome: string | null, allocation: string | null) {
  return {
    reviewCaseId: caseId,
    reason: 'CompetingMatch',
    status,
    invoiceId,
    bankTransactionId: bankId,
    sourceLabel: 'manual',
    note: 'watch the zero balance',
    outcome,
    resolutionNote: status === 'Dismissed' ? null : 'kept',
    allocationId: allocation,
    openedAt: '2026-11-05T08:00:00Z',
    closedAt: '2026-11-05T09:00:00Z',
    concurrencyToken: 'AAAAAAAAB+M=',
  };
}

function renderPage(path: string) {
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[path]}>
        <ReviewCasePage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

function renderBrowserWorkspace() {
  window.history.pushState({}, '', '/review-cases');
  return render(
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <nav>
          <Link to="/invoices">Invoices</Link>
          <Link to="/review-cases">Review cases</Link>
        </nav>
        <Routes>
          <Route path="/review-cases" element={<ReviewCasePage />} />
          <Route path="/invoices" element={<h1>Invoices</h1>} />
        </Routes>
      </BrowserRouter>
    </QueryClientProvider>,
  );
}

function renderReviewCases() {
  const router = createMemoryRouter(
    [{ path: '/review-cases', element: <ReviewCasePage /> }],
    { initialEntries: ['/review-cases'] },
  );
  render(
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return router;
}

function openedCase(openedId: string, openedInvoiceId: string) {
  return {
    reviewCaseId: openedId,
    reason: 'NeedsInvestigation',
    status: 'Open',
    invoiceId: openedInvoiceId,
    bankTransactionId: null,
    sourceLabel: null,
    note: null,
    outcome: null,
    resolutionNote: null,
    allocationId: null,
    openedAt: '2026-11-05T08:00:00Z',
    closedAt: null,
    concurrencyToken: secretToken,
  };
}

function text(body: string) {
  return HttpResponse.text(body, { headers: { 'Content-Type': 'application/json' } });
}

function listDocument(): string {
  return `{"items":[${caseDocument()}],"page":1,"pageSize":50,"totalCount":1}`;
}

function summaryDocument(
  total = '1',
  open = '1',
  resolved = '0',
  dismissed = '0',
  competing = '1',
  needs = '0',
): string {
  return `{"totalCount":${total},"openCount":${open},"resolvedCount":${resolved},"dismissedCount":${dismissed},"openCompetingMatchCount":${competing},"openNeedsInvestigationCount":${needs}}`;
}

function detailDocument(invoiceAmount = '0'): string {
  return `{"reviewCase":${caseDocument()},"linkedFinancials":{"invoiceFound":true,"invoiceOutstanding":{"amount":${invoiceAmount},"currency":"PLN"},"bankTransactionFound":true,"bankTransactionUnapplied":{"amount":0.0001,"currency":"EUR"}}}`;
}

function caseDocument(overrides: Record<string, string | null> = {}): string {
  return JSON.stringify({
    reviewCaseId: caseId,
    reason: 'CompetingMatch',
    status: 'Open',
    invoiceId,
    bankTransactionId: bankId,
    sourceLabel: 'manual',
    note: 'watch the zero balance',
    outcome: null,
    resolutionNote: null,
    allocationId: null,
    openedAt: '2026-11-05T08:00:00Z',
    closedAt: null,
    concurrencyToken: secretToken,
    ...overrides,
  });
}

const dismissedCaseId = '55555555-5555-4555-8555-555555555555';

function resolvedDetail(): string {
  return detailFrom(caseDocument({
    status: 'Resolved',
    sourceLabel: 'exact-1',
    note: 'recorded from the matcher',
    outcome: 'AllocationRecorded',
    resolutionNote: 'allocation was already recorded',
    allocationId,
    openedAt: '2026-11-05T09:00:00Z',
    closedAt: '2026-11-05T09:30:00Z',
  }));
}

function dismissedDetail(): string {
  return detailFrom(
    caseDocument({
      reviewCaseId: dismissedCaseId,
      reason: 'NeedsInvestigation',
      status: 'Dismissed',
      invoiceId: null,
      sourceLabel: 'smart-1',
      note: null,
      resolutionNote: 'closed without an allocation',
      openedAt: '2026-11-05T09:15:00Z',
      closedAt: '2026-11-05T10:00:00Z',
    }),
    '{"invoiceFound":false,"invoiceOutstanding":null,"bankTransactionFound":false,"bankTransactionUnapplied":null}',
  );
}

function detailFrom(reviewCase: string, linkedFinancials = '{"invoiceFound":true,"invoiceOutstanding":{"amount":0,"currency":"PLN"},"bankTransactionFound":true,"bankTransactionUnapplied":{"amount":0.0001,"currency":"EUR"}}'): string {
  return `{"reviewCase":${reviewCase},"linkedFinancials":${linkedFinancials}}`;
}

function listQuery(url: string | undefined) {
  const params = new URL(url ?? 'http://localhost/missing').searchParams;
  return {
    status: params.get('status'),
    invoiceId: params.get('invoiceId'),
    bankTransactionId: params.get('bankTransactionId'),
    selected: params.has('selected'),
  };
}

async function chooseStatus(user: ReturnType<typeof userEvent.setup>, status: string): Promise<void> {
  await user.click(screen.getByLabelText('Status'));
  await user.click(screen.getByRole('option', { name: status }));
}

function cookieSession(
  capabilities: readonly string[],
  organizationId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
  displayName = 'North Ledger',
): CookieSessionFields {
  return {
    authenticated: true,
    csrfToken: 'csrf-session',
    issuer: 'https://issuer.example',
    subject: 'oidc-operator',
    displayName: 'Ada Operator',
    expiresAt: null,
    organizations: [],
    activeOrganization: {
      organizationId,
      displayName,
    },
    authorization: capabilities.length > 0 ? 'verified' : 'restricted',
    capabilities,
  };
}

const otherCaseId = '88888888-8888-4888-8888-888888888888';
const openedAuditId = '99999999-9999-4999-8999-999999999999';
const dismissedAuditId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';

function auditDocument(items: Record<string, unknown>[], totalCount = items.length): string {
  return JSON.stringify({ items, page: 1, pageSize: 50, totalCount });
}

function auditEntry(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: openedAuditId,
    action: 'Opened',
    actorId: 'opening-actor',
    recordedAt: '2026-11-05T08:00:00Z',
    reason: 'CompetingMatch',
    invoiceId,
    bankTransactionId: bankId,
    sourceLabel: 'manual',
    note: 'opening note',
    outcome: null,
    allocationId: null,
    resolutionNote: null,
    ...overrides,
  };
}

async function flush(): Promise<void> {
  await act(async () => {
    await Promise.resolve();
  });
}

function deferred<T>() {
  let resolve: (value: T) => void = () => {
    throw new Error('The gate was not started.');
  };
  const promise = new Promise<T>((next) => {
    resolve = next;
  });
  return { promise, resolve };
}

const commentId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb1';
const otherOrganizationId = '22222222-2222-4222-8222-222222222222';

function jwtForOrganization(organizationId: string): string {
  const exp = Math.floor(Date.now() / 1000) + 3600;
  const encode = (value: object) => btoa(JSON.stringify(value)).replace(/=+$/g, '').replace(/\+/g, '-').replace(/\//g, '_');
  return `${encode({ alg: 'HS256', typ: 'JWT' })}.${encode({
    exp,
    sub: 'operator-1',
    org_id: organizationId,
  })}.signature`;
}

describe('review case comments', () => {
  it('loads comments only after View comments and keeps the returned order', async () => {
    const user = userEvent.setup();
    let commentReads = 0;
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', ({ request }) => {
        const page = new URL(request.url).searchParams.get('page') ?? '1';
        return text(`{"items":[${caseDocument()}],"page":${page},"pageSize":50,"totalCount":60}`);
      }),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
      http.get('/api/review-cases/:id/comments', () => {
        commentReads += 1;
        return text(commentPage([
          commentItem({ id: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb2', text: 'second', recordedAt: '2026-11-06T12:01:00Z' }),
          commentItem({ text: 'first' }),
        ], '2'));
      }),
    );
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    renderPage('/review-cases');

    expect(await screen.findByRole('button', { name: `Show review case ${caseId}` })).toBeInTheDocument();
    expect(commentReads).toBe(0);
    await chooseStatus(user, 'Open');
    await user.click(screen.getByRole('button', { name: 'Apply filters' }));
    await user.click(screen.getByRole('button', { name: 'Go to next page' }));
    await user.click(screen.getByRole('button', { name: `Show review case ${caseId}` }));
    expect(await screen.findByText(`Case ${caseId}`)).toBeInTheDocument();
    expect(commentReads).toBe(0);

    await user.click(screen.getByRole('button', { name: 'View comments' }));
    const comments = await screen.findByRole('region', { name: 'Review case comments' });
    const entries = within(comments).getAllByRole('listitem');
    expect(entries[0]).toHaveTextContent('second');
    expect(entries[1]).toHaveTextContent('first');
    expect(commentReads).toBe(1);
    await user.click(screen.getByRole('button', { name: 'Hide comments' }));
    expect(screen.queryByRole('region', { name: 'Review case comments' })).not.toBeInTheDocument();
  });

  it('pages comments without treating a disagreeing count as corruption', async () => {
    const user = userEvent.setup();
    const requested: string[] = [];
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
      http.get('/api/review-cases/:id/comments', ({ request }) => {
        const page = Number(new URL(request.url).searchParams.get('page'));
        requested.push(new URL(request.url).search);
        if (page === 1) {
          return text(commentPage([commentItem({ text: 'only' })], '0'));
        }

        return text(commentPage([], '0', page));
      }),
    );
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    renderPage(`/review-cases?selected=${caseId}`);
    await user.click(await screen.findByRole('button', { name: 'View comments' }));

    expect(await screen.findByText('only')).toBeInTheDocument();
    expect(screen.getByText('Comment count 0')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Next comments' })).toBeDisabled();
    expect(requested).toEqual(['?page=1&pageSize=50']);
  });

  it('allows one empty page after a full page and stops there', async () => {
    const user = userEvent.setup();
    const requested: string[] = [];
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
      http.get('/api/review-cases/:id/comments', ({ request }) => {
        const url = new URL(request.url);
        const page = Number(url.searchParams.get('page'));
        requested.push(url.search);
        if (page === 1) {
          return text(commentPage(commentItems(50), '50'));
        }

        return text(commentPage([], '50', page));
      }),
    );
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    renderPage(`/review-cases?selected=${caseId}`);
    await user.click(await screen.findByRole('button', { name: 'View comments' }));
    expect(await screen.findByText('Comment count 50')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Next comments' }));

    expect(await screen.findByText('No comments were found for this case.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Next comments' })).toBeDisabled();
    expect(requested).toEqual(['?page=1&pageSize=50', '?page=2&pageSize=50']);
  });

  it('moves forward when a short page says more comments exist', async () => {
    const user = userEvent.setup();
    const requested: string[] = [];
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
      http.get('/api/review-cases/:id/comments', ({ request }) => {
        const url = new URL(request.url);
        requested.push(url.search);
        const page = Number(url.searchParams.get('page'));
        return text(commentPage([commentItem({ text: `page ${page}` })], '120', page));
      }),
    );
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    renderPage(`/review-cases?selected=${caseId}`);
    await user.click(await screen.findByRole('button', { name: 'View comments' }));
    await user.click(await screen.findByRole('button', { name: 'Next comments' }));

    expect(await screen.findByText('page 2')).toBeInTheDocument();
    expect(requested).toEqual(['?page=1&pageSize=50', '?page=2&pageSize=50']);
  });

  it('does not request the page past the API offset limit', async () => {
    const requested: string[] = [];
    server.use(http.get('/api/review-cases/:id/comments', ({ request }) => {
      requested.push(new URL(request.url).search);
      return text(commentPage(commentItems(50), '9223372036854775807', reviewCaseCommentLastPage));
    }));
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    render(
      <QueryClientProvider client={queryClient}>
        <ReviewCaseCommentsPanel reviewCaseId={caseId} initialPage={reviewCaseCommentLastPage} />
      </QueryClientProvider>,
    );

    expect(await screen.findByText('Comment count 9223372036854775807')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Next comments' })).toBeDisabled();
    expect(requested).toEqual([`?page=${reviewCaseCommentLastPage}&pageSize=50`]);
    expect(requested.join('')).not.toContain('42949674');
  });

  it('renders comment text as text and keeps a byte-order mark', async () => {
    const user = userEvent.setup();
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
      http.get('/api/review-cases/:id/comments', () => text(commentPage([
        commentItem({ text: 'line\n<b>bold</b>', actorId: '\uFEFFactor' }),
      ], '1'))),
    );
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    renderPage(`/review-cases?selected=${caseId}`);
    await user.click(await screen.findByRole('button', { name: 'View comments' }));

    expect(await screen.findByText((_, element) => element?.textContent === 'line\n<b>bold</b>')).toBeInTheDocument();
    expect(document.querySelector('b')).toBeNull();
    expect(screen.getByText((_, element) => element?.textContent === 'Actor \uFEFFactor')).toBeInTheDocument();
    expect(screen.getByText('Recorded 2026-11-06T12:00:00Z')).toBeInTheDocument();
  });

  it('hides comments from a cookie session that cannot read audit', async () => {
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    let commentReads = 0;
    let posts = 0;
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
      http.get('/api/review-cases/:id/comments', () => {
        commentReads += 1;
        return text(commentPage([commentItem()], '1'));
      }),
      http.post('/api/review-cases/:id/comments', () => {
        posts += 1;
        return HttpResponse.json(commentItem());
      }),
    );
    sessionStore.replaceCookieSession(cookieSession(['review-cases.read']), true);
    renderPage(`/review-cases?selected=${caseId}`);

    expect(await screen.findByText(`Case ${caseId}`)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'View comments' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Add comment' })).not.toBeInTheDocument();
    expect(commentReads).toBe(0);
    expect(posts).toBe(0);
  });

  it('lets a cookie operator read comments without posting', async () => {
    const user = userEvent.setup();
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    let posts = 0;
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
      http.get('/api/review-cases/:id/comments', () => text(commentPage([commentItem({ text: 'seeded' })], '1'))),
      http.post('/api/review-cases/:id/comments', () => {
        posts += 1;
        return HttpResponse.json(commentItem());
      }),
    );
    sessionStore.replaceCookieSession(cookieSession(['review-cases.read', 'audit.read']), true);
    renderPage(`/review-cases?selected=${caseId}`);

    expect(await screen.findByText(`Case ${caseId}`)).toBeInTheDocument();
    expect(screen.queryByText('seeded')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Comment')).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'View comments' }));
    expect(await screen.findByText('seeded')).toBeInTheDocument();
    expect(posts).toBe(0);
  });

  it('keeps a cookie session when comments are forbidden', async () => {
    const user = userEvent.setup();
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
      http.get('/api/review-cases/:id/comments', () => HttpResponse.json({ title: 'Forbidden' }, { status: 403 })),
    );
    sessionStore.replaceCookieSession(cookieSession(['review-cases.read', 'audit.read']), true);
    renderPage(`/review-cases?selected=${caseId}`);
    await user.click(await screen.findByRole('button', { name: 'View comments' }));

    expect(await screen.findByText('Comments are not available for this organization.')).toBeInTheDocument();
    expect(screen.getByText(`Case ${caseId}`)).toBeInTheDocument();
    expect(sessionStore.getSnapshot().authenticated).toBe(true);
    expect(sessionStore.getSnapshot().authorization).toBe('verified');
    await user.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByText('Comments are not available for this organization.')).toBeInTheDocument();
    expect(sessionStore.getSnapshot().csrfToken).toBe('csrf-session');
  });

  it('keeps the development token and detail when comments are forbidden', async () => {
    const user = userEvent.setup();
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
      http.get('/api/review-cases/:id/comments', () => HttpResponse.json({ title: 'Forbidden' }, { status: 403 })),
    );
    sessionStore.rotate('memory-token', 4_000_000_000, null);
    renderPage(`/review-cases?selected=${caseId}`);
    await user.click(await screen.findByRole('button', { name: 'View comments' }));

    expect(await screen.findByText('You do not have access to these comments.')).toBeInTheDocument();
    expect(screen.getByText(`Case ${caseId}`)).toBeInTheDocument();
    expect(sessionStore.getSnapshot().token).toBe('memory-token');
  });

  it('offers the comment form for an open, resolved, or dismissed case', async () => {
    const user = userEvent.setup();
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument('2', '1', '1', '1', '1', '0'))),
      http.get('/api/review-cases', () => text(`{"items":[${caseDocument()},${caseDocument({ reviewCaseId: otherCaseId, status: 'Resolved', outcome: 'NoActionRequired', resolutionNote: 'done', closedAt: '2026-11-05T09:30:00Z' })},${caseDocument({ reviewCaseId: dismissedCaseId, status: 'Dismissed', reason: 'NeedsInvestigation', invoiceId: null, resolutionNote: 'closed', closedAt: '2026-11-05T10:00:00Z' })}],"page":1,"pageSize":50,"totalCount":3}`)),
      http.get('/api/review-cases/:id', ({ params }) => {
        if (params.id === otherCaseId) {
          return text(detailFrom(caseDocument({
            reviewCaseId: otherCaseId,
            status: 'Resolved',
            outcome: 'NoActionRequired',
            resolutionNote: 'done',
            closedAt: '2026-11-05T09:30:00Z',
          })));
        }

        if (params.id === dismissedCaseId) {
          return text(dismissedDetail());
        }

        return text(detailDocument());
      }),
    );
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    renderPage('/review-cases');
    await user.click(await screen.findByRole('button', { name: `Show review case ${caseId}` }));
    expect(await screen.findByLabelText('Comment')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: `Show review case ${otherCaseId}` }));
    expect(await screen.findByLabelText('Comment')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: `Show review case ${dismissedCaseId}` }));
    expect(await screen.findByLabelText('Comment')).toBeInTheDocument();
  });

  it('keeps a draft on refresh and drops it when the case or session changes', async () => {
    const user = userEvent.setup();
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(`{"items":[${caseDocument()},${caseDocument({ reviewCaseId: otherCaseId, note: 'second case' })}],"page":1,"pageSize":50,"totalCount":2}`)),
      http.get('/api/review-cases/:id', ({ params }) => text(detailFrom(caseDocument({
        reviewCaseId: String(params.id),
        note: params.id === otherCaseId ? 'second case' : 'watch the zero balance',
      })))),
    );
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    renderPage(`/review-cases?selected=${caseId}`);
    const comment = await screen.findByLabelText('Comment');
    await user.type(comment, 'still typing');
    await user.click(screen.getByRole('button', { name: 'Refresh selected case' }));
    expect(screen.getByLabelText('Comment')).toHaveValue('still typing');

    await user.click(screen.getByRole('button', { name: `Show review case ${otherCaseId}` }));
    expect(await screen.findByLabelText('Comment')).toHaveValue('');
    await user.type(screen.getByLabelText('Comment'), 'other draft');
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    await waitFor(() => expect(screen.getByLabelText('Comment')).toHaveValue(''));
  });

  it('confirms a comment when the later read fails and retries only that read', async () => {
    const user = userEvent.setup();
    let posts = 0;
    let commentReads = 0;
    let summaryReads = 0;
    let failComments = false;
    const bodies: string[] = [];
    queryClient.clear();
    server.use(
      http.get('/api/review-cases/summary', () => {
        summaryReads += 1;
        return text(summaryDocument());
      }),
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
      http.get('/api/review-cases/:id/comments', () => {
        commentReads += 1;
        if (failComments) {
          return HttpResponse.json({ title: 'Unavailable' }, { status: 500 });
        }

        return text(commentPage([], '0'));
      }),
      http.post('/api/review-cases/:id/comments', async ({ request }) => {
        posts += 1;
        const body = await request.text();
        bodies.push(body);
        const textValue = (JSON.parse(body) as { text: string }).text;
        return HttpResponse.json(commentItem({ text: textValue, actorId: 'original-actor' }), { status: 201 });
      }),
    );
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    renderPage(`/review-cases?selected=${caseId}`);
    await user.click(await screen.findByRole('button', { name: 'View comments' }));
    expect(await screen.findByText('No comments were found for this case.')).toBeInTheDocument();
    const summaryAfterLoad = summaryReads;
    failComments = true;
    await user.type(screen.getByLabelText('Comment'), '  saved note  ');
    await user.click(screen.getByRole('button', { name: 'Add comment' }));

    expect(await screen.findByText('Comment saved.')).toBeInTheDocument();
    expect(screen.getByText('Stored actor original-actor')).toBeInTheDocument();
    expect(screen.queryByText('operator-1')).not.toBeInTheDocument();
    expect(await screen.findByRole('button', { name: 'Retry reading comments' })).toBeInTheDocument();
    expect(posts).toBe(1);
    expect(bodies).toEqual(['{"text":"saved note"}']);
    expect(summaryReads).toBe(summaryAfterLoad);
    expect(sessionStorage.getItem(reviewCaseCommentAttemptStorageKey)).toBeNull();
    expect(screen.queryByText('saved note', { selector: 'li, li *' })).not.toBeInTheDocument();

    failComments = false;
    const readsAtFailure = commentReads;
    await user.click(screen.getByRole('button', { name: 'Retry reading comments' }));
    await waitFor(() => expect(commentReads).toBeGreaterThan(readsAtFailure));
    expect(posts).toBe(1);
    expect(screen.getByText('Comment saved.')).toBeInTheDocument();
    expect(sessionStorage.getItem(reviewCaseCommentAttemptStorageKey)).toBeNull();
  });

  it('repeats the stored comment without a new key and leaves the other slots', async () => {
    const user = userEvent.setup();
    const bodies: string[] = [];
    const keys: string[] = [];
    sessionStorage.setItem(reviewCaseOpenAttemptStorageKey, 'open-slot');
    sessionStorage.setItem(reviewCaseDecisionAttemptStorageKey, 'decision-slot');
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
      http.post('/api/review-cases/:id/comments', async ({ request }) => {
        bodies.push(await request.text());
        keys.push(request.headers.get('Idempotency-Key') ?? '');
        return HttpResponse.json({ title: 'Unavailable' }, { status: 500 });
      }),
    );
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    renderPage(`/review-cases?selected=${caseId}`);
    await user.type(await screen.findByLabelText('Comment'), 'same body');
    await user.click(screen.getByRole('button', { name: 'Add comment' }));
    expect(await screen.findByText(/The comment was not verified/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Send the same comment again' }));
    await waitFor(() => expect(keys).toHaveLength(2));

    expect(keys[0]).toBe(keys[1]);
    expect(bodies[0]).toBe(bodies[1]);
    expect(sessionStorage.getItem(reviewCaseOpenAttemptStorageKey)).toBe('open-slot');
    expect(sessionStorage.getItem(reviewCaseDecisionAttemptStorageKey)).toBe('decision-slot');
    expect(sessionStorage.getItem(reviewCaseCommentAttemptStorageKey)).toContain(keys[0]);
  });

  it('drops a confirmed replay when the organization changes without leaving the page', async () => {
    const user = userEvent.setup();
    let replay = false;
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
      http.get('/api/review-cases/:id/comments', () => text(commentPage([], '0'))),
      http.post('/api/review-cases/:id/comments', async ({ request }) => {
        const body = JSON.parse(await request.text()) as { text: string };
        if (!replay) {
          return HttpResponse.json({ title: 'Unavailable' }, { status: 500 });
        }

        return HttpResponse.json(commentItem({ text: body.text, actorId: 'original-actor' }), { status: 200 });
      }),
    );
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    renderPage(`/review-cases?selected=${caseId}`);
    await user.type(await screen.findByLabelText('Comment'), 'desk note');
    await user.click(screen.getByRole('button', { name: 'Add comment' }));
    expect(await screen.findByText('This comment has no verified result. Sending it again uses the same request.')).toBeInTheDocument();
    replay = true;
    await user.click(screen.getByRole('button', { name: 'Send the same comment again' }));
    expect(await screen.findByText('This comment was already stored.')).toBeInTheDocument();
    expect(screen.getByText('Stored actor original-actor')).toBeInTheDocument();
    expect(sessionStorage.getItem(reviewCaseCommentAttemptStorageKey)).toBeNull();

    sessionStore.rotate(jwtForOrganization(otherOrganizationId), 4_000_000_000, null);
    await waitFor(() => expect(screen.queryByText('This comment was already stored.')).not.toBeInTheDocument());
    expect(screen.queryByText('Stored actor original-actor')).not.toBeInTheDocument();
    expect(screen.queryByText('Comment saved.')).not.toBeInTheDocument();
    expect(screen.queryByText('The comment is confirmed. The comment list could not be refreshed.')).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Review cases' })).toBeInTheDocument();
    expect(sessionStorage.getItem(reviewCaseCommentAttemptStorageKey)).toBeNull();
  });

  it('drops a confirmed replay when the epoch changes for the same organization', async () => {
    const user = userEvent.setup();
    let replay = false;
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
      http.get('/api/review-cases/:id/comments', () => text(commentPage([], '0'))),
      http.post('/api/review-cases/:id/comments', async ({ request }) => {
        const body = JSON.parse(await request.text()) as { text: string };
        if (!replay) {
          return HttpResponse.json({ title: 'Unavailable' }, { status: 500 });
        }

        return HttpResponse.json(commentItem({ text: body.text, actorId: 'original-actor' }), { status: 200 });
      }),
    );
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    renderPage(`/review-cases?selected=${caseId}`);
    await user.type(await screen.findByLabelText('Comment'), 'desk note');
    await user.click(screen.getByRole('button', { name: 'Add comment' }));
    expect(await screen.findByText('This comment has no verified result. Sending it again uses the same request.')).toBeInTheDocument();
    replay = true;
    await user.click(screen.getByRole('button', { name: 'Send the same comment again' }));
    expect(await screen.findByText('This comment was already stored.')).toBeInTheDocument();

    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    await waitFor(() => expect(screen.queryByText('This comment was already stored.')).not.toBeInTheDocument());
    expect(screen.queryByText('Stored actor original-actor')).not.toBeInTheDocument();
    expect(screen.queryByText('The comment is confirmed. The comment list could not be refreshed.')).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Review cases' })).toBeInTheDocument();
  });

  it('does not restore a confirmed comment when its refresh finishes after the epoch changes', async () => {
    const user = userEvent.setup();
    const gate = deferred<void>();
    let holdReads = false;
    let replay = false;
    let heldReads = 0;
    let finishedHeldReads = 0;
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
      http.get('/api/review-cases/:id/comments', async () => {
        if (holdReads) {
          heldReads += 1;
          await gate.promise;
          finishedHeldReads += 1;
          return HttpResponse.json({ title: 'Unavailable' }, { status: 500 });
        }

        return text(commentPage([], '0'));
      }),
      http.post('/api/review-cases/:id/comments', async ({ request }) => {
        const body = JSON.parse(await request.text()) as { text: string };
        if (!replay) {
          return HttpResponse.json({ title: 'Unavailable' }, { status: 500 });
        }

        return HttpResponse.json(commentItem({ text: body.text, actorId: 'original-actor' }), { status: 200 });
      }),
    );
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    renderPage(`/review-cases?selected=${caseId}`);
    await user.click(await screen.findByRole('button', { name: 'View comments' }));
    expect(await screen.findByText('No comments were found for this case.')).toBeInTheDocument();
    await user.type(screen.getByLabelText('Comment'), 'desk note');
    await user.click(screen.getByRole('button', { name: 'Add comment' }));
    expect(await screen.findByText('This comment has no verified result. Sending it again uses the same request.')).toBeInTheDocument();
    holdReads = true;
    replay = true;
    await user.click(screen.getByRole('button', { name: 'Send the same comment again' }));
    expect(await screen.findByText('This comment was already stored.')).toBeInTheDocument();
    await waitFor(() => expect(heldReads).toBeGreaterThan(0));

    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    await waitFor(() => expect(screen.queryByText('This comment was already stored.')).not.toBeInTheDocument());
    gate.resolve();
    await waitFor(() => expect(finishedHeldReads).toBe(heldReads));
    await flush();
    expect(screen.queryByText('This comment was already stored.')).not.toBeInTheDocument();
    expect(screen.queryByText('Stored actor original-actor')).not.toBeInTheDocument();
    expect(screen.queryByText('The comment is confirmed. The comment list could not be refreshed.')).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Review cases' })).toBeInTheDocument();
  });

  it('keeps another organization comment hidden and recoverable after the epoch changes', async () => {
    const user = userEvent.setup();
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
      http.post('/api/review-cases/:id/comments', () => HttpResponse.json({ title: 'Unavailable' }, { status: 500 })),
    );
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    renderPage(`/review-cases?selected=${caseId}`);
    await user.type(await screen.findByLabelText('Comment'), 'desk note');
    await user.click(screen.getByRole('button', { name: 'Add comment' }));
    expect(await screen.findByText('desk note')).toBeInTheDocument();
    const stored = sessionStorage.getItem(reviewCaseCommentAttemptStorageKey);
    expect(stored).toContain('desk note');

    sessionStore.rotate(jwtForOrganization(otherOrganizationId), 4_000_000_000, null);
    expect(await screen.findByText('A stored comment belongs to another organization in this tab. It was not replaced and it was not sent.')).toBeInTheDocument();
    expect(screen.queryByText('desk note')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Send the same comment again' })).not.toBeInTheDocument();
    expect(sessionStorage.getItem(reviewCaseCommentAttemptStorageKey)).toBe(stored);
    expect(screen.getByRole('heading', { name: 'Review cases' })).toBeInTheDocument();

    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    expect(await screen.findByRole('button', { name: 'Send the same comment again' })).toBeInTheDocument();
    expect(screen.getByText('desk note')).toBeInTheDocument();
    expect(sessionStorage.getItem(reviewCaseCommentAttemptStorageKey)).toBe(stored);
  });

  it('does not apply a late comment after leaving and returning', async () => {
    const user = userEvent.setup();
    queryClient.clear();
    const gate = deferred<void>();
    server.use(
      http.get('/api/review-cases/summary', () => text(summaryDocument())),
      http.get('/api/review-cases', () => text(listDocument())),
      http.get('/api/review-cases/:id', () => text(detailDocument())),
      http.post('/api/review-cases/:id/comments', async ({ request }) => {
        const body = JSON.parse(await request.text()) as { text: string };
        await gate.promise;
        return HttpResponse.json(commentItem({ text: body.text }), { status: 201 });
      }),
    );
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    renderBrowserWorkspace();
    await user.click(await screen.findByRole('button', { name: `Show review case ${caseId}` }));
    await user.type(await screen.findByLabelText('Comment'), 'late');
    await user.click(screen.getByRole('button', { name: 'Add comment' }));
    await user.click(screen.getByRole('link', { name: 'Invoices' }));
    expect(await screen.findByRole('heading', { name: 'Invoices' })).toBeInTheDocument();

    gate.resolve();
    await waitFor(() => expect(sessionStorage.getItem(reviewCaseCommentAttemptStorageKey)).toBeNull());
    expect(screen.queryByText('Comment saved.')).not.toBeInTheDocument();
    await user.click(screen.getByRole('link', { name: 'Review cases' }));
    expect(await screen.findByRole('heading', { name: 'Review cases' })).toBeInTheDocument();
    expect(screen.queryByText('Comment saved.')).not.toBeInTheDocument();
  });
});

function commentPage(items: Record<string, unknown>[], totalCount: string, page = 1): string {
  return `{"items":${JSON.stringify(items)},"page":${page},"pageSize":50,"totalCount":${totalCount}}`;
}

function commentItem(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: commentId,
    text: 'kept',
    actorId: 'original-actor',
    recordedAt: '2026-11-06T12:00:00Z',
    ...overrides,
  };
}

function commentItems(count: number): Record<string, unknown>[] {
  return Array.from({ length: count }, (_, index) => commentItem({
    id: `bbbbbbbb-bbbb-4bbb-8bbb-${index.toString(16).padStart(12, '0')}`,
    text: `comment ${index}`,
  }));
}
