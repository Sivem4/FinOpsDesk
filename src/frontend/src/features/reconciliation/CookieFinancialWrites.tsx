import { Alert, Button, Dialog, DialogActions, DialogContent, DialogTitle, Stack, TextField } from '@mui/material';
import { useQueryClient } from '@tanstack/react-query';
import { parse } from 'lossless-json';
import { useEffect, useRef, useState, useSyncExternalStore } from 'react';

import {
  cookieRevokedMessage,
  cookieUncertainMessage,
  originStillCurrent,
  retryCookieFinancialMutation,
  runCookieFinancialMutation,
  type CookieDispatchResult,
  type CookieSecurityOrigin,
} from '../../shared/api/cookie-financial-post';
import {
  cookieRecoveryVersion,
  dismissRejectedRecovery,
  readRecovery,
  subscribeCookieRecovery,
  type CookieRecoveryOperation,
  type CookieRecoveryRecord,
} from '../../shared/api/cookie-recovery';
import { useSession } from '../../shared/auth/session';
import { readMoney, serializeAllocationBody, type DecimalString } from '../../shared/money/money';
import type { ExactMatchItem } from './reconciliation-api';

type Notice =
  | { kind: 'success'; text: string; origin: CookieSecurityOrigin }
  | { kind: 'rejected'; text: string; origin: CookieSecurityOrigin }
  | { kind: 'uncertain'; text: string; origin: CookieSecurityOrigin }
  | { kind: 'blocked'; text: string };

export function CookieConfirmationControls({
  item,
  onOpen,
}: {
  item: ExactMatchItem;
  onOpen: (item: ExactMatchItem) => void;
}) {
  const session = useSession();
  if (!session.capabilities.includes('reconciliation.confirm')) {
    return null;
  }

  return (
    <Button type="button" onClick={() => onOpen(item)}>
      Confirm allocation
    </Button>
  );
}

export function CookieConfirmationDialog({
  item,
  onClose,
}: {
  item: ExactMatchItem | null;
  onClose: () => void;
}) {
  const session = useSession();
  const queryClient = useQueryClient();
  useSyncExternalStore(subscribeCookieRecovery, cookieRecoveryVersion, cookieRecoveryVersion);
  const recovery = readRecovery('ConfirmExactMatch', session);
  const [notice, setNotice] = useState<Notice | null>(null);
  const [pending, setPending] = useState(false);
  const allowed = session.capabilities.includes('reconciliation.confirm');
  useIdentityReset(setNotice, () => onClose());

  async function send(record: CookieRecoveryRecord | {
    operation: 'ConfirmExactMatch';
    requestBody: string;
    context: Record<string, string>;
  }): Promise<void> {
    setPending(true);
    setNotice(null);
    try {
      const result = 'idempotencyKey' in record
        ? await retryCookieFinancialMutation('ConfirmExactMatch', session.signal)
        : await runCookieFinancialMutation({ ...record, signal: session.signal });
      applyResult(result, setNotice, async (origin) => {
        await queryClient.invalidateQueries({ queryKey: ['matches', origin.epoch, origin.readiness] });
        await queryClient.invalidateQueries({ queryKey: ['allocations', 'history', origin.epoch, origin.readiness] });
      }, 'Allocation recorded.');
    } finally {
      setPending(false);
    }
  }

  return (
    <>
      <RecoveryNotice operation="ConfirmExactMatch" recovery={recovery} allowed={allowed} pending={pending} onRetry={() => void send(recovery.status === 'recoverable' ? recovery.record : blockedRecord())} />
      {recovery.status === 'invalid' || recovery.status === 'hidden' ? <BlockedStorage /> : null}
      <Dialog open={item !== null} onClose={onClose} fullWidth maxWidth="sm">
        <DialogTitle>Confirm exact match</DialogTitle>
        <DialogContent>
          <Stack spacing={2} sx={{ pt: 1 }}>
            {item ? <span>{`Invoice ${item.invoiceId} and bank transaction ${item.bankTransactionId}`}</span> : null}
            <VisibleNotice notice={notice} />
          </Stack>
        </DialogContent>
        <DialogActions>
          <Button type="button" onClick={onClose}>Close</Button>
          {item && allowed ? (
            <Button
              type="button"
              variant="contained"
              disabled={pending || recovery.status === 'recoverable' || recovery.status === 'invalid' || recovery.status === 'hidden'}
              onClick={() => {
                void send({
                  operation: 'ConfirmExactMatch',
                  requestBody: confirmationBody(item),
                  context: {
                    invoiceId: item.invoiceId,
                    bankTransactionId: item.bankTransactionId,
                  },
                });
              }}
            >
              Confirm allocation
            </Button>
          ) : null}
        </DialogActions>
      </Dialog>
    </>
  );
}

export function CookieAllocationForm() {
  const session = useSession();
  const queryClient = useQueryClient();
  useSyncExternalStore(subscribeCookieRecovery, cookieRecoveryVersion, cookieRecoveryVersion);
  const recovery = readRecovery('CreateAllocation', session);
  const [invoiceId, setInvoiceId] = useState('');
  const [bankTransactionId, setBankTransactionId] = useState('');
  const [amount, setAmount] = useState('');
  const [notice, setNotice] = useState<Notice | null>(null);
  const [pending, setPending] = useState(false);
  const allowed = session.capabilities.includes('allocations.create');
  useIdentityReset(setNotice, () => {
    setInvoiceId('');
    setBankTransactionId('');
    setAmount('');
  });
  if (!allowed && recovery.status !== 'recoverable' && recovery.status !== 'invalid' && recovery.status !== 'hidden') {
    return null;
  }

  async function send(retry: boolean): Promise<void> {
    setPending(true);
    try {
      let result: CookieDispatchResult;
      if (retry) {
        result = await retryCookieFinancialMutation('CreateAllocation', session.signal);
      } else {
        const requestBody = serializeAllocationBody({
          invoiceId: invoiceId.trim(),
          bankTransactionId: bankTransactionId.trim(),
          currency: 'PLN',
          amount: amount.trim(),
        });
        const parsed: unknown = parse(requestBody);
        const money = readMoney(isMoneyRecord(parsed) ? parsed.amount : null);
        result = await runCookieFinancialMutation({
          operation: 'CreateAllocation',
          requestBody,
          context: {
            invoiceId: invoiceId.trim(),
            bankTransactionId: bankTransactionId.trim(),
            currency: money.currency,
            amount: money.amount,
          },
          signal: session.signal,
        });
      }
      applyResult(result, setNotice, async (origin) => {
        await queryClient.invalidateQueries({ queryKey: ['allocations', 'history', origin.epoch, origin.readiness] });
      }, 'Allocation recorded.');
    } catch {
      setNotice({ kind: 'blocked', text: 'The financial request was not sent.' });
    } finally {
      setPending(false);
    }
  }

  return (
    <Stack spacing={2} component="section" aria-label="Allocate an amount">
      <RecoveryNotice operation="CreateAllocation" recovery={recovery} allowed={allowed} pending={pending} onRetry={() => void send(true)} />
      {recovery.status === 'invalid' || recovery.status === 'hidden' ? <BlockedStorage /> : null}
      {allowed ? (
        <Stack spacing={1}>
          <TextField label="Invoice" value={invoiceId} onChange={(event) => setInvoiceId(event.target.value)} />
          <TextField label="Bank transaction" value={bankTransactionId} onChange={(event) => setBankTransactionId(event.target.value)} />
          <TextField label="Amount" value={amount} onChange={(event) => setAmount(event.target.value)} />
          <Button type="button" variant="contained" disabled={pending || recovery.status !== 'none'} onClick={() => void send(false)}>
            Allocate an amount
          </Button>
        </Stack>
      ) : null}
      <VisibleNotice notice={notice} />
    </Stack>
  );
}

export function CookieReversalRecovery() {
  const session = useSession();
  const queryClient = useQueryClient();
  useSyncExternalStore(subscribeCookieRecovery, cookieRecoveryVersion, cookieRecoveryVersion);
  const recovery = readRecovery('ReverseAllocation', session);
  const [notice, setNotice] = useState<Notice | null>(null);
  const [pending, setPending] = useState(false);
  const allowed = session.capabilities.includes('allocations.reverse');
  useIdentityReset(setNotice, () => undefined);
  if (recovery.status === 'none') {
    return null;
  }

  return (
    <Stack spacing={1} component="section" aria-label="Reversal recovery">
      <RecoveryNotice
        operation="ReverseAllocation"
        recovery={recovery}
        allowed={allowed}
        pending={pending}
        onRetry={() => {
          setPending(true);
          void retryCookieFinancialMutation('ReverseAllocation', session.signal)
            .then((result) => applyResult(result, setNotice, async (origin) => {
              await queryClient.invalidateQueries({ queryKey: ['allocations', 'history', origin.epoch, origin.readiness] });
            }, 'Reversal recorded.'))
            .finally(() => setPending(false));
        }}
      />
      {recovery.status === 'invalid' || recovery.status === 'hidden' ? <BlockedStorage /> : null}
      <VisibleNotice notice={notice} />
    </Stack>
  );
}

export function CookieReversalControl({
  allocationId,
  invoiceId,
  bankTransactionId,
  amount,
  currency,
}: {
  allocationId: string;
  invoiceId: string;
  bankTransactionId: string;
  amount: DecimalString;
  currency: string;
}) {
  const session = useSession();
  const queryClient = useQueryClient();
  useSyncExternalStore(subscribeCookieRecovery, cookieRecoveryVersion, cookieRecoveryVersion);
  const recovery = readRecovery('ReverseAllocation', session);
  const [reason, setReason] = useState('');
  const [open, setOpen] = useState(false);
  const [notice, setNotice] = useState<Notice | null>(null);
  const [pending, setPending] = useState(false);
  const allowed = session.capabilities.includes('allocations.reverse');
  useIdentityReset(setNotice, () => setOpen(false));
  if (!allowed) {
    return null;
  }

  return (
    <Stack spacing={1}>
      <Button type="button" onClick={() => setOpen(true)} disabled={pending || recovery.status !== 'none'}>
        Reverse allocation
      </Button>
      <Dialog open={open} onClose={() => setOpen(false)} fullWidth maxWidth="sm">
        <DialogTitle>Reverse allocation</DialogTitle>
        <DialogContent>
          <TextField label="Reason" value={reason} onChange={(event) => setReason(event.target.value)} fullWidth />
          <VisibleNotice notice={notice} />
        </DialogContent>
        <DialogActions>
          <Button type="button" onClick={() => setOpen(false)}>Close</Button>
          <Button
            type="button"
            variant="contained"
            disabled={pending || recovery.status !== 'none'}
            onClick={() => {
              const trimmed = reason.trim();
              if (trimmed.length < 2) {
                setNotice({ kind: 'blocked', text: 'The reversal was not sent.' });
                return;
              }

              setPending(true);
              void runCookieFinancialMutation({
                operation: 'ReverseAllocation',
                requestBody: JSON.stringify({ reason: trimmed }),
                context: {
                  allocationId,
                  invoiceId,
                  bankTransactionId,
                  reason: trimmed,
                  amount,
                  currency,
                },
                signal: session.signal,
              }).then((result) => applyResult(result, setNotice, async (origin) => {
                await queryClient.invalidateQueries({ queryKey: ['allocations', 'history', origin.epoch, origin.readiness] });
              }, 'Reversal recorded.')).finally(() => setPending(false));
            }}
          >
            Reverse allocation
          </Button>
        </DialogActions>
      </Dialog>
    </Stack>
  );
}

function RecoveryNotice({
  recovery,
  allowed,
  pending,
  onRetry,
}: {
  operation: CookieRecoveryOperation;
  recovery: ReturnType<typeof readRecovery>;
  allowed: boolean;
  pending: boolean;
  onRetry: () => void;
}) {
  if (recovery.status !== 'recoverable') {
    return null;
  }

  if (recovery.record.disposition === 'rejected') {
    return (
      <Alert
        severity="error"
        action={(
          <Button
            type="button"
            color="inherit"
            onClick={() => {
              dismissRejectedRecovery(recovery.record.operation);
            }}
          >
            Discard this rejected attempt
          </Button>
        )}
      >
        This attempt was rejected before the financial change was saved. Discard it before starting another one.
      </Alert>
    );
  }

  if (!allowed) {
    return <Alert severity="warning">{cookieRevokedMessage}</Alert>;
  }

  return (
    <Alert
      severity="warning"
      action={<Button type="button" color="inherit" disabled={pending} onClick={onRetry}>Send the same request again</Button>}
    >
      {cookieUncertainMessage}
    </Alert>
  );
}

function BlockedStorage() {
  return <Alert severity="warning">An unresolved financial attempt is already stored. A new request was not sent.</Alert>;
}

function VisibleNotice({ notice }: { notice: Notice | null }) {
  if (notice === null) {
    return null;
  }

  if (notice.kind !== 'blocked' && !originStillCurrent(notice.origin)) {
    return null;
  }

  return <Alert severity={severity(notice.kind)}>{notice.text}</Alert>;
}

function applyResult(
  result: CookieDispatchResult,
  setNotice: (notice: Notice | null) => void,
  onConfirmed: (origin: CookieSecurityOrigin) => Promise<void>,
  successText: string,
): void {
  if (result.kind === 'stale') {
    setNotice(null);
    return;
  }

  if (result.kind === 'blocked') {
    setNotice({ kind: 'blocked', text: result.text });
    return;
  }

  if (!originStillCurrent(result.origin)) {
    setNotice(null);
    return;
  }

  if (result.kind === 'confirmed') {
    void onConfirmed(result.origin);
    setNotice({
      kind: 'success',
      text: result.cleanup === 'failed' ? result.text : successText,
      origin: result.origin,
    });
    return;
  }

  setNotice({ kind: result.kind, text: result.text, origin: result.origin });
}

function useIdentityReset(setNotice: (notice: Notice | null) => void, reset: () => void): void {
  const session = useSession();
  const identity = `${session.issuer ?? ''}|${session.subject ?? ''}|${session.activeOrganization?.organizationId ?? ''}`;
  const previous = useRef(identity);
  useEffect(() => {
    if (previous.current === identity) {
      return;
    }

    previous.current = identity;
    setNotice(null);
    reset();
  }, [identity, reset, setNotice]);
}

function isMoneyRecord(value: unknown): value is { amount: unknown } {
  return typeof value === 'object' && value !== null && 'amount' in value;
}

function confirmationBody(item: ExactMatchItem): string {
  return `{"invoiceId":${JSON.stringify(item.invoiceId)},"bankTransactionId":${JSON.stringify(item.bankTransactionId)}}`;
}

function blockedRecord(): CookieRecoveryRecord {
  return {
    schemaVersion: 2,
    operation: 'ConfirmExactMatch',
    issuer: '',
    subject: '',
    organizationId: '',
    idempotencyKey: '',
    requestBody: '',
    fingerprint: '',
    disposition: 'dispatched',
    context: {},
  };
}

function severity(kind: Notice['kind']): 'success' | 'warning' | 'error' | 'info' {
  if (kind === 'success') {
    return 'success';
  }

  if (kind === 'rejected' || kind === 'blocked') {
    return 'error';
  }

  return 'warning';
}
