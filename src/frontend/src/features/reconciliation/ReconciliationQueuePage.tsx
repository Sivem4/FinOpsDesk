import {
  Alert,
  AlertTitle,
  Button,
  Paper,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TablePagination,
  TableRow,
  ToggleButton,
  ToggleButtonGroup,
  Typography,
} from '@mui/material';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useRef, useState, type ReactNode } from 'react';

import { ApiError, NetworkError, StaleSessionError, UnexpectedResponseError } from '../../shared/api/errors';
import { sessionStore } from '../../shared/api/session-store';
import { endSession, useSession } from '../../shared/auth/session';
import { formatMoney, type DecimalString } from '../../shared/money/money';
import { getAllocationContext } from './allocation-context-api';
import {
  AllocationStorageCleanupError,
  inspectAllocationAttempt,
  readAllocationOrganizationId,
  useAllocationAttempt,
} from './allocation-attempt';
import {
  ConfirmationAttemptBanner,
  ExactMatchConfirmationDialog,
  type ConfirmationPairView,
} from './ExactMatchConfirmationDialog';
import { AllocationRecoveryBanner, ManualAllocationDialog } from './ManualAllocationDialog';
import { assessAllocationAmount, describeAllocationBalances } from './manual-allocation-rules';
import { useManualAllocation } from './useManualAllocation';
import {
  createConfirmationAttempt,
  postExactConfirmation,
  type ConfirmationAttempt,
  type ExactConfirmationResult,
} from './confirmation-api';
import {
  getExactMatches,
  getSmartMatches,
  type AmbiguousMatchGroup,
  type ClassifiedMatchRecord,
  type ExactMatchItem,
  type ExactMatchResponse,
  type SmartMatchResponse,
  type SmartMatchSuggestion,
} from './reconciliation-api';

const pageSize = 25;

type MatchingView = 'exact' | 'smart';

const reasonLabels: Record<string, string> = {
  ExactReferenceCurrencyAndOpenAmount: 'Exact reference, currency, and open amount',
  CompetingInvoices: 'Competing invoices',
  CompetingTransactions: 'Competing transactions',
  CompetingInvoicesAndTransactions: 'Competing invoices and transactions',
  NoCounterpart: 'No counterpart',
  MissingReference: 'Missing reference',
  NonPositiveOpenAmount: 'No positive open amount',
};

const signalLabels: Record<string, string> = {
  ReferenceExact: 'Exact reference',
  ReferenceTokenSetEqual: 'Same reference tokens',
  CompactFormEqual: 'Same compact form',
  NumericIdentifierEqual: 'Shared numeric identifier',
  AmountExact: 'Open amounts are equal',
  AmountDiffers: 'Open amounts differ',
  ContestedCandidates: 'Another candidate is close',
  ExactMatchAmbiguous: 'This pair is also an ambiguous exact match',
};

interface ConfirmationDraft {
  pair: ConfirmationPairView;
  epoch: number;
}

interface ActiveConfirmation {
  attempt: ConfirmationAttempt;
  pair: ConfirmationPairView;
}

export function ReconciliationQueuePage() {
  const session = useSession();
  const queryClient = useQueryClient();
  const [view, setView] = useState<MatchingView>('exact');
  const [draft, setDraft] = useState<ConfirmationDraft | null>(null);
  const [activeConfirmation, setActiveConfirmation] = useState<ActiveConfirmation | null>(null);
  const [dialogOpen, setDialogOpen] = useState(false);
  const [operationEpoch, setOperationEpoch] = useState<number | null>(null);
  const [successDismissed, setSuccessDismissed] = useState(true);
  const submitting = useRef(false);
  const requestController = useRef<AbortController | null>(null);
  const allocationAttempt = useAllocationAttempt();
  const manualAllocation = useManualAllocation();
  const [allocationOpen, setAllocationOpen] = useState(false);
  const [redirectedFromOtherPair, setRedirectedFromOtherPair] = useState(false);
  const [draftPair, setDraftPair] = useState<AllocationDraft | null>(null);
  const visibleAttempt = activeConfirmation !== null && activeConfirmation.attempt.epoch === session.epoch
    ? activeConfirmation
    : null;
  const visibleDraft = draft !== null && draft.epoch === session.epoch ? draft : null;

  if (activeConfirmation !== null && visibleAttempt === null) {
    setActiveConfirmation(null);
  }

  if (draft !== null && visibleDraft === null) {
    setDraft(null);
  }

  if (dialogOpen && visibleAttempt === null && visibleDraft === null) {
    setDialogOpen(false);
  }
  const storedAllocationPair = allocationAttempt.status === 'pending'
    ? { invoiceId: allocationAttempt.attempt.invoiceId, bankTransactionId: allocationAttempt.attempt.bankTransactionId }
    : null;
  const allocationContextPair = allocationOpen
    ? storedAllocationPair ?? (allocationAttempt.status === 'none' ? draftPair : null)
    : null;
  const allocationContext = useQuery({
    queryKey: allocationContextPair === null
      ? ['allocations', 'context', session.epoch, 'idle']
      : ['allocations', 'context', session.epoch, allocationContextPair.invoiceId, allocationContextPair.bankTransactionId],
    enabled: allocationContextPair !== null && session.token !== null,
    queryFn: ({ signal }) => {
      if (allocationContextPair === null) {
        throw new Error('Allocation context was requested without a pair.');
      }

      return getAllocationContext(allocationContextPair.invoiceId, allocationContextPair.bankTransactionId, signal);
    },
  });
  const exact = useQuery({
    queryKey: ['matches', session.epoch, 'exact'],
    enabled: session.token !== null && view === 'exact',
    queryFn: ({ signal }) => getExactMatches(signal),
  });
  const smart = useQuery({
    queryKey: ['matches', session.epoch, 'smart'],
    enabled: session.token !== null && view === 'smart',
    queryFn: ({ signal }) => getSmartMatches(signal),
  });
  const confirmation = useMutation({
    networkMode: 'always',
    retry: false,
    onMutate: (attempt: ConfirmationAttempt) => {
      setOperationEpoch(attempt.epoch);
      return attempt.epoch;
    },
    mutationFn: (attempt: ConfirmationAttempt) => {
      const controller = new AbortController();
      requestController.current = controller;
      return postExactConfirmation(attempt, controller.signal).finally(() => {
        if (requestController.current === controller) {
          requestController.current = null;
        }
      });
    },
    onSuccess: (_result, _attempt, epoch) => {
      if (sessionStore.getSnapshot().epoch !== epoch) {
        return;
      }

      setActiveConfirmation(null);
      setSuccessDismissed(false);
      void queryClient.invalidateQueries({ queryKey: ['invoices', epoch] });
      void queryClient.invalidateQueries({ queryKey: ['bank-transactions', epoch] });
      void queryClient.invalidateQueries({ queryKey: ['matches', epoch] });
    },
    onError: (error) => {
      if (error instanceof StaleSessionError) {
        setActiveConfirmation(null);
      }
    },
    onSettled: () => {
      submitting.current = false;
    },
  });
  const active = view === 'exact' ? exact : smart;
  const sameSession = operationEpoch === session.epoch;
  const confirmationResult: ExactConfirmationResult | null = confirmation.isSuccess && sameSession && !successDismissed
    ? confirmation.data
    : null;
  const confirmationError = confirmation.isError && (sameSession || confirmation.error instanceof StaleSessionError)
    ? confirmation.error
    : null;
  const pair = visibleAttempt?.pair ?? visibleDraft?.pair ?? null;
  const showDialog = dialogOpen && pair !== null;
  const successRefreshing = confirmation.isSuccess && sameSession && exact.isFetching;
  const attemptOpen = visibleAttempt !== null && (confirmation.isPending || confirmationError !== null);

  return (
    <Stack spacing={2}>
      <Stack direction="row" spacing={2} sx={{ alignItems: 'center', justifyContent: 'space-between' }}>
        <Typography variant="h4" component="h1">
          Reconciliation
        </Typography>
        {active.isSuccess ? (
          <Button type="button" onClick={() => void active.refetch()} disabled={active.isFetching}>
            Refresh
          </Button>
        ) : null}
      </Stack>
      <Typography color="text.secondary">
        Inspect exact and smart candidates. An exact match can be confirmed explicitly. Smart suggestions stay advisory and do not allocate money.
      </Typography>
      <Typography color="text.secondary">
        Exact and Smart are separate reads and can come from different moments in the books.
      </Typography>
      {allocationOpen ? null : (
        <AllocationRecoveryBanner
          attempt={allocationAttempt}
          result={manualAllocation.result}
          error={manualAllocation.error}
          onReview={openStoredAllocation}
          onDismiss={() => manualAllocation.dismiss()}
        />
      )}
      {showDialog ? null : (
        <ConfirmationAttemptBanner
          pair={visibleAttempt?.pair ?? null}
          pending={confirmation.isPending && sameSession}
          result={confirmationResult}
          error={confirmationError}
          onReview={() => setDialogOpen(true)}
          onDismissSuccess={() => {
            setSuccessDismissed(true);
            setDialogOpen(false);
          }}
        />
      )}
      <ToggleButtonGroup
        exclusive
        value={view}
        aria-label="Matching view"
        onChange={(_event, next: MatchingView | null) => {
          if (next !== null) {
            setView(next);
          }
        }}
      >
        <ToggleButton value="exact">Exact</ToggleButton>
        <ToggleButton value="smart">Smart</ToggleButton>
      </ToggleButtonGroup>
      {active.isPending ? <LoadingState view={view} /> : null}
      {active.isError ? (
        <ErrorState error={active.error} onRetry={() => void active.refetch()} retrying={active.isFetching} />
      ) : null}
      {view === 'exact' && exact.isSuccess ? (
        <ExactResult
          result={exact.data}
          resetKey={exact.dataUpdatedAt}
          onConfirm={openExactMatch}
          actionFor={actionFor}
        />
      ) : null}
      {view === 'smart' && smart.isSuccess ? (
        <SmartResult result={smart.data} resetKey={smart.dataUpdatedAt} onAllocate={openSmartAllocation} />
      ) : null}
      <ExactMatchConfirmationDialog
        open={showDialog}
        pair={pair}
        pending={confirmation.isPending && sameSession}
        result={dialogOpen ? confirmationResult : null}
        error={dialogOpen ? confirmationError : null}
        onClose={() => setDialogOpen(false)}
        onConfirm={submitConfirmation}
        onLeave={leaveAttempt}
      />
      <ManualAllocationDialog
        open={allocationOpen}
        context={allocationContext}
        attempt={allocationAttempt}
        suggestedAmount={draftPair?.suggestedAmount ?? null}
        draftPairKey={draftPair === null ? null : `${draftPair.invoiceId}:${draftPair.bankTransactionId}`}
        redirectedFromOtherPair={redirectedFromOtherPair}
        submitting={manualAllocation.submitting}
        error={manualAllocation.error}
        result={manualAllocation.result}
        onClose={() => setAllocationOpen(false)}
        onAllocate={submitManualAllocation}
        onRetry={() => {
          void manualAllocation.retryStoredAttempt();
        }}
        onAbandon={manualAllocation.abandonStoredAttempt}
        onRefreshContext={() => {
          void allocationContext.refetch();
        }}
      />
    </Stack>
  );

  function actionFor(item: ExactMatchItem): { disabled: boolean; label: string } {
    const activePair = visibleAttempt?.attempt;
    const samePair = activePair !== undefined && activePair.invoiceId === item.invoiceId && activePair.bankTransactionId === item.bankTransactionId;

    if (samePair && attemptOpen) {
      return { disabled: false, label: 'Review' };
    }

    if (attemptOpen || confirmationResult !== null || successRefreshing) {
      return { disabled: true, label: 'Confirm' };
    }

    return { disabled: false, label: 'Confirm' };
  }

  function openExactMatch(item: ExactMatchItem): void {
    if (session.epoch !== sessionStore.getSnapshot().epoch) {
      return;
    }

    const action = actionFor(item);

    if (action.disabled) {
      return;
    }

    if (confirmation.isSuccess) {
      confirmation.reset();
    }

    if (!attemptOpen) {
      setDraft({
        pair: pairFromItem(item),
        epoch: sessionStore.getSnapshot().epoch,
      });
    }

    setDialogOpen(true);
  }

  function submitConfirmation(): void {
    if (submitting.current || confirmation.isPending) {
      return;
    }

    const existing = visibleAttempt;

    if (existing) {
      if (sessionStore.getSnapshot().epoch !== existing.attempt.epoch) {
        return;
      }

      submitting.current = true;
      confirmation.mutate(existing.attempt);
      return;
    }

    const epoch = sessionStore.getSnapshot().epoch;

    if (visibleDraft === null || visibleDraft.epoch !== epoch) {
      return;
    }

    let attempt: ConfirmationAttempt;

    try {
      attempt = createConfirmationAttempt(
        visibleDraft.pair.invoiceId,
        visibleDraft.pair.bankTransactionId,
        epoch,
      );
    } catch {
      return;
    }

    const next = { attempt, pair: visibleDraft.pair };
    setActiveConfirmation(next);
    setSuccessDismissed(true);
    submitting.current = true;
    confirmation.mutate(attempt);
  }

  function openStoredAllocation(): void {
    setRedirectedFromOtherPair(false);
    setAllocationOpen(true);
  }

  function openSmartAllocation(item: SmartMatchSuggestion): void {
    const stored = inspectAllocationAttempt();

    if (manualAllocation.error instanceof AllocationStorageCleanupError && stored.status === 'none') {
      setAllocationOpen(true);
      return;
    }

    if (stored.status !== 'none') {
      setRedirectedFromOtherPair(true);
      setAllocationOpen(true);
      return;
    }

    manualAllocation.dismiss();
    setRedirectedFromOtherPair(false);
    setDraftPair({
      invoiceId: item.invoiceId,
      bankTransactionId: item.bankTransactionId,
      suggestedAmount: item.suggestedAmount,
      suggestedCurrency: item.suggestedAmountCurrency,
    });
    setAllocationOpen(true);
  }

  function submitManualAllocation(amountText: string, currency: string): void {
    if (draftPair === null || !allocationContext.isSuccess || allocationContext.isFetching || allocationContext.data === undefined) {
      return;
    }

    const balances = describeAllocationBalances(allocationContext.data);

    if (balances.status !== 'allocatable' || balances.currency !== currency) {
      return;
    }

    const decision = assessAllocationAmount(balances.maximum, amountText);

    if (!decision.ok) {
      return;
    }

    const organizationId = readAllocationOrganizationId(sessionStore.getSnapshot().token);

    if (organizationId === null) {
      return;
    }

    void manualAllocation.allocateNew({
      invoiceId: draftPair.invoiceId,
      bankTransactionId: draftPair.bankTransactionId,
      amount: decision.amount,
      currency: balances.currency,
      organizationId,
    });
  }

  function leaveAttempt(): void {
    if (confirmation.isPending || !isDefinitiveRejection(confirmationError)) {
      return;
    }

    setActiveConfirmation(null);
    setDraft(null);
    setDialogOpen(false);
    confirmation.reset();
  }
}

function pairFromItem(item: ExactMatchItem): ConfirmationPairView {
  return {
    invoiceId: item.invoiceId,
    bankTransactionId: item.bankTransactionId,
    reference: item.normalizedReference,
    displayedAmount: item.amount,
    displayedCurrency: item.currency,
  };
}

function isDefinitiveRejection(error: unknown): boolean {
  return error instanceof ApiError && (error.status === 400 || error.status === 404 || error.status === 409);
}

function ExactResult({
  result,
  resetKey,
  onConfirm,
  actionFor,
}: {
  result: ExactMatchResponse;
  resetKey: number;
  onConfirm: (item: ExactMatchItem) => void;
  actionFor: (item: ExactMatchItem) => { disabled: boolean; label: string };
}) {
  const empty = result.exactMatches.length === 0
    && result.ambiguousGroups.length === 0
    && result.unmatched.length === 0
    && result.ineligible.length === 0;

  if (empty) {
    return <Alert severity="info">Nothing is open to classify. Algorithm {result.algorithmVersion}.</Alert>;
  }

  return (
    <Stack spacing={2}>
      <Typography color="text.secondary">Algorithm {result.algorithmVersion}.</Typography>
      <RecordSection title="Exact matches" count={result.exactMatches.length}>
        <PagedSection items={result.exactMatches} resetKey={resetKey} label="exact matches" renderPage={(items) => <ExactMatchTable items={items} onConfirm={onConfirm} actionFor={actionFor} />} />
      </RecordSection>
      <RecordSection title="Ambiguous groups" count={result.ambiguousGroups.length}>
        <PagedSection items={result.ambiguousGroups} resetKey={resetKey} label="ambiguous groups" renderPage={(items) => <AmbiguousTable items={items} />} />
      </RecordSection>
      <RecordSection title="Unmatched" count={result.unmatched.length}>
        <PagedSection items={result.unmatched} resetKey={resetKey} label="unmatched records" renderPage={(items) => <ClassifiedTable items={items} />} />
      </RecordSection>
      <RecordSection title="Ineligible" count={result.ineligible.length}>
        <PagedSection items={result.ineligible} resetKey={resetKey} label="ineligible records" renderPage={(items) => <ClassifiedTable items={items} />} />
      </RecordSection>
    </Stack>
  );
}

function SmartResult({
  result,
  resetKey,
  onAllocate,
}: {
  result: SmartMatchResponse;
  resetKey: number;
  onAllocate: (item: SmartMatchSuggestion) => void;
}) {
  return (
    <Stack spacing={2}>
      <Alert severity="info">
        These suggestions are advisory points, not a probability. A suggestion does not create an allocation.
        Algorithm {result.algorithmVersion}.
      </Alert>
      {result.coverageLimited ? (
        <Alert severity="warning">
          <AlertTitle>Candidate coverage is limited</AlertTitle>
          A suggestion that is not contested can still have another candidate outside this result. An empty list
          does not show that no relationship exists.
        </Alert>
      ) : null}
      {result.truncated ? (
        <Alert severity="warning">
          <AlertTitle>This list is truncated</AlertTitle>
          Some scored suggestions were omitted.
        </Alert>
      ) : null}
      {result.suggestions.length === 0 && !result.coverageLimited ? (
        <Alert severity="info">No open pair met a reference signal in this read.</Alert>
      ) : null}
      {result.suggestions.length > 0 ? (
        <RecordSection title="Suggestions" count={result.suggestions.length}>
          <PagedSection items={result.suggestions} resetKey={resetKey} label="suggestions" renderPage={(items) => <SuggestionTable items={items} onAllocate={onAllocate} />} />
        </RecordSection>
      ) : null}
    </Stack>
  );
}

function RecordSection({ title, count, children }: { title: string; count: number; children: ReactNode }) {
  return (
    <Stack spacing={1} component="section" aria-label={title}>
      <Typography variant="h6" component="h2">
        {title} ({count})
      </Typography>
      {count === 0 ? <Typography color="text.secondary">None.</Typography> : children}
    </Stack>
  );
}

function ExactMatchTable({
  items,
  onConfirm,
  actionFor,
}: {
  items: ExactMatchItem[];
  onConfirm: (item: ExactMatchItem) => void;
  actionFor: (item: ExactMatchItem) => { disabled: boolean; label: string };
}) {
  return (
    <MatchTable
      headings={['Reference', 'Amount', 'Invoice', 'Bank transaction', 'Reason', 'Action']}
      rows={items.map((item) => {
        const action = actionFor(item);
        return (
          <TableRow key={`${item.invoiceId}-${item.bankTransactionId}`}>
            <TableCell>{item.normalizedReference}</TableCell>
            <TableCell>{formatMoney(item.amount, item.currency)}</TableCell>
            <TableCell><Identifier id={item.invoiceId} kind="Invoice" /></TableCell>
            <TableCell><Identifier id={item.bankTransactionId} kind="Bank transaction" /></TableCell>
            <TableCell>{reasonLabel(item.reason)}</TableCell>
            <TableCell>
              <Button
                type="button"
                size="small"
                disabled={action.disabled}
                aria-label={`${action.label} exact match for invoice ${item.invoiceId} and bank transaction ${item.bankTransactionId}`}
                onClick={() => onConfirm(item)}
              >
                {action.label}
              </Button>
            </TableCell>
          </TableRow>
        );
      })}
    />
  );
}

function AmbiguousTable({ items }: { items: AmbiguousMatchGroup[] }) {
  return (
    <MatchTable
      headings={['Reference', 'Open amount', 'Reason', 'Invoices', 'Bank transactions']}
      rows={items.map((item) => (
        <TableRow key={`${item.normalizedReference}-${item.currency}-${item.openAmount}`}>
          <TableCell>{item.normalizedReference}</TableCell>
          <TableCell>{formatMoney(item.openAmount, item.currency)}</TableCell>
          <TableCell>{reasonLabel(item.reason)}</TableCell>
          <TableCell><IdentifierList ids={item.invoiceIds} kind="Invoice" /></TableCell>
          <TableCell><IdentifierList ids={item.bankTransactionIds} kind="Bank transaction" /></TableCell>
        </TableRow>
      ))}
    />
  );
}

function ClassifiedTable({ items }: { items: ClassifiedMatchRecord[] }) {
  return (
    <MatchTable
      headings={['Record', 'Reference', 'Open amount', 'Reason']}
      rows={items.map((item) => {
        const id = item.invoiceId ?? item.bankTransactionId ?? '';
        const kind = item.invoiceId !== null ? 'Invoice' : 'Bank transaction';
        return (
          <TableRow key={`${item.classification}-${id}`}>
            <TableCell><Identifier id={id} kind={kind} /></TableCell>
            <TableCell>{item.normalizedReference ?? 'None'}</TableCell>
            <TableCell>{formatMoney(item.openAmount, item.currency)}</TableCell>
            <TableCell>{reasonLabel(item.reason)}</TableCell>
          </TableRow>
        );
      })}
    />
  );
}

function SuggestionTable({
  items,
  onAllocate,
}: {
  items: SmartMatchSuggestion[];
  onAllocate: (item: SmartMatchSuggestion) => void;
}) {
  return (
    <MatchTable
      headings={['Rank', 'Score', 'Invoice', 'Bank transaction', 'Outstanding', 'Unapplied', 'Suggested', 'Flags', 'Signals', 'Action']}
      rows={items.map((item) => (
        <TableRow key={`${item.invoiceId}-${item.bankTransactionId}`}>
          <TableCell>{item.rank}</TableCell>
          <TableCell>{item.score} {item.scoreScale}</TableCell>
          <TableCell><Identifier id={item.invoiceId} kind="Invoice" /></TableCell>
          <TableCell><Identifier id={item.bankTransactionId} kind="Bank transaction" /></TableCell>
          <TableCell>{formatMoney(item.invoiceOutstanding, item.invoiceOutstandingCurrency)}</TableCell>
          <TableCell>{formatMoney(item.bankTransactionUnapplied, item.bankTransactionUnappliedCurrency)}</TableCell>
          <TableCell>{formatMoney(item.suggestedAmount, item.suggestedAmountCurrency)}</TableCell>
          <TableCell>
            <Stack spacing={0.5}>
              <span>{item.contested ? 'Contested' : 'Not marked contested'}</span>
              <span>{item.exactMatchAmbiguous ? 'Ambiguous exact match' : 'Not an ambiguous exact match'}</span>
            </Stack>
          </TableCell>
          <TableCell>
            <Stack spacing={0.5}>
              {item.signals.map((signal) => (
                <span key={signal.code}>
                  {signalLabels[signal.code] ?? signal.code} · {signal.points}
                </span>
              ))}
            </Stack>
          </TableCell>
          <TableCell>
            <Button
              type="button"
              size="small"
              aria-label={`Allocate invoice ${item.invoiceId} and bank transaction ${item.bankTransactionId}`}
              onClick={() => onAllocate(item)}
            >
              Allocate
            </Button>
          </TableCell>
        </TableRow>
      ))}
    />
  );
}

function MatchTable({ headings, rows }: { headings: string[]; rows: ReactNode[] }) {
  return (
    <TableContainer component={Paper} sx={{ overflowX: 'auto' }}>
      <Table size="small">
        <TableHead>
          <TableRow>
            {headings.map((heading) => <TableCell key={heading}>{heading}</TableCell>)}
          </TableRow>
        </TableHead>
        <TableBody>{rows}</TableBody>
      </Table>
    </TableContainer>
  );
}

function IdentifierList({ ids, kind }: { ids: string[]; kind: 'Invoice' | 'Bank transaction' }) {
  return (
    <Stack spacing={0.5}>
      {ids.map((id) => <Identifier key={id} id={id} kind={kind} />)}
    </Stack>
  );
}

function Identifier({ id, kind }: { id: string; kind: 'Invoice' | 'Bank transaction' }) {
  const [copyFailed, setCopyFailed] = useState(false);

  async function copyIdentifier(): Promise<void> {
    const writeText = navigator.clipboard?.writeText?.bind(navigator.clipboard);
    if (writeText === undefined) {
      setCopyFailed(true);
      return;
    }

    try {
      await writeText(id);
      setCopyFailed(false);
    } catch {
      setCopyFailed(true);
    }
  }

  return (
    <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center' }}>
      <Typography component="span" variant="body2" sx={{ fontFamily: 'monospace' }} aria-hidden="true">
        {id.slice(0, 8)}
      </Typography>
      <Typography component="span" sx={hiddenIdentifier}>{id}</Typography>
      <Button
        type="button"
        size="small"
        aria-label={`${kind} identifier ${id}`}
        onClick={() => {
          void copyIdentifier();
        }}
      >
        Copy
      </Button>
      {copyFailed ? <Typography component="span" role="status">Could not copy the identifier.</Typography> : null}
    </Stack>
  );
}

const hiddenIdentifier = {
  position: 'absolute',
  width: '1px',
  height: '1px',
  padding: 0,
  margin: '-1px',
  overflow: 'hidden',
  clip: 'rect(0, 0, 0, 0)',
  whiteSpace: 'nowrap',
  border: 0,
} as const;

interface AllocationDraft {
  invoiceId: string;
  bankTransactionId: string;
  suggestedAmount: DecimalString;
  suggestedCurrency: string;
}

function reasonLabel(reason: string): string {
  return reasonLabels[reason] ?? reason;
}

function LoadingState({ view }: { view: MatchingView }) {
  return <Alert severity="info" role="status">{view === 'exact' ? 'Loading exact matches' : 'Loading smart suggestions'}</Alert>;
}

function ErrorState({
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

  if (error instanceof ApiError && error.status === 409 && error.code === 'reconciliation.matches.candidate_limit_exceeded') {
    return (
      <Alert severity="warning">
        <AlertTitle>Matching was not performed</AlertTitle>
        {error.detail || error.title}
      </Alert>
    );
  }

  if (error instanceof ApiError && error.code === 'reconciliation.matches.inconsistent_money') {
    return (
      <Alert severity="error">
        <AlertTitle>{error.title}</AlertTitle>
        {error.detail}
      </Alert>
    );
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
        The match result could not be read.
      </Alert>
    );
  }

  if (error instanceof ApiError) {
    const diagnostic = [error.detail, error.title].map((part) => part.trim()).filter((part) => part.length > 0);
    return (
      <Alert severity="error" action={<RetryButton onRetry={onRetry} retrying={retrying} />}>
        The match result could not be loaded.
        {diagnostic.length > 0 ? <Typography component="p" variant="body2" sx={{ mt: 1 }}>{diagnostic.join(' ')}</Typography> : null}
      </Alert>
    );
  }

  return (
    <Alert severity="error" action={<RetryButton onRetry={onRetry} retrying={retrying} />}>
      The match result could not be loaded.
    </Alert>
  );
}

function RetryButton({ onRetry, retrying }: { onRetry: () => void; retrying: boolean }) {
  return (
    <Button type="button" color="inherit" onClick={onRetry} disabled={retrying}>
      Try again
    </Button>
  );
}

function PagedSection<T>({
  items,
  resetKey,
  label,
  renderPage,
}: {
  items: T[];
  resetKey: number;
  label: string;
  renderPage: (pageItems: T[]) => ReactNode;
}) {
  const [page, setPage] = useState(0);
  const [pageKey, setPageKey] = useState(resetKey);
  let currentPage = page;
  if (pageKey !== resetKey) {
    setPageKey(resetKey);
    setPage(0);
    currentPage = 0;
  }

  const pageCount = Math.max(1, Math.ceil(items.length / pageSize));
  const safePage = Math.min(currentPage, pageCount - 1);
  const start = safePage * pageSize;
  const visible = items.slice(start, start + pageSize);
  const from = items.length === 0 ? 0 : start + 1;
  const to = start + visible.length;

  return (
    <Stack spacing={1}>
      <Typography>
        Showing {from} to {to} of {items.length} {label}
      </Typography>
      {renderPage(visible)}
      {items.length > pageSize ? (
        <TablePagination
          component="div"
          count={items.length}
          page={safePage}
          rowsPerPage={pageSize}
          rowsPerPageOptions={[pageSize]}
          onPageChange={(_event, nextPage) => setPage(nextPage)}
        />
      ) : null}
    </Stack>
  );
}
