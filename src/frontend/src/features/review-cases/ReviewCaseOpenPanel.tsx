import { zodResolver } from '@hookform/resolvers/zod';
import { Alert, AlertTitle, Button, MenuItem, Stack, TextField, Typography } from '@mui/material';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { Controller, useForm } from 'react-hook-form';

import { ApiError, StaleSessionError } from '../../shared/api/errors';
import { sessionStore } from '../../shared/api/session-store';
import { useSession } from '../../shared/auth/session';
import {
  abandonReviewCaseOpenAttempt,
  abandonUnreadableReviewCaseOpenAttempt,
  beginReviewCaseOpenAttempt,
  ReviewCaseOpenActorRequiredError,
  ReviewCaseOpenAttemptBlockedError,
  ReviewCaseOpenAttemptChangedError,
  ReviewCaseOpenAuthenticationError,
  ReviewCaseOpenPayloadConflictError,
  ReviewCaseOpenRejectedError,
  ReviewCaseOpenStorageCleanupError,
  ReviewCaseOpenStorageError,
  ReviewCaseOpenSubmissionInProgressError,
  ReviewCaseOpenTenantMismatchError,
  ReviewCaseOpenUncertaintyRemainsError,
  submitReviewCaseOpenAttempt,
  UnknownReviewCaseOpenOutcomeError,
  UnreadableReviewCaseOpenError,
  UnresolvedReviewCaseOpenError,
  useReviewCaseOpenAttempt,
  type VerifiedReviewCaseOpen,
} from './review-case-open-attempt';
import {
  normalizeReviewCaseOpen,
  reviewCaseOpenReasons,
  reviewCaseOpenSchema,
  reviewCaseOpenSources,
  type ReviewCaseOpenFormValues,
  developmentActorId,
  developmentOrganizationId,
} from './review-case-open-request';

const emptyForm: ReviewCaseOpenFormValues = {
  reason: 'NeedsInvestigation',
  invoiceId: '',
  bankTransactionId: '',
  sourceLabel: '',
  note: '',
};

export function ReviewCaseOpenPanel({ onSelect }: { onSelect: (reviewCaseId: string) => void }) {
  const view = useReviewCaseOpenAttempt();
  const session = useSession();
  const queryClient = useQueryClient();
  const onSelectRef = useRef(onSelect);
  const resultGeneration = useRef(0);
  const viewMounted = useRef(false);
  const [error, setError] = useState<unknown>(null);
  const [verified, setVerified] = useState<VerifiedReviewCaseOpen | null>(null);
  const [refreshFailed, setRefreshFailed] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  useEffect(() => {
    viewMounted.current = true;
    return () => {
      viewMounted.current = false;
    };
  }, []);
  useEffect(() => {
    onSelectRef.current = onSelect;
  }, [onSelect]);
  const canOpen = developmentOrganizationId(session.token) !== null && developmentActorId(session.token) !== null;
  if (view.status === 'blocked-authentication' || (view.status === 'none' && !canOpen)) {
    return null;
  }

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

    const options = { throwOnError: true } as const;
    await queryClient.invalidateQueries({ queryKey: ['review-cases', 'list', epoch] }, options);
    await queryClient.invalidateQueries({ queryKey: ['review-cases', 'detail', epoch] }, options);
    await queryClient.invalidateQueries({ queryKey: ['review-cases', 'audit', epoch] }, options);
  }

  function acceptResult(result: VerifiedReviewCaseOpen): number | null {
    if (!currentEpochMatches(result.epoch)) {
      setError(new StaleSessionError());
      return null;
    }

    resultGeneration.current += 1;
    setVerified(result);
    setError(result.cleanup === 'failed' ? new ReviewCaseOpenStorageCleanupError() : null);
    setRefreshFailed(false);
    return resultGeneration.current;
  }

  async function applyVerified(result: VerifiedReviewCaseOpen): Promise<void> {
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

      onSelectRef.current(result.reviewCaseId);
      setRefreshFailed(false);
    } catch {
      if (!ownsResult(result.epoch, generation)) {
        return;
      }

      setRefreshFailed(true);
    }
  }

  function retryDisplayedData(result: VerifiedReviewCaseOpen): void {
    const generation = resultGeneration.current;
    const epoch = result.epoch;
    setRefreshing(true);
    void refresh(epoch)
      .then(() => {
        if (!ownsResult(epoch, generation)) {
          return;
        }

        onSelectRef.current(result.reviewCaseId);
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
    <Stack spacing={1} component="section" aria-label="Open a review case">
      <Typography color="text.secondary">
        Two people using this organization in the same tab can see one stored opening. The record follows the organization id and does not identify the operator.
      </Typography>
      {view.status === 'invalid-storage' ? <UnreadableStorage onClear={() => setError(null)} onError={setError} /> : null}
      {view.status === 'blocked-tenant' ? (
        <Alert severity="warning">A stored opening belongs to another organization in this tab. It was not replaced and it was not sent.</Alert>
      ) : null}
      {view.status === 'pending' ? (
        <StoredOpening
          attempt={view.attempt}
          onVerified={(result) => void applyVerified(result)}
          onError={setError}
        />
      ) : null}
      {view.status === 'none' ? (
        <OpenForm
          onVerified={(result) => void applyVerified(result)}
          onError={setError}
        />
      ) : null}
      {verified !== null ? <VerifiedOpening result={verified} /> : null}
      {refreshFailed && verified !== null ? (
        <Alert
          severity="warning"
          action={(
            <Button
              color="inherit"
              disabled={refreshing}
              onClick={() => retryDisplayedData(verified)}
            >
              Refresh displayed data
            </Button>
          )}
        >
          The opening is confirmed. The review case lists could not be refreshed.
        </Alert>
      ) : null}
      {error !== null ? <Alert severity="warning">{failureMessage(error)}</Alert> : null}
    </Stack>
  );
}

function OpenForm({
  onVerified,
  onError,
}: {
  onVerified: (result: VerifiedReviewCaseOpen) => void;
  onError: (error: unknown) => void;
}) {
  const [sending, setSending] = useState(false);
  const form = useForm<ReviewCaseOpenFormValues>({
    resolver: zodResolver(reviewCaseOpenSchema),
    defaultValues: emptyForm,
  });

  return (
    <Stack
      spacing={1}
      component="form"
      onSubmit={form.handleSubmit(async (values) => {
        setSending(true);
        onError(null);
        try {
          beginReviewCaseOpenAttempt(normalizeReviewCaseOpen(values));
          onVerified(await submitReviewCaseOpenAttempt());
          form.reset(emptyForm);
        } catch (error) {
          onError(error);
        } finally {
          setSending(false);
        }
      })}
    >
      <Controller
        name="reason"
        control={form.control}
        render={({ field }) => (
          <TextField select label="Reason" value={field.value} onChange={field.onChange} onBlur={field.onBlur}>
            {reviewCaseOpenReasons.map((reason) => <MenuItem key={reason} value={reason}>{reason}</MenuItem>)}
          </TextField>
        )}
      />
      <Controller
        name="invoiceId"
        control={form.control}
        render={({ field, fieldState }) => (
          <TextField
            label="Opening invoice id"
            value={field.value}
            onChange={field.onChange}
            onBlur={field.onBlur}
            error={fieldState.error !== undefined}
            helperText={fieldState.error?.message ?? 'Copy the invoice id. Leave it empty only for a bank investigation.'}
          />
        )}
      />
      <Controller
        name="bankTransactionId"
        control={form.control}
        render={({ field, fieldState }) => (
          <TextField
            label="Opening bank transaction id"
            value={field.value}
            onChange={field.onChange}
            onBlur={field.onBlur}
            error={fieldState.error !== undefined}
            helperText={fieldState.error?.message ?? 'Copy the bank transaction id. Leave it empty only for an invoice investigation.'}
          />
        )}
      />
      <Controller
        name="sourceLabel"
        control={form.control}
        render={({ field }) => (
          <TextField select label="Source" value={field.value} onChange={field.onChange} onBlur={field.onBlur}>
            <MenuItem value="">None</MenuItem>
            {reviewCaseOpenSources.map((source) => <MenuItem key={source} value={source}>{source}</MenuItem>)}
          </TextField>
        )}
      />
      <Controller
        name="note"
        control={form.control}
        render={({ field, fieldState }) => (
          <TextField
            label="Opening note"
            value={field.value}
            onChange={field.onChange}
            onBlur={field.onBlur}
            error={fieldState.error !== undefined}
            helperText={fieldState.error?.message ?? 'Optional. Leading and trailing whitespace is removed. A byte-order mark is kept. At most 500 characters.'}
            multiline
          />
        )}
      />
      <Button type="submit" variant="contained" disabled={sending}>Open review case</Button>
    </Stack>
  );
}

function StoredOpening({
  attempt,
  onVerified,
  onError,
}: {
  attempt: Extract<ReturnType<typeof useReviewCaseOpenAttempt>, { status: 'pending' }>['attempt'];
  onVerified: (result: VerifiedReviewCaseOpen) => void;
  onError: (error: unknown) => void;
}) {
  const [sending, setSending] = useState(false);
  const [confirmRemoval, setConfirmRemoval] = useState(false);
  const unresolved = attempt.lifecycle !== 'rejected';

  return (
    <Stack spacing={1} aria-label="Stored review-case opening">
      <Typography>Reason {attempt.reason}</Typography>
      <Typography>Invoice {attempt.invoiceId ?? 'none'}</Typography>
      <Typography>Bank transaction {attempt.bankTransactionId ?? 'none'}</Typography>
      <Typography>Source {attempt.sourceLabel ?? 'none'}</Typography>
      <Typography>Note {attempt.note ?? 'none'}</Typography>
      <Typography>
        {unresolved
          ? 'This opening has no verified result. Sending it again uses the same request.'
          : 'The server refused this opening before saving it.'}
      </Typography>
      {unresolved ? (
        <Button
          variant="contained"
          disabled={sending}
          onClick={() => {
            setSending(true);
            onError(null);
            void submitReviewCaseOpenAttempt()
              .then(onVerified)
              .catch(onError)
              .finally(() => setSending(false));
          }}
        >
          Send the same opening again
        </Button>
      ) : null}
      <Button type="button" onClick={() => setConfirmRemoval(true)}>Remove local tracking</Button>
      {confirmRemoval ? (
        <Stack spacing={1}>
          <Typography>
            Removing local tracking does not undo a server write. It can delete the only local copy of this idempotency key.
          </Typography>
          <Button
            color="warning"
            onClick={() => {
              onError(null);
              try {
                abandonReviewCaseOpenAttempt(attempt.idempotencyKey, attempt.requestBody);
                setConfirmRemoval(false);
              } catch (error) {
                onError(error);
              }
            }}
          >
            Confirm removal of local tracking
          </Button>
        </Stack>
      ) : null}
    </Stack>
  );
}

function UnreadableStorage({ onClear, onError }: { onClear: () => void; onError: (error: unknown) => void }) {
  const [confirmRemoval, setConfirmRemoval] = useState(false);
  return (
    <Stack spacing={1}>
      <Alert severity="warning">The stored opening could not be read. It was not replaced and it was not sent.</Alert>
      <Button type="button" onClick={() => setConfirmRemoval(true)}>Remove local tracking</Button>
      {confirmRemoval ? (
        <Stack spacing={1}>
          <Typography>
            Removing local tracking does not undo a server write. It can delete the only local copy of this idempotency key.
          </Typography>
          <Button
            color="warning"
            onClick={() => {
              try {
                abandonUnreadableReviewCaseOpenAttempt();
                onClear();
              } catch (error) {
                onError(error);
              }
            }}
          >
            Confirm removal of local tracking
          </Button>
        </Stack>
      ) : null}
    </Stack>
  );
}

function VerifiedOpening({ result }: { result: VerifiedReviewCaseOpen }) {
  return (
    <Alert severity="success">
      <AlertTitle>
        {result.created ? 'Review case opened.' : 'This opening already names an existing review case.'}
      </AlertTitle>
      <Typography>{`Case ${result.reviewCaseId}`}</Typography>
      <Typography>{result.status}</Typography>
    </Alert>
  );
}

function failureMessage(error: unknown): string {
  if (error instanceof ReviewCaseOpenStorageError || error instanceof ReviewCaseOpenAttemptBlockedError || error instanceof UnresolvedReviewCaseOpenError) {
    return 'The opening was not stored, so it was not sent. An existing stored opening was not replaced.';
  }

  if (error instanceof ReviewCaseOpenStorageCleanupError) {
    return 'The server confirmed this opening. This browser could not remove the local recovery record. The original key was not replaced, and the opening was not rolled back.';
  }

  if (error instanceof ReviewCaseOpenAttemptChangedError) {
    return 'The stored opening changed before the response could be applied. The changed record was not removed.';
  }

  if (error instanceof ReviewCaseOpenAuthenticationError || error instanceof ReviewCaseOpenActorRequiredError) {
    return 'This opening needs a current development token and an operator identity of 1 to 256 characters. The request was not sent.';
  }

  if (error instanceof ReviewCaseOpenTenantMismatchError) {
    return 'The stored opening belongs to another organization. It was not sent.';
  }

  if (error instanceof ReviewCaseOpenSubmissionInProgressError) {
    return 'This opening is still being sent. Wait until that request finishes before removing local tracking or starting another one.';
  }

  if (error instanceof ReviewCaseOpenPayloadConflictError) {
    return 'This idempotency key was already used for a different opening. The stored request was not confirmed and was not replaced.';
  }

  if (error instanceof ReviewCaseOpenRejectedError) {
    return 'The server refused this opening before saving it. Remove local tracking before starting another one.';
  }

  if (error instanceof ReviewCaseOpenUncertaintyRemainsError) {
    return 'This retry was refused. The earlier opening stays unresolved, its key was not replaced, and it was not rolled back.';
  }

  if (error instanceof UnknownReviewCaseOpenOutcomeError) {
    return 'The opening was not verified. A lost response does not show that the server rolled it back. Send the same opening again.';
  }

  if (error instanceof UnreadableReviewCaseOpenError) {
    return 'The server response could not be verified. The stored request was kept.';
  }

  if (error instanceof StaleSessionError) {
    return 'The session changed after the request started. The response was not applied here, and the earlier attempt was not rolled back.';
  }

  if (error instanceof ApiError) {
    return error.title;
  }

  return 'The opening was not verified. The stored request was kept.';
}
