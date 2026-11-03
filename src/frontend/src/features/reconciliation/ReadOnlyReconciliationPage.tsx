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
import { useQuery } from '@tanstack/react-query';
import { useState, type ReactNode } from 'react';

import { ApiError, NetworkError, UnexpectedResponseError } from '../../shared/api/errors';
import { useSession } from '../../shared/auth/session';
import { formatMoney } from '../../shared/money/money';
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

export function ReadOnlyReconciliationPage() {
  const session = useSession();
  const [view, setView] = useState<MatchingView>('exact');
  const readable = session.authorization === 'verified'
    && session.capabilities.includes('reconciliation.read');
  const exact = useQuery({
    queryKey: ['matches', session.epoch, session.readiness, 'exact'],
    enabled: readable && view === 'exact',
    queryFn: ({ signal }) => getExactMatches(signal),
  });
  const smart = useQuery({
    queryKey: ['matches', session.epoch, session.readiness, 'smart'],
    enabled: readable && view === 'smart',
    queryFn: ({ signal }) => getSmartMatches(signal),
  });
  const active = view === 'exact' ? exact : smart;

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
        Inspect exact and smart candidates. This browser can only read matching suggestions. Confirmation and allocation stay unavailable.
      </Typography>
      <ToggleButtonGroup
        exclusive
        size="small"
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
      {!readable ? <Alert severity="warning">That page is not available.</Alert> : null}
      {readable && active.isPending ? <Alert severity="info" role="status">Loading matches</Alert> : null}
      {readable && active.isError ? <ErrorState error={active.error} onRetry={() => void active.refetch()} retrying={active.isFetching} /> : null}
      {readable && view === 'exact' && exact.isSuccess ? (
        <ExactResult result={exact.data} resetKey={session.epoch} />
      ) : null}
      {readable && view === 'smart' && smart.isSuccess ? (
        <SmartResult result={smart.data} resetKey={session.epoch} />
      ) : null}
    </Stack>
  );
}

function ExactResult({ result, resetKey }: { result: ExactMatchResponse; resetKey: number }) {
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
        <PagedSection
          items={result.exactMatches}
          resetKey={resetKey}
          label="exact matches"
          renderPage={(items) => <ExactMatchTable items={items} />}
        />
      </RecordSection>
      <RecordSection title="Ambiguous groups" count={result.ambiguousGroups.length}>
        <PagedSection
          items={result.ambiguousGroups}
          resetKey={resetKey}
          label="ambiguous groups"
          renderPage={(items) => <AmbiguousTable items={items} />}
        />
      </RecordSection>
      <RecordSection title="Unmatched" count={result.unmatched.length}>
        <PagedSection
          items={result.unmatched}
          resetKey={resetKey}
          label="unmatched records"
          renderPage={(items) => <ClassifiedTable items={items} />}
        />
      </RecordSection>
      <RecordSection title="Ineligible" count={result.ineligible.length}>
        <PagedSection
          items={result.ineligible}
          resetKey={resetKey}
          label="ineligible records"
          renderPage={(items) => <ClassifiedTable items={items} />}
        />
      </RecordSection>
    </Stack>
  );
}

function SmartResult({ result, resetKey }: { result: SmartMatchResponse; resetKey: number }) {
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
          <PagedSection
            items={result.suggestions}
            resetKey={resetKey}
            label="suggestions"
            renderPage={(items) => <SuggestionTable items={items} />}
          />
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

function ExactMatchTable({ items }: { items: ExactMatchItem[] }) {
  return (
    <MatchTable
      headings={['Reference', 'Amount', 'Invoice', 'Bank transaction', 'Reason']}
      rows={items.map((item) => (
        <TableRow key={`${item.invoiceId}-${item.bankTransactionId}`}>
          <TableCell>{item.normalizedReference}</TableCell>
          <TableCell>{formatMoney(item.amount, item.currency)}</TableCell>
          <TableCell><Identifier id={item.invoiceId} kind="Invoice" /></TableCell>
          <TableCell><Identifier id={item.bankTransactionId} kind="Bank transaction" /></TableCell>
          <TableCell>{reasonLabels[item.reason] ?? item.reason}</TableCell>
        </TableRow>
      ))}
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
          <TableCell>{reasonLabels[item.reason] ?? item.reason}</TableCell>
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
            <TableCell>{reasonLabels[item.reason] ?? item.reason}</TableCell>
          </TableRow>
        );
      })}
    />
  );
}

function SuggestionTable({ items }: { items: SmartMatchSuggestion[] }) {
  return (
    <MatchTable
      headings={['Rank', 'Score', 'Invoice', 'Bank transaction', 'Outstanding', 'Unapplied', 'Suggested', 'Flags', 'Signals']}
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

function PagedSection<T>({
  items,
  resetKey,
  label,
  renderPage,
}: {
  items: T[];
  resetKey: number;
  label: string;
  renderPage: (items: T[]) => ReactNode;
}) {
  const [page, setPage] = useState(0);
  const [seenKey, setSeenKey] = useState(resetKey);

  if (seenKey !== resetKey) {
    setSeenKey(resetKey);
    setPage(0);
  }

  const start = page * pageSize;
  const slice = items.slice(start, start + pageSize);

  return (
    <Stack spacing={1}>
      {renderPage(slice)}
      <TablePagination
        component="div"
        count={items.length}
        page={page}
        rowsPerPage={pageSize}
        rowsPerPageOptions={[pageSize]}
        onPageChange={(_event, next) => setPage(next)}
        labelDisplayedRows={({ from, to, count }) => `${from}-${to} of ${count} ${label}`}
      />
    </Stack>
  );
}

function Identifier({ id, kind }: { id: string; kind: string }) {
  return <span title={`${kind} ${id}`}>{id}</span>;
}

function IdentifierList({ ids, kind }: { ids: string[]; kind: string }) {
  return (
    <Stack spacing={0.5}>
      {ids.map((id) => <Identifier key={id} id={id} kind={kind} />)}
    </Stack>
  );
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
    return <Alert severity="warning">Sign in again before reading matches.</Alert>;
  }

  if (error instanceof ApiError && error.status === 403) {
    return <Alert severity="warning">Matching is not available for this organization.</Alert>;
  }

  if (error instanceof NetworkError) {
    return (
      <Alert
        severity="error"
        action={<Button color="inherit" onClick={onRetry} disabled={retrying}>Try again</Button>}
      >
        The network request for matches failed.
      </Alert>
    );
  }

  if (error instanceof UnexpectedResponseError) {
    return <Alert severity="error">The matching response could not be read.</Alert>;
  }

  return (
    <Alert
      severity="error"
      action={<Button color="inherit" onClick={onRetry} disabled={retrying}>Try again</Button>}
    >
      Matching could not be loaded.
    </Alert>
  );
}
