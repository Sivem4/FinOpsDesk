import { QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import type { ReactElement } from 'react';
import { MemoryRouter } from 'react-router';
import { describe, expect, it, vi } from 'vitest';

import { queryClient } from '../../shared/api/query-client';
import { sessionStore } from '../../shared/api/session-store';
import { StaleSessionError } from '../../shared/api/errors';
import { fakeJwt } from '../../test/jwt';
import { server } from '../../test/server';
import { AllocationDisputePage } from './AllocationDisputePage';
import {
  allocationDisputeResolutionQueryKey,
  type AllocationDisputeResolutionDecision,
} from './allocation-dispute-api';
import { AllocationDisputeResolutionPanel } from './AllocationDisputeResolutionPanel';
import {
  allocationDisputeResolutionAttemptStorageKey,
  AllocationDisputeResolutionNotFreshError,
  AllocationDisputeResolutionVerifiedTrackingError,
  loadAllocationDisputeResolution,
  replayAllocationDisputeResolutionAttempt,
  resetAllocationDisputeResolutionAttemptStateForTests,
  resolutionBoundaryForTests,
  retryAllocationDisputeResolutionDescriptorRemoval,
  submitAllocationDisputeResolution,
} from './allocation-dispute-resolution-attempt';

const disputeId = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc';
const otherDisputeId = 'dddddddd-dddd-4ddd-8ddd-dddddddddddd';
const organizationId = '11111111-1111-1111-1111-111111111111';
const reason = 'counterparty challenge';

describe('allocation dispute resolution attempt', () => {
  it('keeps the new attempt closed while the fresh read is still running', async () => {
    const user = userEvent.setup();
    const posts: Array<{ key: string | null; body: string }> = [];
    const held: Array<(response: Response) => void> = [];
    let reads = 0;
    server.use(
      http.get('/api/allocation-disputes/:disputeId/resolution', () => {
        reads += 1;
        if (reads === 1) {
          return HttpResponse.json(nullDecision());
        }

        return new Promise<Response>((resolve) => {
          held.push(resolve);
        });
      }),
      http.post('/api/allocation-disputes/:disputeId/resolution', async ({ request }) => {
        posts.push({
          key: request.headers.get('Idempotency-Key'),
          body: await request.text(),
        });
        return HttpResponse.json(decision(), { status: 201 });
      }),
    );
    signIn();
    renderPanel(disputeId);
    await user.click(await screen.findByRole('radio', { name: 'Upheld' }));
    await user.type(screen.getByLabelText('Reason'), reason);
    await user.click(screen.getByRole('button', { name: 'Record decision' }));

    expect(await screen.findByText('Decyzja zapisana.')).toBeInTheDocument();
    expect(held).toHaveLength(1);
    expect(posts).toEqual([{
      key: posts[0]?.key ?? null,
      body: JSON.stringify({ outcome: 'upheld', reason }),
    }]);
    expect(posts[0]?.key).toMatch(/^[0-9a-f-]{36}$/i);
    expect(sessionStorage.getItem(allocationDisputeResolutionAttemptStorageKey)).toBeNull();
    expect(screen.queryByRole('button', { name: 'Record decision' })).not.toBeInTheDocument();
    await expect(submitAllocationDisputeResolution(disputeId, 'upheld', reason))
      .rejects.toBeInstanceOf(AllocationDisputeResolutionNotFreshError);
    expect(posts).toHaveLength(1);
    expect(resolutionBoundaryForTests(epoch(), disputeId).confirmed?.reason).toBe(reason);

    held[0]?.(HttpResponse.json({ title: 'unavailable' }, { status: 500 }));
    expect(await screen.findByText('The decision is stored. The latest read did not finish.')).toBeInTheDocument();
    expect(screen.getByText('Decyzja zapisana.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Record decision' })).not.toBeInTheDocument();
    await expect(submitAllocationDisputeResolution(disputeId, 'not_upheld', 'another'))
      .rejects.toBeInstanceOf(AllocationDisputeResolutionNotFreshError);
    expect(posts).toHaveLength(1);
    expect(sessionStorage.getItem(allocationDisputeResolutionAttemptStorageKey)).toBeNull();
  });

  it('drops the confirmation message on leave and still refuses another command', async () => {
    const user = userEvent.setup();
    let posts = 0;
    const held: Array<(response: Response) => void> = [];
    let reads = 0;
    server.use(
      http.get('/api/allocation-disputes/:disputeId/resolution', () => {
        reads += 1;
        if (reads === 1) {
          return HttpResponse.json(nullDecision());
        }

        return new Promise<Response>((resolve) => {
          held.push(resolve);
        });
      }),
      http.post('/api/allocation-disputes/:disputeId/resolution', () => {
        posts += 1;
        return HttpResponse.json(decision(), { status: 201 });
      }),
    );
    signIn();
    const view = renderPanel(disputeId);
    await record(user);
    expect(await screen.findByText('Decyzja zapisana.')).toBeInTheDocument();
    expect(held).toHaveLength(1);

    view.unmount();
    renderPanel(disputeId);
    expect(screen.queryByText('Decyzja zapisana.')).not.toBeInTheDocument();
    expect(await screen.findByText(`Outcome ${decision().outcome}`)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Record decision' })).not.toBeInTheDocument();
    expect(posts).toBe(1);
    expect(resolutionBoundaryForTests(epoch(), disputeId).confirmed).not.toBeNull();
  });

  it('does not let a read started before the command replace the confirmed decision', async () => {
    const user = userEvent.setup();
    const pendingReads: Array<(response: Response) => void> = [];
    let reads = 0;
    server.use(
      http.get('/api/allocation-disputes/:disputeId/resolution', () => {
        reads += 1;
        if (reads === 1) {
          return HttpResponse.json(nullDecision());
        }

        return new Promise<Response>((resolve) => {
          pendingReads.push(resolve);
        });
      }),
      http.post('/api/allocation-disputes/:disputeId/resolution', () => HttpResponse.json(decision(), { status: 201 })),
    );
    signIn();
    renderPanel(disputeId);
    await screen.findByRole('button', { name: 'Record decision' });
    const startedEpoch = epoch();
    const lateRead = loadAllocationDisputeResolution(disputeId, startedEpoch, new AbortController().signal);
    await waitFor(() => expect(pendingReads).toHaveLength(1));
    await record(user);
    expect(await screen.findByText('Decyzja zapisana.')).toBeInTheDocument();
    pendingReads[0]?.(HttpResponse.json(nullDecision()));
    await expect(lateRead).resolves.toEqual({
      disputeId: decision().disputeId,
      decision: expect.objectContaining({ reason }),
    });
    expect(queryClient.getQueryData(allocationDisputeResolutionQueryKey(startedEpoch, disputeId))).toEqual({
      disputeId: decision().disputeId,
      decision: expect.objectContaining({ outcome: 'upheld', reason }),
    });
    expect(screen.queryByRole('button', { name: 'Record decision' })).not.toBeInTheDocument();
  });

  it('requires a read started after local tracking is removed', async () => {
    const user = userEvent.setup();
    const pending: Array<{ serial: number; resolve: (response: Response) => void }> = [];
    server.use(http.get('/api/allocation-disputes/:disputeId/resolution', () => new Promise<Response>((resolve) => {
      pending.push({ serial: resolutionBoundaryForTests(epoch(), disputeId).serial, resolve });
    })));
    signIn();
    storeAttempt({ lifecycle: 'uncertain', uncertaintyLocked: true });
    renderPanel(disputeId);
    await screen.findByRole('button', { name: 'Remove local tracking' });
    await waitFor(() => expect(pending).toHaveLength(1));
    await user.click(screen.getByRole('button', { name: 'Remove local tracking' }));
    await waitFor(() => expect(pending.some((item) => item.serial > 0)).toBe(true));
    expect(screen.queryByRole('button', { name: 'Record decision' })).not.toBeInTheDocument();

    pending.filter((item) => item.serial === 0).forEach((item) => item.resolve(HttpResponse.json(nullDecision())));
    await Promise.resolve();
    expect(screen.queryByRole('button', { name: 'Record decision' })).not.toBeInTheDocument();
    expect(resolutionBoundaryForTests(epoch(), disputeId).nullObservedSerial)
      .not.toBe(resolutionBoundaryForTests(epoch(), disputeId).serial);

    const currentSerial = resolutionBoundaryForTests(epoch(), disputeId).serial;
    pending.filter((item) => item.serial === currentSerial).forEach((item) => item.resolve(HttpResponse.json(nullDecision())));
    expect(await screen.findByRole('button', { name: 'Record decision' })).toBeInTheDocument();
    expect(resolutionBoundaryForTests(epoch(), disputeId).nullObservedSerial).toBe(currentSerial);
  });

  it('keeps the post-removal boundary across an unmount', async () => {
    const user = userEvent.setup();
    const pending: Array<{ serial: number; resolve: (response: Response) => void }> = [];
    server.use(http.get('/api/allocation-disputes/:disputeId/resolution', () => new Promise<Response>((resolve) => {
      pending.push({ serial: resolutionBoundaryForTests(epoch(), disputeId).serial, resolve });
    })));
    signIn();
    storeAttempt({ lifecycle: 'uncertain', uncertaintyLocked: true });
    const view = renderPanel(disputeId);
    await screen.findByRole('button', { name: 'Remove local tracking' });
    await waitFor(() => expect(pending).toHaveLength(1));
    await user.click(screen.getByRole('button', { name: 'Remove local tracking' }));
    await waitFor(() => expect(resolutionBoundaryForTests(epoch(), disputeId).serial).toBeGreaterThan(0));
    view.unmount();
    pending.filter((item) => item.serial === 0).forEach((item) => item.resolve(HttpResponse.json(nullDecision())));

    renderPanel(disputeId);
    await waitFor(() => expect(pending.some((item) => item.serial > 0)).toBe(true));
    expect(screen.queryByRole('button', { name: 'Record decision' })).not.toBeInTheDocument();
    const currentSerial = resolutionBoundaryForTests(epoch(), disputeId).serial;
    pending.filter((item) => item.serial === currentSerial).forEach((item) => item.resolve(HttpResponse.json(nullDecision())));
    expect(await screen.findByRole('button', { name: 'Record decision' })).toBeInTheDocument();
  });

  it('does not write a new session cache from an older read or command', async () => {
    const first = deferred<Response>();
    server.use(http.get('/api/allocation-disputes/:disputeId/resolution', () => first.promise));
    signIn();
    const startedEpoch = epoch();
    const pending = loadAllocationDisputeResolution(disputeId, startedEpoch, new AbortController().signal);
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    const nextEpoch = epoch();
    first.resolve(HttpResponse.json(nullDecision()));
    await expect(pending).rejects.toBeInstanceOf(StaleSessionError);
    expect(queryClient.getQueryData(allocationDisputeResolutionQueryKey(nextEpoch, disputeId))).toBeUndefined();

    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    const commandEpoch = epoch();
    server.use(
      http.get('/api/allocation-disputes/:disputeId/resolution', () => HttpResponse.json(nullDecision())),
      http.post('/api/allocation-disputes/:disputeId/resolution', () => {
        sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
        return HttpResponse.json(decision(), { status: 201 });
      }),
    );
    renderPanel(disputeId);
    await screen.findByRole('button', { name: 'Record decision' });
    await expect(submitAllocationDisputeResolution(disputeId, 'upheld', reason)).rejects.toThrow();
    const rotatedEpoch = epoch();
    expect(rotatedEpoch).not.toBe(commandEpoch);
    expect(queryClient.getQueryData(allocationDisputeResolutionQueryKey(rotatedEpoch, disputeId))).toBeUndefined();
    expect(resolutionBoundaryForTests(commandEpoch, disputeId).confirmed).toBeNull();
    expect(resolutionBoundaryForTests(rotatedEpoch, disputeId).confirmed).toBeNull();
    const stored = JSON.parse(sessionStorage.getItem(allocationDisputeResolutionAttemptStorageKey) ?? '{}') as {
      lifecycle?: string;
    };
    expect(stored.lifecycle).toBe('uncertain');
  });

  it('rejects the first recognized conflict and keeps replay after an earlier uncertain result', async () => {
    const user = userEvent.setup();
    let mode: 'conflict' | 'uncertain' = 'conflict';
    server.use(
      http.get('/api/allocation-disputes/:disputeId/resolution', () => HttpResponse.json(nullDecision())),
      http.post('/api/allocation-disputes/:disputeId/resolution', () => HttpResponse.json({
        code: 'reconciliation.allocation_dispute.resolution.already_resolved',
      }, { status: mode === 'conflict' ? 409 : 500 })),
    );
    signIn();
    const first = renderPanel(disputeId);
    await record(user);
    expect(await screen.findAllByText('This decision was refused. Sending it again is not available.')).not.toHaveLength(0);
    expect(screen.queryByRole('button', { name: 'Send the same decision again' })).not.toBeInTheDocument();
    const stored = JSON.parse(sessionStorage.getItem(allocationDisputeResolutionAttemptStorageKey) ?? '{}') as {
      lifecycle?: string;
      uncertaintyLocked?: boolean;
    };
    expect(stored.lifecycle).toBe('rejected');
    expect(stored.uncertaintyLocked).toBe(false);
    first.unmount();

    mode = 'uncertain';
    sessionStorage.clear();
    resetAllocationDisputeResolutionAttemptStateForTests();
    queryClient.clear();
    renderPanel(disputeId);
    await record(user);
    expect(await screen.findByRole('button', { name: 'Send the same decision again' })).toBeInTheDocument();
    mode = 'conflict';
    await user.click(screen.getByRole('button', { name: 'Send the same decision again' }));
    expect(await screen.findByText('This response does not show that the earlier decision command finished.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Send the same decision again' })).toBeInTheDocument();
    const replayed = JSON.parse(sessionStorage.getItem(allocationDisputeResolutionAttemptStorageKey) ?? '{}') as {
      lifecycle?: string;
      uncertaintyLocked?: boolean;
    };
    expect(replayed.lifecycle).toBe('uncertain');
    expect(replayed.uncertaintyLocked).toBe(true);
  });

  it('treats HTTP 500 as unresolved even when its body contains a conflict code', async () => {
    const user = userEvent.setup();
    server.use(
      http.get('/api/allocation-disputes/:disputeId/resolution', () => HttpResponse.json(nullDecision())),
      http.post('/api/allocation-disputes/:disputeId/resolution', () => HttpResponse.json({
        code: 'reconciliation.allocation_dispute.resolution.already_resolved',
      }, { status: 500 })),
    );
    signIn();
    renderPanel(disputeId);
    await record(user);
    expect(await screen.findByRole('button', { name: 'Send the same decision again' })).toBeInTheDocument();
    const stored = JSON.parse(sessionStorage.getItem(allocationDisputeResolutionAttemptStorageKey) ?? '{}') as {
      lifecycle?: string;
    };
    expect(stored.lifecycle).toBe('uncertain');
    expect(screen.queryByText('This decision was refused. Sending it again is not available.')).not.toBeInTheDocument();
  });

  it('keeps the attempt when HTTP 201 does not match the stored command', async () => {
    const user = userEvent.setup();
    server.use(
      http.get('/api/allocation-disputes/:disputeId/resolution', () => HttpResponse.json(nullDecision())),
      http.post('/api/allocation-disputes/:disputeId/resolution', () => HttpResponse.json({ outcome: 'upheld' }, { status: 201 })),
    );
    signIn();
    renderPanel(disputeId);
    await record(user);
    expect(await screen.findByRole('button', { name: 'Send the same decision again' })).toBeInTheDocument();
    expect(screen.queryByText('Decyzja zapisana.')).not.toBeInTheDocument();
    expect(resolutionBoundaryForTests(epoch(), disputeId).confirmed).toBeNull();
    expect(sessionStorage.getItem(allocationDisputeResolutionAttemptStorageKey)).not.toBeNull();
  });

  it('sends nothing when the descriptor cannot be stored', async () => {
    const user = userEvent.setup();
    let posts = 0;
    server.use(
      http.get('/api/allocation-disputes/:disputeId/resolution', () => HttpResponse.json(nullDecision())),
      http.post('/api/allocation-disputes/:disputeId/resolution', () => {
        posts += 1;
        return HttpResponse.json(decision(), { status: 201 });
      }),
    );
    signIn();
    const view = renderPanel(disputeId);
    await screen.findByRole('button', { name: 'Record decision' });
    const setItem = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('storage unavailable');
    });
    await record(user);
    expect(await screen.findByText('The decision was not sent because it could not be stored first.')).toBeInTheDocument();
    expect(posts).toBe(0);
    setItem.mockRestore();
    view.unmount();

    storeAttempt({ lifecycle: 'uncertain', uncertaintyLocked: true });
    renderPanel(disputeId);
    const before = sessionStorage.getItem(allocationDisputeResolutionAttemptStorageKey);
    const replaySet = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('storage unavailable');
    });
    await user.click(await screen.findByRole('button', { name: 'Send the same decision again' }));
    expect(await screen.findByText('The same decision was not sent. Local tracking was left unchanged.')).toBeInTheDocument();
    expect(posts).toBe(0);
    expect(sessionStorage.getItem(allocationDisputeResolutionAttemptStorageKey)).toBe(before);
    replaySet.mockRestore();
  });

  it('keeps a newer descriptor when an older response arrives', async () => {
    const user = userEvent.setup();
    const replacement = storedAttempt({
      idempotencyKey: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
      normalizedReason: 'later reason',
      requestBody: JSON.stringify({ outcome: 'upheld', reason: 'later reason' }),
    });
    server.use(
      http.get('/api/allocation-disputes/:disputeId/resolution', () => HttpResponse.json(nullDecision())),
      http.post('/api/allocation-disputes/:disputeId/resolution', () => {
        sessionStorage.setItem(allocationDisputeResolutionAttemptStorageKey, JSON.stringify(replacement));
        return HttpResponse.json(decision(), { status: 201 });
      }),
    );
    signIn();
    renderPanel(disputeId);
    await record(user);
    expect(await screen.findByText('The stored decision command could not be used.')).toBeInTheDocument();
    expect(sessionStorage.getItem(allocationDisputeResolutionAttemptStorageKey)).toBe(JSON.stringify(replacement));
    expect(resolutionBoundaryForTests(epoch(), disputeId).confirmed).toBeNull();
  });

  it('keeps the confirmation when local deletion fails and retries only that deletion', async () => {
    const user = userEvent.setup();
    let posts = 0;
    let reads = 0;
    server.use(
      http.get('/api/allocation-disputes/:disputeId/resolution', () => {
        reads += 1;
        return HttpResponse.json(reads === 1 ? nullDecision() : { disputeId, decision: decision() });
      }),
      http.post('/api/allocation-disputes/:disputeId/resolution', () => {
        posts += 1;
        return HttpResponse.json(decision(), { status: 201 });
      }),
    );
    signIn();
    renderPanel(disputeId);
    await screen.findByText('No decision is stored.');
    const removeItem = vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => {
      throw new Error('storage locked');
    });
    await record(user);
    expect(await screen.findByText('Decyzja zapisana.')).toBeInTheDocument();
    expect(posts).toBe(1);
    expect(screen.queryByRole('button', { name: 'Send the same decision again' })).not.toBeInTheDocument();
    expect(screen.queryByText('This decision has no verified result. Sending it again uses the same request.')).not.toBeInTheDocument();
    expect(sessionStorage.getItem(allocationDisputeResolutionAttemptStorageKey)).not.toBeNull();
    removeItem.mockRestore();
    await user.click(screen.getByRole('button', { name: 'Try removing local tracking again' }));
    await waitFor(() => expect(sessionStorage.getItem(allocationDisputeResolutionAttemptStorageKey)).toBeNull());
    expect(posts).toBe(1);
    expect(screen.getByText('Decyzja zapisana.')).toBeInTheDocument();
  });

  it('does not treat a similar decision read as confirmation of the stored command', async () => {
    server.use(http.get('/api/allocation-disputes/:disputeId/resolution', () => HttpResponse.json({
      disputeId,
      decision: decision({ actorId: 'original-operator' }),
    })));
    signIn();
    storeAttempt({ lifecycle: 'uncertain', uncertaintyLocked: true });
    renderPanel(disputeId);
    expect(await screen.findByText('original-operator')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Send the same decision again' })).toBeInTheDocument();
    expect(sessionStorage.getItem(allocationDisputeResolutionAttemptStorageKey)).not.toBeNull();
    expect(resolutionBoundaryForTests(epoch(), disputeId).confirmed).toBeNull();
  });

  it('shows an already stored decision without replacing its actor', async () => {
    const user = userEvent.setup();
    const posts: Array<{ key: string | null; body: string }> = [];
    server.use(
      http.get('/api/allocation-disputes/:disputeId/resolution', () => HttpResponse.json(nullDecision())),
      http.post('/api/allocation-disputes/:disputeId/resolution', async ({ request }) => {
        const key = request.headers.get('Idempotency-Key');
        const body = await request.text();
        posts.push({ key, body });
        return HttpResponse.json(decision({
          actorId: 'original-operator',
          decidedAt: '2026-11-07T08:00:00Z',
        }), { status: posts.length === 1 ? 500 : 200 });
      }),
    );
    signIn();
    renderPanel(disputeId);
    await record(user);
    await user.click(await screen.findByRole('button', { name: 'Send the same decision again' }));
    expect(await screen.findByText('Ta decyzja była już zapisana.')).toBeInTheDocument();
    expect(screen.getByText('original-operator 2026-11-07T08:00:00Z')).toBeInTheDocument();
    expect(posts).toHaveLength(2);
    expect(posts[1]).toEqual(posts[0]);
  });

  it('hides another operator’s stored command and reads without a subject', async () => {
    server.use(http.get('/api/allocation-disputes/:disputeId/resolution', () => HttpResponse.json({
      disputeId,
      decision: decision(),
    })));
    signIn();
    storeAttempt({ lifecycle: 'uncertain', uncertaintyLocked: true });
    const first = renderPanel(disputeId);
    expect(await screen.findByText(`Dispute ${disputeId}`)).toBeInTheDocument();
    first.unmount();
    sessionStore.rotate(tokenFor('operator-2'), 4_000_000_000, null);
    renderPanel(disputeId);
    expect(await screen.findByText('A stored decision command belongs to another operator. Its details stay hidden.')).toBeInTheDocument();
    expect(screen.queryByText(`Dispute ${disputeId}`)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Send the same decision again' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Remove local tracking' })).not.toBeInTheDocument();

    sessionStore.rotate(tokenWithoutSubject(), 4_000_000_000, null);
    renderPanel(disputeId);
    expect(await screen.findByText(`Outcome ${decision().outcome}`)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Record decision' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Send the same decision again' })).not.toBeInTheDocument();
  });

  it('keeps the registration visible when the decision read fails, and the decision when registration fails', async () => {
    server.use(
      http.get('/api/allocation-disputes', () => HttpResponse.json({
        items: [registration()],
        page: 1,
        pageSize: 50,
        totalCount: 1,
      })),
      http.get('/api/allocation-disputes/:disputeId', () => HttpResponse.json(registration())),
      http.get('/api/allocation-disputes/:disputeId/resolution', () => HttpResponse.json({
        title: 'Not found',
      }, { status: 404 })),
    );
    signIn();
    const { unmount } = render(withClient(
      <MemoryRouter initialEntries={[`/allocation-disputes?selected=${disputeId}`]}>
        <AllocationDisputePage />
      </MemoryRouter>,
    ));
    expect(await screen.findAllByText('counterparty challenge')).not.toHaveLength(0);
    expect(await screen.findByText('Ten spór nie został znaleziony.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Record decision' })).not.toBeInTheDocument();
    expect(screen.queryByText('No allocation disputes are stored for this organization.')).not.toBeInTheDocument();
    unmount();

    server.use(
      http.get('/api/allocation-disputes/:disputeId', ({ request }) => (
        new URL(request.url).pathname.endsWith('/resolution')
          ? HttpResponse.json(nullDecision())
          : HttpResponse.json({ title: 'broken' }, { status: 500 })
      )),
      http.get('/api/allocation-disputes/:disputeId/resolution', () => HttpResponse.json(nullDecision())),
    );
    render(withClient(
      <MemoryRouter initialEntries={[`/allocation-disputes?selected=${disputeId}`]}>
        <AllocationDisputePage />
      </MemoryRouter>,
    ));
    expect(await screen.findByText('Allocation disputes could not be loaded.')).toBeInTheDocument();
    expect(await screen.findByText('No decision is stored.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Record decision' })).toBeInTheDocument();
  });

  it('forgets a decision message when the selection returns to the same dispute', async () => {
    const user = userEvent.setup();
    server.use(
      http.get('/api/allocation-disputes/:disputeId/resolution', () => HttpResponse.json(nullDecision())),
      http.post('/api/allocation-disputes/:disputeId/resolution', () => HttpResponse.json(decision(), { status: 201 })),
    );
    signIn();
    const view = renderPanel(disputeId);
    await record(user);
    expect(await screen.findByText('Decyzja zapisana.')).toBeInTheDocument();
    view.rerender(panel(otherDisputeId));
    expect(screen.queryByText('Decyzja zapisana.')).not.toBeInTheDocument();
    view.rerender(panel(disputeId));
    expect(screen.queryByText('Decyzja zapisana.')).not.toBeInTheDocument();
    expect(await screen.findByText(`Outcome ${decision().outcome}`)).toBeInTheDocument();
  });

  it('keeps a verified first post closed when descriptor removal fails', async () => {
    const user = userEvent.setup();
    let posts = 0;
    server.use(
      http.get('/api/allocation-disputes/:disputeId/resolution', ({ params }) => HttpResponse.json({
        disputeId: String(params.disputeId),
        decision: null,
      })),
      http.post('/api/allocation-disputes/:disputeId/resolution', () => {
        posts += 1;
        return HttpResponse.json(decision(), { status: 201 });
      }),
    );
    signIn();
    const view = renderPanel(disputeId);
    await screen.findByRole('button', { name: 'Record decision' });
    const removeItem = vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => {
      throw new Error('storage locked');
    });
    try {
    await record(user);
    expect(await screen.findByText('Decyzja zapisana.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Send the same decision again' })).not.toBeInTheDocument();
    expect(screen.queryByText('This decision has no verified result. Sending it again uses the same request.')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Try removing local tracking again' })).toBeInTheDocument();

    view.rerender(panel(otherDisputeId));
    expect(screen.queryByRole('button', { name: 'Send the same decision again' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Try removing local tracking again' })).toBeInTheDocument();
    view.unmount();
    renderPanel(disputeId);
    expect(screen.queryByRole('button', { name: 'Send the same decision again' })).not.toBeInTheDocument();
    await expect(replayAllocationDisputeResolutionAttempt()).rejects.toBeInstanceOf(AllocationDisputeResolutionVerifiedTrackingError);
    expect(posts).toBe(1);

    const replacement = storedAttempt({
      idempotencyKey: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
      normalizedReason: 'later reason',
      requestBody: JSON.stringify({ outcome: 'upheld', reason: 'later reason' }),
    });
    sessionStorage.setItem(allocationDisputeResolutionAttemptStorageKey, JSON.stringify(replacement));
    expect(() => retryAllocationDisputeResolutionDescriptorRemoval()).toThrow();
    expect(sessionStorage.getItem(allocationDisputeResolutionAttemptStorageKey)).toBe(JSON.stringify(replacement));
    expect(posts).toBe(1);
    } finally {
      removeItem.mockRestore();
    }
  });

  it('keeps a verified replay closed when descriptor removal fails', async () => {
    const user = userEvent.setup();
    const posts: Array<{ key: string | null; body: string }> = [];
    server.use(
      http.get('/api/allocation-disputes/:disputeId/resolution', () => HttpResponse.json(nullDecision())),
      http.post('/api/allocation-disputes/:disputeId/resolution', async ({ request }) => {
        const key = request.headers.get('Idempotency-Key');
        const body = await request.text();
        posts.push({ key, body });
        return HttpResponse.json(decision(), { status: posts.length === 1 ? 500 : 200 });
      }),
    );
    signIn();
    const view = renderPanel(disputeId);
    await record(user);
    expect(await screen.findByRole('button', { name: 'Send the same decision again' })).toBeInTheDocument();
    expect(posts).toHaveLength(1);
    const removeItem = vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => {
      throw new Error('storage locked');
    });
    try {
    await user.click(screen.getByRole('button', { name: 'Send the same decision again' }));
    expect(await screen.findByText('Ta decyzja była już zapisana.')).toBeInTheDocument();
    expect(screen.queryByText('This decision has no verified result. Sending it again uses the same request.')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Try removing local tracking again' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Send the same decision again' })).not.toBeInTheDocument();
    view.unmount();
    renderPanel(disputeId);
    expect(screen.queryByRole('button', { name: 'Send the same decision again' })).not.toBeInTheDocument();
    await expect(replayAllocationDisputeResolutionAttempt()).rejects.toBeInstanceOf(AllocationDisputeResolutionVerifiedTrackingError);
    expect(posts).toHaveLength(2);
    expect(posts[1]).toEqual(posts[0]);
    } finally {
      removeItem.mockRestore();
    }
  });

  it('replays the original command after the in-memory confirmation is gone', async () => {
    const user = userEvent.setup();
    const posts: Array<{ key: string | null; body: string }> = [];
    server.use(
      http.get('/api/allocation-disputes/:disputeId/resolution', () => HttpResponse.json(nullDecision())),
      http.post('/api/allocation-disputes/:disputeId/resolution', async ({ request }) => {
        posts.push({
          key: request.headers.get('Idempotency-Key'),
          body: await request.text(),
        });
        return HttpResponse.json(decision(), { status: posts.length === 1 ? 500 : 200 });
      }),
    );
    signIn();
    const view = renderPanel(disputeId);
    await record(user);
    expect(await screen.findByRole('button', { name: 'Send the same decision again' })).toBeInTheDocument();
    view.unmount();
    resetAllocationDisputeResolutionAttemptStateForTests();
    renderPanel(disputeId);
    await user.click(await screen.findByRole('button', { name: 'Send the same decision again' }));
    expect(await screen.findByText('Ta decyzja była już zapisana.')).toBeInTheDocument();
    expect(posts).toHaveLength(2);
    expect(posts[1]).toEqual(posts[0]);
  });

  it('shows Update access for the first HTTP 401 and keeps the descriptor', async () => {
    const user = userEvent.setup();
    let posts = 0;
    server.use(
      http.get('/api/allocation-disputes/:disputeId/resolution', () => HttpResponse.json(nullDecision())),
      http.post('/api/allocation-disputes/:disputeId/resolution', () => {
        posts += 1;
        return HttpResponse.json({ title: 'Unauthorized' }, { status: 401 });
      }),
    );
    signIn();
    const token = sessionStore.getSnapshot().token;
    renderPanel(disputeId);
    await record(user);
    expect(await screen.findByText('Your access has expired or is no longer valid. Update it to continue.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Update access' })).toBeInTheDocument();
    expect(sessionStore.getSnapshot().token).toBe(token);
    const stored = JSON.parse(sessionStorage.getItem(allocationDisputeResolutionAttemptStorageKey) ?? '{}') as {
      lifecycle?: string;
      uncertaintyLocked?: boolean;
    };
    expect(stored.lifecycle).toBe('rejected');
    expect(stored.uncertaintyLocked).toBe(false);
    expect(posts).toBe(1);
    expect(screen.queryByRole('button', { name: 'Send the same decision again' })).not.toBeInTheDocument();
  });

  it('shows Update access for HTTP 401 after an uncertain decision and keeps the lock', async () => {
    const user = userEvent.setup();
    let posts = 0;
    server.use(
      http.get('/api/allocation-disputes/:disputeId/resolution', () => HttpResponse.json(nullDecision())),
      http.post('/api/allocation-disputes/:disputeId/resolution', () => {
        posts += 1;
        return HttpResponse.json({ title: 'Unauthorized' }, { status: posts === 1 ? 500 : 401 });
      }),
    );
    signIn();
    const token = sessionStore.getSnapshot().token;
    renderPanel(disputeId);
    await record(user);
    await user.click(await screen.findByRole('button', { name: 'Send the same decision again' }));
    expect(await screen.findByRole('button', { name: 'Update access' })).toBeInTheDocument();
    expect(screen.getByText('Your access has expired or is no longer valid. Update it to continue.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Send the same decision again' })).toBeInTheDocument();
    expect(sessionStore.getSnapshot().token).toBe(token);
    const stored = JSON.parse(sessionStorage.getItem(allocationDisputeResolutionAttemptStorageKey) ?? '{}') as {
      lifecycle?: string;
      uncertaintyLocked?: boolean;
    };
    expect(stored.lifecycle).toBe('uncertain');
    expect(stored.uncertaintyLocked).toBe(true);
    expect(posts).toBe(2);
  });

  it.each([
    {
      label: 'HTTP 401',
      failure: () => HttpResponse.json({ title: 'Unauthorized' }, { status: 401 }),
      message: 'Your access has expired or is no longer valid. Update it to continue.',
      updateAccess: true,
      retry: false,
    },
    {
      label: 'HTTP 403',
      failure: () => HttpResponse.json({ title: 'Forbidden' }, { status: 403 }),
      message: 'You don’t have access to this allocation dispute decision.',
      updateAccess: false,
      retry: false,
    },
    {
      label: 'HTTP 404',
      failure: () => HttpResponse.json({ title: 'Not found' }, { status: 404 }),
      message: 'Ten spór nie został znaleziony.',
      updateAccess: false,
      retry: false,
    },
    {
      label: 'HTTP 500',
      failure: () => HttpResponse.json({ title: 'Unavailable' }, { status: 500 }),
      message: 'The allocation dispute decision could not be loaded.',
      updateAccess: false,
      retry: true,
    },
    {
      label: 'a defective HTTP 200',
      failure: () => HttpResponse.json({ disputeId, decision: { disputeId } }),
      message: 'We couldn’t read this allocation dispute decision.',
      updateAccess: false,
      retry: true,
    },
  ])('keeps the recorded decision locked when the following read fails with $label', async (variant) => {
    const user = userEvent.setup();
    let posts = 0;
    let reads = 0;
    server.use(
      http.get('/api/allocation-disputes/:disputeId/resolution', () => {
        reads += 1;
        return reads === 1 ? HttpResponse.json(nullDecision()) : variant.failure();
      }),
      http.post('/api/allocation-disputes/:disputeId/resolution', () => {
        posts += 1;
        return HttpResponse.json(decision(), { status: 201 });
      }),
    );
    signIn();
    const token = sessionStore.getSnapshot().token;
    renderPanel(disputeId);
    await record(user);
    expect(await screen.findByText('Decyzja zapisana.')).toBeInTheDocument();
    expect(await screen.findByText(variant.message)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Record decision' })).not.toBeInTheDocument();
    expect(screen.queryByText('No decision is stored.')).not.toBeInTheDocument();
    expect(screen.queryByText(`Outcome ${decision().outcome}`)).not.toBeInTheDocument();
    expect(posts).toBe(1);
    expect(sessionStore.getSnapshot().token).toBe(token);
    if (variant.updateAccess) {
      expect(screen.getByRole('button', { name: 'Update access' })).toBeInTheDocument();
    } else {
      expect(screen.queryByRole('button', { name: 'Update access' })).not.toBeInTheDocument();
    }

    if (variant.retry) {
      const readsBeforeRetry = reads;
      await user.click(screen.getByRole('button', { name: 'Try again' }));
      await waitFor(() => expect(reads).toBeGreaterThan(readsBeforeRetry));
      expect(posts).toBe(1);
      expect(screen.getByText('Decyzja zapisana.')).toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'Record decision' })).not.toBeInTheDocument();
    }
  });

  it('retries the verified dispute when another dispute is selected', async () => {
    const user = userEvent.setup();
    const gets: string[] = [];
    let posts = 0;
    server.use(
      http.get('/api/allocation-disputes/:disputeId/resolution', ({ params }) => {
        const id = String(params.disputeId);
        gets.push(id);
        return id === disputeId
          ? HttpResponse.json({ title: 'Unavailable' }, { status: 500 })
          : HttpResponse.json({ disputeId: id, decision: null });
      }),
      http.post('/api/allocation-disputes/:disputeId/resolution', () => {
        posts += 1;
        return HttpResponse.json(decision(), { status: 200 });
      }),
    );
    signIn();
    storeAttempt({ lifecycle: 'uncertain', uncertaintyLocked: true });
    renderPanel(otherDisputeId);
    await screen.findByRole('button', { name: 'Send the same decision again' });
    await user.click(screen.getByRole('button', { name: 'Send the same decision again' }));
    expect(await screen.findByText('The decision is stored. The latest read did not finish.')).toBeInTheDocument();
    expect(posts).toBe(1);
    const otherBeforeRefresh = gets.filter((id) => id === otherDisputeId).length;
    const targetBeforeRefresh = gets.filter((id) => id === disputeId).length;
    await queryClient.refetchQueries({
      queryKey: allocationDisputeResolutionQueryKey(epoch(), otherDisputeId),
    });
    await waitFor(() => expect(gets.filter((id) => id === otherDisputeId).length).toBe(otherBeforeRefresh + 1));
    expect(gets.filter((id) => id === disputeId).length).toBe(targetBeforeRefresh);
    expect(screen.getByText('The decision is stored. The latest read did not finish.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Read the decision again' }));
    await waitFor(() => expect(gets.filter((id) => id === disputeId).length).toBe(targetBeforeRefresh + 1));
    expect(gets.filter((id) => id === otherDisputeId).length).toBe(otherBeforeRefresh + 1);
    expect(posts).toBe(1);
    expect(screen.getByText('The decision is stored. The latest read did not finish.')).toBeInTheDocument();
  });

  it('retries the verified dispute when no dispute is selected', async () => {
    const user = userEvent.setup();
    const gets: string[] = [];
    let posts = 0;
    server.use(
      http.get('/api/allocation-disputes/:disputeId/resolution', ({ params }) => {
        gets.push(String(params.disputeId));
        return HttpResponse.json({ title: 'Unavailable' }, { status: 500 });
      }),
      http.post('/api/allocation-disputes/:disputeId/resolution', () => {
        posts += 1;
        return HttpResponse.json(decision(), { status: 200 });
      }),
    );
    signIn();
    storeAttempt({ lifecycle: 'uncertain', uncertaintyLocked: true });
    render(withClient(<AllocationDisputeResolutionPanel epoch={epoch()} disputeId={null} />));
    await user.click(await screen.findByRole('button', { name: 'Send the same decision again' }));
    expect(await screen.findByText('The decision is stored. The latest read did not finish.')).toBeInTheDocument();
    expect(gets).toEqual([disputeId]);
    expect(posts).toBe(1);
    await user.click(screen.getByRole('button', { name: 'Read the decision again' }));
    await waitFor(() => expect(gets).toEqual([disputeId, disputeId]));
    expect(posts).toBe(1);
    expect(screen.getByText('The decision is stored. The latest read did not finish.')).toBeInTheDocument();
  });

  it('keeps the newer decision warning when an older refresh fails afterwards', async () => {
    const user = userEvent.setup();
    const traffic = trackResolutionTraffic(true);
    signIn();
    storeAttempt({ lifecycle: 'uncertain', uncertaintyLocked: true });
    renderPanel(otherDisputeId);
    await user.click(await screen.findByRole('button', { name: 'Send the same decision again' }));
    await waitFor(() => expect(traffic.heldA).toHaveLength(1));
    await record(user);
    expect(await screen.findByText('Decyzja zapisana.')).toBeInTheDocument();
    expect(screen.getByText('The decision is stored. The latest read did not finish.')).toBeInTheDocument();
    expect(traffic.posts).toEqual([disputeId, otherDisputeId]);
    traffic.heldA[0]?.resolve(HttpResponse.json({ title: 'Unavailable' }, { status: 500 }));
    await assertRetryTargets(user, traffic, otherDisputeId);
  });

  it('keeps the newer decision warning when an older refresh succeeds afterwards', async () => {
    const user = userEvent.setup();
    const traffic = trackResolutionTraffic(true);
    signIn();
    storeAttempt({ lifecycle: 'uncertain', uncertaintyLocked: true });
    renderPanel(otherDisputeId);
    await user.click(await screen.findByRole('button', { name: 'Send the same decision again' }));
    await waitFor(() => expect(traffic.heldA).toHaveLength(1));
    await record(user);
    expect(await screen.findByText('The decision is stored. The latest read did not finish.')).toBeInTheDocument();
    traffic.heldA[0]?.resolve(HttpResponse.json({ disputeId, decision: decision() }));
    await assertRetryTargets(user, traffic, otherDisputeId);
  });

  it.each([
    ['error', () => HttpResponse.json({ title: 'Unavailable' }, { status: 500 })],
    ['success', () => HttpResponse.json({ disputeId, decision: decision() })],
  ])('does not let a late manual %s replace a newer refresh warning', async (_label, lateResponse) => {
    const user = userEvent.setup();
    const traffic = trackResolutionTraffic(false);
    signIn();
    storeAttempt({ lifecycle: 'uncertain', uncertaintyLocked: true });
    renderPanel(otherDisputeId);
    await user.click(await screen.findByRole('button', { name: 'Send the same decision again' }));
    expect(await screen.findByText('The decision is stored. The latest read did not finish.')).toBeInTheDocument();
    traffic.holdFurtherA = true;
    await user.click(screen.getByRole('button', { name: 'Read the decision again' }));
    await waitFor(() => expect(traffic.heldA).toHaveLength(1));
    await record(user);
    expect(await screen.findByText('Decyzja zapisana.')).toBeInTheDocument();
    traffic.heldA[0]?.resolve(lateResponse());
    await assertRetryTargets(user, traffic, otherDisputeId);
  });
});

function trackResolutionTraffic(holdA: boolean) {
  const gets: string[] = [];
  const posts: string[] = [];
  const heldA: Array<ReturnType<typeof deferred<Response>>> = [];
  const traffic = { gets, posts, heldA, holdFurtherA: holdA };
  server.use(
    http.get('/api/allocation-disputes/:disputeId/resolution', ({ params }) => {
      const id = String(params.disputeId);
      gets.push(id);
      if (id === disputeId && traffic.holdFurtherA) {
        const gate = deferred<Response>();
        heldA.push(gate);
        return gate.promise;
      }

      if (id === disputeId) {
        return HttpResponse.json({ title: 'Unavailable' }, { status: 500 });
      }

      return gets.filter((item) => item === id).length === 1
        ? HttpResponse.json({ disputeId: id, decision: null })
        : HttpResponse.json({ title: 'Unavailable' }, { status: 500 });
    }),
    http.post('/api/allocation-disputes/:disputeId/resolution', ({ params }) => {
      const id = String(params.disputeId);
      posts.push(id);
      return id === disputeId
        ? HttpResponse.json(decision(), { status: 200 })
        : HttpResponse.json(decision({ disputeId: id }), { status: 201 });
    }),
  );
  return traffic;
}

async function assertRetryTargets(
  user: ReturnType<typeof userEvent.setup>,
  traffic: { gets: string[]; posts: string[] },
  expectedId: string,
): Promise<void> {
  await screen.findByRole('button', { name: 'Read the decision again' });
  const before = traffic.gets.filter((id) => id === expectedId).length;
  const otherBefore = traffic.gets.filter((id) => id !== expectedId).length;
  await user.click(screen.getByRole('button', { name: 'Read the decision again' }));
  await waitFor(() => expect(traffic.gets.filter((id) => id === expectedId).length).toBe(before + 1));
  expect(traffic.gets.filter((id) => id !== expectedId).length).toBe(otherBefore);
  expect(traffic.posts).toEqual([disputeId, otherDisputeId]);
  expect(sessionStorage.getItem(allocationDisputeResolutionAttemptStorageKey)).toBeNull();
  expect(screen.getByText('The decision is stored. The latest read did not finish.')).toBeInTheDocument();
}

function signIn(): void {
  sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
}

function epoch(): number {
  return sessionStore.getSnapshot().epoch;
}

function panel(selectedId: string): ReactElement {
  return withClient(<AllocationDisputeResolutionPanel epoch={epoch()} disputeId={selectedId} />);
}

function renderPanel(selectedId: string) {
  return render(panel(selectedId));
}

function withClient(child: ReactElement): ReactElement {
  return <QueryClientProvider client={queryClient}>{child}</QueryClientProvider>;
}

async function record(user: ReturnType<typeof userEvent.setup>): Promise<void> {
  await user.click(await screen.findByRole('radio', { name: 'Upheld' }));
  await user.type(screen.getByLabelText('Reason'), reason);
  await user.click(screen.getByRole('button', { name: 'Record decision' }));
}

function nullDecision(): { disputeId: string; decision: null } {
  return { disputeId, decision: null };
}

function decision(overrides: Partial<AllocationDisputeResolutionDecision> = {}): AllocationDisputeResolutionDecision {
  return {
    disputeId,
    outcome: 'upheld',
    reason,
    actorSource: 'developmentJwt',
    actorId: 'operator-1',
    decidedAt: '2026-11-07T10:00:00Z',
    ...overrides,
  };
}

function registration(): Record<string, unknown> {
  return {
    disputeId,
    allocationId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
    statement: 'counterparty challenge',
    actorSource: 'developmentJwt',
    actorId: 'e2e-operator',
    recordedAt: '2026-11-07T09:00:00Z',
  };
}

function storeAttempt(overrides: Record<string, unknown>): void {
  sessionStorage.setItem(allocationDisputeResolutionAttemptStorageKey, JSON.stringify(storedAttempt(overrides)));
}

function storedAttempt(overrides: Record<string, unknown>): Record<string, unknown> {
  const outcome = 'upheld';
  const normalizedReason = typeof overrides.normalizedReason === 'string' ? overrides.normalizedReason : reason;
  return {
    schemaVersion: 1,
    operation: 'AllocationDisputeResolution',
    organizationId,
    actorSource: 'developmentJwt',
    actorId: 'operator-1',
    disputeId,
    idempotencyKey: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
    requestBody: JSON.stringify({ outcome, reason: normalizedReason }),
    outcome,
    normalizedReason,
    lifecycle: 'ready',
    uncertaintyLocked: false,
    ...overrides,
  };
}

function tokenFor(subject: string): string {
  return unsignedToken({
    exp: Math.floor(Date.now() / 1000) + 3600,
    sub: subject,
    org_id: organizationId,
  });
}

function tokenWithoutSubject(): string {
  return unsignedToken({
    exp: Math.floor(Date.now() / 1000) + 3600,
    org_id: organizationId,
  });
}

function unsignedToken(payload: object): string {
  const encode = (value: object) => btoa(JSON.stringify(value)).replace(/=+$/g, '').replace(/\+/g, '-').replace(/\//g, '_');
  return `${encode({ alg: 'HS256', typ: 'JWT' })}.${encode(payload)}.signature`;
}

function deferred<T>(): { promise: Promise<T>; resolve: (value: T) => void } {
  let resolve: (value: T) => void = () => undefined;
  const promise = new Promise<T>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}
