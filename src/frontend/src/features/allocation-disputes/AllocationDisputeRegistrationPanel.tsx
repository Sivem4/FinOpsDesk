import { zodResolver } from '@hookform/resolvers/zod';
import { Alert, Button, Stack, TextField } from '@mui/material';
import { useEffect, useState } from 'react';
import { useForm } from 'react-hook-form';
import { Link } from 'react-router';
import { z } from 'zod';

import { ApiError } from '../../shared/api/errors';
import { sessionStore } from '../../shared/api/session-store';
import { endSession, useSession } from '../../shared/auth/session';
import {
  abandonAllocationDisputeAttempt,
  abandonUnreadableAllocationDisputeAttempt,
  AllocationDisputeActorRequiredError,
  AllocationDisputeAttemptBlockedError,
  AllocationDisputeAttemptChangedError,
  AllocationDisputeAuthenticationError,
  AllocationDisputeCommandNotSentError,
  AllocationDisputeRejectedError,
  AllocationDisputeRetryNotSentError,
  AllocationDisputeStorageCleanupError,
  AllocationDisputeStorageError,
  AllocationDisputeSubmissionInProgressError,
  AllocationDisputeUncertaintyRemainsError,
  beginAllocationDisputeRegistration,
  normalizeAllocationDisputeStatement,
  promotePersistedSendingAllocationDispute,
  refreshAllocationDisputeLists,
  rememberedAllocationDisputeMatches,
  retryAllocationDisputeDescriptorRemoval,
  submitAllocationDisputeAttempt,
  UnknownAllocationDisputeOutcomeError,
  UnreadableAllocationDisputeError,
  UnresolvedAllocationDisputeError,
  useAllocationDisputeAttempt,
  useRememberedAllocationDispute,
  type VerifiedAllocationDisputeRegistration,
} from './allocation-dispute-attempt';

const statementSchema = z.object({
  statement: z.string().superRefine((value, context) => {
    if (normalizeAllocationDisputeStatement(value) === null) {
      context.addIssue({
        code: 'custom',
        message: 'Enter a statement of 1 to 500 characters. Spaces at the ends are removed.',
      });
    }
  }),
});

type StatementValues = z.infer<typeof statementSchema>;

export function AllocationDisputeRecovery({ visit }: { visit: number }) {
  const session = useSession();
  const view = useAllocationDisputeAttempt();
  const remembered = useRememberedAllocationDispute();
  const publisherId = useViewPublisher(session.epoch, visit);
  const [replayed, setReplayed] = useState<Bound<VerifiedAllocationDisputeRegistration> | null>(null);
  const [replayError, setReplayError] = useState<Bound<unknown> | null>(null);
  const [removalError, setRemovalError] = useState<Bound<unknown> | null>(null);
  const [refreshFailed, setRefreshFailed] = useState<Bound<boolean> | null>(null);

  useEffect(() => {
    promotePersistedSendingAllocationDispute();
  }, [view]);

  if (view.status === 'blocked-authentication') {
    return <Alert severity="warning">Sign in before this browser can show a stored dispute.</Alert>;
  }

  if (view.status === 'blocked-tenant') {
    return <Alert severity="warning">A stored dispute belongs to another organization. Its details stay hidden.</Alert>;
  }

  if (view.status === 'blocked-actor' || view.status === 'blocked-other-actor') {
    return <Alert severity="warning">A stored dispute command belongs to another operator. Its details stay hidden.</Alert>;
  }

  if (view.status === 'invalid-storage') {
    return (
      <Alert
        severity="error"
        action={<Button color="inherit" onClick={() => abandonUnreadableAllocationDisputeAttempt()}>Remove unreadable tracking</Button>}
      >
        A stored dispute cannot be read safely. It was not replaced.
      </Alert>
    );
  }

  const visibleReplay = shown(replayed, session.epoch, visit);
  const replayMatchesPending = visibleReplay !== null
    && view.status === 'pending'
    && sameRegistration(visibleReplay, view.attempt);
  const showReplay = visibleReplay !== null && (view.status !== 'pending' || replayMatchesPending);
  const showTracking = view.status === 'pending' && remembered !== null && rememberedAllocationDisputeMatches(view.attempt);
  const visibleReplayError = shown(replayError, session.epoch, visit);
  const visibleRemovalError = shown(removalError, session.epoch, visit);
  const visibleRefreshFailure = shown(refreshFailed, session.epoch, visit) === true;

  return (
    <>
      {showReplay && visibleReplay !== null ? (
        <Alert severity="success">
          {visibleReplay.created ? 'Dispute recorded.' : 'This dispute was already stored.'}
          {' '}
          <Link to={`/allocation-disputes?selected=${visibleReplay.record.disputeId}`}>Open this allocation dispute</Link>
        </Alert>
      ) : null}
      {showTracking ? (
        <Alert
          severity="warning"
          action={<Button color="inherit" onClick={() => removeTrackedDispute(publisherId, (failure) => {
            setRemovalError(failure === null ? null : { epoch: session.epoch, visit, value: failure });
          })}>Remove local tracking</Button>}
        >
          Local tracking for a recorded dispute could not be removed.
          {visibleRemovalError !== null ? ` ${removalFailureMessage(visibleRemovalError)}` : ''}
        </Alert>
      ) : null}
      {visibleReplayError !== null ? <DisputeFailure error={visibleReplayError} /> : null}
      {visibleRefreshFailure ? (
        <Alert
          severity="warning"
          action={visibleReplay !== null ? (
            <Button color="inherit" onClick={() => {
              if (visibleReplay === null) {
                return;
              }

              const refreshEpoch = visibleReplay.epoch;
              void readListsAgain(publisherId, refreshEpoch, (failed) => setRefreshFailed({ epoch: refreshEpoch, visit, value: failed }));
            }}>
              Read the dispute list again
            </Button>
          ) : null}
        >
          The dispute list could not be refreshed. The recorded dispute stays recorded.
        </Alert>
      ) : null}
      {view.status === 'pending' && !showReplay && !showTracking ? (
        <Alert
          severity={view.attempt.lifecycle === 'rejected' ? 'error' : 'warning'}
          action={
            <Stack direction="row" spacing={1}>
              {view.attempt.lifecycle !== 'rejected' ? (
                <Button color="inherit" onClick={() => void replayStoredDispute(publisherId, setReplayed, setReplayError, setRefreshFailed)}>
                  Send the same dispute again
                </Button>
              ) : null}
              <Button
                color="inherit"
                onClick={() => abandonAllocationDisputeAttempt(view.attempt.idempotencyKey, view.attempt.requestBody)}
              >
                Remove local tracking
              </Button>
            </Stack>
          }
        >
          {view.attempt.lifecycle === 'rejected'
            ? 'The server refused this dispute command.'
            : view.attempt.lifecycle === 'ready'
              ? 'This dispute was stored locally and has not been sent.'
              : 'This dispute has no verified result. Sending it again uses the same request.'}
          {' '}
          Removing local tracking does not undo a stored dispute. A new key can record another dispute for the same allocation and the same statement.
        </Alert>
      ) : null}
    </>
  );
}

export function AllocationDisputeRegistrationPanel({
  allocationId,
  epoch,
  onClose,
}: {
  allocationId: string;
  epoch: number;
  onClose: () => void;
}) {
  const publisherId = useViewPublisher(epoch, 0);
  const [published, setPublished] = useState<VerifiedAllocationDisputeRegistration | null>(null);
  const [trackingFailed, setTrackingFailed] = useState(false);
  const [removalError, setRemovalError] = useState<unknown>(null);
  const [refreshFailed, setRefreshFailed] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [submitting, setSubmitting] = useState(false);
  const form = useForm<StatementValues>({
    resolver: zodResolver(statementSchema),
    defaultValues: { statement: '' },
  });

  function publishRefresh(ticket: number, currentEpoch: number, serial: number, failed: boolean) {
    if (!publisherOwns(publisherId, ticket, serial) || !stillCurrent(ticket, currentEpoch)) {
      return;
    }

    setRefreshFailed(failed);
  }

  async function recordDispute(values: StatementValues) {
    const statement = normalizeAllocationDisputeStatement(values.statement);
    if (statement === null) {
      return;
    }

    const claimed = claimRefresh(publisherId);
    if (claimed === null) {
      return;
    }

    const ticket = claimed.ticket;
    const currentEpoch = sessionStore.getSnapshot().epoch;
    const serial = claimed.serial;
    setError(null);
    setSubmitting(true);
    try {
      beginAllocationDisputeRegistration(allocationId, statement);
      const result = await submitAllocationDisputeAttempt();
      if (!stillCurrent(ticket, currentEpoch)) {
        return;
      }

      setPublished(result);
      setTrackingFailed(result.cleanup === 'failed');
      void result.refresh.then(
        () => publishRefresh(ticket, currentEpoch, serial, false),
        () => publishRefresh(ticket, currentEpoch, serial, true),
      );
    } catch (caught) {
      if (!stillCurrent(ticket, currentEpoch)) {
        return;
      }

      setError(caught);
    } finally {
      if (stillCurrent(ticket, currentEpoch)) {
        setSubmitting(false);
      }
    }
  }

  function stillCurrent(ticket: number, currentEpoch: number): boolean {
    return publisherGeneration(publisherId) === ticket && sessionStore.getSnapshot().epoch === currentEpoch && currentEpoch === epoch;
  }

  return (
    <Stack spacing={1} component="section" aria-label={`Record dispute for allocation ${allocationId}`}>
      <form onSubmit={form.handleSubmit((values) => void recordDispute(values))}>
        <Stack spacing={1}>
          <TextField
            label="Statement"
            {...form.register('statement')}
            error={form.formState.errors.statement !== undefined}
            helperText={form.formState.errors.statement?.message ?? 'Record the challenge received outside FinOpsDesk.'}
            disabled={submitting || published !== null}
          />
          <Stack direction="row" spacing={1}>
            <Button type="submit" variant="contained" disabled={submitting || published !== null}>
              Record dispute
            </Button>
            <Button type="button" onClick={onClose} disabled={submitting}>Close</Button>
          </Stack>
        </Stack>
      </form>
      {published !== null ? (
        <Alert
          severity="success"
          action={
            <Button color="inherit" onClick={() => void readListsAgain(publisherId, published.epoch, setRefreshFailed)}>
              Read the dispute list again
            </Button>
          }
        >
          {published.created ? 'Dispute recorded.' : 'This dispute was already stored.'}
          {' '}
          <Link to={`/allocation-disputes?selected=${published.record.disputeId}`}>Open this allocation dispute</Link>
        </Alert>
      ) : null}
      {published !== null && trackingFailed ? (
        <Alert
          severity="warning"
          action={<Button color="inherit" onClick={() => removeTrackedDispute(publisherId, (failure) => {
            setRemovalError(failure);
            setTrackingFailed(failure !== null);
          })}>Remove local tracking</Button>}
        >
          Local tracking for this dispute could not be removed.
          {removalError !== null ? ` ${removalFailureMessage(removalError)}` : ''}
        </Alert>
      ) : null}
      {refreshFailed ? (
        <Alert severity="warning">The dispute list could not be refreshed. The recorded dispute stays recorded.</Alert>
      ) : null}
      {error !== null ? <DisputeFailure error={error} /> : null}
    </Stack>
  );
}

function DisputeFailure({ error }: { error: unknown }) {
  if ((error instanceof AllocationDisputeRejectedError || error instanceof AllocationDisputeUncertaintyRemainsError) && error.status === 401) {
    return (
      <Alert
        severity="error"
        action={<Button color="inherit" onClick={() => endSession('unauthorized')}>Update access</Button>}
      >
        Sign in again before recording this dispute.
      </Alert>
    );
  }

  if (error instanceof AllocationDisputeRejectedError && error.status === 403) {
    return <Alert severity="error">The server requires an operator identity. This dispute was not recorded.</Alert>;
  }

  if (error instanceof AllocationDisputeRejectedError) {
    return <Alert severity="error">{refusalMessage(error)}</Alert>;
  }

  if (error instanceof AllocationDisputeRetryNotSentError) {
    return <Alert severity="error">The same dispute was not sent again. The unresolved command remains stored.</Alert>;
  }

  if (error instanceof AllocationDisputeUncertaintyRemainsError || error instanceof UnknownAllocationDisputeOutcomeError || error instanceof UnreadableAllocationDisputeError) {
    return <Alert severity="warning">This dispute has no verified result. Sending it again uses the same request.</Alert>;
  }

  if (error instanceof AllocationDisputeCommandNotSentError || error instanceof AllocationDisputeStorageError) {
    return <Alert severity="error">The dispute was not sent because this browser could not store the command.</Alert>;
  }

  if (error instanceof UnresolvedAllocationDisputeError || error instanceof AllocationDisputeAttemptBlockedError) {
    return <Alert severity="warning">An unresolved dispute command is already stored in this browser tab.</Alert>;
  }

  if (error instanceof AllocationDisputeAuthenticationError || error instanceof AllocationDisputeActorRequiredError) {
    return <Alert severity="warning">An operator identity is required before this dispute can be sent.</Alert>;
  }

  if (error instanceof ApiError) {
    return <Alert severity="error">The dispute command was not sent.</Alert>;
  }

  return <Alert severity="error">The dispute command was not verified.</Alert>;
}

function refusalMessage(error: AllocationDisputeRejectedError): string {
  if (error.status === 404 && error.code === 'reconciliation.allocation.not_found') {
    return 'The allocation was not found. This dispute was not recorded.';
  }

  if (error.status === 409 && error.code === 'reconciliation.allocation_dispute.allocation_not_confirmed') {
    return 'The allocation is not confirmed. This dispute was not recorded.';
  }

  if (error.status === 409 && error.code === 'reconciliation.allocation_dispute.idempotency_payload_conflict') {
    return 'This key was already used for a different dispute. Removing local tracking does not undo that dispute.';
  }

  if (error.status === 409 && error.code === 'reconciliation.allocation_dispute.concurrency_conflict') {
    return 'The server did not find a stored dispute for this key. This dispute was not recorded.';
  }

  if (error.status === 413 && error.code === 'reconciliation.allocation.request_too_large') {
    return 'The server rejected the size of this dispute. It was not recorded.';
  }

  if (error.status === 415 && error.code === 'reconciliation.allocation.unsupported_media_type') {
    return 'The server rejected the content type of this dispute. It was not recorded.';
  }

  return 'The server refused this dispute. It was not recorded.';
}

async function replayStoredDispute(
  publisherId: number,
  setReplayed: (result: Bound<VerifiedAllocationDisputeRegistration>) => void,
  setReplayError: (error: Bound<unknown> | null) => void,
  setRefreshFailed: (failed: Bound<boolean> | null) => void,
): Promise<void> {
  const claimed = claimRefresh(publisherId);
  if (claimed === null) {
    return;
  }

  const epoch = sessionStore.getSnapshot().epoch;
  setReplayError(null);
  try {
    const result = await submitAllocationDisputeAttempt();
    if (!publisherCurrent(publisherId, claimed.ticket) || sessionStore.getSnapshot().epoch !== epoch) {
      return;
    }

    setReplayed({ epoch, visit: claimed.visit, value: result });
    void result.refresh.then(
      () => publishRecoveryRefresh(publisherId, claimed.ticket, epoch, claimed.visit, claimed.serial, false, setRefreshFailed),
      () => publishRecoveryRefresh(publisherId, claimed.ticket, epoch, claimed.visit, claimed.serial, true, setRefreshFailed),
    );
  } catch (error) {
    if (!publisherCurrent(publisherId, claimed.ticket) || sessionStore.getSnapshot().epoch !== epoch) {
      return;
    }

    setReplayError({ epoch, visit: claimed.visit, value: error });
  }
}

async function readListsAgain(
  publisherId: number,
  epoch: number,
  publish: (failed: boolean) => void,
): Promise<void> {
  const claimed = claimRefresh(publisherId);
  if (claimed === null) {
    return;
  }

  try {
    await refreshAllocationDisputeLists(epoch);
    if (publisherOwns(publisherId, claimed.ticket, claimed.serial) && sessionStore.getSnapshot().epoch === epoch) {
      publish(false);
    }
  } catch {
    if (publisherOwns(publisherId, claimed.ticket, claimed.serial) && sessionStore.getSnapshot().epoch === epoch) {
      publish(true);
    }
  }
}

function publishRecoveryRefresh(
  publisherId: number,
  ticket: number,
  epoch: number,
  visit: number,
  serial: number,
  failed: boolean,
  setRefreshFailed: (failed: Bound<boolean> | null) => void,
): void {
  if (!publisherOwns(publisherId, ticket, serial) || sessionStore.getSnapshot().epoch !== epoch) {
    return;
  }

  setRefreshFailed({ epoch, visit, value: failed });
}

function removeTrackedDispute(
  publisherId: number,
  setFailed: (failure: unknown | null) => void,
): void {
  const ticket = publisherGeneration(publisherId);
  const epoch = sessionStore.getSnapshot().epoch;
  try {
    retryAllocationDisputeDescriptorRemoval();
    if (publisherGeneration(publisherId) === ticket && sessionStore.getSnapshot().epoch === epoch) {
      setFailed(null);
    }
  } catch (error) {
    if (publisherGeneration(publisherId) !== ticket || sessionStore.getSnapshot().epoch !== epoch) {
      return;
    }

    setFailed(error);
  }
}

function shown<T>(value: Bound<T> | null, epoch: number, visit: number): T | null {
  if (value === null || value.epoch !== epoch || value.visit !== visit) {
    return null;
  }

  return value.value;
}

function sameRegistration(
  result: VerifiedAllocationDisputeRegistration,
  attempt: { allocationId: string; idempotencyKey: string; requestBody: string; normalizedStatement: string },
): boolean {
  return result.allocationId === attempt.allocationId
    && result.idempotencyKey === attempt.idempotencyKey
    && result.requestBody === attempt.requestBody
    && result.normalizedStatement === attempt.normalizedStatement;
}

function removalFailureMessage(error: unknown): string {
  if (error instanceof AllocationDisputeStorageCleanupError) {
    return 'The local record is still here.';
  }

  if (error instanceof AllocationDisputeAttemptChangedError) {
    return 'The stored dispute command changed. Local tracking was left unchanged.';
  }

  if (error instanceof AllocationDisputeAuthenticationError) {
    return 'Sign in before removing local dispute tracking.';
  }

  if (error instanceof AllocationDisputeActorRequiredError) {
    return 'An operator identity is required before local dispute tracking can be removed.';
  }

  if (error instanceof AllocationDisputeSubmissionInProgressError) {
    return 'This dispute command is still being sent. Local tracking was left unchanged.';
  }

  return 'Local tracking could not be removed.';
}

interface Bound<T> {
  epoch: number;
  visit: number;
  value: T;
}

interface ViewPublisher {
  generation: number;
  epoch: number;
  visit: number;
  refreshSerial: number;
}

const viewPublishers = new Map<number, ViewPublisher>();
let nextViewPublisher = 0;

function useViewPublisher(epoch: number, visit: number): number {
  const [id] = useState(() => {
    const publisherId = nextViewPublisher + 1;
    nextViewPublisher = publisherId;
    viewPublishers.set(publisherId, { generation: 0, epoch, visit, refreshSerial: 0 });
    return publisherId;
  });
  const publisher = viewPublishers.get(id) ?? { generation: 0, epoch, visit, refreshSerial: 0 };
  if (!viewPublishers.has(id)) {
    viewPublishers.set(id, publisher);
  }

  if (publisher.epoch !== epoch || publisher.visit !== visit) {
    publisher.generation += 1;
    publisher.epoch = epoch;
    publisher.visit = visit;
  }

  useEffect(() => () => {
    const current = viewPublishers.get(id);
    if (current !== undefined) {
      current.generation += 1;
    }
  }, [id]);

  return id;
}

function claimRefresh(id: number): { ticket: number; serial: number; visit: number } | null {
  const publisher = viewPublishers.get(id);
  if (publisher === undefined) {
    return null;
  }

  publisher.refreshSerial += 1;
  return { ticket: publisher.generation, serial: publisher.refreshSerial, visit: publisher.visit };
}

function publisherGeneration(id: number): number {
  return viewPublishers.get(id)?.generation ?? -1;
}

function publisherCurrent(id: number, ticket: number): boolean {
  return publisherGeneration(id) === ticket;
}

function publisherOwns(id: number, ticket: number, serial: number): boolean {
  const publisher = viewPublishers.get(id);
  return publisher !== undefined && publisher.generation === ticket && publisher.refreshSerial === serial;
}
