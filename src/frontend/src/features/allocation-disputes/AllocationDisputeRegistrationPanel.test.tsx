import { QueryClientProvider, useQuery } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { createMemoryRouter, MemoryRouter, RouterProvider } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { AllocationHistoryPage } from '../allocations/AllocationHistoryPage';
import { ReadOnlyAllocationHistoryPage } from '../allocations/ReadOnlyAllocationHistoryPage';
import { queryClient } from '../../shared/api/query-client';
import { sessionStore } from '../../shared/api/session-store';
import { useSession } from '../../shared/auth/session';
import { server } from '../../test/server';
import { fakeJwt } from '../../test/jwt';
import { listAllocationDisputes } from './allocation-dispute-api';
import { AllocationDisputePage } from './AllocationDisputePage';
import {
  allocationDisputeAttemptStorageKey,
  resetAllocationDisputeAttemptStateForTests,
} from './allocation-dispute-attempt';

const allocationId = '11111111-1111-4111-8111-111111111111';
const otherAllocationId = '22222222-2222-4222-8222-222222222222';
const disputeId = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc';
const statement = 'counterparty challenge';

afterEach(() => {
  vi.restoreAllMocks();
  sessionStorage.clear();
  resetAllocationDisputeAttemptStateForTests();
  queryClient.clear();
  sessionStore.rotate(null, null, null);
});

describe('allocation dispute registration panel', () => {
  it('keeps a draft on the allocation that opened it', async () => {
    const user = userEvent.setup();
    renderHistory([confirmedItem(), confirmedItem({ allocationId: otherAllocationId, invoiceNumber: 'OTHER' })]);
    await user.click(await screen.findByRole('button', { name: `Record dispute for allocation ${allocationId}` }));
    await user.type(screen.getByRole('textbox', { name: 'Statement' }), 'alpha');
    await user.click(screen.getByRole('button', { name: `Record dispute for allocation ${otherAllocationId}` }));
    expect(screen.getByRole('textbox', { name: 'Statement' })).toHaveValue('');
    expect(screen.queryByDisplayValue('alpha')).not.toBeInTheDocument();
  });

  it('does not offer registration for a reversed row or the read-only history', async () => {
    const first = renderHistory([confirmedItem(), reversedItem()]);
    expect(await screen.findByRole('button', { name: `Record dispute for allocation ${allocationId}` })).toBeVisible();
    expect(screen.queryByRole('button', { name: `Record dispute for allocation ${otherAllocationId}` })).not.toBeInTheDocument();
    first.unmount();

    sessionStorage.clear();
    renderReadOnly();
    expect(await screen.findByRole('heading', { name: 'Allocation history' })).toBeVisible();
    expect(screen.queryByRole('button', { name: /Record dispute/ })).not.toBeInTheDocument();
  });

  it('shows the recorded dispute when local removal fails and retries only that removal', async () => {
    const user = userEvent.setup();
    let posts = 0;
    let blockedRemoval = true;
    const originalRemove = Storage.prototype.removeItem;
    vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(function (this: Storage, key: string) {
      if (key === allocationDisputeAttemptStorageKey && blockedRemoval) {
        blockedRemoval = false;
        throw new Error('storage failed');
      }

      return originalRemove.call(this, key);
    });
    server.use(
      http.get('/api/allocations', () => text(historyPage([confirmedItem()]))),
      http.post(`/api/allocations/${allocationId}/disputes`, () => {
        posts += 1;
        return HttpResponse.json(registration(), { status: 201 });
      }),
    );
    renderHistory([confirmedItem()]);
    await record(user);
    const panel = await screen.findByRole('region', { name: `Record dispute for allocation ${allocationId}` });
    expect(within(panel).getByText('Dispute recorded.')).toBeVisible();
    expect(within(panel).getByText('Local tracking for this dispute could not be removed.')).toBeVisible();
    await user.click(within(panel).getByRole('button', { name: 'Remove local tracking' }));
    await waitFor(() => expect(sessionStorage.getItem(allocationDisputeAttemptStorageKey)).toBeNull());
    expect(posts).toBe(1);
    expect(within(panel).getByText('Dispute recorded.')).toBeVisible();
  });

  it('refreshes the active dispute list after leaving the form and leaves a new epoch untouched', async () => {
    const user = userEvent.setup();
    const lists: Array<(response: ReturnType<typeof text>) => void> = [];
    let historyReads = 0;
    let releasePost: ((response: ReturnType<typeof text>) => void) | undefined;
    server.use(
      http.get('/api/allocations', () => {
        historyReads += 1;
        return text(historyPage([confirmedItem()]));
      }),
      http.post(`/api/allocations/${allocationId}/disputes`, () => new Promise<ReturnType<typeof text>>((resolve) => {
        releasePost = resolve;
      })),
      http.get('/api/allocation-disputes', () => new Promise<ReturnType<typeof text>>((resolve) => {
        lists.push(resolve);
      })),
    );
    const router = createMemoryRouter([
      { path: '/allocations', element: <AllocationHistoryPage /> },
      { path: '/allocation-disputes', element: <AllocationDisputePage /> },
    ], { initialEntries: ['/allocations'] });
    signIn();
    render(<QueryClientProvider client={queryClient}><RouterProvider router={router} /></QueryClientProvider>);
    await record(user);
    await router.navigate('/allocation-disputes');
    await waitFor(() => expect(lists).toHaveLength(1));
    lists[0]?.(text(disputeList([])));
    expect(await screen.findByText('No allocation disputes are stored for this organization.')).toBeVisible();
    const historyReadsBeforeCommit = historyReads;
    releasePost?.(text(JSON.stringify(registration()), 201));
    await waitFor(() => expect(lists).toHaveLength(2));
    expect(lists).toHaveLength(2);
    lists[1]?.(text(disputeList([registration()])));
    expect(await screen.findByText(statement)).toBeVisible();
    expect(screen.queryByText('Dispute recorded.')).not.toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/allocation-disputes');
    expect(router.state.location.search).toBe('');
    expect(historyReads).toBe(historyReadsBeforeCommit);

    sessionStorage.clear();
    resetAllocationDisputeAttemptStateForTests();
    queryClient.clear();
    let nextLists = 0;
    server.use(
      http.get('/api/allocations', () => text(historyPage([confirmedItem()]))),
      http.post(`/api/allocations/${allocationId}/disputes`, () => new Promise<ReturnType<typeof text>>(() => undefined)),
      http.get('/api/allocation-disputes', () => {
        nextLists += 1;
        return text(disputeList([]));
      }),
    );
    await router.navigate('/allocations');
    await record(user);
    const epoch = sessionStore.getSnapshot().epoch;
    sessionStore.rotate(fakeJwt(), Math.floor(Date.now() / 1000) + 3600, null);
    queryClient.setQueryData(
      ['allocation-disputes', 'list', sessionStore.getSnapshot().epoch, 1, null],
      { marker: 'kept' },
    );
    await waitFor(() => expect(screen.queryByText('Dispute recorded.')).not.toBeInTheDocument());
    expect(nextLists).toBe(0);
    expect(queryClient.getQueryData(['allocation-disputes', 'list', sessionStore.getSnapshot().epoch, 1, null])).toEqual({ marker: 'kept' });
    expect(sessionStore.getSnapshot().epoch).not.toBe(epoch);
  });

  it('shows a recorded dispute before the active list refresh finishes, then retries only that read', async () => {
    const user = userEvent.setup();
    let posts = 0;
    const held: Array<(response: ReturnType<typeof text>) => void> = [];
    const gate = { hold: false };
    server.use(
      http.get('/api/allocations', () => text(historyPage([confirmedItem()]))),
      http.post(`/api/allocations/${allocationId}/disputes`, () => {
        posts += 1;
        return text(JSON.stringify(registration()), 201);
      }),
      http.get('/api/allocation-disputes', () => {
        if (!gate.hold) {
          return text(disputeList([]));
        }

        return new Promise<ReturnType<typeof text>>((resolve) => {
          held.push(resolve);
        });
      }),
    );
    renderObservedHistory();
    expect(await screen.findByText('Dispute list ready')).toBeVisible();
    gate.hold = true;
    await record(user);
    expect(await screen.findByText('Dispute recorded.')).toBeVisible();
    expect(screen.queryByText('The dispute list could not be refreshed. The recorded dispute stays recorded.')).not.toBeInTheDocument();
    await waitFor(() => expect(held).toHaveLength(1));
    held[0]?.(text('{"title":"Unavailable"}', 500));
    expect(await screen.findByText('The dispute list could not be refreshed. The recorded dispute stays recorded.')).toBeVisible();
    expect(screen.getByText('Dispute recorded.')).toBeVisible();
    expect(posts).toBe(1);

    held.length = 0;
    await user.click(screen.getByRole('button', { name: 'Read the dispute list again' }));
    await waitFor(() => expect(held).toHaveLength(1));
    held[0]?.(text(disputeList([registration()])));
    await waitFor(() => expect(screen.queryByText('The dispute list could not be refreshed. The recorded dispute stays recorded.')).not.toBeInTheDocument());
    expect(screen.getByText('Dispute recorded.')).toBeVisible();
    expect(await screen.findByText('Dispute list ready')).toBeVisible();
    expect(posts).toBe(1);
  });

  it('does not publish a late list refresh after the selected allocation changes', async () => {
    const user = userEvent.setup();
    const held: Array<(response: ReturnType<typeof text>) => void> = [];
    const gate = { hold: false };
    server.use(
      http.get('/api/allocations', () => text(historyPage([
        confirmedItem(),
        confirmedItem({ allocationId: otherAllocationId, invoiceNumber: 'OTHER' }),
      ]))),
      http.post(`/api/allocations/${allocationId}/disputes`, () => text(JSON.stringify(registration()), 201)),
      http.get('/api/allocation-disputes', () => holdDisputeList(gate, held)),
    );
    renderObservedHistory();
    expect(await screen.findByText('Dispute list ready')).toBeVisible();
    gate.hold = true;
    await record(user);
    expect(await screen.findByText('Dispute recorded.')).toBeVisible();
    await waitFor(() => expect(held).toHaveLength(1));
    await user.click(screen.getByRole('button', { name: `Record dispute for allocation ${otherAllocationId}` }));
    held[0]?.(text('{"title":"Unavailable"}', 500));
    expect(screen.queryByText('Dispute recorded.')).not.toBeInTheDocument();
    expect(screen.queryByText('The dispute list could not be refreshed. The recorded dispute stays recorded.')).not.toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'Statement' })).toHaveValue('');
  });

  it('does not publish a late list refresh after the session epoch changes', async () => {
    const user = userEvent.setup();
    const held: Array<(response: ReturnType<typeof text>) => void> = [];
    const gate = { hold: false };
    server.use(
      http.get('/api/allocations', () => text(historyPage([confirmedItem()]))),
      http.post(`/api/allocations/${allocationId}/disputes`, () => text(JSON.stringify(registration()), 201)),
      http.get('/api/allocation-disputes', () => holdDisputeList(gate, held)),
    );
    renderObservedHistory();
    expect(await screen.findByText('Dispute list ready')).toBeVisible();
    gate.hold = true;
    await record(user);
    expect(await screen.findByText('Dispute recorded.')).toBeVisible();
    await waitFor(() => expect(held).toHaveLength(1));
    sessionStore.rotate(fakeJwt(), Math.floor(Date.now() / 1000) + 3600, null);
    const nextKey = ['allocation-disputes', 'list', sessionStore.getSnapshot().epoch, 1, null] as const;
    queryClient.setQueryData(nextKey, { marker: 'kept' });
    held[0]?.(text(disputeList([{ ...registration(), statement: 'late refresh' }])));
    await waitFor(() => expect(screen.queryByText('Dispute recorded.')).not.toBeInTheDocument());
    expect(screen.queryByText('The dispute list could not be refreshed. The recorded dispute stays recorded.')).not.toBeInTheDocument();
    expect(screen.queryByText('late refresh')).not.toBeInTheDocument();
    expect(queryClient.getQueryData(nextKey)).toEqual({ marker: 'kept' });
  });

  it('drops a recovery confirmation when the operator and organization change', async () => {
    const user = userEvent.setup();
    server.use(
      http.get('/api/allocations', () => text(historyPage([confirmedItem()]))),
      http.post(`/api/allocations/${allocationId}/disputes`, () => text(JSON.stringify(registration()), 201)),
    );
    storeUnresolved();
    renderHistory([confirmedItem()]);
    await user.click(await screen.findByRole('button', { name: 'Send the same dispute again' }));
    expect(await screen.findByRole('link', { name: 'Open this allocation dispute' })).toHaveAttribute('href', `/allocation-disputes?selected=${disputeId}`);
    sessionStore.rotate(otherJwt(), Math.floor(Date.now() / 1000) + 3600, null);
    await waitFor(() => expect(screen.queryByRole('link', { name: 'Open this allocation dispute' })).not.toBeInTheDocument());
    expect(screen.queryByText('Dispute recorded.')).not.toBeInTheDocument();
  });

  it('does not publish a replay that finishes after another allocation is selected and the first returns', async () => {
    const user = userEvent.setup();
    let release: ((response: ReturnType<typeof text>) => void) | undefined;
    server.use(
      http.get('/api/allocations', () => text(historyPage([
        confirmedItem(),
        confirmedItem({ allocationId: otherAllocationId, invoiceNumber: 'OTHER' }),
      ]))),
      http.post(`/api/allocations/${allocationId}/disputes`, () => new Promise<ReturnType<typeof text>>((resolve) => {
        release = resolve;
      })),
    );
    storeUnresolved();
    renderHistory([
      confirmedItem(),
      confirmedItem({ allocationId: otherAllocationId, invoiceNumber: 'OTHER' }),
    ]);
    await user.click(await screen.findByRole('button', { name: 'Send the same dispute again' }));
    await waitFor(() => expect(release).toEqual(expect.any(Function)));
    await user.click(screen.getByRole('button', { name: `Record dispute for allocation ${otherAllocationId}` }));
    await user.click(screen.getByRole('button', { name: `Record dispute for allocation ${allocationId}` }));
    release?.(text(JSON.stringify(registration()), 201));
    await waitFor(() => expect(screen.queryByText('This dispute has no verified result. Sending it again uses the same request.')).not.toBeInTheDocument());
    expect(screen.queryByText('Dispute recorded.')).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Open this allocation dispute' })).not.toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'Statement' })).toHaveValue('');
  });

  it('does not publish a replay that finishes after leaving and reopening allocation history', async () => {
    const user = userEvent.setup();
    let release: ((response: ReturnType<typeof text>) => void) | undefined;
    server.use(
      http.get('/api/allocations', () => text(historyPage([confirmedItem()]))),
      http.get('/api/allocation-disputes', () => text(disputeList([]))),
      http.post(`/api/allocations/${allocationId}/disputes`, () => new Promise<ReturnType<typeof text>>((resolve) => {
        release = resolve;
      })),
    );
    storeUnresolved();
    const router = createMemoryRouter([
      { path: '/allocations', element: <AllocationHistoryPage /> },
      { path: '/allocation-disputes', element: <AllocationDisputePage /> },
    ], { initialEntries: ['/allocations'] });
    signIn();
    render(<QueryClientProvider client={queryClient}><RouterProvider router={router} /></QueryClientProvider>);
    await user.click(await screen.findByRole('button', { name: 'Send the same dispute again' }));
    await waitFor(() => expect(release).toEqual(expect.any(Function)));
    await router.navigate('/allocation-disputes');
    expect(await screen.findByRole('heading', { name: 'Allocation disputes' })).toBeVisible();
    await router.navigate('/allocations');
    expect(await screen.findByRole('heading', { name: 'Allocation history' })).toBeVisible();
    release?.(text(JSON.stringify(registration()), 201));
    await waitFor(() => expect(screen.queryByText('This dispute has no verified result. Sending it again uses the same request.')).not.toBeInTheDocument());
    expect(screen.queryByText('Dispute recorded.')).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Open this allocation dispute' })).not.toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/allocations');
    expect(router.state.location.search).toBe('');
  });

  it('keeps local removal beside a recovery replay when deleting the descriptor fails', async () => {
    const user = userEvent.setup();
    let posts = 0;
    const seen: Array<{ key: string | null; body: string }> = [];
    let blockedRemoval = true;
    const originalRemove = Storage.prototype.removeItem;
    vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(function (this: Storage, key: string) {
      if (key === allocationDisputeAttemptStorageKey && blockedRemoval) {
        blockedRemoval = false;
        throw new Error('storage failed');
      }

      return originalRemove.call(this, key);
    });
    server.use(
      http.get('/api/allocations', () => text(historyPage([confirmedItem()]))),
      http.post(`/api/allocations/${allocationId}/disputes`, async ({ request }) => {
        posts += 1;
        seen.push({ key: request.headers.get('Idempotency-Key'), body: await request.text() });
        return text(JSON.stringify(registration()), 201);
      }),
    );
    const stored = storeUnresolved();
    renderHistory([confirmedItem()]);
    await user.click(await screen.findByRole('button', { name: 'Send the same dispute again' }));
    expect(await screen.findByText('Dispute recorded.')).toBeVisible();
    expect(screen.getByText('Local tracking for a recorded dispute could not be removed.')).toBeVisible();
    expect(seen).toEqual([{ key: stored.idempotencyKey, body: stored.requestBody }]);
    await user.click(screen.getByRole('button', { name: 'Remove local tracking' }));
    await waitFor(() => expect(sessionStorage.getItem(allocationDisputeAttemptStorageKey)).toBeNull());
    expect(posts).toBe(1);
    expect(screen.getByText('Dispute recorded.')).toBeVisible();

    const laterKey = 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee';
    sessionStorage.setItem(allocationDisputeAttemptStorageKey, JSON.stringify({ ...stored, idempotencyKey: laterKey }));
    expect(JSON.parse(sessionStorage.getItem(allocationDisputeAttemptStorageKey) ?? '{}').idempotencyKey).toBe(laterKey);
    expect(posts).toBe(1);
  });

  it('shows Update access when replay receives HTTP 401 and keeps the locked command', async () => {
    const user = userEvent.setup();
    let posts = 0;
    const seen: Array<{ key: string | null; body: string }> = [];
    server.use(
      http.get('/api/allocations', () => text(historyPage([confirmedItem()]))),
      http.post(`/api/allocations/${allocationId}/disputes`, async ({ request }) => {
        posts += 1;
        seen.push({ key: request.headers.get('Idempotency-Key'), body: await request.text() });
        return text('{"title":"Unauthorized"}', 401);
      }),
    );
    const stored = storeUnresolved();
    renderHistory([confirmedItem()]);
    await user.click(await screen.findByRole('button', { name: 'Send the same dispute again' }));
    expect(await screen.findByRole('button', { name: 'Update access' })).toBeVisible();
    expect(screen.getByText('Sign in again before recording this dispute.')).toBeVisible();
    expect(screen.queryByText(/This dispute was not recorded/)).not.toBeInTheDocument();
    expect(posts).toBe(1);
    expect(seen).toEqual([{ key: stored.idempotencyKey, body: stored.requestBody }]);
    expect(JSON.parse(sessionStorage.getItem(allocationDisputeAttemptStorageKey) ?? '{}')).toMatchObject({
      idempotencyKey: stored.idempotencyKey,
      requestBody: stored.requestBody,
      uncertaintyLocked: true,
      lifecycle: 'uncertain',
    });
  });

  it('sends nothing when replay cannot be stored and keeps the unresolved command', async () => {
    const user = userEvent.setup();
    let posts = 0;
    server.use(
      http.get('/api/allocations', () => text(historyPage([confirmedItem()]))),
      http.post(`/api/allocations/${allocationId}/disputes`, () => {
        posts += 1;
        return text(JSON.stringify(registration()), 201);
      }),
    );
    const stored = storeUnresolved();
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('storage failed');
    });
    renderHistory([confirmedItem()]);
    await user.click(await screen.findByRole('button', { name: 'Send the same dispute again' }));
    expect(await screen.findByText('The same dispute was not sent again. The unresolved command remains stored.')).toBeVisible();
    expect(screen.queryByText(/was not recorded/)).not.toBeInTheDocument();
    expect(posts).toBe(0);
    expect(JSON.parse(sessionStorage.getItem(allocationDisputeAttemptStorageKey) ?? '{}')).toMatchObject({
      idempotencyKey: stored.idempotencyKey,
      requestBody: stored.requestBody,
      uncertaintyLocked: true,
    });
  });
});

function renderObservedHistory() {
  signIn();
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <AllocationHistoryPage />
        <ActiveDisputeList />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

function ActiveDisputeList() {
  const session = useSession();
  const query = useQuery({
    queryKey: ['allocation-disputes', 'list', session.epoch, 1, null],
    enabled: session.token !== null,
    queryFn: ({ signal }) => listAllocationDisputes(1, null, signal),
  });
  if (query.isError) {
    return <p>Dispute list failed</p>;
  }

  if (query.isSuccess) {
    return <p>Dispute list ready</p>;
  }

  return <p>Dispute list loading</p>;
}

function holdDisputeList(
  gate: { hold: boolean },
  held: Array<(response: ReturnType<typeof text>) => void>,
) {
  if (!gate.hold) {
    return text(disputeList([]));
  }

  return new Promise<ReturnType<typeof text>>((resolve) => {
    held.push(resolve);
  });
}

function storeUnresolved() {
  const idempotencyKey = 'dddddddd-dddd-4ddd-8ddd-dddddddddddd';
  const requestBody = JSON.stringify({ statement });
  const attempt = {
    schemaVersion: 1,
    operation: 'AllocationDisputeRegistration',
    organizationId: '11111111-1111-1111-1111-111111111111',
    actorSource: 'developmentJwt',
    actorId: 'operator-1',
    allocationId,
    idempotencyKey,
    requestBody,
    normalizedStatement: statement,
    lifecycle: 'uncertain',
    uncertaintyLocked: true,
  };
  sessionStorage.setItem(allocationDisputeAttemptStorageKey, JSON.stringify(attempt));
  return attempt;
}

function otherJwt(): string {
  const exp = Math.floor(Date.now() / 1000) + 3600;
  const encode = (value: object) => btoa(JSON.stringify(value)).replace(/=+$/g, '').replace(/\+/g, '-').replace(/\//g, '_');
  return `${encode({ alg: 'HS256', typ: 'JWT' })}.${encode({
    exp,
    sub: 'operator-2',
    org_id: '22222222-2222-4222-8222-222222222222',
  })}.signature`;
}

function renderHistory(items: string[]) {
  signIn();
  server.use(http.get('/api/allocations', () => text(historyPage(items))));
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <AllocationHistoryPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

function renderReadOnly() {
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <ReadOnlyAllocationHistoryPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

async function record(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByRole('button', { name: `Record dispute for allocation ${allocationId}` }));
  await user.type(screen.getByRole('textbox', { name: 'Statement' }), statement);
  await user.click(screen.getByRole('button', { name: (name) => name === 'Record dispute' }));
}

function signIn() {
  sessionStore.rotate(fakeJwt(), Math.floor(Date.now() / 1000) + 3600, null);
}

function text(body: string, status = 200) {
  return HttpResponse.text(body, { status, headers: { 'Content-Type': 'application/json' } });
}

function historyPage(items: string[]) {
  return `{"items":[${items.join(',')}],"page":1,"pageSize":50,"totalCount":${items.length}}`;
}

function confirmedItem(overrides: { allocationId?: string; invoiceNumber?: string } = {}) {
  return `{
    "allocationId":"${overrides.allocationId ?? allocationId}",
    "invoiceId":"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
    "bankTransactionId":"bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
    "invoiceNumber":"${overrides.invoiceNumber ?? 'SAME-NO'}",
    "reference":"REF",
    "amount":{"amount":15.5,"currency":"PLN"},
    "status":"confirmed",
    "confirmedAt":"2026-10-31T12:00:00Z",
    "reversedAt":null,
    "reversalReason":null,
    "idempotencyOperation":"exactMatch"
  }`;
}

function reversedItem() {
  return `{
    "allocationId":"${otherAllocationId}",
    "invoiceId":"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
    "bankTransactionId":"bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
    "invoiceNumber":"OLD",
    "reference":"REF",
    "amount":{"amount":15.5,"currency":"PLN"},
    "status":"reversed",
    "confirmedAt":"2026-10-31T12:00:00Z",
    "reversedAt":"2026-10-31T13:00:00Z",
    "reversalReason":"Duplicate payment",
    "idempotencyOperation":null
  }`;
}

function disputeList(items: Record<string, unknown>[]) {
  return `{"items":${JSON.stringify(items)},"page":1,"pageSize":50,"totalCount":${items.length}}`;
}

function registration() {
  return {
    disputeId,
    allocationId,
    statement,
    actorSource: 'developmentJwt',
    actorId: 'historical-operator',
    recordedAt: '2026-11-07T09:00:00Z',
  };
}
