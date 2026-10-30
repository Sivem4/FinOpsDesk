import {
  Alert,
  AlertTitle,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Stack,
  Typography,
} from '@mui/material';

import { ApiError, StaleSessionError } from '../../shared/api/errors';
import { endSession } from '../../shared/auth/session';
import { formatMoney, type DecimalString } from '../../shared/money/money';
import {
  UnknownConfirmationOutcomeError,
  UnreadableConfirmationResultError,
  type ExactConfirmationResult,
} from './confirmation-api';

export interface ConfirmationPairView {
  invoiceId: string;
  bankTransactionId: string;
  reference: string;
  displayedAmount: DecimalString;
  displayedCurrency: string;
}

const reloadLimitation =
  'Reloading the browser discards this attempt. Leaving the Reconciliation page discards it too. A later confirmation is a new operation with a new idempotency key and cannot tell you whether this one was saved.';

const refreshLimitation =
  'Refreshing the list does not prove whether this confirmation was saved.';

export function ExactMatchConfirmationDialog({
  open,
  pair,
  pending,
  result,
  error,
  onClose,
  onConfirm,
  onLeave,
}: {
  open: boolean;
  pair: ConfirmationPairView | null;
  pending: boolean;
  result: ExactConfirmationResult | null;
  error: unknown;
  onClose: () => void;
  onConfirm: () => void;
  onLeave: () => void;
}) {
  return (
    <Dialog open={open && pair !== null} onClose={onClose} aria-labelledby="exact-confirmation-title" fullWidth maxWidth="sm" transitionDuration={0}>
      {pair ? (
        <>
          <DialogTitle id="exact-confirmation-title">Confirm exact match</DialogTitle>
          <DialogContent>
            <Stack spacing={2} sx={{ pt: 1 }}>
              <PairSummary pair={pair} />
              <Alert severity="warning">
                <AlertTitle>This match is not reserved</AlertTitle>
                The books can change before the server accepts this request, so the amount in this read can be out of date.
                The server calculates the full current open amount for this invoice and this bank transaction.
                It checks this pair. It does not check whether the pair is still the only exact match in the whole book.
              </Alert>
              {pending ? <Alert severity="info" role="status">Confirmation in progress</Alert> : null}
              {result ? <ConfirmationSuccess result={result} /> : null}
              {error ? <ConfirmationFailure error={error} onRetry={onConfirm} onLeave={onLeave} /> : null}
            </Stack>
          </DialogContent>
          <DialogActions>
            <Button type="button" onClick={onClose}>Close</Button>
            {result === null && error === null ? (
              <Button type="button" variant="contained" onClick={onConfirm} disabled={pending}>
                Confirm allocation
              </Button>
            ) : null}
          </DialogActions>
        </>
      ) : null}
    </Dialog>
  );
}

export function ConfirmationAttemptBanner({
  pair,
  pending,
  result,
  error,
  onReview,
  onDismissSuccess,
}: {
  pair: ConfirmationPairView | null;
  pending: boolean;
  result: ExactConfirmationResult | null;
  error: unknown;
  onReview: () => void;
  onDismissSuccess: () => void;
}) {
  if (error instanceof StaleSessionError) {
    return (
      <Alert severity="warning">
        Your session changed. This confirmation was not sent again under the new sign-in. A cancelled request does not show that the server rolled the attempt back.
      </Alert>
    );
  }

  if (result) {
    return (
      <Alert
        severity="success"
        role="status"
        action={<Button type="button" color="inherit" onClick={onDismissSuccess}>Done</Button>}
      >
        <ConfirmationSuccess result={result} />
      </Alert>
    );
  }

  if (pair === null && error === null && !pending) {
    return null;
  }

  return (
    <Alert
      severity={pending ? 'info' : 'warning'}
      role="status"
      action={<Button type="button" color="inherit" onClick={onReview}>Review the confirmation attempt</Button>}
    >
      <AlertTitle>{pending ? 'Confirmation in progress' : 'Confirmation attempt is still open'}</AlertTitle>
      {pair ? `${pair.reference} remains the confirmation in progress for this page.` : 'A confirmation attempt is still open on this page.'}
      {' '}
      Closing this notice does not start a new confirmation.
      {isUncertainConfirmationError(error) ? ` ${reloadLimitation} ${refreshLimitation}` : null}
    </Alert>
  );
}

function isUncertainConfirmationError(error: unknown): boolean {
  if (error instanceof UnknownConfirmationOutcomeError || error instanceof UnreadableConfirmationResultError) {
    return true;
  }

  return error instanceof ApiError && error.status !== 400 && error.status !== 401 && error.status !== 403 && error.status !== 404 && error.status !== 409;
}

function PairSummary({ pair }: { pair: ConfirmationPairView }) {
  return (
    <Stack spacing={1}>
      <Typography>Reference {pair.reference}</Typography>
      <Typography>Amount in this read {formatMoney(pair.displayedAmount, pair.displayedCurrency)}</Typography>
      <Typography sx={{ fontFamily: 'monospace' }}>Invoice {pair.invoiceId}</Typography>
      <Typography sx={{ fontFamily: 'monospace' }}>Bank transaction {pair.bankTransactionId}</Typography>
      <Typography>
        Confirming allocates the full current matching open amount. The server checks that amount when the request arrives.
      </Typography>
    </Stack>
  );
}

function ConfirmationSuccess({ result }: { result: ExactConfirmationResult }) {
  return (
    <Stack spacing={1}>
      <AlertTitle>{result.replayed ? 'This confirmation was already recorded.' : 'Allocation recorded.'}</AlertTitle>
      <Typography>Amount recorded {formatMoney(result.amount, result.currency)}</Typography>
      <Typography sx={{ fontFamily: 'monospace' }}>Allocation {result.allocationId}</Typography>
    </Stack>
  );
}

function ConfirmationFailure({
  error,
  onRetry,
  onLeave,
}: {
  error: unknown;
  onRetry: () => void;
  onLeave: () => void;
}) {
  if (error instanceof UnknownConfirmationOutcomeError || error instanceof UnreadableConfirmationResultError) {
    return (
      <Alert severity="warning">
        <AlertTitle>We couldn’t confirm the result</AlertTitle>
        We don’t know whether this confirmation was saved. Send the same confirmation again. {reloadLimitation} {refreshLimitation}
        <Stack direction="row" spacing={1} sx={{ mt: 1 }}>
          <Button type="button" color="inherit" onClick={onRetry}>Send the same confirmation again</Button>
        </Stack>
      </Alert>
    );
  }

  if (error instanceof StaleSessionError) {
    return (
      <Alert severity="warning">
        Your session changed. This confirmation was not sent again under the new sign-in. A cancelled request does not show that the server rolled the attempt back.
      </Alert>
    );
  }

  if (error instanceof ApiError && error.status === 401) {
    return (
      <Alert
        severity="warning"
        action={<Button type="button" color="inherit" onClick={() => endSession('unauthorized')}>Update access</Button>}
      >
        Your access has expired or is no longer valid. Update it to continue.
      </Alert>
    );
  }

  if (error instanceof ApiError && error.status === 403) {
    return (
      <Alert
        severity="warning"
        action={<Button type="button" color="inherit" onClick={() => endSession('forbidden')}>Update access</Button>}
      >
        You don’t have access to this workspace. Check your access details.
      </Alert>
    );
  }

  if (error instanceof ApiError && (error.status === 400 || error.status === 404 || error.status === 409)) {
    return (
      <Alert severity="error">
        <AlertTitle>{error.title}</AlertTitle>
        {error.detail}
        {error.code ? <Typography variant="body2">{error.code}</Typography> : null}
        <Typography variant="body2" sx={{ mt: 1 }}>
          {error.code === 'reconciliation.confirmation.idempotency_payload_conflict'
            ? 'A new idempotency key was not created.'
            : 'This confirmation was rejected before it was saved. Refresh shows the current book, but it does not prove the result of a different attempt.'}
        </Typography>
        <Stack direction="row" spacing={1} sx={{ mt: 1 }}>
          <Button type="button" color="inherit" onClick={onRetry}>Send the same confirmation again</Button>
          <Button type="button" color="inherit" onClick={onLeave}>Leave this attempt</Button>
        </Stack>
      </Alert>
    );
  }

  if (error instanceof ApiError) {
    return (
      <Alert severity="warning">
        <AlertTitle>We couldn’t confirm the result</AlertTitle>
        The server returned an error. This does not show whether the allocation was saved. {reloadLimitation} {refreshLimitation}
        <Typography variant="body2" sx={{ mt: 1 }}>{[error.detail, error.title].filter((part) => part.trim().length > 0).join(' ')}</Typography>
        <Stack direction="row" spacing={1} sx={{ mt: 1 }}>
          <Button type="button" color="inherit" onClick={onRetry}>Send the same confirmation again</Button>
        </Stack>
      </Alert>
    );
  }

  return (
    <Alert severity="warning">
      <AlertTitle>We couldn’t confirm the result</AlertTitle>
      {reloadLimitation} {refreshLimitation}
      <Stack direction="row" spacing={1} sx={{ mt: 1 }}>
        <Button type="button" color="inherit" onClick={onRetry}>Send the same confirmation again</Button>
      </Stack>
    </Alert>
  );
}
