import { QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { queryClient } from '../../shared/api/query-client';
import { sessionStore } from '../../shared/api/session-store';
import { server } from '../../test/server';
import { fakeJwt } from '../../test/jwt';
import { AllocationDisputeCorrectionPanel } from './AllocationDisputeCorrectionPanel';
import {
  allocationDisputeDetailQueryKey,
  allocationDisputeResolutionQueryKey,
  getAllocationDispute,
} from './allocation-dispute-api';
import {
  abandonAllocationDisputeCorrectionAttempt,
  allocationDisputeCorrectionAttemptStorageKey,
  allocationDisputeCorrectionQueryKey,
  AllocationDisputeCorrectionCommandNotSentError,
  AllocationDisputeCorrectionNotFreshError,
  AllocationDisputeCorrectionRejectedError,
  AllocationDisputeCorrectionSubmissionInProgressError,
  AllocationDisputeCorrectionUncertaintyRemainsError,
  AllocationDisputeCorrectionVerifiedTrackingError,
  bindCorrectionCommandView,
  correctionAttemptAllowed,
  loadContextObservation,
  loadCorrectionObservation,
  loadDecisionObservation,
  replayAllocationDisputeCorrectionAttempt,
  resetAllocationDisputeCorrectionAttemptStateForTests,
  retryAllocationDisputeCorrectionDescriptorRemoval,
  reversalContextQueryKey,
  submitAllocationDisputeCorrection,
  UnknownAllocationDisputeCorrectionOutcomeError,
  UnreadableAllocationDisputeCorrectionError,
} from './allocation-dispute-correction-attempt';

const organizationId = '11111111-1111-1111-1111-111111111111';
const disputeId = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc';
const otherDisputeId = 'dddddddd-dddd-4ddd-8ddd-dddddddddddd';
const allocationId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
const invoiceId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
const bankTransactionId = 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee';
const reason = 'settlement correction';

afterEach(() => {
  vi.restoreAllMocks();
  sessionStorage.clear();
  resetAllocationDisputeCorrectionAttemptStateForTests();
  queryClient.clear();
  sessionStore.rotate(null, null, null);
});

describe('allocation dispute correction attempt', () => {
  it('shows the full confirmed amount before sending', async () => {
    signIn();
    armReads();
    render(
      <QueryClientProvider client={queryClient}>
        <AllocationDisputeCorrectionPanel epoch={sessionStore.getSnapshot().epoch} disputeId={disputeId} />
      </QueryClientProvider>,
    );

    expect(await screen.findByText(`Full reversal of 10.25 PLN for allocation ${allocationId}.`)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Reverse the full allocation' })).toBeEnabled();
  });

  it('stores the key and raw body before POST and replays that same command', async () => {
    signIn();
    armReads();
    await prime(disputeId);
    const keys: string[] = [];
    const bodies: string[] = [];
    server.use(http.post(`/api/allocation-disputes/${disputeId}/correction`, async ({ request }) => {
      const stored = readStored();
      keys.push(request.headers.get('Idempotency-Key') ?? '');
      bodies.push(await request.text());
      expect(stored.lifecycle).toBe('sending');
      expect(stored.requestBody).toBe(bodies.at(-1));
      return HttpResponse.json(correction(disputeId, false));
    }));

    const verified = await submitAllocationDisputeCorrection(disputeId, `  ${reason}  `);
    expect(verified.cleanup).toBe('removed');
    expect(verified.amount).toBe('10.25');
    expect(verified.currency).toBe('PLN');
    expect(sessionStorage.getItem(allocationDisputeCorrectionAttemptStorageKey)).toBeNull();

    sessionStorage.setItem(allocationDisputeCorrectionAttemptStorageKey, JSON.stringify({
      ...verifiedStored(keys[0]!, bodies[0]!),
      lifecycle: 'uncertain',
      uncertaintyLocked: true,
    }));
    server.use(http.post(`/api/allocation-disputes/${disputeId}/correction`, async ({ request }) => {
      keys.push(request.headers.get('Idempotency-Key') ?? '');
      bodies.push(await request.text());
      return HttpResponse.json(correction(disputeId, true));
    }));
    const replayed = await replayAllocationDisputeCorrectionAttempt();
    expect(replayed.replayed).toBe(true);
    expect(replayed.actorId).toBe('historical-operator');
    expect(replayed.reversedAt).toBe('2026-11-08T12:00:00Z');
    expect(keys[1]).toBe(keys[0]);
    expect(bodies[1]).toBe(bodies[0]);
    expect(bodies[0]).toBe(JSON.stringify({ reason }));
  });

  it('rejects a recognized conflict and keeps an earlier uncertain attempt after a later refusal', async () => {
    signIn();
    armReads();
    await prime(disputeId);
    server.use(http.post(`/api/allocation-disputes/${disputeId}/correction`, () => problem(
      409,
      'reconciliation.allocation_dispute.correction.not_upheld',
    )));
    await expect(submitAllocationDisputeCorrection(disputeId, reason)).rejects.toBeInstanceOf(
      AllocationDisputeCorrectionRejectedError,
    );
    expect(readStored().lifecycle).toBe('rejected');

    sessionStorage.clear();
    resetAllocationDisputeCorrectionAttemptStateForTests();
    armReads();
    await prime(disputeId);
    server.use(http.post(`/api/allocation-disputes/${disputeId}/correction`, () => HttpResponse.error()));
    await expect(submitAllocationDisputeCorrection(disputeId, reason)).rejects.toBeInstanceOf(
      UnknownAllocationDisputeCorrectionOutcomeError,
    );
    const uncertain = readStored();
    server.use(http.post(`/api/allocation-disputes/${disputeId}/correction`, () => problem(
      409,
      'reconciliation.concurrency_conflict',
    )));
    await expect(replayAllocationDisputeCorrectionAttempt()).rejects.toBeInstanceOf(
      AllocationDisputeCorrectionUncertaintyRemainsError,
    );
    expect(readStored().idempotencyKey).toBe(uncertain.idempotencyKey);
    expect(readStored().uncertaintyLocked).toBe(true);
  });

  it('does not send when the descriptor cannot be stored and leaves a defective 200 uncertain', async () => {
    signIn();
    armReads();
    await prime(disputeId);
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('storage failed');
    });
    await expect(submitAllocationDisputeCorrection(disputeId, reason)).rejects.toBeInstanceOf(
      AllocationDisputeCorrectionCommandNotSentError,
    );
    expect(sessionStorage.getItem(allocationDisputeCorrectionAttemptStorageKey)).toBeNull();

    vi.restoreAllMocks();
    server.use(http.post(`/api/allocation-disputes/${disputeId}/correction`, () => HttpResponse.json(
      correction(disputeId, false, '9.00'),
    )));
    await expect(submitAllocationDisputeCorrection(disputeId, reason)).rejects.toBeInstanceOf(
      UnreadableAllocationDisputeCorrectionError,
    );
    expect(readStored().lifecycle).toBe('uncertain');
  });

  it('keeps local removal available when deleting the verified descriptor fails', async () => {
    signIn();
    armReads();
    await prime(disputeId);
    let posts = 0;
    server.use(http.post(`/api/allocation-disputes/${disputeId}/correction`, () => {
      posts += 1;
      return HttpResponse.json(correction(disputeId, false));
    }));
    vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => {
      throw new Error('remove failed');
    });
    const verified = await submitAllocationDisputeCorrection(disputeId, reason);
    expect(verified.cleanup).toBe('failed');
    expect(posts).toBe(1);
    expect(readStored().idempotencyKey).toBe(verified.idempotencyKey);
    await expect(replayAllocationDisputeCorrectionAttempt()).rejects.toBeInstanceOf(
      AllocationDisputeCorrectionVerifiedTrackingError,
    );
    expect(posts).toBe(1);
    vi.mocked(Storage.prototype.removeItem).mockRestore();
    retryAllocationDisputeCorrectionDescriptorRemoval();
    expect(sessionStorage.getItem(allocationDisputeCorrectionAttemptStorageKey)).toBeNull();
    expect(posts).toBe(1);
    expect(correctionAttemptAllowed(sessionStore.getSnapshot().epoch, disputeId)).toBe(false);
    await expect(submitAllocationDisputeCorrection(disputeId, reason)).rejects.toBeInstanceOf(
      AllocationDisputeCorrectionNotFreshError,
    );
    expect(posts).toBe(1);
  });

  it('requires new reads after abandon and ignores a read that started before it', async () => {
    signIn();
    armReads();
    await prime(disputeId);
    server.use(http.post(`/api/allocation-disputes/${disputeId}/correction`, () => HttpResponse.error()));
    await expect(submitAllocationDisputeCorrection(disputeId, reason)).rejects.toBeInstanceOf(
      UnknownAllocationDisputeCorrectionOutcomeError,
    );
    const attempt = readStored();
    let release: (value: Response) => void = () => undefined;
    let markStarted: () => void = () => undefined;
    const started = new Promise<void>((resolve) => {
      markStarted = resolve;
    });
    server.use(http.get(`/api/allocation-disputes/${disputeId}/resolution`, () => {
      markStarted();
      return new Promise<Response>((resolve) => {
        release = resolve;
      });
    }));
    const late = loadDecisionObservation(disputeId, sessionStore.getSnapshot().epoch, new AbortController().signal);
    await started;
    await abandonAllocationDisputeCorrectionAttempt(attempt.idempotencyKey, attempt.requestBody);
    release(HttpResponse.json(decision(disputeId)));
    await late;
    expect(correctionAttemptAllowed(sessionStore.getSnapshot().epoch, disputeId)).toBe(false);
    await expect(submitAllocationDisputeCorrection(disputeId, reason)).rejects.toBeInstanceOf(
      AllocationDisputeCorrectionNotFreshError,
    );

    armReads();
    await refetchCorrectionReads(disputeId);
    expect(correctionAttemptAllowed(sessionStore.getSnapshot().epoch, disputeId)).toBe(true);
  });

  it('does not let a GET that started before abandon unlock the form when it finishes later', async () => {
    signIn();
    armReads();
    await prime(disputeId);
    server.use(http.post(`/api/allocation-disputes/${disputeId}/correction`, () => HttpResponse.error()));
    await expect(submitAllocationDisputeCorrection(disputeId, reason)).rejects.toBeInstanceOf(
      UnknownAllocationDisputeCorrectionOutcomeError,
    );
    const attempt = readStored();
    const epoch = sessionStore.getSnapshot().epoch;
    const correctionKey = allocationDisputeCorrectionQueryKey(epoch, disputeId);
    const writtenAt = queryClient.getQueryState(correctionKey)?.dataUpdatedAt ?? 0;
    let release: () => void = () => undefined;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    let started = 0;
    let markStarted: () => void = () => undefined;
    const allStarted = new Promise<void>((resolve) => {
      markStarted = resolve;
    });
    const hold = () => {
      started += 1;
      if (started === 4) {
        markStarted();
      }

      return gate;
    };
    server.use(
      http.get(`/api/allocation-disputes/${disputeId}/resolution`, async () => {
        await hold();
        return HttpResponse.json(decision(disputeId));
      }),
      http.get(`/api/allocation-disputes/${disputeId}/correction`, async () => {
        await hold();
        return HttpResponse.json({ disputeId, correction: null });
      }),
      http.get(`/api/allocation-disputes/${disputeId}`, async () => {
        await hold();
        return HttpResponse.json(detail(disputeId));
      }),
      http.get(`/api/allocations/${allocationId}/reversal-context`, async () => {
        await hold();
        return HttpResponse.json(context());
      }),
    );
    const late = refetchCorrectionReads(disputeId);
    await allStarted;
    await abandonAllocationDisputeCorrectionAttempt(attempt.idempotencyKey, attempt.requestBody);
    release();
    await late;
    expect(queryClient.getQueryState(correctionKey)?.dataUpdatedAt ?? 0).toBeGreaterThan(writtenAt);
    expect(correctionAttemptAllowed(epoch, disputeId)).toBe(false);
    let posts = 0;
    server.use(http.post(`/api/allocation-disputes/${disputeId}/correction`, () => {
      posts += 1;
      return HttpResponse.json(correction(disputeId, false));
    }));
    await expect(submitAllocationDisputeCorrection(disputeId, reason)).rejects.toBeInstanceOf(
      AllocationDisputeCorrectionNotFreshError,
    );
    expect(posts).toBe(0);
    await refetchCorrectionReads(disputeId);
    expect(correctionAttemptAllowed(epoch, disputeId)).toBe(true);
  });

  it('invalidates the financial cache and correction queue when the following correction read fails', async () => {
    signIn();
    armReads();
    await prime(disputeId);
    const epoch = sessionStore.getSnapshot().epoch;
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
    server.use(
      http.post(`/api/allocation-disputes/${disputeId}/correction`, () => HttpResponse.json(correction(disputeId, false))),
      http.get(`/api/allocation-disputes/${disputeId}/correction`, () => new HttpResponse(null, { status: 500 })),
    );
    const verified = await submitAllocationDisputeCorrection(disputeId, reason);
    await expect(verified.refresh).rejects.toThrow();
    expect(invalidate).toHaveBeenCalledWith({
      queryKey: ['allocation-disputes', 'queue', epoch, 'needs-correction'],
    });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['invoices', epoch] });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['bank-transactions', epoch] });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['allocations', 'history', epoch] });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['matches', epoch] });
  });

  it('keeps an attempt for dispute A across a newer dispute and does not let a GET settle it', async () => {
    signIn();
    armReads();
    await prime(disputeId);
    server.use(http.post(`/api/allocation-disputes/${disputeId}/correction`, () => HttpResponse.error()));
    await expect(submitAllocationDisputeCorrection(disputeId, reason)).rejects.toBeInstanceOf(
      UnknownAllocationDisputeCorrectionOutcomeError,
    );
    const attempt = readStored();
    await prime(otherDisputeId);
    expect(readStored().disputeId).toBe(disputeId);
    let gets = 0;
    server.use(http.get(`/api/allocation-disputes/${disputeId}/correction`, () => {
      gets += 1;
      return HttpResponse.json({ disputeId, correction: null });
    }));
    await loadCorrectionObservation(disputeId, sessionStore.getSnapshot().epoch, new AbortController().signal);
    expect(gets).toBe(1);
    expect(readStored().idempotencyKey).toBe(attempt.idempotencyKey);
    expect(readStored().lifecycle).toBe('uncertain');
  });

  it('does not refresh a newer session from an older correction response', async () => {
    signIn();
    armReads();
    await prime(disputeId);
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
    const removeItem = Storage.prototype.removeItem;
    vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(function (this: Storage, key: string) {
      removeItem.call(this, key);
      sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
    });
    server.use(http.post(`/api/allocation-disputes/${disputeId}/correction`, () => HttpResponse.json(correction(disputeId, false))));
    const verified = await submitAllocationDisputeCorrection(disputeId, reason);
    expect(verified.replayed).toBe(false);
    expect(invalidate).not.toHaveBeenCalled();
  });

  it('does not send when a later correction read fails after a successful read', async () => {
    signIn();
    armReads();
    await prime(disputeId);
    let posts = 0;
    server.use(
      http.post(`/api/allocation-disputes/${disputeId}/correction`, () => {
        posts += 1;
        return HttpResponse.json(correction(disputeId, false));
      }),
      http.get(`/api/allocation-disputes/${disputeId}/correction`, () => new HttpResponse(null, { status: 403 })),
    );
    const epoch = sessionStore.getSnapshot().epoch;
    await expect(queryClient.fetchQuery({
      queryKey: allocationDisputeCorrectionQueryKey(epoch, disputeId),
      queryFn: ({ signal }) => loadCorrectionObservation(disputeId, epoch, signal),
    })).rejects.toThrow();
    expect(queryClient.getQueryData(allocationDisputeCorrectionQueryKey(epoch, disputeId))).toEqual({
      disputeId,
      correction: null,
    });
    expect(correctionAttemptAllowed(epoch, disputeId)).toBe(false);
    await expect(submitAllocationDisputeCorrection(disputeId, reason)).rejects.toBeInstanceOf(
      AllocationDisputeCorrectionNotFreshError,
    );
    expect(posts).toBe(0);
  });

  it('keeps the original descriptor when reversedAt is not a timestamp', async () => {
    signIn();
    armReads();
    await prime(disputeId);
    server.use(http.post(`/api/allocation-disputes/${disputeId}/correction`, () => HttpResponse.json({
      ...correction(disputeId, false),
      reversedAt: 'yesterday',
    })));
    await expect(submitAllocationDisputeCorrection(disputeId, reason)).rejects.toBeInstanceOf(
      UnreadableAllocationDisputeCorrectionError,
    );
    const stored = readStored();
    expect(stored.lifecycle).toBe('uncertain');
    expect(stored.requestBody).toBe(JSON.stringify({ reason }));
    expect(stored.idempotencyKey).toMatch(/^[0-9a-f-]{36}$/);
  });

  it('blocks a second click while the first correction is being sent', async () => {
    signIn();
    armReads();
    await prime(disputeId);
    let release: () => void = () => undefined;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(http.post(`/api/allocation-disputes/${disputeId}/correction`, async () => {
      await gate;
      return HttpResponse.json(correction(disputeId, false));
    }));
    const first = submitAllocationDisputeCorrection(disputeId, reason);
    const second = submitAllocationDisputeCorrection(disputeId, reason);
    await expect(second).rejects.toBeInstanceOf(AllocationDisputeCorrectionSubmissionInProgressError);
    release();
    await expect(first).resolves.toMatchObject({ actorId: 'historical-operator' });
  });

  it('uses the decision read that a decision write updates', async () => {
    signIn();
    armReads();
    await prime(disputeId);
    const epoch = sessionStore.getSnapshot().epoch;
    queryClient.setQueryData(allocationDisputeResolutionQueryKey(epoch, disputeId), {
      disputeId,
      decision: null,
    });
    expect(correctionAttemptAllowed(epoch, disputeId)).toBe(false);
    queryClient.setQueryData(allocationDisputeResolutionQueryKey(epoch, disputeId), decision(disputeId));
    expect(correctionAttemptAllowed(epoch, disputeId)).toBe(true);
  });

  it('does not treat another dispute of the same allocation as a fresh reversal context', async () => {
    signIn();
    armReads();
    await prime(disputeId);
    const epoch = sessionStore.getSnapshot().epoch;
    bindCorrectionCommandView(epoch, 2, otherDisputeId, allocationId);
    queryClient.setQueryData(allocationDisputeDetailQueryKey(epoch, otherDisputeId), {
      disputeId: otherDisputeId,
      allocationId,
      statement: 'stored',
      actorSource: 'developmentJwt',
      actorId: 'operator-1',
      recordedAt: '2026-11-08T09:00:00Z',
    });
    await loadDecisionObservation(otherDisputeId, epoch, new AbortController().signal);
    await loadCorrectionObservation(otherDisputeId, epoch, new AbortController().signal);
    expect(correctionAttemptAllowed(epoch, otherDisputeId)).toBe(false);
    expect(queryClient.getQueryData(reversalContextQueryKey(epoch, allocationId, disputeId))).toMatchObject({
      status: 'confirmed',
    });
    await loadContextObservation(allocationId, otherDisputeId, epoch, new AbortController().signal);
    expect(correctionAttemptAllowed(epoch, otherDisputeId)).toBe(true);
  });

  it('keeps a confirmed correction closed when the following read fails and does not show it for another visit', async () => {
    signIn();
    armReads();
    const user = userEvent.setup();
    let posts = 0;
    let correctionReads = 0;
    let release: (value: Response) => void = () => undefined;
    server.use(
      http.get(`/api/allocation-disputes/${disputeId}/correction`, () => {
        correctionReads += 1;
        if (correctionReads === 1) {
          return HttpResponse.json({ disputeId, correction: null });
        }

        return HttpResponse.json({ title: 'unavailable' }, { status: 500 });
      }),
      http.post(`/api/allocation-disputes/${disputeId}/correction`, () => {
        posts += 1;
        return new Promise<Response>((resolve) => {
          release = resolve;
        });
      }),
    );
    render(
      <QueryClientProvider client={queryClient}>
        <VisitHarness />
      </QueryClientProvider>,
    );
    await user.type(await screen.findByLabelText('Reason'), reason);
    await user.click(screen.getByRole('button', { name: 'Reverse the full allocation' }));
    await waitFor(() => expect(posts).toBe(1));
    release(HttpResponse.json(correction(disputeId, false)));
    expect(await screen.findByText(/The allocation was reversed/)).toBeInTheDocument();
    expect(await screen.findByRole('button', { name: 'Read the correction again' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Reverse the full allocation' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Select dispute B' }));
    expect(await screen.findByText('detail-b')).toBeInTheDocument();
    expect(screen.queryByText(/The allocation was reversed/)).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Select dispute A' }));
    expect(await screen.findByText('detail-a')).toBeInTheDocument();
    expect(screen.queryByText(/The allocation was reversed/)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Reverse the full allocation' })).not.toBeInTheDocument();
    await waitFor(() => expect(sessionStorage.getItem(allocationDisputeCorrectionAttemptStorageKey)).toBeNull());
    expect(posts).toBe(1);
  });

  it('shows a missing correction link only after a successful read of correction null', async () => {
    signIn();
    armReads();
    let correctionReads = 0;
    server.use(
      http.get(`/api/allocations/${allocationId}/reversal-context`, () => HttpResponse.json({
        ...context(),
        status: 'reversed',
        reversedAt: '2026-11-08T12:00:00Z',
      })),
      http.get(`/api/allocation-disputes/${disputeId}/correction`, () => {
        correctionReads += 1;
        if (correctionReads === 1) {
          return new HttpResponse(null, { status: 500 });
        }

        return HttpResponse.json({ disputeId, correction: null });
      }),
    );
    const user = userEvent.setup();
    render(
      <QueryClientProvider client={queryClient}>
        <AllocationDisputeCorrectionPanel epoch={sessionStore.getSnapshot().epoch} disputeId={disputeId} />
      </QueryClientProvider>,
    );

    expect(await screen.findByRole('button', { name: 'Read the correction again' })).toBeInTheDocument();
    expect(screen.getByText('The correction could not be read.')).toBeInTheDocument();
    expect(screen.queryByText(
      'This allocation is already reversed and this dispute has no correction link.',
    )).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Read the correction again' }));
    expect(await screen.findByText(
      'This allocation is already reversed and this dispute has no correction link.',
    )).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Read the correction again' })).not.toBeInTheDocument();
    expect(correctionReads).toBe(2);
  });

  it('reopens the form from new reads after the visible tracking is removed', async () => {
    signIn();
    armReads();
    const epoch = sessionStore.getSnapshot().epoch;
    let posts = 0;
    server.use(http.post(`/api/allocation-disputes/${disputeId}/correction`, () => {
      posts += 1;
      return HttpResponse.error();
    }));
    const user = userEvent.setup();
    render(
      <QueryClientProvider client={queryClient}>
        <AllocationDisputeCorrectionPanel epoch={epoch} disputeId={disputeId} />
      </QueryClientProvider>,
    );
    await user.type(await screen.findByLabelText('Reason'), reason);
    await user.click(screen.getByRole('button', { name: 'Reverse the full allocation' }));
    expect(await screen.findByRole('button', { name: 'Remove local correction tracking' })).toBeInTheDocument();
    expect(posts).toBe(1);

    const waves = { detail: 0, decision: 0, correction: 0, context: 0 };
    let releaseOld: () => void = () => undefined;
    const oldGate = new Promise<void>((resolve) => {
      releaseOld = resolve;
    });
    let releaseNew: () => void = () => undefined;
    const newGate = new Promise<void>((resolve) => {
      releaseNew = resolve;
    });
    let markReplacement: () => void = () => undefined;
    const replacementsStarted = new Promise<void>((resolve) => {
      markReplacement = resolve;
    });
    const waitForWave = (name: keyof typeof waves) => {
      waves[name] += 1;
      if (waves.detail >= 2 && waves.decision >= 2 && waves.correction >= 2 && waves.context >= 2) {
        markReplacement();
      }

      return waves[name] === 1 ? oldGate : newGate;
    };
    server.use(
      http.get(`/api/allocation-disputes/${disputeId}/resolution`, async () => {
        await waitForWave('decision');
        return HttpResponse.json(decision(disputeId));
      }),
      http.get(`/api/allocation-disputes/${disputeId}/correction`, async () => {
        await waitForWave('correction');
        return HttpResponse.json({ disputeId, correction: null });
      }),
      http.get(`/api/allocation-disputes/${disputeId}`, async () => {
        await waitForWave('detail');
        return HttpResponse.json(detail(disputeId));
      }),
      http.get(`/api/allocations/${allocationId}/reversal-context`, async () => {
        await waitForWave('context');
        return HttpResponse.json(context());
      }),
    );
    void queryClient.refetchQueries({ queryKey: allocationDisputeDetailQueryKey(epoch, disputeId), exact: true });
    void queryClient.refetchQueries({ queryKey: allocationDisputeResolutionQueryKey(epoch, disputeId), exact: true });
    void queryClient.refetchQueries({ queryKey: allocationDisputeCorrectionQueryKey(epoch, disputeId), exact: true });
    void queryClient.refetchQueries({ queryKey: reversalContextQueryKey(epoch, allocationId, disputeId), exact: true });
    await waitFor(() => expect(waves).toEqual({ detail: 1, decision: 1, correction: 1, context: 1 }));

    await user.click(screen.getByRole('button', { name: 'Remove local correction tracking' }));
    await replacementsStarted;
    expect(waves).toEqual({ detail: 2, decision: 2, correction: 2, context: 2 });
    expect(screen.queryByRole('button', { name: 'Reverse the full allocation' })).not.toBeInTheDocument();
    expect(posts).toBe(1);

    releaseNew();
    expect(await screen.findByRole('button', { name: 'Reverse the full allocation' })).toBeInTheDocument();
    expect(posts).toBe(1);
    releaseOld();
    expect(posts).toBe(1);
  });

  it('invalidates the current epoch before the held correction read finishes', async () => {
    signIn();
    armReads();
    const epoch = sessionStore.getSnapshot().epoch;
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
    let correctionReads = 0;
    let releaseHeld: (value: Response) => void = () => undefined;
    let markHeld: () => void = () => undefined;
    const held = new Promise<void>((resolve) => {
      markHeld = resolve;
    });
    server.use(
      http.post(`/api/allocation-disputes/${disputeId}/correction`, () => HttpResponse.json(correction(disputeId, false))),
      http.get(`/api/allocation-disputes/${disputeId}/correction`, () => {
        correctionReads += 1;
        if (correctionReads === 1) {
          return HttpResponse.json({ disputeId, correction: null });
        }

        if (correctionReads === 2) {
          markHeld();
          return new Promise<Response>((resolve) => {
            releaseHeld = resolve;
          });
        }

        return HttpResponse.json({ disputeId, correction: null });
      }),
    );
    const user = userEvent.setup();
    render(
      <QueryClientProvider client={queryClient}>
        <AllocationDisputeCorrectionPanel epoch={epoch} disputeId={disputeId} />
      </QueryClientProvider>,
    );
    await user.type(await screen.findByLabelText('Reason'), reason);
    await user.click(screen.getByRole('button', { name: 'Reverse the full allocation' }));
    await held;
    expect(invalidate).toHaveBeenCalledWith({
      queryKey: ['allocation-disputes', 'work-summary', epoch],
    });
    expect(invalidate).toHaveBeenCalledWith({
      queryKey: ['allocation-disputes', 'queue', epoch, 'needs-correction'],
    });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['invoices', epoch] });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['bank-transactions', epoch] });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['allocations', 'history', epoch] });
    expect(screen.getByText(/The allocation was reversed/)).toBeInTheDocument();

    releaseHeld(new HttpResponse(null, { status: 500 }));
    expect(await screen.findByText('The correction is stored. The latest read did not finish.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Read the correction again' })).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Read the correction again' }));
    await waitFor(() => expect(correctionReads).toBe(3));
    await waitFor(() => expect(screen.queryByText('The correction is stored. The latest read did not finish.')).not.toBeInTheDocument());
    expect(screen.getByText(/The allocation was reversed/)).toBeInTheDocument();
  });
});

function VisitHarness() {
  const [selected, setSelected] = useState(disputeId);
  return (
    <>
      <button type="button" onClick={() => setSelected(otherDisputeId)}>Select dispute B</button>
      <button type="button" onClick={() => setSelected(disputeId)}>Select dispute A</button>
      <p>{selected === disputeId ? 'detail-a' : 'detail-b'}</p>
      <AllocationDisputeCorrectionPanel epoch={sessionStore.getSnapshot().epoch} disputeId={selected} />
    </>
  );
}

function signIn(): void {
  sessionStore.rotate(fakeJwt(), 4_000_000_000, null);
}

function armReads(): void {
  server.use(
    http.get('/api/allocation-disputes/:disputeId/resolution', ({ params }) => HttpResponse.json(decision(String(params.disputeId)))),
    http.get('/api/allocation-disputes/:disputeId/correction', ({ params }) => HttpResponse.json({
      disputeId: String(params.disputeId),
      correction: null,
    })),
    http.get('/api/allocation-disputes/:disputeId', ({ params }) => HttpResponse.json({
      disputeId: String(params.disputeId),
      allocationId,
      statement: 'stored',
      actorSource: 'developmentJwt',
      actorId: 'operator-1',
      recordedAt: '2026-11-08T09:00:00Z',
    })),
    http.get('/api/allocations/:allocationId/reversal-context', () => HttpResponse.json(context())),
  );
}

async function prime(id: string): Promise<void> {
  const epoch = sessionStore.getSnapshot().epoch;
  bindCorrectionCommandView(epoch, 1, id, allocationId);
  queryClient.setQueryData(allocationDisputeDetailQueryKey(epoch, id), detail(id));
  const signal = new AbortController().signal;
  await loadDecisionObservation(id, epoch, signal);
  await loadCorrectionObservation(id, epoch, signal);
  await loadContextObservation(allocationId, id, epoch, signal);
}

async function refetchCorrectionReads(id: string): Promise<void> {
  const epoch = sessionStore.getSnapshot().epoch;
  bindCorrectionCommandView(epoch, 1, id, allocationId);
  await Promise.all([
    queryClient.fetchQuery({
      queryKey: allocationDisputeDetailQueryKey(epoch, id),
      queryFn: ({ signal }) => getAllocationDispute(id, signal),
    }),
    queryClient.fetchQuery({
      queryKey: allocationDisputeResolutionQueryKey(epoch, id),
      queryFn: ({ signal }) => loadDecisionObservation(id, epoch, signal),
    }),
    queryClient.fetchQuery({
      queryKey: allocationDisputeCorrectionQueryKey(epoch, id),
      queryFn: ({ signal }) => loadCorrectionObservation(id, epoch, signal),
    }),
    queryClient.fetchQuery({
      queryKey: reversalContextQueryKey(epoch, allocationId, id),
      queryFn: ({ signal }) => loadContextObservation(allocationId, id, epoch, signal),
    }),
  ]);
}

function readStored(): {
  lifecycle: string;
  requestBody: string;
  idempotencyKey: string;
  uncertaintyLocked: boolean;
  disputeId: string;
} {
  return JSON.parse(sessionStorage.getItem(allocationDisputeCorrectionAttemptStorageKey) ?? '{}') as {
    lifecycle: string;
    requestBody: string;
    idempotencyKey: string;
    uncertaintyLocked: boolean;
    disputeId: string;
  };
}

function verifiedStored(idempotencyKey: string, requestBody: string) {
  return {
    schemaVersion: 1,
    operation: 'AllocationDisputeCorrection',
    organizationId,
    actorSource: 'developmentJwt',
    actorId: 'operator-1',
    disputeId,
    allocationId,
    invoiceId,
    bankTransactionId,
    amount: '10.25',
    currency: 'PLN',
    idempotencyKey,
    requestBody,
    normalizedReason: reason,
    lifecycle: 'uncertain',
    uncertaintyLocked: true,
  };
}

function detail(id: string) {
  return {
    disputeId: id,
    allocationId,
    statement: 'stored',
    actorSource: 'developmentJwt',
    actorId: 'operator-1',
    recordedAt: '2026-11-08T09:00:00Z',
  };
}

function decision(id: string) {
  return {
    disputeId: id,
    decision: {
      disputeId: id,
      outcome: 'upheld',
      reason: 'decision reason',
      actorSource: 'developmentJwt',
      actorId: 'operator-1',
      decidedAt: '2026-11-08T10:00:00Z',
    },
  };
}

function context() {
  return {
    allocationId,
    invoiceId,
    bankTransactionId,
    invoiceNumber: 'INV-1',
    reference: null,
    amount: { amount: 10.25, currency: 'PLN' },
    status: 'confirmed',
    confirmedAt: '2026-11-08T08:00:00Z',
    reversedAt: null,
    reversalReason: null,
  };
}

function correction(id: string, replayed: boolean, amount = '10.25') {
  return {
    disputeId: id,
    allocationId,
    invoiceId,
    bankTransactionId,
    status: 'reversed',
    amount: { amount: Number(amount), currency: 'PLN' },
    reversedAt: '2026-11-08T12:00:00Z',
    reason,
    actorSource: 'developmentJwt',
    actorId: 'historical-operator',
    replayed,
  };
}

function problem(status: number, code: string) {
  return HttpResponse.json({ title: 'Conflict', code }, { status });
}
