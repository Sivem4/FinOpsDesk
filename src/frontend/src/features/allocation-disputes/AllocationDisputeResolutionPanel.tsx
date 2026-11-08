import { zodResolver } from '@hookform/resolvers/zod';
import {
  Alert,
  Button,
  FormControl,
  FormControlLabel,
  FormLabel,
  Radio,
  RadioGroup,
  Stack,
  TextField,
  Typography,
} from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { z } from 'zod';

import { ApiError, NetworkError, StaleSessionError, UnexpectedResponseError } from '../../shared/api/errors';
import { sessionStore } from '../../shared/api/session-store';
import { endSession } from '../../shared/auth/session';
import {
  allocationDisputeResolutionQueryKey,
  isExactGuid,
  normalizeAllocationDisputeResolutionReason,
  type AllocationDisputeResolutionDecision,
  type AllocationDisputeResolutionOutcome,
} from './allocation-dispute-api';
import {
  abandonAllocationDisputeResolutionAttempt,
  abandonUnreadableAllocationDisputeResolutionAttempt,
  AllocationDisputeResolutionActorMismatchError,
  AllocationDisputeResolutionActorRequiredError,
  AllocationDisputeResolutionAttemptBlockedError,
  AllocationDisputeResolutionAttemptChangedError,
  AllocationDisputeResolutionAuthenticationError,
  AllocationDisputeResolutionCommandNotSentError,
  AllocationDisputeResolutionNotFreshError,
  AllocationDisputeResolutionRejectedError,
  AllocationDisputeResolutionRetryNotSentError,
  AllocationDisputeResolutionStorageCleanupError,
  AllocationDisputeResolutionStorageError,
  AllocationDisputeResolutionSubmissionInProgressError,
  AllocationDisputeResolutionTenantMismatchError,
  AllocationDisputeResolutionUncertaintyRemainsError,
  loadAllocationDisputeResolution,
  promotePersistedSendingAllocationDisputeResolution,
  refreshAllocationDisputeResolution,
  rememberedResolutionMatches,
  replayAllocationDisputeResolutionAttempt,
  resolutionAttemptAllowed,
  retryAllocationDisputeResolutionDescriptorRemoval,
  submitAllocationDisputeResolution,
  UnknownAllocationDisputeResolutionOutcomeError,
  UnreadableAllocationDisputeResolutionError,
  UnresolvedAllocationDisputeResolutionError,
  useAllocationDisputeResolutionAttempt,
  useConfirmedAllocationDisputeResolution,
  useRememberedAllocationDisputeResolution,
  type AllocationDisputeResolutionAttempt,
  type VerifiedAllocationDisputeResolution,
} from './allocation-dispute-resolution-attempt';

const resolutionFormSchema = z.object({
  outcome: z.union([z.literal('upheld'), z.literal('not_upheld')]),
  reason: z.string().refine(
    (value) => normalizeAllocationDisputeResolutionReason(value) !== null,
    'Enter a reason of 1 to 500 characters.',
  ),
});

type ResolutionFormValues = z.infer<typeof resolutionFormSchema>;

export function AllocationDisputeResolutionPanel({
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
  const view = useAllocationDisputeResolutionAttempt();
  const remembered = useRememberedAllocationDisputeResolution();
  const confirmed = useConfirmedAllocationDisputeResolution(epoch, disputeId);
  const [published, setPublished] = useState<Bound<VerifiedAllocationDisputeResolution> | null>(null);
  const [refreshWarning, setRefreshWarning] = useState<RefreshWarning | null>(null);
  const [commandError, setCommandError] = useState<Bound<unknown> | null>(null);
  const [cleanupError, setCleanupError] = useState<Bound<string> | null>(null);
  const pageAlive = useRef(true);
  const refreshGeneration = useRef(0);
  const readableId = disputeId !== null && isExactGuid(disputeId) ? disputeId : null;
  const query = useQuery({
    queryKey: allocationDisputeResolutionQueryKey(epoch, readableId ?? '00000000-0000-0000-0000-000000000000'),
    enabled: readableId !== null,
    queryFn: ({ signal }) => {
      if (readableId === null) {
        throw new UnexpectedResponseError('The allocation dispute decision could not be read.');
      }

      return loadAllocationDisputeResolution(readableId, epoch, signal);
    },
  });

  useEffect(() => {
    pageAlive.current = true;
    promotePersistedSendingAllocationDisputeResolution();
    return () => {
      pageAlive.current = false;
    };
  }, [pageAlive]);

  const visibleConfirmation = shown(epoch, visit, published);
  const visibleRefreshWarning = refreshWarning !== null && refreshWarning.epoch === epoch ? refreshWarning : null;
  const visibleCommandError = shown(epoch, visit, commandError);
  const visibleCleanupError = shown(epoch, visit, cleanupError);
  const allowForm = readableId !== null
    && confirmed === null
    && query.isSuccess
    && query.data.decision === null
    && resolutionAttemptAllowed(epoch, readableId);
  const showSection = readableId !== null
    || view.status !== 'none'
    || visibleConfirmation !== null
    || visibleRefreshWarning !== null
    || visibleCommandError !== null
    || visibleCleanupError !== null;

  async function record(outcome: AllocationDisputeResolutionOutcome, reason: string) {
    if (readableId === null) {
      return;
    }

    const ticket = publisherGeneration(publisherId);
    const commandVisit = visit;
    setCommandError(null);
    try {
      const result = await submitAllocationDisputeResolution(readableId, outcome, reason);
      if (publisherCurrent(publisherId, ticket) && visit === commandVisit) {
        setPublished({ epoch, visit: commandVisit, value: result });
      }

      await settleRefresh(result);
    } catch (error) {
      if (publisherCurrent(publisherId, ticket) && visit === commandVisit) {
        setCommandError({ epoch, visit: commandVisit, value: error });
      }
    }
  }

  async function replay() {
    const ticket = publisherGeneration(publisherId);
    const commandVisit = visit;
    setCommandError(null);
    try {
      const result = await replayAllocationDisputeResolutionAttempt();
      if (publisherCurrent(publisherId, ticket) && visit === commandVisit) {
        setPublished({ epoch, visit: commandVisit, value: result });
      }

      await settleRefresh(result);
    } catch (error) {
      if (publisherCurrent(publisherId, ticket) && visit === commandVisit) {
        setCommandError({ epoch, visit: commandVisit, value: error });
      }
    }
  }

  function claimRefresh(): number {
    refreshGeneration.current += 1;
    return refreshGeneration.current;
  }

  function refreshCurrent(generation: number, resultEpoch: number): boolean {
    return pageAlive.current
      && generation === refreshGeneration.current
      && sessionStore.getSnapshot().epoch === resultEpoch;
  }

  async function settleRefresh(result: VerifiedAllocationDisputeResolution) {
    const generation = claimRefresh();
    try {
      await result.refresh;
      finishRefresh(generation, result.epoch, result.disputeId, 'clear');
    } catch {
      finishRefresh(generation, result.epoch, result.disputeId, 'warn');
    }
  }

  function finishRefresh(
    generation: number,
    resultEpoch: number,
    resultDisputeId: string,
    outcome: 'clear' | 'warn',
  ) {
    if (!refreshCurrent(generation, resultEpoch)) {
      return;
    }

    if (outcome === 'clear') {
      setRefreshWarning((current) => (
        current?.epoch === resultEpoch && current.disputeId === resultDisputeId ? null : current
      ));
      return;
    }

    setRefreshWarning({
      epoch: resultEpoch,
      disputeId: resultDisputeId,
    });
  }

  if (!showSection) {
    return null;
  }

  return (
    <Stack spacing={1} component="section" aria-label="Allocation dispute decision">
      {visibleConfirmation === null ? null : (
        <Alert severity="success">
          {visibleConfirmation.created ? 'Decyzja zapisana.' : 'Ta decyzja była już zapisana.'}
          {visibleConfirmation.created ? null : (
            <Typography>
              {`${visibleConfirmation.decision.actorId} ${visibleConfirmation.decision.decidedAt}`}
            </Typography>
          )}
        </Alert>
      )}
      {visibleRefreshWarning === null ? null : (
        <Alert
          severity="warning"
          action={(
            <Button
              color="inherit"
              onClick={() => {
                const target = visibleRefreshWarning;
                const generation = claimRefresh();
                void refreshAllocationDisputeResolution(target.epoch, target.disputeId).then(() => {
                  finishRefresh(generation, target.epoch, target.disputeId, 'clear');
                }).catch(() => {
                  finishRefresh(generation, target.epoch, target.disputeId, 'warn');
                });
              }}
            >
              Read the decision again
            </Button>
          )}
        >
          The decision is stored. The latest read did not finish.
        </Alert>
      )}
      {visibleCommandError === null ? null : <DecisionCommandFailure error={visibleCommandError} />}
      {visibleCleanupError === null ? null : <Alert severity="warning">{visibleCleanupError}</Alert>}
      {readableId === null ? null : (
        <ResolutionRead
          query={query}
          allowForm={allowForm}
          onRecord={record}
        />
      )}
      <ResolutionRecovery
        view={view}
        verifiedTracking={view.status === 'pending' && remembered !== null && rememberedResolutionMatches(view.attempt)}
        onReplay={() => {
          void replay();
        }}
        onRemove={(attempt) => {
          void abandonAllocationDisputeResolutionAttempt(attempt.idempotencyKey, attempt.requestBody).catch((error: unknown) => {
            if (publisherCurrent(publisherId, publisherGeneration(publisherId))) {
              setCleanupError({ epoch, visit, value: commandMessage(error) });
            }
          });
        }}
        onRemoveInvalid={() => {
          try {
            abandonUnreadableAllocationDisputeResolutionAttempt();
          } catch (error) {
            setCleanupError({ epoch, visit, value: commandMessage(error) });
          }
        }}
        onRetryRemoval={() => {
          try {
            retryAllocationDisputeResolutionDescriptorRemoval();
            setCleanupError(null);
          } catch (error) {
            setCleanupError({ epoch, visit, value: commandMessage(error) });
          }
        }}
      />
    </Stack>
  );
}

function ResolutionRead({
  query,
  allowForm,
  onRecord,
}: {
  query: ReturnType<typeof useQuery<{ decision: AllocationDisputeResolutionDecision | null }, Error>>;
  allowForm: boolean;
  onRecord: (outcome: AllocationDisputeResolutionOutcome, reason: string) => Promise<void>;
}) {
  if (query.isError) {
    if (query.error instanceof StaleSessionError) {
      return null;
    }

    return (
      <ResolutionQueryError
        error={query.error}
        onRetry={() => {
          void query.refetch();
        }}
        retrying={query.isFetching}
      />
    );
  }

  if (query.isSuccess && query.data.decision !== null) {
    return <ResolutionFields decision={query.data.decision} />;
  }

  if (!query.isSuccess || query.isFetching) {
    return <Alert severity="info">Loading the allocation dispute decision.</Alert>;
  }

  return (
    <Stack spacing={1}>
      <Alert severity="info">No decision is stored.</Alert>
      {allowForm ? <ResolutionForm onRecord={onRecord} /> : (
        <Typography color="text.secondary">
          Recording a decision needs the operator identity and a current read of no stored decision.
        </Typography>
      )}
    </Stack>
  );
}

function ResolutionFields({ decision }: { decision: AllocationDisputeResolutionDecision }) {
  return (
    <Stack spacing={0.5}>
      <Typography>{`Outcome ${decision.outcome}`}</Typography>
      <Typography sx={{ whiteSpace: 'pre-wrap' }}>{decision.reason}</Typography>
      <Typography>{`Actor source ${decision.actorSource}`}</Typography>
      <Typography sx={{ whiteSpace: 'pre-wrap' }}>{decision.actorId}</Typography>
      <Typography>{decision.decidedAt}</Typography>
    </Stack>
  );
}

function ResolutionForm({
  onRecord,
}: {
  onRecord: (outcome: AllocationDisputeResolutionOutcome, reason: string) => Promise<void>;
}) {
  const [pending, setPending] = useState(false);
  const form = useForm<ResolutionFormValues>({
    resolver: zodResolver(resolutionFormSchema),
    defaultValues: { outcome: 'upheld', reason: '' },
  });

  return (
    <Stack
      spacing={1}
      component="form"
      onSubmit={form.handleSubmit(async (values) => {
        setPending(true);
        try {
          await onRecord(values.outcome, values.reason);
        } finally {
          setPending(false);
        }
      })}
    >
      <Controller
        name="outcome"
        control={form.control}
        render={({ field }) => (
          <FormControl>
            <FormLabel id="allocation-dispute-resolution-outcome">Outcome</FormLabel>
            <RadioGroup
              aria-labelledby="allocation-dispute-resolution-outcome"
              value={field.value}
              onChange={field.onChange}
            >
              <FormControlLabel value="upheld" control={<Radio />} label="Upheld" />
              <FormControlLabel value="not_upheld" control={<Radio />} label="Not upheld" />
            </RadioGroup>
          </FormControl>
        )}
      />
      <Controller
        name="reason"
        control={form.control}
        render={({ field, fieldState }) => (
          <TextField
            label="Reason"
            value={field.value}
            onChange={field.onChange}
            onBlur={field.onBlur}
            error={fieldState.error !== undefined}
            helperText={fieldState.error?.message}
          />
        )}
      />
      <Button type="submit" variant="contained" disabled={pending} sx={{ alignSelf: 'flex-start' }}>
        Record decision
      </Button>
    </Stack>
  );
}

function ResolutionRecovery({
  view,
  verifiedTracking,
  onReplay,
  onRemove,
  onRemoveInvalid,
  onRetryRemoval,
}: {
  view: ReturnType<typeof useAllocationDisputeResolutionAttempt>;
  verifiedTracking: boolean;
  onReplay: () => void;
  onRemove: (attempt: AllocationDisputeResolutionAttempt) => void;
  onRemoveInvalid: () => void;
  onRetryRemoval: () => void;
}) {
  if (view.status === 'blocked-other-actor' || view.status === 'blocked-actor' || view.status === 'blocked-tenant' || view.status === 'blocked-authentication') {
    return <Alert severity="warning">A stored decision command belongs to another operator. Its details stay hidden.</Alert>;
  }

  if (view.status === 'invalid-storage') {
    return (
      <Alert
        severity="warning"
        action={<Button color="inherit" onClick={onRemoveInvalid}>Remove unreadable tracking</Button>}
      >
        The stored decision command can’t be read.
      </Alert>
    );
  }

  if (view.status === 'pending' && verifiedTracking) {
    return (
      <Alert
        severity="warning"
        action={<Button color="inherit" onClick={onRetryRemoval}>Try removing local tracking again</Button>}
      >
        The decision is stored. Local tracking could not be removed.
      </Alert>
    );
  }

  if (view.status !== 'pending') {
    return null;
  }

  const rejected = view.attempt.lifecycle === 'rejected';
  return (
    <Alert
      severity={rejected ? 'warning' : 'info'}
      action={(
        <Stack direction="row" spacing={1}>
          {rejected ? null : (
            <Button color="inherit" onClick={onReplay}>Send the same decision again</Button>
          )}
          <Button color="inherit" onClick={() => onRemove(view.attempt)}>Remove local tracking</Button>
        </Stack>
      )}
    >
      <Typography>
        {rejected
          ? 'This decision was refused. Sending it again is not available.'
          : 'This decision has no verified result. Sending it again uses the same request.'}
      </Typography>
      <Typography>{`Dispute ${view.attempt.disputeId}`}</Typography>
    </Alert>
  );
}

function ResolutionQueryError({
  error,
  onRetry,
  retrying,
}: {
  error: unknown;
  onRetry: () => void;
  retrying: boolean;
}) {
  if (error instanceof ApiError && error.status === 401) {
    return (
      <Alert
        severity="warning"
        action={(
          <Button color="inherit" onClick={() => endSession('unauthorized')}>
            Update access
          </Button>
        )}
      >
        Your access has expired or is no longer valid. Update it to continue.
      </Alert>
    );
  }

  if (error instanceof ApiError && error.status === 403) {
    return <Alert severity="warning">You don’t have access to this allocation dispute decision.</Alert>;
  }

  if (error instanceof ApiError && error.status === 404) {
    return <Alert severity="warning">Ten spór nie został znaleziony.</Alert>;
  }

  if (error instanceof NetworkError) {
    return (
      <Alert severity="error" action={<RetryButton onRetry={onRetry} retrying={retrying} />}>
        We couldn’t connect. Check your connection and try again.
      </Alert>
    );
  }

  if (error instanceof UnexpectedResponseError) {
    return (
      <Alert severity="error" action={<RetryButton onRetry={onRetry} retrying={retrying} />}>
        We couldn’t read this allocation dispute decision.
      </Alert>
    );
  }

  return (
    <Alert severity="error" action={<RetryButton onRetry={onRetry} retrying={retrying} />}>
      The allocation dispute decision could not be loaded.
    </Alert>
  );
}

function RetryButton({ onRetry, retrying }: { onRetry: () => void; retrying: boolean }) {
  return (
    <Button color="inherit" onClick={onRetry} disabled={retrying}>
      Try again
    </Button>
  );
}

function DecisionCommandFailure({ error }: { error: unknown }) {
  if ((error instanceof AllocationDisputeResolutionRejectedError || error instanceof AllocationDisputeResolutionUncertaintyRemainsError) && error.status === 401) {
    return (
      <Alert
        severity="warning"
        action={(
          <Button color="inherit" onClick={() => endSession('unauthorized')}>
            Update access
          </Button>
        )}
      >
        Your access has expired or is no longer valid. Update it to continue.
      </Alert>
    );
  }

  return <Alert severity="warning">{commandMessage(error)}</Alert>;
}

function commandMessage(error: unknown): string {
  if (error instanceof AllocationDisputeResolutionRejectedError) {
    return 'This decision was refused. Sending it again is not available.';
  }

  if (error instanceof AllocationDisputeResolutionUncertaintyRemainsError) {
    return 'This response does not show that the earlier decision command finished.';
  }

  if (error instanceof UnknownAllocationDisputeResolutionOutcomeError || error instanceof UnreadableAllocationDisputeResolutionError) {
    return 'This decision has no verified result. Sending it again uses the same request.';
  }

  if (error instanceof AllocationDisputeResolutionCommandNotSentError) {
    return 'The decision was not sent because it could not be stored first.';
  }

  if (error instanceof AllocationDisputeResolutionRetryNotSentError) {
    return 'The same decision was not sent. Local tracking was left unchanged.';
  }

  if (error instanceof AllocationDisputeResolutionNotFreshError) {
    return 'A new decision waits for a current read of no stored decision.';
  }

  if (error instanceof AllocationDisputeResolutionActorRequiredError) {
    return 'Recording a decision needs the operator identity in this token.';
  }

  if (error instanceof AllocationDisputeResolutionAuthenticationError) {
    return 'Recording a decision needs the current development session.';
  }

  if (error instanceof AllocationDisputeResolutionActorMismatchError || error instanceof AllocationDisputeResolutionTenantMismatchError) {
    return 'A stored decision command belongs to another operator. Its details stay hidden.';
  }

  if (error instanceof AllocationDisputeResolutionStorageCleanupError) {
    return 'The decision is stored. Local tracking could not be removed.';
  }

  if (error instanceof AllocationDisputeResolutionStorageError || error instanceof AllocationDisputeResolutionAttemptBlockedError || error instanceof UnresolvedAllocationDisputeResolutionError || error instanceof AllocationDisputeResolutionAttemptChangedError) {
    return 'The stored decision command could not be used.';
  }

  if (error instanceof AllocationDisputeResolutionSubmissionInProgressError) {
    return 'This decision command is still being sent. Local tracking was left unchanged.';
  }

  if (error instanceof ApiError && error.status === 401) {
    return 'Your access has expired or is no longer valid. Update it to continue.';
  }

  if (error instanceof ApiError && error.status === 403) {
    return 'You don’t have access to record this allocation dispute decision.';
  }

  return 'The allocation dispute decision could not be recorded.';
}

interface Bound<T> {
  epoch: number;
  visit: number;
  value: T;
}

interface RefreshWarning {
  epoch: number;
  disputeId: string;
}

interface ViewPublisher {
  generation: number;
  epoch: number;
  visit: number;
}

const viewPublishers = new Map<number, ViewPublisher>();
let nextViewPublisher = 0;

function useViewPublisher(epoch: number, visit: number): number {
  const [id] = useState(() => {
    const publisherId = nextViewPublisher + 1;
    nextViewPublisher = publisherId;
    viewPublishers.set(publisherId, { generation: 0, epoch, visit });
    return publisherId;
  });
  const publisher = viewPublishers.get(id) ?? { generation: 0, epoch, visit };
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

function publisherGeneration(id: number): number {
  return viewPublishers.get(id)?.generation ?? -1;
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
