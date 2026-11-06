import { Alert, Button, Stack, Typography } from '@mui/material';
import { useQueryClient, type QueryClient } from '@tanstack/react-query';
import { useEffect, useLayoutEffect, useRef, useState } from 'react';

import { sessionStore } from '../../shared/api/session-store';
import { useSession } from '../../shared/auth/session';
import {
  type ReviewCaseAssignmentRead,
} from './review-case-assignment-api';
import {
  abandonReviewCaseAssignmentAttempt,
  abandonUnreadableReviewCaseAssignmentAttempt,
  beginReviewCaseAssignmentAttempt,
  reviewCaseAssignmentAction,
  ReviewCaseAssignmentActorMismatchError,
  ReviewCaseAssignmentActorRequiredError,
  ReviewCaseAssignmentAttemptBlockedError,
  ReviewCaseAssignmentAttemptChangedError,
  ReviewCaseAssignmentAuthenticationError,
  ReviewCaseAssignmentCommandNotSentError,
  ReviewCaseAssignmentConcurrencyUncheckedError,
  ReviewCaseAssignmentNotReadyError,
  ReviewCaseAssignmentRejectedError,
  ReviewCaseAssignmentRetryNotSentError,
  ReviewCaseAssignmentStorageCleanupError,
  ReviewCaseAssignmentStorageError,
  ReviewCaseAssignmentSubmissionInProgressError,
  ReviewCaseAssignmentTenantMismatchError,
  ReviewCaseAssignmentUncertaintyRemainsError,
  submitReviewCaseAssignmentAttempt,
  UnknownReviewCaseAssignmentOutcomeError,
  UnreadableReviewCaseAssignmentError,
  UnresolvedReviewCaseAssignmentError,
  useReviewCaseAssignmentAttempt,
  type ReviewCaseAssignmentAttempt,
  type ReviewCaseAssignmentClick,
  type VerifiedReviewCaseAssignment,
} from './review-case-assignment-attempt';
import { type ReviewCaseCommandContext, type ReviewCaseDetailRead, type ReviewCaseStatusName } from './review-case-api';
import { developmentActorId, developmentOrganizationId } from './review-case-open-request';

export type AssignmentResultPublisher = (
  result: VerifiedReviewCaseAssignment,
  selectionGeneration: number,
) => Promise<void>;

export function ReviewCaseAssignmentRecovery({
  selectedCaseId = null,
  readSelectionGeneration = () => 0,
  registerPublisher = () => undefined,
}: {
  selectedCaseId?: string | null;
  readSelectionGeneration?: () => number;
  registerPublisher?: (publish: AssignmentResultPublisher | null) => void;
}) {
  const view = useReviewCaseAssignmentAttempt();
  const session = useSession();
  const queryClient = useQueryClient();
  const viewMounted = useRef(false);
  const publishResult = useRef<AssignmentResultPublisher | null>(null);
  const generationReader = useRef(readSelectionGeneration);
  useLayoutEffect(() => {
    generationReader.current = readSelectionGeneration;
  }, [readSelectionGeneration]);
  const [error, setError] = useState<unknown>(null);
  const [verified, setVerified] = useState<VerifiedReviewCaseAssignment | null>(null);
  const [refreshFailed, setRefreshFailed] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [viewEpoch, setViewEpoch] = useState(session.epoch);
  const [visibleSelection, setVisibleSelection] = useState(selectedCaseId);
  if (viewEpoch !== session.epoch) {
    setViewEpoch(session.epoch);
    setVerified(null);
    setError(null);
    setRefreshFailed(false);
    setRefreshing(false);
  }

  if (visibleSelection !== selectedCaseId) {
    setVisibleSelection(selectedCaseId);
    setVerified(null);
    setError(null);
    setRefreshFailed(false);
    setRefreshing(false);
  }

  function stillCurrent(selectionGeneration: number): boolean {
    return viewMounted.current
      && sessionStore.getSnapshot().epoch === viewEpoch
      && readSelectionGeneration() === selectionGeneration;
  }

  useEffect(() => {
    let active = true;
    const publish: AssignmentResultPublisher = async (result, selectionGeneration) => {
      const current = () => active
        && viewMounted.current
        && sessionStore.getSnapshot().epoch === viewEpoch
        && generationReader.current() === selectionGeneration
        && result.epoch === viewEpoch;
      if (!current()) {
        return;
      }

      await publishVerifiedResult(
        queryClient,
        result,
        current,
        (next) => {
          setVerified(next);
          setError(next.cleanup === 'failed' ? new ReviewCaseAssignmentStorageCleanupError() : null);
        },
        setRefreshFailed,
      );
    };
    publishResult.current = publish;
    registerPublisher(publish);
    return () => {
      active = false;
      publishResult.current = null;
      registerPublisher(null);
    };
  }, [queryClient, registerPublisher, viewEpoch]);

  function publishError(next: unknown): void {
    if (!viewMounted.current || sessionStore.getSnapshot().epoch !== viewEpoch) {
      return;
    }

    setError(next);
  }

  useEffect(() => {
    viewMounted.current = true;
    return () => {
      viewMounted.current = false;
    };
  }, []);

  function retryReading(result: VerifiedReviewCaseAssignment): void {
    const selectionGeneration = readSelectionGeneration();
    const epoch = result.epoch;
    setRefreshing(true);
    void refreshReviewCaseAssignmentReads(queryClient, epoch, result.caseId)
      .then(() => {
        if (stillCurrent(selectionGeneration)) {
          setRefreshFailed(false);
        }
      })
      .catch(() => {
        if (stillCurrent(selectionGeneration)) {
          setRefreshFailed(true);
        }
      })
      .finally(() => {
        if (stillCurrent(selectionGeneration)) {
          setRefreshing(false);
        }
      });
  }

  if (view.status === 'blocked-authentication' || (view.status === 'none' && verified === null && error === null)) {
    return null;
  }

  const canSend = developmentOrganizationId(session.token) !== null && developmentActorId(session.token) !== null;

  return (
    <Stack spacing={1} component="section" aria-label="Review case assignment recovery">
      <Typography color="text.secondary">
        An assignment command in this tab follows the organization and the development operator. Another operator can read the assignment and cannot send, retry, or remove that command.
      </Typography>
      {view.status === 'invalid-storage' ? <UnreadableStorage onClear={() => publishError(null)} onError={publishError} /> : null}
      {view.status === 'blocked-tenant' ? (
        <Alert severity="warning">A stored assignment command belongs to another organization in this tab. It was not replaced and it was not sent.</Alert>
      ) : null}
      {view.status === 'blocked-actor' ? (
        <Alert severity="warning">Sign in with an operator identity before replaying a stored assignment command. The stored command was not sent.</Alert>
      ) : null}
      {view.status === 'blocked-other-actor' ? (
        <Alert severity="warning">This tab holds an assignment command for another operator. It was not sent or replaced.</Alert>
      ) : null}
      {view.status === 'pending' ? (
        <StoredAssignment
          attempt={view.attempt}
          canSend={canSend && view.attempt.actorId === developmentActorId(session.token)}
          readSelectionGeneration={readSelectionGeneration}
          onError={publishError}
          onReplayError={(next, selectionGeneration) => {
            if (stillCurrent(selectionGeneration)) {
              publishError(next);
            }
          }}
          onReplayConfirmed={(result, selectionGeneration) => {
            const publish = publishResult.current;
            if (publish !== null) {
              void publish(result, selectionGeneration);
            }
          }}
        />
      ) : null}
      {verified !== null ? <VerifiedAssignment result={verified} /> : null}
      {refreshFailed && verified !== null ? (
        <Alert
          severity="warning"
          action={(
            <Button color="inherit" disabled={refreshing} onClick={() => retryReading(verified)}>
              Retry reading the case and assignment
            </Button>
          )}
        >
          The command is confirmed. The case and assignment could not be refreshed.
        </Alert>
      ) : null}
      {error !== null ? <Alert severity="warning">{failureMessage(error)}</Alert> : null}
    </Stack>
  );
}

export function ReviewCaseAssignmentSection({
  reviewCaseId,
  status,
  detailCommand,
  assignment,
  assignmentPending,
  assignmentFailed,
  readSelectionGeneration,
  readAssignmentPublisher,
}: {
  reviewCaseId: string;
  status: ReviewCaseStatusName;
  detailCommand: ReviewCaseCommandContext | null;
  assignment: ReviewCaseAssignmentRead | null;
  assignmentPending: boolean;
  assignmentFailed: boolean;
  readSelectionGeneration: () => number;
  readAssignmentPublisher: () => AssignmentResultPublisher | null;
}) {
  const session = useSession();
  const queryClient = useQueryClient();
  const view = useReviewCaseAssignmentAttempt();
  const mounted = useRef(false);
  const [sending, setSending] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [viewEpoch, setViewEpoch] = useState(session.epoch);
  if (viewEpoch !== session.epoch) {
    setViewEpoch(session.epoch);
    setError(null);
    setSending(false);
    setRefreshing(false);
  }

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const click = currentClick(reviewCaseId, status, detailCommand, assignment);
  const action = reviewCaseAssignmentAction(session, click);
  const actorReady = developmentOrganizationId(session.token) !== null && developmentActorId(session.token) !== null;

  function senderStillVisible(epoch: number, caseAtSend: string): boolean {
    return mounted.current
      && caseAtSend === reviewCaseId
      && sessionStore.getSnapshot().epoch === epoch;
  }

  function send(operation: 'claim' | 'release'): void {
    if (sending) {
      return;
    }

    setSending(true);
    setError(null);
    const epoch = sessionStore.getSnapshot().epoch;
    const snapshot = clickFromQueries(queryClient, reviewCaseId, epoch);
    try {
      if (snapshot === null) {
        throw new ReviewCaseAssignmentNotReadyError();
      }

      beginReviewCaseAssignmentAttempt(operation, snapshot);
    } catch (caught) {
      setSending(false);
      setError(caught);
      return;
    }

    const selectionGeneration = readSelectionGeneration();
    const publish = readAssignmentPublisher();
    void submitReviewCaseAssignmentAttempt()
      .then(async (result) => {
        if (publish !== null) {
          await publish(result, selectionGeneration);
        }
      })
      .catch((caught: unknown) => {
        if (senderStillVisible(epoch, reviewCaseId)) {
          setError(caught);
        }
      })
      .finally(() => {
        if (senderStillVisible(epoch, reviewCaseId)) {
          setSending(false);
        }
      });
  }

  function refresh(): void {
    setRefreshing(true);
    setError(null);
    void refreshReviewCaseAssignmentReads(queryClient, session.epoch, reviewCaseId)
      .catch((caught: unknown) => {
        if (mounted.current) {
          setError(caught);
        }
      })
      .finally(() => {
        if (mounted.current) {
          setRefreshing(false);
        }
      });
  }

  return (
    <Stack spacing={1} component="section" aria-label="Review case assignment" sx={{ mt: 2 }}>
      <Typography variant="h6" component="h3">Current assignment</Typography>
      {assignmentPending ? <Typography role="status">Loading the assignment</Typography> : null}
      {assignmentFailed ? <Alert severity="warning">The assignment could not be read.</Alert> : null}
      {assignment !== null ? <CurrentAssignment status={status} assignment={assignment} /> : null}
      {status !== 'Open' && assignment !== null ? (
        <Typography>This closed case keeps the stored assignment.</Typography>
      ) : null}
      {status === 'Open' && assignment !== null && action === 'none' ? (
        <Typography color="text.secondary">{blockedReason(view.status, assignment, detailCommand, actorReady, developmentActorId(session.token))}</Typography>
      ) : null}
      {view.status === 'blocked-other-actor' ? (
        <Alert severity="warning">This tab holds an assignment command for another operator. It was not sent or replaced.</Alert>
      ) : null}
      {action === 'claim' ? (
        <Button type="button" disabled={sending} onClick={() => send('claim')}>Claim this case</Button>
      ) : null}
      {action === 'release' ? (
        <Button type="button" disabled={sending} onClick={() => send('release')}>Release this assignment</Button>
      ) : null}
      <Button type="button" disabled={refreshing || sending} onClick={refresh}>Refresh the case and assignment</Button>
      {error !== null ? <Alert severity="warning">{failureMessage(error)}</Alert> : null}
    </Stack>
  );
}

function CurrentAssignment({
  status,
  assignment,
}: {
  status: ReviewCaseStatusName;
  assignment: ReviewCaseAssignmentRead;
}) {
  const assignee = assignment.state.assignee;
  if (assignee === null) {
    return <Typography>{status === 'Open' ? 'No operator is assigned.' : 'No operator was assigned.'}</Typography>;
  }

  return (
    <Typography>
      {`Assigned to ${assignee.actorSource} ${assignee.actorId} at ${assignee.assignedAt}.`}
    </Typography>
  );
}

function StoredAssignment({
  attempt,
  canSend,
  readSelectionGeneration,
  onError,
  onReplayError,
  onReplayConfirmed,
}: {
  attempt: ReviewCaseAssignmentAttempt;
  canSend: boolean;
  readSelectionGeneration: () => number;
  onError: (error: unknown) => void;
  onReplayError: (error: unknown, selectionGeneration: number) => void;
  onReplayConfirmed: (result: VerifiedReviewCaseAssignment, selectionGeneration: number) => void;
}) {
  const [confirming, setConfirming] = useState(false);
  const operation = attempt.operation === 'ReviewCaseClaim' ? 'Claim' : 'Release';
  const replayLabel = attempt.lifecycle === 'ready'
    ? `Send the stored ${operation.toLowerCase()}`
    : `Send the same ${operation.toLowerCase()} again`;

  return (
    <Stack spacing={1}>
      <Typography>{`${operation} for case ${attempt.caseId}`}</Typography>
      {attempt.lifecycle === 'sending' || attempt.lifecycle === 'uncertain' ? (
        <Alert severity="warning">This assignment command has no verified result. Sending it again uses the same request.</Alert>
      ) : null}
      {attempt.lifecycle === 'rejected' ? (
        <Alert severity="warning">
          {attempt.uncertaintyLocked
            ? 'This assignment command was refused. Sending it again is turned off. A later refusal did not clear the unresolved assignment command.'
            : 'This assignment command was refused. Sending it again is turned off.'}
        </Alert>
      ) : null}
      {attempt.lifecycle !== 'rejected' ? (
        <Button
          type="button"
          disabled={!canSend}
          onClick={() => {
            const selectionGeneration = readSelectionGeneration();
            void submitReviewCaseAssignmentAttempt()
              .then((result) => onReplayConfirmed(result, selectionGeneration))
              .catch((caught: unknown) => onReplayError(caught, selectionGeneration));
          }}
        >
          {replayLabel}
        </Button>
      ) : null}
      {confirming ? (
        <Stack direction="row" spacing={1}>
          <Button
            type="button"
            color="warning"
            onClick={() => {
              try {
                abandonReviewCaseAssignmentAttempt(attempt.idempotencyKey, attempt.requestBody);
                setConfirming(false);
              } catch (caught) {
                onError(caught);
              }
            }}
          >
            Remove assignment tracking now
          </Button>
          <Button type="button" onClick={() => setConfirming(false)}>Keep assignment tracking</Button>
        </Stack>
      ) : (
        <Button type="button" onClick={() => setConfirming(true)}>Remove assignment tracking</Button>
      )}
      {confirming ? (
        <Typography color="text.secondary">
          Removing local tracking does not undo a claim or release that the server already stored.
        </Typography>
      ) : null}
    </Stack>
  );
}

function VerifiedAssignment({ result }: { result: VerifiedReviewCaseAssignment }) {
  const stored = result.created
    ? `This ${result.operation} was stored.`
    : `This ${result.operation} was already stored.`;
  const assignee = result.assignee === null
    ? 'The command released the assignment.'
    : `Historical assignee ${result.assignee.actorSource} ${result.assignee.actorId} at ${result.assignee.assignedAt}.`;

  return (
    <Alert severity="success">
      <Typography>{stored}</Typography>
      <Typography>{`Case ${result.caseId}`}</Typography>
      <Typography>{assignee}</Typography>
      <Typography>{`Recorded ${result.recordedAt}`}</Typography>
      <Typography>This result confirms the command. It is separate from the current assignment.</Typography>
    </Alert>
  );
}

function UnreadableStorage({
  onClear,
  onError,
}: {
  onClear: () => void;
  onError: (error: unknown) => void;
}) {
  return (
    <Alert
      severity="warning"
      action={(
        <Button
          color="inherit"
          onClick={() => {
            try {
              abandonUnreadableReviewCaseAssignmentAttempt();
              onClear();
            } catch (caught) {
              onError(caught);
            }
          }}
        >
          Remove unreadable assignment tracking
        </Button>
      )}
    >
      The stored assignment command could not be read. It was not sent.
    </Alert>
  );
}

function clickFromQueries(
  client: QueryClient,
  reviewCaseId: string,
  epoch: number,
): ReviewCaseAssignmentClick | null {
  const detailState = client.getQueryState<ReviewCaseDetailRead>([
    'review-cases',
    'detail',
    epoch,
    reviewCaseId,
  ]);
  const assignmentState = client.getQueryState<ReviewCaseAssignmentRead>([
    'review-cases',
    'assignment',
    epoch,
    reviewCaseId,
  ]);
  if (detailState?.status !== 'success' || detailState.data === undefined) {
    return null;
  }

  if (assignmentState?.status !== 'success' || assignmentState.data === undefined) {
    return null;
  }

  return currentClick(
    reviewCaseId,
    detailState.data.detail.reviewCase.status,
    detailState.data.command,
    assignmentState.data,
  );
}

async function publishVerifiedResult(
  client: QueryClient,
  result: VerifiedReviewCaseAssignment,
  canShow: () => boolean,
  show: (result: VerifiedReviewCaseAssignment) => void,
  setRefreshFailed: (failed: boolean) => void,
): Promise<void> {
  if (!canShow()) {
    return;
  }

  show(result);
  setRefreshFailed(false);
  try {
    await refreshReviewCaseAssignmentReads(client, result.epoch, result.caseId);
    if (!canShow()) {
      return;
    }

    setRefreshFailed(false);
  } catch {
    if (!canShow()) {
      return;
    }

    setRefreshFailed(true);
  }
}

function currentClick(
  reviewCaseId: string,
  status: ReviewCaseStatusName,
  detailCommand: ReviewCaseCommandContext | null,
  assignment: ReviewCaseAssignmentRead | null,
): ReviewCaseAssignmentClick | null {
  if (detailCommand === null || assignment === null) {
    return null;
  }

  if (detailCommand.caseId !== reviewCaseId || assignment.state.reviewCaseId !== reviewCaseId || assignment.epoch !== detailCommand.epoch) {
    return null;
  }

  return {
    caseId: reviewCaseId,
    epoch: detailCommand.epoch,
    status,
    detailToken: detailCommand.concurrencyToken,
    detailSequence: detailCommand.readSequence,
    assignmentCaseId: assignment.state.reviewCaseId,
    assignmentEpoch: assignment.epoch,
    assignmentToken: assignment.state.concurrencyToken,
    assignmentSequence: assignment.readSequence,
    assignee: assignment.state.assignee,
  };
}

function blockedReason(
  viewStatus: ReturnType<typeof useReviewCaseAssignmentAttempt>['status'],
  assignment: ReviewCaseAssignmentRead,
  detailCommand: ReviewCaseCommandContext | null,
  actorReady: boolean,
  actorId: string | null,
): string {
  if (!actorReady || viewStatus === 'blocked-actor') {
    return 'Sign in with an operator identity before claiming or releasing a case.';
  }

  if (viewStatus === 'pending' || viewStatus === 'invalid-storage' || viewStatus === 'blocked-tenant' || viewStatus === 'blocked-other-actor') {
    return 'A stored assignment command is still in this tab. Finish or remove that tracking before starting another command.';
  }

  if (detailCommand === null
    || detailCommand.concurrencyToken !== assignment.state.concurrencyToken
    || detailCommand.caseId !== assignment.state.reviewCaseId
    || detailCommand.epoch !== assignment.epoch) {
    return 'The case and its assignment were read as different versions. Refresh before claiming or releasing.';
  }

  const assignee = assignment.state.assignee;
  const mine = assignee !== null
    && actorId !== null
    && assignee.actorSource === 'developmentJwt'
    && assignee.actorId === actorId;
  if (assignee !== null && !mine) {
    return 'This case is assigned to another operator.';
  }

  return 'Claim and release stay off until this case and its assignment are both read again.';
}

async function refreshReviewCaseAssignmentReads(
  queryClient: QueryClient,
  epoch: number,
  caseId: string,
): Promise<void> {
  if (sessionStore.getSnapshot().epoch !== epoch) {
    return;
  }

  await Promise.all([
    queryClient.invalidateQueries({
      queryKey: ['review-cases', 'detail', epoch, caseId],
    }, { throwOnError: true, cancelRefetch: true }),
    queryClient.invalidateQueries({
      queryKey: ['review-cases', 'assignment', epoch, caseId],
    }, { throwOnError: true, cancelRefetch: true }),
  ]);
}

function failureMessage(error: unknown): string {
  if (error instanceof ReviewCaseAssignmentCommandNotSentError) {
    return 'The assignment command was not sent.';
  }

  if (error instanceof ReviewCaseAssignmentRetryNotSentError) {
    return 'The assignment retry was not sent.';
  }

  if (error instanceof ReviewCaseAssignmentStorageError || error instanceof ReviewCaseAssignmentAttemptBlockedError || error instanceof UnresolvedReviewCaseAssignmentError) {
    return 'The assignment command was not stored, so it was not sent. An existing stored command was not replaced.';
  }

  if (error instanceof ReviewCaseAssignmentStorageCleanupError) {
    return 'The server confirmed this assignment command. This browser could not remove the local recovery record. The original key was not replaced, and the command was not rolled back.';
  }

  if (error instanceof ReviewCaseAssignmentAttemptChangedError) {
    return 'The stored assignment command changed before the response could be applied. The changed record was not removed.';
  }

  if (error instanceof ReviewCaseAssignmentAuthenticationError || error instanceof ReviewCaseAssignmentActorRequiredError) {
    return 'This assignment command needs a current development token and an operator identity. The request was not sent.';
  }

  if (error instanceof ReviewCaseAssignmentActorMismatchError) {
    return 'The stored assignment command belongs to another operator. It was not sent or replaced.';
  }

  if (error instanceof ReviewCaseAssignmentTenantMismatchError) {
    return 'The stored assignment command belongs to another organization. It was not sent.';
  }

  if (error instanceof ReviewCaseAssignmentNotReadyError) {
    return 'Claim and release need a fresh matching read of this case. The command was not sent.';
  }

  if (error instanceof ReviewCaseAssignmentSubmissionInProgressError) {
    return 'This assignment command is still being sent. Wait until that request finishes before removing local tracking or starting another one.';
  }

  if (error instanceof ReviewCaseAssignmentConcurrencyUncheckedError) {
    return 'The server did not find an entry for this key at the time of the check.';
  }

  if (error instanceof ReviewCaseAssignmentRejectedError) {
    if (error.code === 'reconciliation.review_case_assignment.idempotency_payload_conflict') {
      return 'This key was already used for a different assignment command. Sending it again is turned off.';
    }

    if (error.code === 'reconciliation.review_case_assignment.concurrency_conflict') {
      return 'The server did not find an entry for this key when it checked. This command was not stored.';
    }

    return 'The server refused this assignment command. Remove local tracking before starting another one.';
  }

  if (error instanceof ReviewCaseAssignmentUncertaintyRemainsError) {
    return 'A later refusal did not clear the unresolved assignment command.';
  }

  if (error instanceof UnknownReviewCaseAssignmentOutcomeError || error instanceof UnreadableReviewCaseAssignmentError) {
    return 'The assignment command was not verified. A lost response does not show that the server rolled it back. Send the same command again.';
  }

  return 'The assignment command could not be completed.';
}
