import {
  Alert,
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
  Tooltip,
  Typography,
} from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { useSearchParams } from 'react-router';

import { ApiError, NetworkError, StaleSessionError } from '../../shared/api/errors';
import { endSession, useSession } from '../../shared/auth/session';
import { formatMoney } from '../../shared/money/money';
import { listAllocationAudit, type AllocationAuditEntry } from './allocation-audit-api';
import { listAllocationHistory, type AllocationHistoryItem } from './allocation-history-api';

const defaultPage = 1;
const defaultPageSize = 50;
const auditPageSize = 50;
const pageSizeOptions = [25, 50, 100];
const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const emptyGuid = '00000000-0000-0000-0000-000000000000';

export function AllocationHistoryPage() {
  const session = useSession();
  const [searchParams, setSearchParams] = useSearchParams();
  const page = readPage(searchParams.get('page'));
  const pageSize = readPageSize(searchParams.get('pageSize'));
  const invoiceFilter = readOptionalGuid(searchParams, 'invoiceId');
  const bankFilter = readOptionalGuid(searchParams, 'bankTransactionId');
  const paginationValid = page !== null && pageSize !== null;
  const filtersValid = invoiceFilter !== 'invalid' && bankFilter !== 'invalid';
  const invoiceId = invoiceFilter === 'absent' ? null : invoiceFilter === 'invalid' ? null : invoiceFilter;
  const bankTransactionId = bankFilter === 'absent' ? null : bankFilter === 'invalid' ? null : bankFilter;
  const [expandedId, setExpandedId] = useState<string | null>(null);

  const query = useQuery({
    queryKey: [
      'allocations',
      'history',
      session.epoch,
      page,
      pageSize,
      invoiceId ?? '',
      bankTransactionId ?? '',
    ],
    enabled: session.token !== null && paginationValid && filtersValid,
    queryFn: ({ signal }) => listAllocationHistory(
      page ?? defaultPage,
      pageSize ?? defaultPageSize,
      invoiceId,
      bankTransactionId,
      signal,
    ),
  });

  const list = paginationValid && filtersValid && query.isSuccess ? query.data : null;
  const filtered = invoiceId !== null || bankTransactionId !== null;
  const emptyOrganization = list !== null && list.totalCount === 0 && !filtered;
  const emptyFilter = list !== null && list.totalCount === 0 && filtered;
  const pageOutOfRange = list !== null && isPageOutOfRange(list.page, list.pageSize, list.totalCount);
  const emptyPage = list !== null && list.totalCount > 0 && list.items.length === 0;

  function replaceQuery(nextPage: number, nextPageSize: number, nextInvoiceId: string | null, nextBankId: string | null) {
    const next = new URLSearchParams();
    next.set('page', String(nextPage));
    next.set('pageSize', String(nextPageSize));
    if (nextInvoiceId !== null) {
      next.set('invoiceId', nextInvoiceId);
    }
    if (nextBankId !== null) {
      next.set('bankTransactionId', nextBankId);
    }
    setExpandedId(null);
    setSearchParams(next);
  }

  return (
    <Stack spacing={2}>
      <Stack direction="row" spacing={2} sx={{ alignItems: 'center', justifyContent: 'space-between' }}>
        <Typography variant="h4" component="h1">
          Allocation history
        </Typography>
        {list !== null ? (
          <Button onClick={() => void query.refetch()} disabled={query.isFetching}>
            Refresh
          </Button>
        ) : null}
      </Stack>
      <Typography color="text.secondary">
        These are persisted allocations. A reversed row keeps its original amount. This list does not recover an unfinished allocation attempt.
      </Typography>
      {filtered && filtersValid ? (
        <Alert
          severity="info"
          action={
            <Button
              color="inherit"
              onClick={() => replaceQuery(defaultPage, pageSize ?? defaultPageSize, null, null)}
            >
              Clear filters
            </Button>
          }
        >
          {filterSummary(invoiceId, bankTransactionId)}
        </Alert>
      ) : null}
      {!paginationValid || !filtersValid ? <InvalidQuery /> : null}
      {paginationValid && filtersValid && query.isPending ? <LoadingState /> : null}
      {paginationValid && filtersValid && query.isError ? (
        <ErrorState
          error={query.error}
          onRetry={() => void query.refetch()}
          retrying={query.isFetching}
        />
      ) : null}
      {emptyOrganization ? <EmptyOrganization /> : null}
      {emptyFilter ? <EmptyFilter /> : null}
      {emptyPage ? (
        <EmptyPage onFirstPage={() => replaceQuery(defaultPage, list.pageSize, invoiceId, bankTransactionId)} />
      ) : null}
      {list !== null && list.totalCount > 0 && !pageOutOfRange ? (
        <HistoryTable
          items={list.items}
          page={list.page}
          pageSize={list.pageSize}
          totalCount={list.totalCount}
          expandedId={expandedId}
          epoch={session.epoch}
          onToggle={(allocationId) => setExpandedId((current) => (current === allocationId ? null : allocationId))}
          onPageChange={(nextPage, nextPageSize) => replaceQuery(nextPage, nextPageSize, invoiceId, bankTransactionId)}
        />
      ) : null}
    </Stack>
  );
}

function HistoryTable({
  items,
  page,
  pageSize,
  totalCount,
  expandedId,
  epoch,
  onToggle,
  onPageChange,
}: {
  items: AllocationHistoryItem[];
  page: number;
  pageSize: number;
  totalCount: number;
  expandedId: string | null;
  epoch: number;
  onToggle: (allocationId: string) => void;
  onPageChange: (page: number, pageSize: number) => void;
}) {
  return (
    <Paper>
      <TableContainer>
        <Table size="small" aria-label="Allocation history">
          <TableHead>
            <TableRow>
              <TableCell scope="col">Confirmed</TableCell>
              <TableCell scope="col">Invoice</TableCell>
              <TableCell scope="col">Bank reference</TableCell>
              <TableCell scope="col" align="right">Original amount</TableCell>
              <TableCell scope="col">Status</TableCell>
              <TableCell scope="col">Reversal</TableCell>
              <TableCell scope="col">Allocation</TableCell>
              <TableCell scope="col">Audit</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {items.map((item) => (
              <HistoryRows
                key={item.allocationId}
                item={item}
                expanded={expandedId === item.allocationId}
                epoch={epoch}
                onToggle={onToggle}
              />
            ))}
          </TableBody>
        </Table>
      </TableContainer>
      <TablePagination
        component="div"
        count={totalCount}
        page={Math.max(0, page - 1)}
        rowsPerPage={pageSize}
        rowsPerPageOptions={pageSizeOptions}
        onPageChange={(_event, nextPage) => onPageChange(nextPage + 1, pageSize)}
        onRowsPerPageChange={(event) => onPageChange(1, Number(event.target.value))}
      />
    </Paper>
  );
}

function HistoryRows({
  item,
  expanded,
  epoch,
  onToggle,
}: {
  item: AllocationHistoryItem;
  expanded: boolean;
  epoch: number;
  onToggle: (allocationId: string) => void;
}) {
  const auditId = `allocation-audit-${item.allocationId}`;
  return (
    <>
      <TableRow hover>
        <TableCell>{item.confirmedAt}</TableCell>
        <TableCell>
          <Stack spacing={0.5}>
            <span>{item.invoiceNumber}</span>
            <RecordId id={item.invoiceId} label="Invoice" />
          </Stack>
        </TableCell>
        <TableCell>
          <Stack spacing={0.5}>
            <span>{item.reference === null || item.reference.trim().length === 0 ? 'None' : item.reference}</span>
            <RecordId id={item.bankTransactionId} label="Bank transaction" />
          </Stack>
        </TableCell>
        <TableCell align="right">{formatMoney(item.amount, item.currency)}</TableCell>
        <TableCell>{item.status === 'confirmed' ? 'Confirmed' : 'Reversed'}</TableCell>
        <TableCell>
          {item.reversedAt === null ? 'Not reversed' : item.reversedAt}
          {item.reversalReason !== null ? ` · ${item.reversalReason}` : ''}
        </TableCell>
        <TableCell>
          <RecordId id={item.allocationId} label="Allocation" />
        </TableCell>
        <TableCell>
          <Button
            type="button"
            aria-expanded={expanded}
            aria-controls={expanded ? auditId : undefined}
            onClick={() => onToggle(item.allocationId)}
          >
            {expanded ? 'Hide audit' : 'View audit'}
          </Button>
        </TableCell>
      </TableRow>
      {expanded ? (
        <TableRow>
          <TableCell colSpan={8} id={auditId}>
            <AuditPanel epoch={epoch} allocationId={item.allocationId} />
          </TableCell>
        </TableRow>
      ) : null}
    </>
  );
}

function AuditPanel({ epoch, allocationId }: { epoch: number; allocationId: string }) {
  const [page, setPage] = useState(defaultPage);
  const query = useQuery({
    queryKey: ['allocations', 'audit', epoch, allocationId, page, auditPageSize],
    queryFn: ({ signal }) => listAllocationAudit(allocationId, page, auditPageSize, signal),
  });
  const list = query.isSuccess ? query.data : null;

  return (
    <Stack spacing={1} component="section" aria-label={`Audit for allocation ${allocationId}`}>
      <Typography variant="subtitle1" component="h2">
        Audit
      </Typography>
      {query.isPending ? <Alert severity="info" role="status">Loading audit</Alert> : null}
      {query.isError ? (
        <ErrorState error={query.error} onRetry={() => void query.refetch()} retrying={query.isFetching} audit />
      ) : null}
      {list !== null && list.totalCount === 0 ? (
        <Alert severity="info">No audit entries were found for this allocation.</Alert>
      ) : null}
      {list !== null && list.items.length > 0 ? (
        <Table size="small" aria-label={`Audit entries for allocation ${allocationId}`}>
          <TableHead>
            <TableRow>
              <TableCell scope="col">Recorded</TableCell>
              <TableCell scope="col">Action</TableCell>
              <TableCell scope="col">Operation</TableCell>
              <TableCell scope="col">Actor</TableCell>
              <TableCell scope="col" align="right">Amount</TableCell>
              <TableCell scope="col">Reason</TableCell>
              <TableCell scope="col">Batch</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {list.items.map((entry) => (
              <AuditRow key={entry.id} entry={entry} />
            ))}
          </TableBody>
        </Table>
      ) : null}
      {list !== null && list.totalCount > auditPageSize ? (
        <TablePagination
          component="div"
          count={list.totalCount}
          page={Math.max(0, list.page - 1)}
          rowsPerPage={auditPageSize}
          rowsPerPageOptions={[auditPageSize]}
          onPageChange={(_event, nextPage) => setPage(nextPage + 1)}
        />
      ) : null}
    </Stack>
  );
}

function AuditRow({ entry }: { entry: AllocationAuditEntry }) {
  return (
    <TableRow>
      <TableCell>{entry.recordedAt}</TableCell>
      <TableCell>{entry.action === 'confirmation' ? 'Confirmation' : 'Reversal'}</TableCell>
      <TableCell>{operationLabel(entry.operation)}</TableCell>
      <TableCell>{entry.actorId}</TableCell>
      <TableCell align="right">{formatMoney(entry.amount, entry.currency)}</TableCell>
      <TableCell>{entry.reason ?? 'None'}</TableCell>
      <TableCell>
        {entry.batchExecutionId === null ? 'None' : <RecordId id={entry.batchExecutionId} label="Batch" />}
      </TableCell>
    </TableRow>
  );
}

function RecordId({ id, label }: { id: string; label: string }) {
  return (
    <Tooltip describeChild title={id}>
      <span tabIndex={0} aria-label={`${label} ${id}`}>{id.slice(0, 8)}</span>
    </Tooltip>
  );
}

function operationLabel(operation: AllocationAuditEntry['operation']): string {
  switch (operation) {
    case 'exactMatch':
      return 'Exact match';
    case 'explicitAmount':
      return 'Explicit amount';
    case 'batchExactMatch':
      return 'Batch exact match';
    case 'reversal':
      return 'Reversal';
    default:
      return operation;
  }
}

function filterSummary(invoiceId: string | null, bankTransactionId: string | null): string {
  const parts = [];
  if (invoiceId !== null) {
    parts.push(`invoice ${invoiceId}`);
  }
  if (bankTransactionId !== null) {
    parts.push(`bank transaction ${bankTransactionId}`);
  }
  return `Showing allocations for ${parts.join(' and ')}.`;
}

function LoadingState() {
  return <Alert severity="info" role="status">Loading allocation history</Alert>;
}

function EmptyOrganization() {
  return <Alert severity="info">No allocations are in this organization yet.</Alert>;
}

function EmptyFilter() {
  return <Alert severity="info">No allocations match this filter.</Alert>;
}

function EmptyPage({ onFirstPage }: { onFirstPage: () => void }) {
  return (
    <Alert
      severity="info"
      action={<Button color="inherit" onClick={onFirstPage}>Go to first page</Button>}
    >
      This page has no allocations. Other pages still contain allocations.
    </Alert>
  );
}

function InvalidQuery() {
  return <Alert severity="warning">This history address is not valid. Open Allocation history again from the menu.</Alert>;
}

function ErrorState({
  error,
  onRetry,
  retrying,
  audit = false,
}: {
  error: unknown;
  onRetry: () => void;
  retrying: boolean;
  audit?: boolean;
}) {
  if (error instanceof StaleSessionError) {
    return null;
  }

  if (error instanceof ApiError && error.status === 401) {
    return (
      <Alert
        severity="warning"
        action={<Button color="inherit" onClick={() => endSession('unauthorized')}>Update access</Button>}
      >
        Your access has expired or is no longer valid. Update it to continue.
      </Alert>
    );
  }

  if (error instanceof ApiError && error.status === 403) {
    return (
      <Alert
        severity="warning"
        action={<Button color="inherit" onClick={() => endSession('forbidden')}>Update access</Button>}
      >
        You don’t have access to this workspace. Check your access details.
      </Alert>
    );
  }

  if (audit && error instanceof ApiError && error.status === 404) {
    return <Alert severity="warning">This allocation was not found.</Alert>;
  }

  if (error instanceof NetworkError) {
    return (
      <Alert severity="error" action={<RetryButton onRetry={onRetry} retrying={retrying} />}>
        We couldn’t connect. Check your connection and try again.
      </Alert>
    );
  }

  return (
    <Alert severity="error" action={<RetryButton onRetry={onRetry} retrying={retrying} />}>
      {audit ? 'The audit could not be loaded.' : 'The allocation history could not be loaded.'}
    </Alert>
  );
}

function RetryButton({ onRetry, retrying }: { onRetry: () => void; retrying: boolean }) {
  return (
    <Button color="inherit" onClick={onRetry} disabled={retrying}>
      Try again
    </Button>
  );
}

function isPageOutOfRange(page: number, pageSize: number, totalCount: number): boolean {
  if (pageSize < 1) {
    return true;
  }

  return page > Math.ceil(totalCount / pageSize);
}

function readPage(value: string | null): number | null {
  if (value === null) {
    return defaultPage;
  }

  return readBoundedInt(value, 1, 2_147_483_647);
}

function readPageSize(value: string | null): number | null {
  if (value === null) {
    return defaultPageSize;
  }

  const parsed = readBoundedInt(value, 1, 100);
  if (parsed !== 25 && parsed !== 50 && parsed !== 100) {
    return null;
  }

  return parsed;
}

function readOptionalGuid(params: URLSearchParams, name: string): 'absent' | 'invalid' | string {
  const values = params.getAll(name);
  if (values.length === 0) {
    return 'absent';
  }

  if (values.length !== 1 || !guidPattern.test(values[0]) || values[0].toLowerCase() === emptyGuid) {
    return 'invalid';
  }

  return values[0];
}

function readBoundedInt(value: string, minimum: number, maximum: number): number | null {
  if (!/^\d+$/.test(value) || value.length > 15) {
    return null;
  }

  const parsed = Number(value);
  if (!Number.isSafeInteger(parsed) || parsed < minimum || parsed > maximum) {
    return null;
  }

  return parsed;
}
