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
import { useState } from 'react';

import { ApiError, NetworkError, StaleSessionError, UnexpectedResponseError } from '../../shared/api/errors';
import { formatMoney, type DecimalString } from '../../shared/money/money';
import type { AllocationContext } from './allocation-context-api';
import {
  AllocationActorRequiredError,
  AllocationAttemptChangedError,
  AllocationAuthenticationError,
  AllocationStorageCleanupError,
  AllocationStorageError,
  AllocationSubmissionInProgressError,
  AllocationTenantMismatchError,
  UnknownAllocationOutcomeError,
  UnreadableAllocationResultError,
  UnresolvedAllocationAttemptError,
  type AllocationAttemptView,
  type VerifiedExplicitAmount,
} from './allocation-attempt';
import { assessAllocationAmount, describeAllocationBalances } from './manual-allocation-rules';

const observation =
  'These balances are a current observation. They do not reserve either record, and they can change before the server accepts a request.';

export function ManualAllocationDialog({
  open,
  context,
  attempt,
  suggestedAmount,
  draftPairKey,
  redirectedFromOtherPair,
  submitting,
  error,
  result,
  onClose,
  onAllocate,
  onRetry,
  onAbandon,
  onRefreshContext,
}: {
  open: boolean;
  context: UseQueryResult<AllocationContext>;
  attempt: AllocationAttemptView;
  suggestedAmount: DecimalString | null;
  draftPairKey: string | null;
  redirectedFromOtherPair: boolean;
  submitting: boolean;
  error: unknown;
  result: VerifiedExplicitAmount | null;
  onClose: () => void;
  onAllocate: (amountText: string, currency: string) => void;
  onRetry: () => void;
  onAbandon: () => void;
  onRefreshContext: () => void;
}) {
  const [amountText, setAmountText] = useState(suggestedAmount ?? '');
  const [seenPairKey, setSeenPairKey] = useState<string | null>(null);
  const [confirmingAbandon, setConfirmingAbandon] = useState(false);
  const recovery = attempt.status !== 'none';
  const cleanupWithoutAttempt = error instanceof AllocationStorageCleanupError && attempt.status === 'none';
  const showNewForm = !recovery && result === null && !cleanupWithoutAttempt;
  const currentContext = context.isSuccess && !context.isFetching ? context.data ?? null : null;
  const balances = !showNewForm || currentContext === null ? null : describeAllocationBalances(currentContext);
  const amountDecision = balances?.status === 'allocatable' ? assessAllocationAmount(balances.maximum, amountText) : null;

  if (open && draftPairKey !== seenPairKey) {
    setSeenPairKey(draftPairKey);
    setAmountText(suggestedAmount ?? '');
    setConfirmingAbandon(false);
  } else if (!open && confirmingAbandon) {
    setConfirmingAbandon(false);
  }

  return (
    <Dialog
      open={open}
      onClose={onClose}
      aria-labelledby="manual-allocation-title"
      aria-describedby="manual-allocation-description"
      fullWidth
      maxWidth="sm"
      transitionDuration={0}
    >
      <DialogTitle id="manual-allocation-title">
        {recovery ? 'Recover allocation attempt' : 'Allocate an amount'}
      </DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ pt: 1 }}>
          <Typography id="manual-allocation-description" color="text.secondary">
            {observation} A suggestion score does not authorize an allocation.
          </Typography>
          {redirectedFromOtherPair && attempt.status === 'pending' ? (
            <Alert severity="info">
              This row was not started. An unresolved allocation attempt is already stored.
            </Alert>
          ) : null}
          {attempt.status === 'blocked-authentication' ? <BlockedSession /> : null}
          {attempt.status === 'blocked-tenant' ? <BlockedTenant /> : null}
          {attempt.status === 'invalid-storage' ? <InvalidStorage /> : null}
          {attempt.status === 'pending' ? <StoredAttempt attempt={attempt.attempt} /> : null}
          {context.isFetching && attempt.status !== 'blocked-authentication' && attempt.status !== 'blocked-tenant' && attempt.status !== 'invalid-storage' ? (
            <Alert severity="info" role="status">Loading current balances</Alert>
          ) : null}
          {context.isError ? <ContextFailure error={context.error} recovery={attempt.status === 'pending'} /> : null}
          {currentContext !== null && attempt.status !== 'blocked-authentication' && attempt.status !== 'blocked-tenant' ? (
            <ContextDetails context={currentContext} />
          ) : null}
          {balances?.status === 'currency-mismatch' && showNewForm ? (
            <Alert severity="warning">
              The invoice and the bank transaction use different currencies. This screen does not convert currency.
            </Alert>
          ) : null}
          {balances?.status === 'fully-allocated' && showNewForm ? (
            <Alert severity="warning">
              This read has no remaining amount on {fullyAllocatedSide(balances.outstanding, balances.unapplied)}. A new allocation cannot start from it.
            </Alert>
          ) : null}
          {balances?.status === 'allocatable' && showNewForm ? (
            <Typography>
              At most {formatMoney(balances.maximum, balances.currency)} can be requested from this read.
            </Typography>
          ) : null}
          {showNewForm && balances?.status === 'allocatable' ? (
            <TextField
              label="Allocation amount"
              value={amountText}
              onChange={(event) => setAmountText(event.target.value)}
              fullWidth
              error={amountDecision !== null && !amountDecision.ok && amountText.trim().length > 0}
              helperText={amountText.trim().length > 0 && amountDecision !== null && !amountDecision.ok
                ? amountDecision.message
                : 'Enter the amount as text. The suggestion amount is only a starting value.'}
              slotProps={{ htmlInput: { inputMode: 'decimal' } }}
              disabled={submitting}
            />
          ) : null}
          {submitting ? <Alert severity="info" role="status">Allocation request in progress</Alert> : null}
          {result ? <VerifiedResult result={result} /> : null}
          {error ? <FailureNotice error={error} attempt={attempt} /> : null}
          {confirmingAbandon ? (
            <Alert severity="warning">
              <AlertTitle>Remove local tracking only</AlertTitle>
              This removes the attempt from this browser tab. It does not cancel or reverse an allocation that the server may already have committed. Another allocation started afterwards can create a second financial record.
            </Alert>
          ) : null}
        </Stack>
      </DialogContent>
      <DialogActions sx={{ flexWrap: 'wrap', gap: 1 }}>
        <Button type="button" onClick={onClose}>Close</Button>
        {contextPairVisible(attempt) ? (
          <Button type="button" onClick={onRefreshContext} disabled={context.isFetching}>Refresh balances</Button>
        ) : null}
        {showNewForm && balances?.status === 'allocatable' && result === null ? (
          <Button
            type="button"
            variant="contained"
            onClick={() => onAllocate(amountText, balances.currency)}
            disabled={submitting || amountDecision === null || !amountDecision.ok || context.isFetching}
          >
            Allocate this amount
          </Button>
        ) : null}
        {attempt.status === 'pending' && result === null ? (
          <Button type="button" variant="contained" onClick={onRetry} disabled={submitting}>
            Send the same allocation again
          </Button>
        ) : null}
        {canAbandon(attempt) && result === null && !confirmingAbandon ? (
          <Button type="button" color="warning" onClick={() => setConfirmingAbandon(true)} disabled={submitting}>
            Stop tracking this attempt
          </Button>
        ) : null}
        {confirmingAbandon ? (
          <Button type="button" color="warning" variant="contained" onClick={onAbandon} disabled={submitting}>
            Remove local tracking
          </Button>
        ) : null}
        {cleanupWithoutAttempt ? (
          <Button type="button" onClick={onClose}>Dismiss</Button>
        ) : null}
      </DialogActions>
    </Dialog>
  );
}

export function AllocationRecoveryBanner({
  attempt,
  result,
  error,
  onReview,
  onDismiss,
}: {
  attempt: AllocationAttemptView;
  result: VerifiedExplicitAmount | null;
  error: unknown;
  onReview: () => void;
  onDismiss: () => void;
}) {
  if (result) {
    return (
      <Alert severity="success" role="status" action={<Button type="button" color="inherit" onClick={onDismiss}>Done</Button>}>
        <VerifiedResult result={result} />
      </Alert>
    );
  }

  if (error instanceof AllocationStorageCleanupError && attempt.status === 'none') {
    return (
      <Alert severity="warning" action={<Button type="button" color="inherit" onClick={onDismiss}>Dismiss</Button>}>
        The local record of this attempt is no longer available. Check the invoice and bank transaction in the books. This screen will not replace it with a new allocation.
      </Alert>
    );
  }

  if (attempt.status === 'blocked-authentication') {
    return <Alert severity="warning">Sign in before this browser can show a stored allocation attempt.</Alert>;
  }

  if (attempt.status === 'blocked-tenant') {
    return <Alert severity="warning">A stored allocation attempt belongs to another organization. Its details stay hidden.</Alert>;
  }

  if (attempt.status === 'invalid-storage') {
    return (
      <Alert severity="error" action={<Button type="button" color="inherit" onClick={onReview}>Review stored attempt</Button>}>
        The stored allocation attempt cannot be read safely. It was not sent.
      </Alert>
    );
  }

  if (attempt.status === 'pending') {
    return (
      <Alert
        severity={attempt.attempt.lifecycle === 'uncertain' ? 'warning' : 'info'}
        action={<Button type="button" color="inherit" onClick={onReview}>Review</Button>}
      >
        <AlertTitle>An allocation attempt is stored for this organization</AlertTitle>
        <StoredAttempt attempt={attempt.attempt} />
      </Alert>
    );
  }

  if (error instanceof StaleSessionError) {
    return (
      <Alert severity="warning">
        Your session changed. The allocation result was not applied to this sign-in, and the request was not sent again.
      </Alert>
    );
  }

  return null;
}

function StoredAttempt({ attempt }: { attempt: Extract<AllocationAttemptView, { status: 'pending' }>['attempt'] }) {
  return (
    <Stack spacing={0.5}>
      <Typography>Invoice {attempt.invoiceId}</Typography>
      <Typography>Bank transaction {attempt.bankTransactionId}</Typography>
      <Typography>Original request {formatMoney(attempt.amount, attempt.currency)}</Typography>
      <Typography>
        {attempt.lifecycle === 'uncertain'
          ? 'The last send ended without a verified result. Sending it again uses the same request.'
          : 'This attempt has not had a verified result.'}
      </Typography>
    </Stack>
  );
}

function ContextDetails({ context }: { context: AllocationContext }) {
  return (
    <Stack spacing={0.5} component="section" aria-label="Current balances">
      <Typography>Invoice {context.invoice.invoiceNumber}</Typography>
      <Typography>Bank reference {context.bankTransaction.reference ?? 'None'}</Typography>
      <Typography>Booking date {context.bankTransaction.bookingDate}</Typography>
      <Typography>Invoice original {formatMoney(context.invoice.totalAmount.amount, context.invoice.totalAmount.currency)}</Typography>
      <Typography>Invoice allocated {formatMoney(context.invoice.allocatedAmount.amount, context.invoice.allocatedAmount.currency)}</Typography>
      <Typography>Invoice outstanding {formatMoney(context.invoice.outstandingAmount.amount, context.invoice.outstandingAmount.currency)}</Typography>
      <Typography>Bank original {formatMoney(context.bankTransaction.amount.amount, context.bankTransaction.amount.currency)}</Typography>
      <Typography>Bank allocated {formatMoney(context.bankTransaction.allocatedAmount.amount, context.bankTransaction.allocatedAmount.currency)}</Typography>
      <Typography>Bank unapplied {formatMoney(context.bankTransaction.unappliedAmount.amount, context.bankTransaction.unappliedAmount.currency)}</Typography>
    </Stack>
  );
}

function VerifiedResult({ result }: { result: VerifiedExplicitAmount }) {
  return (
    <Stack spacing={0.5}>
      <Typography>{result.replayed ? 'This allocation was already recorded.' : 'Allocation recorded.'}</Typography>
      <Typography>Allocation {result.allocationId}</Typography>
      <Typography>{formatMoney(result.amount, result.currency)}</Typography>
    </Stack>
  );
}

function FailureNotice({ error, attempt }: { error: unknown; attempt: AllocationAttemptView }) {
  return <Alert severity="warning">{failureMessage(error, attempt)}</Alert>;
}

function failureMessage(error: unknown, attempt: AllocationAttemptView): string {
  if (error instanceof AllocationSubmissionInProgressError) {
    return 'This attempt is still being sent. Wait until that request finishes before removing local tracking or starting another one.';
  }

  if (error instanceof AllocationStorageError) {
    return 'The attempt could not be stored in this browser tab. The allocation was not sent.';
  }

  if (error instanceof AllocationStorageCleanupError) {
    return attempt.status === 'pending'
      ? 'The response was verified, but local tracking could not be cleared. Send the same stored attempt again.'
      : 'The local record of this attempt is no longer available. Check the invoice and bank transaction in the books before deciding what happened. A new allocation will not be created from this message.';
  }

  if (error instanceof AllocationAttemptChangedError) {
    return 'The response did not match the attempt stored now. The stored attempt was left unchanged.';
  }

  if (error instanceof UnknownAllocationOutcomeError) {
    return 'The request ended before a result was received. Stopping the request does not show that the server rolled the allocation back. Send the same stored attempt again.';
  }

  if (error instanceof UnreadableAllocationResultError) {
    return 'The server returned a result this screen cannot verify. The allocation may already be stored. Send the same stored attempt again.';
  }

  if (error instanceof StaleSessionError) {
    return 'Your session changed before this result could be applied. The request was not sent again.';
  }

  if (error instanceof AllocationAuthenticationError) {
    return 'Sign in with an organization before starting an allocation.';
  }

  if (error instanceof AllocationTenantMismatchError) {
    return 'The stored attempt belongs to another organization and was not sent.';
  }

  if (error instanceof AllocationActorRequiredError) {
    return 'This session has no actor, so the allocation was not sent.';
  }

  if (error instanceof UnresolvedAllocationAttemptError) {
    return 'An unresolved allocation attempt is already stored. It was not replaced.';
  }

  if (error instanceof ApiError) {
    return apiFailureMessage(error);
  }

  if (error instanceof NetworkError) {
    return 'The current balances could not be loaded.';
  }

  return 'The allocation could not be completed from this screen.';
}

function apiFailureMessage(error: ApiError): string {
  const text = [error.title, error.detail].map((part) => part.trim()).filter((part) => part.length > 0).join(' ');

  if (error.status === 500) {
    return `${text} This does not show whether the allocation was saved. Send the same stored attempt again.`.trim();
  }

  if (error.code === 'reconciliation.concurrency_conflict') {
    return `${text} Send the same stored attempt again when you want to retry that request.`.trim();
  }

  if (error.code === 'reconciliation.confirmation.idempotency_payload_conflict') {
    return `${text} This key is already bound to a different request. A new key will not be created automatically.`.trim();
  }

  if (error.status === 409) {
    return `${text} The stored attempt is still here. A different amount is a new allocation and starts only after local tracking is removed.`.trim();
  }

  return text.length > 0 ? text : 'The server rejected the allocation request.';
}

function ContextFailure({ error, recovery }: { error: unknown; recovery: boolean }) {
  const suffix = recovery ? ' You can still send the original stored attempt.' : ' A new allocation was not started.';

  if (error instanceof ApiError && error.status === 404 && error.code === 'reconciliation.invoice.not_found') {
    return <Alert severity="warning">The invoice was not found.{suffix}</Alert>;
  }

  if (error instanceof ApiError && error.status === 404 && error.code === 'reconciliation.bank_transaction.not_found') {
    return <Alert severity="warning">The bank transaction was not found.{suffix}</Alert>;
  }

  if (error instanceof ApiError && (error.status === 401 || error.status === 403)) {
    return <Alert severity="warning">The current balances are not available for this session.{suffix}</Alert>;
  }

  if (error instanceof ApiError && error.code === 'reconciliation.allocation_context.inconsistent_money') {
    return <Alert severity="error">Stored amounts for this pair are inconsistent.{suffix}</Alert>;
  }

  if (error instanceof NetworkError) {
    return <Alert severity="error">The current balances could not be loaded.{suffix}</Alert>;
  }

  if (error instanceof UnexpectedResponseError || error instanceof ApiError) {
    return <Alert severity="error">The current balances could not be read.{suffix}</Alert>;
  }

  return <Alert severity="error">The current balances could not be loaded.{suffix}</Alert>;
}

function BlockedSession() {
  return <Alert severity="warning">Sign in before this browser can show a stored allocation attempt.</Alert>;
}

function BlockedTenant() {
  return <Alert severity="warning">A stored allocation attempt belongs to another organization. Its details stay hidden.</Alert>;
}

function InvalidStorage() {
  return <Alert severity="error">The stored allocation attempt cannot be read safely. It was not sent.</Alert>;
}

function fullyAllocatedSide(outstanding: DecimalString, unapplied: DecimalString): string {
  const outstandingEmpty = outstanding === '0' || outstanding.startsWith('-');
  const unappliedEmpty = unapplied === '0' || unapplied.startsWith('-');

  if (outstandingEmpty && unappliedEmpty) {
    return 'the invoice or the bank transaction';
  }

  return outstandingEmpty ? 'the invoice' : 'the bank transaction';
}

function canAbandon(attempt: AllocationAttemptView): boolean {
  return attempt.status === 'pending' || attempt.status === 'invalid-storage';
}

function contextPairVisible(attempt: AllocationAttemptView): boolean {
  return attempt.status === 'none' || attempt.status === 'pending';
}
