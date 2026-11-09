import { zodResolver } from '@hookform/resolvers/zod';
import { Alert, Button, Stack, TextField, Typography } from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';

import {
  allocationDisputeDetailQueryKey,
  allocationDisputeIdsMatch,
  allocationDisputeResolutionQueryKey,
  getAllocationDispute,
  isExactGuid,
  normalizeAllocationDisputeResolutionReason,
} from './allocation-dispute-api';
import { loadAllocationDisputeResolution } from './allocation-dispute-resolution-attempt';
import {
  abandonAllocationDisputeCorrectionAttempt,
  allocationDisputeCorrectionQueryKey,
  bindCorrectionCommandView,
  correctionAttemptAllowed,
  loadContextObservation,
  loadCorrectionObservation,
  correctionVerifiedTrackingMatches,
  promotePersistedSendingAllocationDisputeCorrection,
  rereadAllocationDisputeCorrection,
  replayAllocationDisputeCorrectionAttempt,
  reloadVisibleCorrectionReads,
  retryAllocationDisputeCorrectionDescriptorRemoval,
  reversalContextQueryKey,
  submitAllocationDisputeCorrection,
  useAllocationDisputeCorrectionAttempt,
  type VerifiedAllocationDisputeCorrection,
} from './allocation-dispute-correction-attempt';

const schema = z.object({
  reason: z.string().refine(
    (value) => normalizeAllocationDisputeResolutionReason(value) !== null,
    'Enter a reason of 1 to 500 characters.',
  ),
});

interface Bound<T> {
  epoch: number;
  visit: number;
  value: T;
}

interface RefreshWarning {
  epoch: number;
  visit: number;
  disputeId: string;
  allocationId: string;
  generation: number;
}

export function AllocationDisputeCorrectionPanel({
  epoch,
  disputeId,
}: {
  epoch: number;
  disputeId: string | null;
}) {
  const identity = `${epoch}:${disputeId ?? ''}`;
  const [visit, setVisit] = useState(1);
  const [trackedIdentity, setTrackedIdentity] = useState(identity);
  if (trackedIdentity !== identity) {
    setTrackedIdentity(identity);
    setVisit((value) => value + 1);
  }

  const publisherId = useViewPublisher(epoch, visit);
  const view = useAllocationDisputeCorrectionAttempt();
  const [published, setPublished] = useState<Bound<VerifiedAllocationDisputeCorrection> | null>(null);
  const [failure, setFailure] = useState<Bound<string> | null>(null);
  const [refreshWarning, setRefreshWarning] = useState<RefreshWarning | null>(null);
  const pageAlive = useRef(true);
  const refreshGeneration = useRef(0);
  const readableDispute = disputeId !== null && isExactGuid(disputeId) ? disputeId : null;
  const detail = useQuery({
    queryKey: allocationDisputeDetailQueryKey(epoch, readableDispute ?? ''),
    enabled: readableDispute !== null,
    queryFn: ({ signal }) => getAllocationDispute(readableDispute!, signal),
  });
  const decision = useQuery({
    queryKey: allocationDisputeResolutionQueryKey(epoch, readableDispute ?? ''),
    enabled: readableDispute !== null,
    queryFn: ({ signal }) => loadAllocationDisputeResolution(readableDispute!, epoch, signal),
  });
  const correction = useQuery({
    queryKey: allocationDisputeCorrectionQueryKey(epoch, readableDispute ?? ''),
    enabled: readableDispute !== null,
    queryFn: ({ signal }) => loadCorrectionObservation(readableDispute!, epoch, signal),
  });
  const allocationId = detail.data !== undefined && allocationDisputeIdsMatch(detail.data.disputeId, readableDispute ?? '')
    ? detail.data.allocationId
    : null;
  const context = useQuery({
    queryKey: reversalContextQueryKey(epoch, allocationId ?? '', readableDispute ?? ''),
    enabled: allocationId !== null && readableDispute !== null,
    queryFn: ({ signal }) => loadContextObservation(allocationId!, readableDispute!, epoch, signal),
  });

  if (readableDispute !== null && allocationId !== null && detail.isSuccess && detail.fetchStatus === 'idle') {
    bindCorrectionCommandView(epoch, visit, readableDispute, allocationId);
  }

  useEffect(() => {
    pageAlive.current = true;
    promotePersistedSendingAllocationDisputeCorrection();
    return () => {
      pageAlive.current = false;
    };
  }, []);

  const visibleSuccess = shown(epoch, visit, published);
  const visibleFailure = shown(epoch, visit, failure);
  const visibleWarning = refreshWarning !== null && refreshWarning.epoch === epoch && refreshWarning.visit === visit
    ? refreshWarning
    : null;
  const correctionSettled = correction.isSuccess && correction.fetchStatus === 'idle';
  const storedCorrection = correctionSettled ? correction.data.correction : null;
  const allowed = readableDispute !== null && correctionAttemptAllowed(epoch, readableDispute);
  const confirmed = context.isSuccess && context.fetchStatus === 'idle' && context.data.status === 'confirmed'
    ? context.data
    : null;
  const reversedWithoutLink = correctionSettled
    && correction.data.correction === null
    && context.isSuccess
    && context.fetchStatus === 'idle'
    && context.data.status === 'reversed';
  const correctionReadFailed = correction.isError && correction.fetchStatus === 'idle';
  const upheld = decision.isSuccess
    && decision.fetchStatus === 'idle'
    && decision.data.decision?.outcome === 'upheld';

  async function send(reason: string): Promise<void> {
    if (readableDispute === null || !publisherCurrent(publisherId, publisherGeneration(publisherId))) {
      return;
    }

    const ticket = publisherGeneration(publisherId);
    const commandVisit = visit;
    setFailure(null);
    try {
      const result = await submitAllocationDisputeCorrection(readableDispute, reason);
      publishResult(ticket, commandVisit, result);
      await settleRefresh(ticket, commandVisit, result);
    } catch (error) {
      publishFailure(ticket, commandVisit, error);
    }
  }

  function publishResult(ticket: number, commandVisit: number, result: VerifiedAllocationDisputeCorrection): void {
    if (!pageAlive.current || !publisherCurrent(publisherId, ticket) || visit !== commandVisit) {
      return;
    }

    setPublished({ epoch, visit: commandVisit, value: result });
  }

  function publishFailure(ticket: number, commandVisit: number, error: unknown): void {
    if (!pageAlive.current || !publisherCurrent(publisherId, ticket) || visit !== commandVisit) {
      return;
    }

    setFailure({
      epoch,
      visit: commandVisit,
      value: error instanceof Error ? error.message : 'The correction could not be sent.',
    });
  }

  function claimRefresh(): number {
    refreshGeneration.current += 1;
    return refreshGeneration.current;
  }

  async function settleRefresh(
    ticket: number,
    commandVisit: number,
    result: VerifiedAllocationDisputeCorrection,
  ): Promise<void> {
    const generation = claimRefresh();
    try {
      await result.refresh;
      finishRefresh(ticket, commandVisit, generation, result.epoch, result.disputeId, result.allocationId, 'clear');
    } catch {
      finishRefresh(ticket, commandVisit, generation, result.epoch, result.disputeId, result.allocationId, 'warn');
    }
  }

  function finishRefresh(
    ticket: number,
    commandVisit: number,
    generation: number,
    resultEpoch: number,
    resultDisputeId: string,
    resultAllocationId: string,
    outcome: 'clear' | 'warn',
  ): void {
    if (!pageAlive.current || !publisherCurrent(publisherId, ticket) || generation !== refreshGeneration.current) {
      return;
    }

    if (outcome === 'clear') {
      setRefreshWarning((current) => (
        current?.epoch === resultEpoch && current.disputeId === resultDisputeId && current.generation < generation
          ? null
          : current
      ));
      return;
    }

    setRefreshWarning((current) => {
      if (current !== null && current.disputeId === resultDisputeId && current.generation > generation) {
        return current;
      }

      return {
        epoch: resultEpoch,
        visit: commandVisit,
        disputeId: resultDisputeId,
        allocationId: resultAllocationId,
        generation,
      };
    });
  }

  return (
    <Stack spacing={1} component="section" aria-label="Allocation dispute correction">
      <Typography variant="h6" component="h2">Correction</Typography>
      {visibleSuccess !== null ? (
        <Alert severity="success">
          {visibleSuccess.replayed ? 'This correction was already stored.' : 'The allocation was reversed for this dispute.'}
          {' '}
          {visibleSuccess.amount}
          {' '}
          {visibleSuccess.currency}
          {' by '}
          {visibleSuccess.actorId}
        </Alert>
      ) : null}
      {storedCorrection !== null ? (
        <Alert severity="info">
          {`Stored correction ${storedCorrection.amount} ${storedCorrection.currency} by ${storedCorrection.actorId} at ${storedCorrection.reversedAt}. ${storedCorrection.reason}`}
        </Alert>
      ) : null}
      {reversedWithoutLink ? (
        <Alert severity="info">This allocation is already reversed and this dispute has no correction link.</Alert>
      ) : null}
      {correctionReadFailed && visibleWarning === null && readableDispute !== null ? (
        <Alert
          severity="warning"
          action={(
            <Button
              color="inherit"
              onClick={() => {
                void rereadAllocationDisputeCorrection(epoch, readableDispute).catch(() => {
                  // The failed query stays visible and can be read again.
                });
              }}
            >
              Read the correction again
            </Button>
          )}
        >
          The correction could not be read.
        </Alert>
      ) : null}
      {visibleWarning !== null ? (
        <Alert
          severity="warning"
          action={(
            <Button
              color="inherit"
              onClick={() => {
                const target = visibleWarning;
                const generation = claimRefresh();
                void rereadAllocationDisputeCorrection(target.epoch, target.disputeId).then(() => {
                  finishRefresh(
                    publisherGeneration(publisherId),
                    target.visit,
                    generation,
                    target.epoch,
                    target.disputeId,
                    target.allocationId,
                    'clear',
                  );
                }).catch(() => {
                  finishRefresh(
                    publisherGeneration(publisherId),
                    target.visit,
                    generation,
                    target.epoch,
                    target.disputeId,
                    target.allocationId,
                    'warn',
                  );
                });
              }}
            >
              Read the correction again
            </Button>
          )}
        >
          The correction is stored. The latest read did not finish.
        </Alert>
      ) : null}
      {view.status === 'pending' && correctionVerifiedTrackingMatches(view.attempt) ? (
        <Alert
          severity="warning"
          action={(
            <Button color="inherit" onClick={() => {
              try {
                retryAllocationDisputeCorrectionDescriptorRemoval();
              } catch (error) {
                setFailure({
                  epoch,
                  visit,
                  value: error instanceof Error ? error.message : 'The correction could not be sent.',
                });
              }
            }}
            >
              Try removing local tracking again
            </Button>
          )}
        >
          The correction is stored. Local tracking could not be removed.
        </Alert>
      ) : null}
      {view.status === 'pending' && !correctionVerifiedTrackingMatches(view.attempt) ? (
        <Alert severity="warning">
          This correction has no verified result. Sending it again uses the same request.
          <Button onClick={() => {
            const ticket = publisherGeneration(publisherId);
            const commandVisit = visit;
            void replayAllocationDisputeCorrectionAttempt()
              .then((result) => {
                publishResult(ticket, commandVisit, result);
                return settleRefresh(ticket, commandVisit, result);
              })
              .catch((error: unknown) => {
                publishFailure(ticket, commandVisit, error);
              });
          }}
          >
            Send the same correction again
          </Button>
          <Button onClick={() => {
            if (view.status !== 'pending') {
              return;
            }

            const attempt = view.attempt;
            void abandonAllocationDisputeCorrectionAttempt(attempt.idempotencyKey, attempt.requestBody)
              .then(() => {
                if (readableDispute === null
                  || allocationId === null
                  || !allocationDisputeIdsMatch(attempt.disputeId, readableDispute)) {
                  return undefined;
                }

                return reloadVisibleCorrectionReads(epoch, attempt.disputeId, attempt.allocationId);
              })
              .catch((error: unknown) => {
                setFailure({
                  epoch,
                  visit,
                  value: error instanceof Error ? error.message : 'The correction could not be sent.',
                });
              });
          }}
          >
            Remove local correction tracking
          </Button>
        </Alert>
      ) : null}
      {allowed && confirmed !== null && upheld && storedCorrection === null ? (
        <CorrectionForm confirmed={confirmed} onSend={send} />
      ) : null}
      {visibleFailure !== null ? <Alert severity="error">{visibleFailure}</Alert> : null}
    </Stack>
  );
}

function CorrectionForm({
  confirmed,
  onSend,
}: {
  confirmed: { amount: string; currency: string; allocationId: string };
  onSend: (reason: string) => Promise<void>;
}) {
  const form = useForm({ resolver: zodResolver(schema), defaultValues: { reason: '' } });

  return (
    <form onSubmit={form.handleSubmit((values) => void onSend(values.reason))}>
      <Typography>
        Full reversal of {confirmed.amount} {confirmed.currency} for allocation {confirmed.allocationId}.
      </Typography>
      <TextField label="Reason" {...form.register('reason')} error={form.formState.errors.reason !== undefined} helperText={form.formState.errors.reason?.message} />
      <Button type="submit">Reverse the full allocation</Button>
    </form>
  );
}

const viewPublishers = new Map<number, { generation: number; epoch: number; visit: number }>();
let nextPublisherId = 1;

function useViewPublisher(epoch: number, visit: number): number {
  const [publisherId] = useState(() => {
    const id = nextPublisherId;
    nextPublisherId += 1;
    viewPublishers.set(id, { generation: 0, epoch, visit });
    return id;
  });
  const publisher = viewPublishers.get(publisherId) ?? { generation: 0, epoch, visit };
  if (!viewPublishers.has(publisherId)) {
    viewPublishers.set(publisherId, publisher);
  }

  if (publisher.epoch !== epoch || publisher.visit !== visit) {
    publisher.generation += 1;
    publisher.epoch = epoch;
    publisher.visit = visit;
  }

  return publisherId;
}

function publisherGeneration(id: number): number {
  return viewPublishers.get(id)?.generation ?? 0;
}

function publisherCurrent(id: number, ticket: number): boolean {
  return publisherGeneration(id) === ticket;
}

function shown<T>(epoch: number, visit: number, bound: Bound<T> | null): T | null {
  if (bound === null || bound.epoch !== epoch || bound.visit !== visit) {
    return null;
  }

  return bound.value;
}
