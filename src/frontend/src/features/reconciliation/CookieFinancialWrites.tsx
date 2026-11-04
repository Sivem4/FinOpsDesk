import { Alert, Button, Dialog, DialogActions, DialogContent, DialogTitle, Stack, TextField } from '@mui/material';
import { useQueryClient, type QueryClient, type QueryKey } from '@tanstack/react-query';
import { parse } from 'lossless-json';
import { createContext, useCallback, useContext, useEffect, useRef, useState, useSyncExternalStore, type Dispatch, type ReactNode, type SetStateAction } from 'react';

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

const confirmedStaleMessage = 'The financial operation was confirmed, but the displayed data could not be refreshed. The lists may be outdated.';

type Notice =
  | { kind: 'success'; text: string; origin: CookieSecurityOrigin; generation: number }
  | { kind: 'rejected'; text: string; origin: CookieSecurityOrigin; generation: number }
  | { kind: 'uncertain'; text: string; origin: CookieSecurityOrigin; generation: number }
  | {
    kind: 'confirmed-stale';
    text: string;
    successText: string;
    origin: CookieSecurityOrigin;
    queryKeys: readonly QueryKey[];
    refreshing: boolean;
    generation: number;
  }
  | { kind: 'blocked'; text: string; generation: number };

type RefreshLifecycle = { generation: number; manual: boolean };

const ReversalOutcomeContext = createContext<((result: CookieDispatchResult) => void) | null>(null);

type RefreshOutcome = 'refreshed' | 'failed' | 'ignored';

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
  const refreshLifecycle = useRef<RefreshLifecycle>({ generation: 0, manual: false });
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
      applyResult(result, setNotice, queryClient, refreshLifecycle, 'Allocation recorded.');
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
            <VisibleNotice notice={notice} onRefresh={() => retryConfirmedRefresh(notice, setNotice, queryClient, refreshLifecycle)} />
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
  const refreshLifecycle = useRef<RefreshLifecycle>({ generation: 0, manual: false });
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
      applyResult(result, setNotice, queryClient, refreshLifecycle, 'Allocation recorded.');
    } catch {
      refreshLifecycle.current.generation += 1;
      setNotice({ kind: 'blocked', text: 'The financial request was not sent.', generation: refreshLifecycle.current.generation });
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
      <VisibleNotice notice={notice} onRefresh={() => retryConfirmedRefresh(notice, setNotice, queryClient, refreshLifecycle)} />
    </Stack>
  );
}

export function CookieReversalOutcome({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient();
  const [notice, setNotice] = useState<Notice | null>(null);
  const refreshLifecycle = useRef<RefreshLifecycle>({ generation: 0, manual: false });
  const report = useCallback((result: CookieDispatchResult) => {
    applyResult(result, setNotice, queryClient, refreshLifecycle, 'Reversal recorded.');
  }, [queryClient]);
  useIdentityReset(setNotice, () => undefined);

  return (
    <ReversalOutcomeContext.Provider value={report}>
      <VisibleNotice notice={notice} onRefresh={() => retryConfirmedRefresh(notice, setNotice, queryClient, refreshLifecycle)} />
      {children}
    </ReversalOutcomeContext.Provider>
  );
}

export function CookieReversalRecovery() {
  const session = useSession();
  const queryClient = useQueryClient();
  const reportReversal = useContext(ReversalOutcomeContext);
  useSyncExternalStore(subscribeCookieRecovery, cookieRecoveryVersion, cookieRecoveryVersion);
  const recovery = readRecovery('ReverseAllocation', session);
  const [notice, setNotice] = useState<Notice | null>(null);
  const [pending, setPending] = useState(false);
  const refreshLifecycle = useRef<RefreshLifecycle>({ generation: 0, manual: false });
  const allowed = session.capabilities.includes('allocations.reverse');
  useIdentityReset(setNotice, () => undefined);
  if (recovery.status === 'none' && notice === null) {
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
            .then((result) => deliverReversalResult(result, reportReversal, setNotice, queryClient, refreshLifecycle))
            .finally(() => setPending(false));
        }}
      />
      {recovery.status === 'invalid' || recovery.status === 'hidden' ? <BlockedStorage /> : null}
      <VisibleNotice notice={notice} onRefresh={() => retryConfirmedRefresh(notice, setNotice, queryClient, refreshLifecycle)} />
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
  const reportReversal = useContext(ReversalOutcomeContext);
  useSyncExternalStore(subscribeCookieRecovery, cookieRecoveryVersion, cookieRecoveryVersion);
  const recovery = readRecovery('ReverseAllocation', session);
  const [reason, setReason] = useState('');
  const [open, setOpen] = useState(false);
  const [notice, setNotice] = useState<Notice | null>(null);
  const [pending, setPending] = useState(false);
  const refreshLifecycle = useRef<RefreshLifecycle>({ generation: 0, manual: false });
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
          <VisibleNotice notice={notice} onRefresh={() => retryConfirmedRefresh(notice, setNotice, queryClient, refreshLifecycle)} />
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
                refreshLifecycle.current.generation += 1;
                setNotice({ kind: 'blocked', text: 'The reversal was not sent.', generation: refreshLifecycle.current.generation });
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
              }).then((result) => deliverReversalResult(result, reportReversal, setNotice, queryClient, refreshLifecycle)).finally(() => setPending(false));
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

function VisibleNotice({ notice, onRefresh }: { notice: Notice | null; onRefresh: () => void }) {
  if (notice === null) {
    return null;
  }

  if (notice.kind !== 'blocked' && !originStillCurrent(notice.origin)) {
    return null;
  }

  if (notice.kind === 'confirmed-stale') {
    return (
      <>
        <Alert severity="success">{notice.text}</Alert>
        <Alert
          severity="warning"
          action={(
            <Button type="button" color="inherit" disabled={notice.refreshing} onClick={onRefresh}>
              Refresh displayed data
            </Button>
          )}
        >
          {confirmedStaleMessage}
        </Alert>
      </>
    );
  }

  return <Alert severity={severity(notice.kind)}>{notice.text}</Alert>;
}

function deliverReversalResult(
  result: CookieDispatchResult,
  reportReversal: ((result: CookieDispatchResult) => void) | null,
  setNotice: Dispatch<SetStateAction<Notice | null>>,
  queryClient: QueryClient,
  refreshLifecycle: { current: RefreshLifecycle },
): void {
  if (reportReversal !== null && result.kind === 'confirmed') {
    setNotice(null);
    reportReversal(result);
    return;
  }

  applyResult(result, setNotice, queryClient, refreshLifecycle, 'Reversal recorded.');
}

function applyResult(
  result: CookieDispatchResult,
  setNotice: Dispatch<SetStateAction<Notice | null>>,
  queryClient: QueryClient,
  refreshLifecycle: { current: RefreshLifecycle },
  successText: string,
): void {
  const generation = refreshLifecycle.current.generation + 1;
  refreshLifecycle.current.generation = generation;

  if (result.kind === 'stale' || (result.kind !== 'blocked' && !originStillCurrent(result.origin))) {
    setNotice(null);
    return;
  }

  if (result.kind === 'blocked') {
    setNotice({ kind: 'blocked', text: result.text, generation });
    return;
  }

  if (result.kind === 'confirmed') {
    const text = result.cleanup === 'failed' ? result.text : successText;
    setNotice({ kind: 'success', text, origin: result.origin, generation });
    beginConfirmedRefresh(queryClient, setNotice, result.origin, text, generation);
    return;
  }

  setNotice({ kind: result.kind, text: result.text, origin: result.origin, generation });
}

function beginConfirmedRefresh(
  queryClient: QueryClient,
  setNotice: Dispatch<SetStateAction<Notice | null>>,
  origin: CookieSecurityOrigin,
  successText: string,
  generation: number,
): void {
  if (!originStillCurrent(origin)) {
    return;
  }

  const queryKeys = financialReadKeys(origin);
  void refreshFinancialReads(queryClient, origin, queryKeys).then((outcome) => {
    publishRefresh(setNotice, origin, successText, queryKeys, generation, outcome);
  });
}

function retryConfirmedRefresh(
  notice: Notice | null,
  setNotice: Dispatch<SetStateAction<Notice | null>>,
  queryClient: QueryClient,
  refreshLifecycle: { current: RefreshLifecycle },
): void {
  if (notice === null || notice.kind !== 'confirmed-stale' || notice.refreshing || refreshLifecycle.current.manual) {
    return;
  }

  if (!originStillCurrent(notice.origin)) {
    return;
  }

  refreshLifecycle.current.manual = true;
  const { origin, successText, queryKeys, generation } = notice;
  setNotice({ ...notice, refreshing: true });
  void refreshFinancialReads(queryClient, origin, queryKeys).then((outcome) => {
    refreshLifecycle.current.manual = false;
    publishRefresh(setNotice, origin, successText, queryKeys, generation, outcome);
  });
}

async function refreshFinancialReads(
  queryClient: QueryClient,
  origin: CookieSecurityOrigin,
  queryKeys: readonly QueryKey[],
): Promise<RefreshOutcome> {
  try {
    if (!originStillCurrent(origin)) {
      return 'ignored';
    }

    await Promise.all(queryKeys.map((queryKey) => queryClient.invalidateQueries(
      { queryKey, refetchType: 'active' },
      { throwOnError: true },
    )));

    return originStillCurrent(origin) ? 'refreshed' : 'ignored';
  } catch {
    return originStillCurrent(origin) ? 'failed' : 'ignored';
  }
}

function publishRefresh(
  setNotice: Dispatch<SetStateAction<Notice | null>>,
  origin: CookieSecurityOrigin,
  successText: string,
  queryKeys: readonly QueryKey[],
  generation: number,
  outcome: RefreshOutcome,
): void {
  setNotice((latest) => {
    if (latest === null || latest.generation !== generation || !originStillCurrent(origin) || outcome === 'ignored') {
      return latest;
    }

    if (outcome === 'refreshed') {
      return { kind: 'success', text: successText, origin, generation };
    }

    return {
      kind: 'confirmed-stale',
      text: successText,
      successText,
      origin,
      queryKeys,
      refreshing: false,
      generation,
    };
  });
}

function financialReadKeys(origin: CookieSecurityOrigin): readonly QueryKey[] {
  const history: QueryKey = ['allocations', 'history', origin.epoch, origin.readiness];
  if (origin.capability === 'reconciliation.confirm') {
    return [['matches', origin.epoch, origin.readiness], history];
  }

  return [history];
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
