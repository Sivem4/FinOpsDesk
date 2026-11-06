import { QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { queryClient } from '../../shared/api/query-client';
import { sessionStore, type CookieSessionFields } from '../../shared/api/session-store';
import { server } from '../../test/server';
import { fakeJwt } from '../../test/jwt';
import { resetReviewCaseDetailReadsForTests, type ReviewCaseDetailRead } from './review-case-api';
import { resetReviewCaseAssignmentReadsForTests } from './review-case-assignment-api';
import {
  beginReviewCaseAssignmentAttempt,
  resetReviewCaseAssignmentAttemptStateForTests,
  reviewCaseAssignmentAttemptStorageKey,
  type ReviewCaseAssignmentAttempt,
  type ReviewCaseAssignmentClick,
} from './review-case-assignment-attempt';
import { ReviewCaseAssignmentRecovery } from './ReviewCaseAssignmentPanel';
import { ReviewCasePage } from './ReviewCasePage';

const caseId = '11111111-1111-4111-8111-111111111111';
const otherCaseId = '88888888-8888-4888-8888-888888888888';
const otherToken = 'AAAAAAAAAAE=';
const invoiceId = '22222222-2222-4222-8222-222222222222';
const bankId = '33333333-3333-4333-8333-333333333333';
const token = 'AAAAAAAAAAA=';
const assignedAt = '2026-11-06T12:00:00Z';
const recordedAt = '2026-11-06T12:00:01Z';

afterEach(() => {
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
  queryClient.clear();
  resetReviewCaseDetailReadsForTests();
  resetReviewCaseAssignmentReadsForTests();
  resetReviewCaseAssignmentAttemptStateForTests();
});

describe('review case assignment panel', () => {
  it('starts fresh reads after confirmation and ignores a read that began before it', async () => {
    const user = userEvent.setup();
    let posts = 0;
    let detailReads = 0;
    let assignmentReads = 0;
    let lateFinished = false;
    let refreshClicks = 0;
    let readsAtConfirmation = { detail: 0, assignment: 0 };
    let releaseLate: () => void = () => undefined;
    const late = new Promise<void>((resolve) => {
      releaseLate = resolve;
    });
    const countRefresh = (event: Event) => {
      const target = event.target;
      if (!(target instanceof Element)) {
        return;
      }

      if (target.closest('button')?.textContent?.trim() === 'Refresh the case and assignment') {
        refreshClicks += 1;
      }
    };
    document.addEventListener('click', countRefresh);
    server.use(
      http.get('/api/review-cases/summary', () => json(summaryDocument())),
      http.get('/api/review-cases', () => json(listDocument())),
      http.get(`/api/review-cases/${caseId}`, () => {
        detailReads += 1;
        return json(detailDocument());
      }),
      http.get(`/api/review-cases/${caseId}/assignment`, async () => {
        assignmentReads += 1;
        if (assignmentReads === 2) {
          await late;
          lateFinished = true;
          return json(assignmentDocument(null));
        }

        if (assignmentReads === 1) {
          return json(assignmentDocument(null));
        }

        return json(assignmentDocument({
          actorSource: 'developmentJwt',
          actorId: 'operator-1',
          assignedAt,
        }));
      }),
      http.post(`/api/review-cases/${caseId}/assignment`, () => {
        posts += 1;
        readsAtConfirmation = { detail: detailReads, assignment: assignmentReads };
        return json(claimDocument(), 201);
      }),
    );
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    try {
      renderPage('/review-cases');
      await user.click(await screen.findByRole('button', { name: `Show review case ${caseId}` }));
      expect(await screen.findByRole('button', { name: 'Claim this case' })).toBeEnabled();
      expect(screen.getByRole('button', { name: 'Resolve' })).toBeEnabled();
      expect(screen.getByRole('button', { name: 'Dismiss' })).toBeEnabled();
      expect(screen.getByLabelText('Comment')).toBeEnabled();

      await user.click(screen.getByRole('button', { name: 'Refresh the case and assignment' }));
      await waitFor(() => expect(assignmentReads).toBe(2));
      expect(screen.getByRole('button', { name: 'Claim this case' })).toBeEnabled();
      await user.click(screen.getByRole('button', { name: 'Claim this case' }));
      expect(await screen.findByText('This claim was stored.')).toBeInTheDocument();
      expect(screen.getByText('This result confirms the command. It is separate from the current assignment.')).toBeInTheDocument();
      expect(await screen.findByRole('button', { name: 'Release this assignment' })).toBeEnabled();
      expect(screen.getByText(`Assigned to developmentJwt operator-1 at ${assignedAt}.`)).toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'Claim this case' })).not.toBeInTheDocument();
      expect(detailReads).toBeGreaterThan(readsAtConfirmation.detail);
      expect(assignmentReads).toBeGreaterThan(readsAtConfirmation.assignment);
      const shown = assignmentCache();
      expect(shown?.readSequence).toBeGreaterThan(readsAtConfirmation.assignment);
      expect(shown?.state.assignee?.actorId).toBe('operator-1');

      releaseLate();
      await waitFor(() => expect(lateFinished).toBe(true));
      expect(assignmentCache()?.readSequence).toBe(shown?.readSequence);
      expect(screen.getByText(`Assigned to developmentJwt operator-1 at ${assignedAt}.`)).toBeInTheDocument();
      expect(screen.queryByText('No operator is assigned.')).not.toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'Release this assignment' })).toBeEnabled();
      expect(screen.queryByRole('button', { name: 'Claim this case' })).not.toBeInTheDocument();
      expect(posts).toBe(1);
      expect(refreshClicks).toBe(1);
    } finally {
      document.removeEventListener('click', countRefresh);
    }
  });

  it.each(['detail', 'assignment'] as const)('keeps the confirmation when the fresh %s read fails and retries with GET only', async (failingRead) => {
    const user = userEvent.setup();
    let posts = 0;
    let detailReads = 0;
    let assignmentReads = 0;
    let freshReadsOpen = false;
    server.use(
      http.get('/api/review-cases/summary', () => json(summaryDocument())),
      http.get('/api/review-cases', () => json(listDocument())),
      http.get(`/api/review-cases/${caseId}`, () => {
        detailReads += 1;
        if (failingRead === 'detail' && detailReads > 1 && !freshReadsOpen) {
          return new HttpResponse(null, { status: 500 });
        }

        return json(detailDocument());
      }),
      http.get(`/api/review-cases/${caseId}/assignment`, () => {
        assignmentReads += 1;
        if (assignmentReads === 1) {
          return json(assignmentDocument(null));
        }

        if (failingRead === 'assignment' && !freshReadsOpen) {
          return new HttpResponse(null, { status: 500 });
        }

        return json(assignmentDocument({
          actorSource: 'developmentJwt',
          actorId: 'operator-1',
          assignedAt,
        }));
      }),
      http.post(`/api/review-cases/${caseId}/assignment`, () => {
        posts += 1;
        return json(claimDocument(), 201);
      }),
    );
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    renderPage('/review-cases');
    await user.click(await screen.findByRole('button', { name: `Show review case ${caseId}` }));
    await user.click(await screen.findByRole('button', { name: 'Claim this case' }));
    expect(await screen.findByText('The command is confirmed. The case and assignment could not be refreshed.')).toBeInTheDocument();
    expect(screen.getByText('This claim was stored.')).toBeInTheDocument();
    expect(screen.getByText('This result confirms the command. It is separate from the current assignment.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Retry reading the case and assignment' })).toBeEnabled();
    expect(posts).toBe(1);
    const detailBeforeRetry = detailReads;
    const assignmentBeforeRetry = assignmentReads;
    freshReadsOpen = true;
    await user.click(screen.getByRole('button', { name: 'Retry reading the case and assignment' }));
    expect(await screen.findByRole('button', { name: 'Release this assignment' })).toBeEnabled();
    expect(detailReads).toBeGreaterThan(detailBeforeRetry);
    expect(assignmentReads).toBeGreaterThan(assignmentBeforeRetry);
    expect(screen.getByText(`Assigned to developmentJwt operator-1 at ${assignedAt}.`)).toBeInTheDocument();
    expect(posts).toBe(1);
    expect(screen.getByText('This claim was stored.')).toBeInTheDocument();
  });

  it('keeps the action off when one read fails and refreshes without another POST', async () => {
    const user = userEvent.setup();
    let posts = 0;
    let assignmentReads = 0;
    server.use(
      http.get('/api/review-cases/summary', () => json(summaryDocument())),
      http.get('/api/review-cases', () => json(listDocument())),
      http.get(`/api/review-cases/${caseId}`, () => json(detailDocument())),
      http.get(`/api/review-cases/${caseId}/assignment`, () => {
        assignmentReads += 1;
        if (assignmentReads > 1) {
          return new HttpResponse(null, { status: 500 });
        }

        return json(assignmentDocument(null));
      }),
      http.post(`/api/review-cases/${caseId}/assignment`, () => {
        posts += 1;
        return json(claimDocument(), 201);
      }),
    );
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    renderPage('/review-cases');
    await user.click(await screen.findByRole('button', { name: `Show review case ${caseId}` }));
    expect(await screen.findByRole('button', { name: 'Claim this case' })).toBeEnabled();
    await user.click(screen.getByRole('button', { name: 'Refresh the case and assignment' }));
    expect(await screen.findByText('The assignment could not be read.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Claim this case' })).not.toBeInTheDocument();
    expect(posts).toBe(0);
  });

  it('does not send when the rendered button is older than the cache', async () => {
    const user = userEvent.setup();
    let posts = 0;
    server.use(
      http.get('/api/review-cases/summary', () => json(summaryDocument())),
      http.get('/api/review-cases', () => json(listDocument())),
      http.get(`/api/review-cases/${caseId}`, () => json(detailDocument())),
      http.get(`/api/review-cases/${caseId}/assignment`, () => json(assignmentDocument(null))),
      http.post(`/api/review-cases/${caseId}/assignment`, () => {
        posts += 1;
        return json(claimDocument(), 201);
      }),
    );
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    renderPage('/review-cases');
    await user.click(await screen.findByRole('button', { name: `Show review case ${caseId}` }));
    const claim = await screen.findByRole('button', { name: 'Claim this case' });
    const epoch = sessionStore.getSnapshot().epoch;
    const detailKey = ['review-cases', 'detail', epoch, caseId] as const;
    const assignmentKey = ['review-cases', 'assignment', epoch, caseId] as const;
    const detail = queryClient.getQueryData<ReviewCaseDetailRead>(detailKey);
    const command = detail?.command;
    if (detail === undefined || command === null || command === undefined) {
      throw new Error('The detail read was not ready.');
    }

    queryClient.setQueryData<ReviewCaseDetailRead>(detailKey, {
      ...detail,
      command: { ...command, concurrencyToken: otherToken },
    });
    expect(claim.isConnected).toBe(true);
    claim.click();
    expect(await screen.findByText('Claim and release need a fresh matching read of this case. The command was not sent.')).toBeInTheDocument();
    expect(posts).toBe(0);
    expect(sessionStorage.getItem(reviewCaseAssignmentAttemptStorageKey)).toBeNull();

    queryClient.setQueryData<ReviewCaseDetailRead>(detailKey, detail);
    const restored = await screen.findByRole('button', { name: 'Claim this case' });
    const assignmentQuery = queryClient.getQueryCache().find({ queryKey: assignmentKey, exact: true });
    if (assignmentQuery === undefined) {
      throw new Error('The assignment read was not ready.');
    }

    assignmentQuery.setState({
      status: 'error',
      fetchStatus: 'idle',
      error: new Error('stale assignment'),
      data: assignmentQuery.state.data,
    });
    expect(restored.isConnected).toBe(true);
    restored.click();
    expect(await screen.findByText('Claim and release need a fresh matching read of this case. The command was not sent.')).toBeInTheDocument();
    expect(posts).toBe(0);
    expect(sessionStorage.getItem(reviewCaseAssignmentAttemptStorageKey)).toBeNull();
  });

  it('does not publish a claim result after the operator selects another case', async () => {
    const user = userEvent.setup();
    let posts = 0;
    let releasePost: (value: Response) => void = () => undefined;
    const gate = new Promise<Response>((resolve) => {
      releasePost = resolve;
    });
    server.use(
      http.get('/api/review-cases/summary', () => json(summaryDocument(2))),
      http.get('/api/review-cases', () => json(listDocument([
        detailDocument().reviewCase,
        detailDocumentFor(otherCaseId).reviewCase,
      ]))),
      http.get('/api/review-cases/:id', ({ params }) => json(detailDocumentFor(String(params.id)))),
      http.get('/api/review-cases/:id/assignment', ({ params }) => json(assignmentDocumentFor(String(params.id), null))),
      http.post('/api/review-cases/:id/assignment', async ({ params }) => {
        posts += 1;
        expect(params.id).toBe(caseId);
        return gate;
      }),
    );
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    renderPage('/review-cases');
    await user.click(await screen.findByRole('button', { name: `Show review case ${caseId}` }));
    await user.click(await screen.findByRole('button', { name: 'Claim this case' }));
    await waitFor(() => expect(posts).toBe(1));
    await user.click(screen.getByRole('button', { name: `Show review case ${otherCaseId}` }));
    expect(screen.getByText(`Claim for case ${caseId}`)).toBeInTheDocument();
    releasePost(json(claimDocument(), 201));
    await settledWithoutResult();
    expect(posts).toBe(1);
  });

  it('does not publish a claim result when the page returns before the response', async () => {
    const user = userEvent.setup();
    let posts = 0;
    let releasePost: (value: Response) => void = () => undefined;
    const gate = new Promise<Response>((resolve) => {
      releasePost = resolve;
    });
    server.use(
      http.get('/api/review-cases/summary', () => json(summaryDocument())),
      http.get('/api/review-cases', () => json(listDocument())),
      http.get(`/api/review-cases/${caseId}`, () => json(detailDocument())),
      http.get(`/api/review-cases/${caseId}/assignment`, () => json(assignmentDocument(null))),
      http.post(`/api/review-cases/${caseId}/assignment`, async () => {
        posts += 1;
        return gate;
      }),
    );
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    const first = renderPage('/review-cases');
    await user.click(await screen.findByRole('button', { name: `Show review case ${caseId}` }));
    await user.click(await screen.findByRole('button', { name: 'Claim this case' }));
    await waitFor(() => expect(posts).toBe(1));
    first.unmount();
    renderPage('/review-cases');
    await user.click(await screen.findByRole('button', { name: `Show review case ${caseId}` }));
    expect(screen.getByText(`Claim for case ${caseId}`)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Send the same claim again' })).toBeEnabled();
    releasePost(json(claimDocument(), 201));
    await settledWithoutResult();
    expect(posts).toBe(1);
  });

  it('does not publish a recovery replay after returning to the same case', async () => {
    const user = userEvent.setup();
    let posts = 0;
    let replayKey = '';
    let replayBody = '';
    let releasePost: (value: Response) => void = () => undefined;
    const gate = new Promise<Response>((resolve) => {
      releasePost = resolve;
    });
    server.use(
      http.get('/api/review-cases/summary', () => json(summaryDocument(2))),
      http.get('/api/review-cases', () => json(listDocument([
        detailDocument().reviewCase,
        detailDocumentFor(otherCaseId).reviewCase,
      ]))),
      http.get('/api/review-cases/:id', ({ params }) => json(detailDocumentFor(String(params.id)))),
      http.get('/api/review-cases/:id/assignment', ({ params }) => json(assignmentDocumentFor(String(params.id), null))),
      http.post('/api/review-cases/:id/assignment', async ({ params, request }) => {
        posts += 1;
        replayKey = request.headers.get('Idempotency-Key') ?? '';
        replayBody = await request.text();
        expect(String(params.id).toLowerCase()).toBe(caseId);
        return gate;
      }),
    );
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    const attempt = beginReviewCaseAssignmentAttempt('claim', click());
    renderPage('/review-cases');
    await user.click(await screen.findByRole('button', { name: `Show review case ${caseId}` }));
    await user.click(await screen.findByRole('button', { name: 'Send the stored claim' }));
    await waitFor(() => expect(posts).toBe(1));
    await user.click(screen.getByRole('button', { name: `Show review case ${otherCaseId}` }));
    await user.click(screen.getByRole('button', { name: `Show review case ${caseId}` }));
    expect(await screen.findByRole('heading', { name: 'Selected review case' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: `Show review case ${caseId}` })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: 'Send the same claim again' })).toBeEnabled();
    releasePost(json(claimDocument(), 200));
    await settledWithoutResult();
    expect(posts).toBe(1);
    expect(replayKey).toBe(attempt.idempotencyKey);
    expect(replayBody).toBe(attempt.requestBody);
  });

  it('does not mount assignment recovery or call assignment for a cookie session', async () => {
    const user = userEvent.setup();
    vi.stubEnv('VITE_AUTH_MODE', 'cookie');
    let assignmentReads = 0;
    server.use(
      http.get('/api/review-cases/summary', () => json(summaryDocument())),
      http.get('/api/review-cases', () => json(listDocument())),
      http.get(`/api/review-cases/${caseId}`, () => json(detailDocument())),
      http.get('/api/review-cases/:id/assignment', () => {
        assignmentReads += 1;
        return json(assignmentDocument(null));
      }),
      http.post('/api/review-cases/:id/assignment', () => new HttpResponse(null, { status: 500 })),
      http.post('/api/review-cases/:id/assignment/release', () => new HttpResponse(null, { status: 500 })),
    );
    sessionStore.replaceCookieSession(cookieSession([
      'review-cases.read',
      'audit.read',
      'review-cases.assign',
      'review-cases.assignment.read',
    ]), true);
    renderPage('/review-cases');
    await user.click(await screen.findByRole('button', { name: `Show review case ${caseId}` }));
    expect(await screen.findByText(`Case ${caseId}`)).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Review case assignment' })).not.toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Review case assignment recovery' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Claim this case' })).not.toBeInTheDocument();
    expect(assignmentReads).toBe(0);
  });

  it('shows the two storage failures and hides another operator command', async () => {
    const user = userEvent.setup();
    let posts = 0;
    server.use(http.post(`/api/review-cases/${caseId}/assignment`, () => {
      posts += 1;
      return json(claimDocument(), 201);
    }));
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    const attempt = beginReviewCaseAssignmentAttempt('claim', click());
    render(<QueryClientProvider client={queryClient}><ReviewCaseAssignmentRecovery /></QueryClientProvider>);
    expect(screen.getByText(`Claim for case ${caseId}`)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Remove assignment tracking' }));
    expect(screen.getByText(`Claim for case ${caseId}`)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Keep assignment tracking' }));
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('storage failed');
    });
    await user.click(screen.getByRole('button', { name: 'Send the stored claim' }));
    expect(await screen.findByText('The assignment command was not sent.')).toBeInTheDocument();
    expect(posts).toBe(0);
    expect(sessionStorage.getItem(reviewCaseAssignmentAttemptStorageKey)).toContain(attempt.idempotencyKey);

    vi.restoreAllMocks();
    const uncertain = JSON.parse(sessionStorage.getItem(reviewCaseAssignmentAttemptStorageKey) ?? '{}') as ReviewCaseAssignmentAttempt;
    uncertain.lifecycle = 'uncertain';
    uncertain.uncertaintyLocked = true;
    sessionStorage.setItem(reviewCaseAssignmentAttemptStorageKey, JSON.stringify(uncertain));
    resetReviewCaseAssignmentAttemptStateForTests();
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('storage failed');
    });
    await user.click(await screen.findByRole('button', { name: 'Send the same claim again' }));
    expect(await screen.findByText('The assignment retry was not sent.')).toBeInTheDocument();
    expect(posts).toBe(0);
    expect(sessionStorage.getItem(reviewCaseAssignmentAttemptStorageKey)).toContain(attempt.idempotencyKey);

    vi.restoreAllMocks();
    cleanup();
    sessionStore.rotate(jwt('operator-2'), 4_000_000_000, null);
    render(<QueryClientProvider client={queryClient}><ReviewCaseAssignmentRecovery /></QueryClientProvider>);
    expect(await screen.findByText('This tab holds an assignment command for another operator. It was not sent or replaced.')).toBeInTheDocument();
    expect(document.body.textContent ?? '').not.toContain(caseId);
    expect(document.body.textContent ?? '').not.toContain(attempt.idempotencyKey);
    expect(document.body.textContent ?? '').not.toContain(attempt.requestBody);
    expect(posts).toBe(0);
  });

  it('shows a stored release case id while its owner confirms removal', async () => {
    const user = userEvent.setup();
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    beginReviewCaseAssignmentAttempt('release', {
      ...click(),
      assignee: { actorSource: 'developmentJwt', actorId: 'operator-1', assignedAt },
    });
    render(<QueryClientProvider client={queryClient}><ReviewCaseAssignmentRecovery /></QueryClientProvider>);
    expect(screen.getByText(`Release for case ${caseId}`)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Remove assignment tracking' }));
    expect(screen.getByText(`Release for case ${caseId}`)).toBeInTheDocument();
    expect(screen.getByText('Removing local tracking does not undo a claim or release that the server already stored.')).toBeInTheDocument();
  });
});

function renderPage(path: string) {
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[path]}>
        <ReviewCasePage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

function json(body: unknown, status = 200) {
  return HttpResponse.text(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function summaryDocument(openCount = 1) {
  return {
    totalCount: openCount,
    openCount,
    resolvedCount: 0,
    dismissedCount: 0,
    openCompetingMatchCount: openCount,
    openNeedsInvestigationCount: 0,
  };
}

function listDocument(items = [detailDocument().reviewCase]) {
  return { items, page: 1, pageSize: 50, totalCount: items.length };
}

function detailDocument() {
  return {
    reviewCase: {
      reviewCaseId: caseId,
      reason: 'CompetingMatch',
      status: 'Open',
      invoiceId,
      bankTransactionId: bankId,
      sourceLabel: 'manual',
      note: 'assignment seed',
      outcome: null,
      resolutionNote: null,
      allocationId: null,
      openedAt: '2026-11-05T08:00:00Z',
      closedAt: null,
      concurrencyToken: token,
    },
    linkedFinancials: {
      invoiceFound: true,
      invoiceOutstanding: { amount: 0, currency: 'PLN' },
      bankTransactionFound: true,
      bankTransactionUnapplied: { amount: 0.0001, currency: 'EUR' },
    },
  };
}

function detailDocumentFor(id: string) {
  const document = detailDocument();
  return {
    ...document,
    reviewCase: { ...document.reviewCase, reviewCaseId: id },
  };
}

function assignmentDocument(assignee: { actorSource: string; actorId: string; assignedAt: string } | null) {
  return assignmentDocumentFor(caseId, assignee);
}

function assignmentDocumentFor(
  id: string,
  assignee: { actorSource: string; actorId: string; assignedAt: string } | null,
) {
  return { reviewCaseId: id, assignee, concurrencyToken: token };
}

function assignmentCache() {
  return queryClient.getQueryData<{
    readSequence: number;
    state: { assignee: { actorId: string } | null };
  }>([
    'review-cases',
    'assignment',
    sessionStore.getSnapshot().epoch,
    caseId,
  ]);
}

async function settledWithoutResult(): Promise<void> {
  await waitFor(() => {
    expect(sessionStorage.getItem(reviewCaseAssignmentAttemptStorageKey)).toBeNull();
    expect(screen.queryByText('This claim was stored.')).not.toBeInTheDocument();
    expect(screen.queryByText('This claim was already stored.')).not.toBeInTheDocument();
    expect(screen.queryByText('This assignment command has no verified result. Sending it again uses the same request.')).not.toBeInTheDocument();
    expect(screen.queryByText('The command is confirmed. The case and assignment could not be refreshed.')).not.toBeInTheDocument();
    expect(screen.queryByText('The assignment command was not verified. A lost response does not show that the server rolled it back. Send the same command again.')).not.toBeInTheDocument();
  });
}

function claimDocument() {
  return {
    reviewCaseId: caseId,
    operation: 'claim',
    assignee: { actorSource: 'developmentJwt', actorId: 'operator-1', assignedAt },
    recordedAt,
  };
}

function click(): ReviewCaseAssignmentClick {
  const epoch = sessionStore.getSnapshot().epoch;
  return {
    caseId,
    epoch,
    status: 'Open',
    detailToken: token,
    detailSequence: 1,
    assignmentCaseId: caseId,
    assignmentEpoch: epoch,
    assignmentToken: token,
    assignmentSequence: 1,
    assignee: null,
  };
}

function jwt(subject: string): string {
  const exp = Math.floor(Date.now() / 1000) + 3600;
  const encode = (value: object) => btoa(JSON.stringify(value)).replace(/=+$/g, '').replace(/\+/g, '-').replace(/\//g, '_');
  return `${encode({ alg: 'HS256', typ: 'JWT' })}.${encode({
    exp,
    sub: subject,
    org_id: '11111111-1111-1111-1111-111111111111',
  })}.signature`;
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
