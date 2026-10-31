import {
  Alert,
  AlertTitle,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Stack,
  TextField,
  Typography,
} from '@mui/material';
import type { UseQueryResult } from '@tanstack/react-query';
import { useEffect, useRef, useState, type RefObject } from 'react';
import { Link } from 'react-router';

import { ApiError, NetworkError, StaleSessionError } from '../../shared/api/errors';
import { endSession } from '../../shared/auth/session';
import { formatMoney } from '../../shared/money/money';
import type { ReversalContext } from './reversal-api';
import { normalizeReversalReason } from './reversal-reason';
import {
  ReversalActorRequiredError,
  ReversalAttemptBlockedError,
  ReversalAttemptChangedError,
  ReversalAuthenticationError,
  ReversalPreflightError,
  ReversalReasonError,
  ReversalStorageCleanupError,
  ReversalStorageError,
  ReversalSubmissionInProgressError,
  ReversalTenantMismatchError,
  UnknownReversalOutcomeError,
  UnreadableReversalResultError,
  UnresolvedReversalAttemptError,
  type ReversalAttemptView,
  type VerifiedReversal,
} from './reversal-attempt';

const balanceWarning =
  'Reversal restores the original amount to the invoice outstanding balance and the bank unapplied balance. It does not delete the allocation.';

const abandonWarning =
  'Removing local tracking does not reverse or undo a server-side operation. A previous request may already have been saved even if this browser lost the response. Removing the stored request drops the idempotency key needed to send that same request again. A later new reversal can be rejected because the allocation is already reversed.';

export function ReversalDialog({
  open,
  allocationId,
  attempt,
  context,
  submitting,
  error,
  result,
  onClose,
  onReverse,
  onRetry,
  onAbandon,
}: {
  open: boolean;
  allocationId: string | null;
  attempt: ReversalAttemptView;
  context: UseQueryResult<ReversalContext>;
  submitting: boolean;
  error: unknown;
  result: VerifiedReversal | null;
  onClose: () => void;
  onReverse: (reason: string) => void;
  onRetry: () => void;
  onAbandon: (idempotencyKey: string) => void;
}) {
  const [reason, setReason] = useState('');
  const [reasonError, setReasonError] = useState<string | null>(null);
  const [confirmingAbandon, setConfirmingAbandon] = useState(false);
  const [seenAllocationId, setSeenAllocationId] = useState<string | null>(null);
  const recoveryRef = useRef<HTMLDivElement>(null);
  const recovery = attempt.status !== 'none';
  const currentContext = !recovery && context.isSuccess && !context.isFetching ? context.data : null;
  const alreadyReversed = currentContext?.status === 'reversed';
  const showNewForm = !recovery && result === null && currentContext?.status === 'confirmed';

  if (open && allocationId !== seenAllocationId) {
    setSeenAllocationId(allocationId);
    setReason('');
    setReasonError(null);
    setConfirmingAbandon(false);
  } else if (!open && (confirmingAbandon || reasonError !== null || reason !== '')) {
    setReason('');
    setReasonError(null);
    setConfirmingAbandon(false);
  }

  useEffect(() => {
    if (open && recovery) {
      recoveryRef.current?.focus();
    }
  }, [open, recovery, attempt.status]);

  function confirmNew(): void {
    const decision = normalizeReversalReason(reason);

    if (!decision.ok) {
      setReasonError(decision.message);
      return;
    }

    setReasonError(null);
    onReverse(reason);
  }

  return (
    <Dialog
      open={open}
      onClose={onClose}
      aria-labelledby="allocation-reversal-title"
      aria-describedby="allocation-reversal-description"
      fullWidth
      maxWidth="sm"
      transitionDuration={0}
    >
      <DialogTitle id="allocation-reversal-title">
        {recovery ? 'Recover reversal attempt' : 'Reverse allocation'}
      </DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ pt: 1 }}>
          <Typography id="allocation-reversal-description" color="text.secondary">
            {balanceWarning}
          </Typography>
          {attempt.status === 'pending' && allocationId !== null && allocationId.toLowerCase() !== attempt.attempt.allocationId.toLowerCase() ? (
            <Alert severity="info">This row was not started. An unresolved reversal attempt is already stored.</Alert>
          ) : null}
          {attempt.status === 'blocked-authentication' ? <BlockedSession /> : null}
          {attempt.status === 'blocked-tenant' ? <BlockedTenant /> : null}
          {attempt.status === 'invalid-storage' ? <InvalidStorage /> : null}
          {attempt.status === 'pending' ? (
            <StoredAttempt attempt={attempt.attempt} recoveryRef={recoveryRef} />
          ) : null}
          {!recovery && context.isFetching ? <Alert severity="info" role="status">Loading the current allocation</Alert> : null}
          {!recovery && context.isError ? <ContextFailure error={context.error} /> : null}
          {currentContext !== null ? <ContextDetails context={currentContext} /> : null}
          {alreadyReversed ? (
            <Alert severity="warning">This allocation is already reversed. A new reversal was not started.</Alert>
          ) : null}
          {showNewForm ? (
            <TextField
              label="Reversal reason"
              value={reason}
              onChange={(event) => {
                setReason(event.target.value);
                setReasonError(null);
              }}
              fullWidth
              autoFocus
              disabled={submitting}
              error={reasonError !== null || (error instanceof ReversalReasonError)}
              helperText={reasonError ?? (error instanceof ReversalReasonError ? error.message : 'Required. Leading and trailing whitespace is removed when the request is stored.')}
              slotProps={{ htmlInput: { 'aria-required': true } }}
            />
          ) : null}
          {submitting ? <Alert severity="info" role="status">Reversal request in progress</Alert> : null}
          {result ? <VerifiedResult result={result} /> : null}
          {error ? <FailureNotice error={error} /> : null}
          {confirmingAbandon ? (
            <Alert severity="warning">
              <AlertTitle>Remove local tracking only</AlertTitle>
              {abandonWarning}
            </Alert>
          ) : null}
        </Stack>
      </DialogContent>
      <DialogActions sx={{ flexWrap: 'wrap', gap: 1 }}>
        <Button type="button" onClick={onClose}>Close</Button>
        {showNewForm ? (
          <Button type="button" variant="contained" color="warning" onClick={confirmNew} disabled={submitting || context.isFetching}>
            Reverse this allocation
          </Button>
        ) : null}
        {attempt.status === 'pending' && result === null ? (
          <Button type="button" variant="contained" onClick={onRetry} disabled={submitting}>
            Send the same reversal again
          </Button>
        ) : null}
        {attempt.status === 'pending' && result === null && !confirmingAbandon ? (
          <Button type="button" color="warning" onClick={() => setConfirmingAbandon(true)} disabled={submitting}>
            Remove local tracking
          </Button>
        ) : null}
        {attempt.status === 'pending' && confirmingAbandon ? (
          <Button type="button" color="warning" variant="contained" onClick={() => onAbandon(attempt.attempt.idempotencyKey)} disabled={submitting}>
            Remove local tracking now
          </Button>
        ) : null}
      </DialogActions>
    </Dialog>
  );
}

export function ReversalDiscoveryNotice({ view }: { view: ReversalAttemptView }) {
  if (view.status === 'pending') {
    return (
      <Alert
        severity="warning"
        action={<Button component={Link} to="/allocations">Open Allocation history</Button>}
      >
        An unresolved reversal attempt is stored for this organization.
      </Alert>
    );
  }

  if (view.status === 'blocked-tenant') {
    return <Alert severity="warning">A stored reversal belongs to another organization. Its details stay hidden.</Alert>;
  }

  if (view.status === 'invalid-storage') {
    return <Alert severity="error">A stored reversal cannot be read safely. It was not replaced.</Alert>;
  }

  if (view.status === 'blocked-authentication') {
    return <Alert severity="warning">Sign in before this browser can show a stored reversal.</Alert>;
  }

  return null;
}

export function ExplicitAmountDiscoveryNotice({
  status,
}: {
  status: 'none' | 'pending' | 'blocked-tenant' | 'invalid-storage' | 'blocked-authentication';
}) {
  if (status === 'pending') {
    return (
      <Alert
        severity="info"
        action={<Button component={Link} to="/reconciliation">Open Reconciliation</Button>}
      >
        An unresolved allocation attempt is stored for this organization.
      </Alert>
    );
  }

  if (status === 'blocked-tenant') {
    return <Alert severity="warning">A stored allocation attempt belongs to another organization. Its details stay hidden.</Alert>;
  }

  if (status === 'invalid-storage') {
    return <Alert severity="error">A stored allocation attempt cannot be read safely. It was not replaced.</Alert>;
  }

  return null;
}

function ContextDetails({ context }: { context: ReversalContext }) {
  return (
    <Stack spacing={0.5} component="section" aria-label="Current allocation">
      <Typography>Allocation {context.allocationId}</Typography>
      <Typography>Invoice number {context.invoiceNumber}</Typography>
      <Typography>Invoice id {context.invoiceId}</Typography>
      <Typography>Bank reference {context.reference ?? 'None'}</Typography>
      <Typography>Bank transaction {context.bankTransactionId}</Typography>
      <Typography>Original amount {formatMoney(context.amount, context.currency)}</Typography>
      <Typography>Status {context.status === 'confirmed' ? 'Confirmed' : 'Reversed'}</Typography>
    </Stack>
  );
}

function StoredAttempt({
  attempt,
  recoveryRef,
}: {
  attempt: Extract<ReversalAttemptView, { status: 'pending' }>['attempt'];
  recoveryRef: RefObject<HTMLDivElement | null>;
}) {
  return (
    <Stack spacing={0.5} ref={recoveryRef} tabIndex={-1} aria-label="Stored reversal attempt">
      <Typography>Allocation {attempt.allocationId}</Typography>
      <Typography>Invoice {attempt.invoiceId}</Typography>
      <Typography>Bank transaction {attempt.bankTransactionId}</Typography>
      <Typography>Original amount {formatMoney(attempt.amount, attempt.currency)}</Typography>
      <Typography>Reason {attempt.reason}</Typography>
      <Typography>
        {attempt.lifecycle === 'uncertain'
          ? 'The last send ended without a verified result. Sending it again uses the same request.'
          : 'This attempt has not had a verified result. Sending it uses the stored request.'}
      </Typography>
    </Stack>
  );
}

function VerifiedResult({ result }: { result: VerifiedReversal }) {
  return (
    <Alert severity="success">
      <AlertTitle>{result.replayed ? 'This reversal was already recorded for the same request.' : 'Reversal recorded.'}</AlertTitle>
      <Typography>Allocation {result.allocationId}</Typography>
      <Typography>{formatMoney(result.amount, result.currency)}</Typography>
      <Typography>Reason {result.reason}</Typography>
    </Alert>
  );
}

function FailureNotice({ error }: { error: unknown }) {
  if (error instanceof ReversalReasonError) {
    return null;
  }

  return <Alert severity="warning">{failureMessage(error)}</Alert>;
}

function failureMessage(error: unknown): string {
  if (error instanceof ReversalSubmissionInProgressError) {
    return 'This reversal is still being sent. Wait until that request finishes before removing local tracking or starting another one.';
  }

  if (error instanceof ReversalStorageError || error instanceof ReversalAttemptBlockedError || error instanceof UnresolvedReversalAttemptError) {
    return 'The reversal was not stored, so it was not sent. An existing stored reversal was not replaced.';
  }

  if (error instanceof ReversalStorageCleanupError) {
    return 'The response was verified, but local tracking could not be cleared. Send the same stored reversal again. A new reversal was not started.';
  }

  if (error instanceof ReversalAttemptChangedError) {
    return 'The stored reversal no longer matches this screen. It was not removed.';
  }

  if (error instanceof ReversalAuthenticationError || error instanceof ReversalActorRequiredError) {
    return 'This reversal needs a current signed-in operator. The stored request was kept.';
  }

  if (error instanceof ReversalTenantMismatchError) {
    return 'The stored reversal belongs to another organization. It was not sent.';
  }

  if (error instanceof ReversalPreflightError) {
    return 'This allocation is already reversed. A new reversal was not started.';
  }

  if (error instanceof UnknownReversalOutcomeError || error instanceof NetworkError) {
    return 'The reversal was not verified. It may already have been saved. The stored request was kept, and it was not sent again.';
  }

  if (error instanceof UnreadableReversalResultError) {
    return 'The server response could not be verified. The stored request was kept, and this was not shown as a completed reversal.';
  }

  if (error instanceof StaleSessionError) {
    return 'Your session changed before this reversal finished. The result was not applied to this sign-in, and the request was not sent again.';
  }

  if (error instanceof ApiError) {
    return apiFailureMessage(error);
  }

  return 'The reversal was not verified. The stored request was kept.';
}

function apiFailureMessage(error: ApiError): string {
  if (error.code === 'reconciliation.allocation.already_reversed') {
    return 'The allocation is already reversed. This stored request did not verify that it performed that reversal.';
  }

  if (error.code === 'reconciliation.allocation.reversal_idempotency_payload_conflict') {
    return 'This idempotency key was already used for a different reversal. The stored request was not changed.';
  }

  if (error.code === 'reconciliation.allocation.reversal_integrity_conflict') {
    return 'The stored reversal does not agree with the server. It was not treated as a completed reversal.';
  }

  if (error.code === 'reconciliation.concurrency_conflict') {
    return 'The records changed before this reversal was saved. The same request can be sent again. This was not recorded as a reversal.';
  }

  if (error.code === 'reconciliation.actor.required' || error.status === 401) {
    return 'This request needs a signed-in operator identity. The stored reversal was kept.';
  }

  if (error.status === 403) {
    return 'This reversal is not allowed for the current access. The stored request was kept.';
  }

  if (error.code === 'reconciliation.allocation.not_found') {
    return 'The allocation was not found for this organization. The stored request was kept.';
  }

  if (error.code === 'reconciliation.allocation.reversal_reason_too_long' || error.code === 'reconciliation.allocation.reversal_invalid_request') {
    return 'The server rejected the stored reversal request. It was not changed and it was not treated as completed.';
  }

  if (error.status === 413 || error.status === 415) {
    return 'The server rejected the stored reversal request. It was not changed and it was not treated as completed.';
  }

  return 'The reversal was not verified. The stored request was kept.';
}

function ContextFailure({ error }: { error: unknown }) {
  if (error instanceof StaleSessionError) {
    return null;
  }

  if (error instanceof ApiError && error.status === 401) {
    return (
      <Alert severity="warning" action={<Button color="inherit" onClick={() => endSession('unauthorized')}>Update access</Button>}>
        Your access has expired or is no longer valid. A new reversal was not started.
      </Alert>
    );
  }

  if (error instanceof ApiError && error.status === 403) {
    return (
      <Alert severity="warning" action={<Button color="inherit" onClick={() => endSession('forbidden')}>Update access</Button>}>
        You cannot read this allocation. A new reversal was not started.
      </Alert>
    );
  }

  if (error instanceof ApiError && error.status === 404) {
    return <Alert severity="warning">This allocation was not found. A new reversal was not started.</Alert>;
  }

  if (error instanceof NetworkError) {
    return <Alert severity="error">The current allocation could not be loaded. A new reversal was not started.</Alert>;
  }

  return <Alert severity="error">The current allocation could not be read. A new reversal was not started.</Alert>;
}

function BlockedSession() {
  return <Alert severity="warning">Sign in before this browser can show a stored reversal.</Alert>;
}

function BlockedTenant() {
  return <Alert severity="warning">A stored reversal belongs to another organization. Its details stay hidden.</Alert>;
}

function InvalidStorage() {
  return <Alert severity="error">A stored reversal cannot be read safely. It was not replaced.</Alert>;
}
