import { zodResolver } from '@hookform/resolvers/zod';
import { Alert, Button, MenuItem, Stack, TextField, Typography } from '@mui/material';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';

import { ApiError, StaleSessionError } from '../../shared/api/errors';
import { sessionStore } from '../../shared/api/session-store';
import { useSession } from '../../shared/auth/session';
import {
  type ReviewCaseCommandContext,
  type ReviewCaseStatusName,
} from './review-case-api';
import {
  abandonReviewCaseDecisionAttempt,
  decisionCommandAllowsDraft,
  abandonUnreadableReviewCaseDecisionAttempt,
  beginReviewCaseDecisionAttempt,
  ReviewCaseDecisionActorRequiredError,
  ReviewCaseDecisionAttemptBlockedError,
  ReviewCaseDecisionAttemptChangedError,
  ReviewCaseDecisionAuthenticationError,
  ReviewCaseDecisionConflictError,
  ReviewCaseDecisionRefusedError,
  ReviewCaseDecisionStorageCleanupError,
  ReviewCaseDecisionStorageError,
  ReviewCaseDecisionSubmissionInProgressError,
  ReviewCaseDecisionTenantMismatchError,
  ReviewCaseDecisionUncertaintyRemainsError,
  submitReviewCaseDecisionAttempt,
  UnknownReviewCaseDecisionOutcomeError,
  UnreadableReviewCaseDecisionError,
  UnresolvedReviewCaseDecisionError,
  useDecisionReadBoundary,
  useReviewCaseDecisionAttempt,
  type ReviewCaseDecisionAttempt,
  type VerifiedReviewCaseDecision,
} from './review-case-decision-attempt';
import {
  normalizeReviewCaseDismiss,
  normalizeReviewCaseResolve,
  reviewCaseDecisionOutcomes,
  reviewCaseDismissSchema,
  reviewCaseResolveSchema,
  type ReviewCaseDismissFormValues,
  type ReviewCaseResolveFormValues,
} from './review-case-decision-request';
import { developmentActorId, developmentOrganizationId } from './review-case-open-request';
import { startReviewCaseSummaryRefresh } from './review-case-summary-api';

const emptyResolve: ReviewCaseResolveFormValues = {
  outcome: 'NoActionRequired',
  allocationId: '',
  resolutionNote: '',
};

const emptyDismiss: ReviewCaseDismissFormValues = {
  resolutionNote: '',
};

interface DecisionDraft {
  operation: 'ReviewCaseResolve' | 'ReviewCaseDismiss';
  command: ReviewCaseCommandContext;
}

export function ReviewCaseDecisionPanel({
  selectedCaseId,
  caseStatus,
  command,
  onReadAgain,
}: {
  selectedCaseId: string | null;
  caseStatus: ReviewCaseStatusName | null;
  command: ReviewCaseCommandContext | null;
  onReadAgain: () => Promise<void>;
}) {
  const view = useReviewCaseDecisionAttempt();
  const boundary = useDecisionReadBoundary();
  const session = useSession();
  const queryClient = useQueryClient();
  const onReadAgainRef = useRef(onReadAgain);
  const ensuredRead = useRef<string | null>(null);
  const resultGeneration = useRef(0);
  const viewMounted = useRef(false);
  const [draft, setDraft] = useState<DecisionDraft | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [verified, setVerified] = useState<VerifiedReviewCaseDecision | null>(null);
  const [refreshFailed, setRefreshFailed] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [readingAgain, setReadingAgain] = useState(false);
  useEffect(() => {
    viewMounted.current = true;
    return () => {
      viewMounted.current = false;
    };
  }, []);
  useEffect(() => {
    onReadAgainRef.current = onReadAgain;
  }, [onReadAgain]);
  const draftMatches = draft !== null
    && draft.command.caseId === selectedCaseId
    && draft.command.epoch === session.epoch;
  if (draft !== null && !draftMatches) {
    setDraft(null);
  }

  const visibleDraft = draftMatches ? draft : null;
  useEffect(() => {
    if (boundary === 0 || selectedCaseId === null) {
      return;
    }

    const mark = `${boundary}:${session.epoch}:${selectedCaseId}`;
    if (ensuredRead.current === mark || decisionCommandAllowsDraft(command, session.epoch, selectedCaseId)) {
      ensuredRead.current = mark;
      return;
    }

    ensuredRead.current = mark;
    void onReadAgainRef.current().catch((readError: unknown) => {
      if (viewMounted.current) {
        setError(readError);
      }
    });
  }, [boundary, command, selectedCaseId, session.epoch]);

  const canDecide = developmentOrganizationId(session.token) !== null && developmentActorId(session.token) !== null;
  if (view.status === 'blocked-authentication' || (view.status === 'none' && !canDecide)) {
    return null;
  }

  const draftReady = visibleDraft !== null && caseStatus === 'Open';
  const canStart = view.status === 'none'
    && caseStatus === 'Open'
    && selectedCaseId !== null
    && decisionCommandAllowsDraft(command, session.epoch, selectedCaseId);
  const needsFreshRead = view.status === 'none'
    && boundary > 0
    && selectedCaseId !== null
    && caseStatus === 'Open'
    && !decisionCommandAllowsDraft(command, session.epoch, selectedCaseId);

  function currentEpochMatches(epoch: number): boolean {
    return sessionStore.getSnapshot().epoch === epoch;
  }

  function ownsResult(epoch: number, generation: number): boolean {
    return viewMounted.current && currentEpochMatches(epoch) && resultGeneration.current === generation;
  }

  async function refresh(epoch: number): Promise<void> {
    if (!currentEpochMatches(epoch)) {
      return;
    }

    startReviewCaseSummaryRefresh(queryClient, epoch);
    const options = { throwOnError: true } as const;
    await queryClient.invalidateQueries({ queryKey: ['review-cases', 'list', epoch] }, options);
    await queryClient.invalidateQueries({ queryKey: ['review-cases', 'detail', epoch] }, options);
    await queryClient.invalidateQueries({ queryKey: ['review-cases', 'audit', epoch] }, options);
  }

  function acceptResult(result: VerifiedReviewCaseDecision): number | null {
    if (!currentEpochMatches(result.epoch)) {
      setError(new StaleSessionError());
      return null;
    }

    resultGeneration.current += 1;
    setVerified(result);
    setError(result.cleanup === 'failed' ? new ReviewCaseDecisionStorageCleanupError() : null);
    setRefreshFailed(false);
    return resultGeneration.current;
  }

  async function applyVerified(result: VerifiedReviewCaseDecision): Promise<void> {
    if (!viewMounted.current) {
      return;
    }

    const generation = acceptResult(result);
    if (generation === null) {
      return;
    }

    try {
      await refresh(result.epoch);
      if (!ownsResult(result.epoch, generation)) {
        return;
      }

      setRefreshFailed(false);
    } catch {
      if (!ownsResult(result.epoch, generation)) {
        return;
      }

      setRefreshFailed(true);
    }
  }

  function retryDisplayedData(result: VerifiedReviewCaseDecision): void {
    const generation = resultGeneration.current;
    const epoch = result.epoch;
    setRefreshing(true);
    void refresh(epoch)
      .then(() => {
        if (!ownsResult(epoch, generation)) {
          return;
        }

        setRefreshFailed(false);
      })
      .catch(() => {
        if (!ownsResult(epoch, generation)) {
          return;
        }

        setRefreshFailed(true);
      })
      .finally(() => {
        if (viewMounted.current) {
          setRefreshing(false);
        }
      });
  }

  return (
    <Stack spacing={1} component="section" aria-label="Decide a review case">
      <Typography color="text.secondary">
        Two people using this organization in the same tab can see one stored decision. The record follows the organization id and does not identify the operator.
      </Typography>
      {view.status === 'invalid-storage' ? <UnreadableStorage onClear={() => setError(null)} onError={setError} /> : null}
      {view.status === 'blocked-tenant' ? (
        <Alert severity="warning">A stored decision belongs to another organization in this tab. It was not replaced and it was not sent.</Alert>
      ) : null}
      {view.status === 'pending' ? (
        <StoredDecision
          attempt={view.attempt}
          onVerified={(result) => void applyVerified(result)}
          onError={setError}
        />
      ) : null}
      {canStart && visibleDraft === null ? (
        <Stack direction="row" spacing={1}>
          <Button type="button" onClick={() => {
            if (command !== null) {
              setDraft({ operation: 'ReviewCaseResolve', command });
            }
          }}
          >
            Resolve
          </Button>
          <Button type="button" onClick={() => {
            if (command !== null) {
              setDraft({ operation: 'ReviewCaseDismiss', command });
            }
          }}
          >
            Dismiss
          </Button>
        </Stack>
      ) : null}
      {needsFreshRead ? (
        <Alert
          severity="info"
          action={(
            <Button
              color="inherit"
              disabled={readingAgain}
              onClick={() => {
                setReadingAgain(true);
                void onReadAgain()
                  .catch((readError: unknown) => setError(readError))
                  .finally(() => setReadingAgain(false));
              }}
            >
              Read this case again
            </Button>
          )}
        >
          Read this case again before starting another decision.
        </Alert>
      ) : null}
      {draftReady && visibleDraft.operation === 'ReviewCaseResolve' ? (
        <ResolveForm
          draft={visibleDraft}
          onVerified={(result) => {
            setDraft(null);
            void applyVerified(result);
          }}
          onError={setError}
          onStarted={() => setDraft(null)}
        />
      ) : null}
      {draftReady && visibleDraft.operation === 'ReviewCaseDismiss' ? (
        <DismissForm
          draft={visibleDraft}
          onVerified={(result) => {
            setDraft(null);
            void applyVerified(result);
          }}
          onError={setError}
          onStarted={() => setDraft(null)}
        />
      ) : null}
      {verified !== null ? <VerifiedDecision result={verified} /> : null}
      {refreshFailed && verified !== null ? (
        <Alert
          severity="warning"
          action={(
            <Button color="inherit" disabled={refreshing} onClick={() => retryDisplayedData(verified)}>
              Refresh displayed data
            </Button>
          )}
        >
          The decision is confirmed. The review case lists could not be refreshed.
        </Alert>
      ) : null}
      {error !== null ? <Alert severity="warning">{failureMessage(error)}</Alert> : null}
    </Stack>
  );
}

function ResolveForm({
  draft,
  onVerified,
  onError,
  onStarted,
}: {
  draft: DecisionDraft;
  onVerified: (result: VerifiedReviewCaseDecision) => void;
  onError: (error: unknown) => void;
  onStarted: () => void;
}) {
  const [sending, setSending] = useState(false);
  const form = useForm<ReviewCaseResolveFormValues>({
    resolver: zodResolver(reviewCaseResolveSchema),
    defaultValues: emptyResolve,
  });
  const outcome = useWatch({ control: form.control, name: 'outcome' });

  return (
    <Stack
      spacing={1}
      component="form"
      onSubmit={form.handleSubmit(async (values) => {
        setSending(true);
        onError(null);
        try {
          beginReviewCaseDecisionAttempt(normalizeReviewCaseResolve(
            draft.command.caseId,
            draft.command.concurrencyToken,
            values,
          ));
          onStarted();
          onVerified(await submitReviewCaseDecisionAttempt());
        } catch (caught) {
          onError(caught);
        } finally {
          setSending(false);
        }
      })}
    >
      <Controller
        name="outcome"
        control={form.control}
        render={({ field }) => (
          <TextField
            select
            label="Outcome"
            value={field.value}
            onChange={(event) => {
              field.onChange(event);
              if (event.target.value !== 'AllocationRecorded') {
                form.setValue('allocationId', '');
              }
            }}
            onBlur={field.onBlur}
          >
            {reviewCaseDecisionOutcomes.map((item) => <MenuItem key={item} value={item}>{item}</MenuItem>)}
          </TextField>
        )}
      />
      {outcome === 'AllocationRecorded' ? (
        <Controller
          name="allocationId"
          control={form.control}
          render={({ field, fieldState }) => (
            <TextField
              label="Decision allocation id"
              value={field.value}
              onChange={field.onChange}
              onBlur={field.onBlur}
              error={fieldState.error !== undefined}
              helperText={fieldState.error?.message ?? 'Copy the allocation id. This decision does not create an allocation.'}
            />
          )}
        />
      ) : null}
      <Controller
        name="resolutionNote"
        control={form.control}
        render={({ field, fieldState }) => (
          <TextField
            label="Decision resolution note"
            value={field.value}
            onChange={field.onChange}
            onBlur={field.onBlur}
            error={fieldState.error !== undefined}
            helperText={fieldState.error?.message ?? 'Optional. Whitespace alone leaves the note empty.'}
          />
        )}
      />
      <Button type="submit" disabled={sending}>Resolve review case</Button>
    </Stack>
  );
}

function DismissForm({
  draft,
  onVerified,
  onError,
  onStarted,
}: {
  draft: DecisionDraft;
  onVerified: (result: VerifiedReviewCaseDecision) => void;
  onError: (error: unknown) => void;
  onStarted: () => void;
}) {
  const [sending, setSending] = useState(false);
  const form = useForm<ReviewCaseDismissFormValues>({
    resolver: zodResolver(reviewCaseDismissSchema),
    defaultValues: emptyDismiss,
  });

  return (
    <Stack
      spacing={1}
      component="form"
      onSubmit={form.handleSubmit(async (values) => {
        setSending(true);
        onError(null);
        try {
          beginReviewCaseDecisionAttempt(normalizeReviewCaseDismiss(
            draft.command.caseId,
            draft.command.concurrencyToken,
            values,
          ));
          onStarted();
          onVerified(await submitReviewCaseDecisionAttempt());
        } catch (caught) {
          onError(caught);
        } finally {
          setSending(false);
        }
      })}
    >
      <Controller
        name="resolutionNote"
        control={form.control}
        render={({ field, fieldState }) => (
          <TextField
            label="Decision resolution note"
            value={field.value}
            onChange={field.onChange}
            onBlur={field.onBlur}
            error={fieldState.error !== undefined}
            helperText={fieldState.error?.message ?? 'Optional. Whitespace alone leaves the note empty.'}
          />
        )}
      />
      <Button type="submit" disabled={sending}>Dismiss review case</Button>
    </Stack>
  );
}

function StoredDecision({
  attempt,
  onVerified,
  onError,
}: {
  attempt: ReviewCaseDecisionAttempt;
  onVerified: (result: VerifiedReviewCaseDecision) => void;
  onError: (error: unknown) => void;
}) {
  const [sending, setSending] = useState(false);
  const [confirmingRemoval, setConfirmingRemoval] = useState(false);

  return (
    <Stack spacing={1}>
      <Alert severity={attempt.lifecycle === 'refused' ? 'warning' : 'info'}>
        {attempt.lifecycle === 'refused'
          ? 'The server refused this decision before saving it.'
          : 'This decision has no verified result. Sending it again uses the same request.'}
      </Alert>
      <Typography>{attempt.operation === 'ReviewCaseResolve' ? 'Resolve' : 'Dismiss'}</Typography>
      <Typography>{`Case ${attempt.caseId}`}</Typography>
      <Typography>{`Outcome ${attempt.outcome ?? 'none'}`}</Typography>
      <Typography>{`Allocation ${attempt.allocationId ?? 'none'}`}</Typography>
      <Typography>{`Resolution note ${attempt.resolutionNote ?? 'none'}`}</Typography>
      {attempt.lifecycle !== 'refused' ? (
        <Button
          type="button"
          disabled={sending}
          onClick={() => {
            setSending(true);
            onError(null);
            void submitReviewCaseDecisionAttempt()
              .then(onVerified)
              .catch(onError)
              .finally(() => setSending(false));
          }}
        >
          Send the same decision again
        </Button>
      ) : null}
      {confirmingRemoval ? (
        <Button
          type="button"
          onClick={() => {
            try {
              abandonReviewCaseDecisionAttempt(attempt.operation, attempt.caseId, attempt.requestBody);
              setConfirmingRemoval(false);
            } catch (caught) {
              onError(caught);
            }
          }}
        >
          Confirm removal of local tracking
        </Button>
      ) : (
        <Button type="button" onClick={() => setConfirmingRemoval(true)}>Remove local tracking</Button>
      )}
      <Typography color="text.secondary">
        Removing local tracking does not undo the server write. It can delete the only local copy of the original request.
      </Typography>
    </Stack>
  );
}

function UnreadableStorage({
  onClear,
  onError,
}: {
  onClear: () => void;
  onError: (error: unknown) => void;
}) {
  const [confirmingRemoval, setConfirmingRemoval] = useState(false);
  return (
    <Stack spacing={1}>
      <Alert severity="warning">The stored decision could not be read. It was not replaced and it was not sent.</Alert>
      {confirmingRemoval ? (
        <Button
          type="button"
          onClick={() => {
            try {
              abandonUnreadableReviewCaseDecisionAttempt();
              setConfirmingRemoval(false);
              onClear();
            } catch (caught) {
              onError(caught);
            }
          }}
        >
          Confirm removal of local tracking
        </Button>
      ) : (
        <Button type="button" onClick={() => setConfirmingRemoval(true)}>Remove local tracking</Button>
      )}
    </Stack>
  );
}

function VerifiedDecision({ result }: { result: VerifiedReviewCaseDecision }) {
  return (
    <Alert severity="success">
      <div>The review case matches this decision.</div>
      <div>{`Case ${result.reviewCaseId}`}</div>
      <div>{`Status ${result.status}`}</div>
      <div>{`Outcome ${result.outcome ?? 'none'}`}</div>
      <div>{`Allocation ${result.allocationId ?? 'none'}`}</div>
      <div>{`Resolution note ${result.resolutionNote ?? 'none'}`}</div>
    </Alert>
  );
}

function failureMessage(error: unknown): string {
  if (error instanceof ReviewCaseDecisionConflictError) {
    return error.code === 'reconciliation.review_case.terminal_conflict'
      ? 'The review case already has a different terminal decision. This request did not replace it.'
      : 'The review case changed. This decision was not sent again with a newer token.';
  }

  if (error instanceof ReviewCaseDecisionRefusedError) {
    return 'The server refused this decision before saving it.';
  }

  if (error instanceof UnknownReviewCaseDecisionOutcomeError || error instanceof UnreadableReviewCaseDecisionError) {
    return 'The decision was not verified. Sending it again uses the same request.';
  }

  if (error instanceof ReviewCaseDecisionUncertaintyRemainsError) {
    return 'A later refusal did not clear the unresolved decision.';
  }

  if (error instanceof ReviewCaseDecisionStorageCleanupError) {
    return 'The decision is confirmed. This browser could not remove the local record.';
  }

  if (error instanceof StaleSessionError) {
    return 'The session changed after the request started. The earlier decision was not replaced.';
  }

  if (error instanceof ReviewCaseDecisionStorageError || error instanceof ReviewCaseDecisionAttemptBlockedError) {
    return 'The decision could not be stored, so it was not sent.';
  }

  if (error instanceof UnresolvedReviewCaseDecisionError) {
    return 'An unresolved decision is already stored in this tab.';
  }

  if (error instanceof ReviewCaseDecisionActorRequiredError || error instanceof ReviewCaseDecisionAuthenticationError) {
    return 'Sign in with an operator identity before deciding a review case.';
  }

  if (error instanceof ReviewCaseDecisionTenantMismatchError || error instanceof ReviewCaseDecisionAttemptChangedError) {
    return 'The stored decision no longer matches this session. It was not replaced.';
  }

  if (error instanceof ReviewCaseDecisionSubmissionInProgressError) {
    return 'A review case decision is already being sent.';
  }

  if (error instanceof ApiError) {
    return 'The decision could not be sent.';
  }

  return 'The decision could not be sent.';
}
