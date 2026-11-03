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

import { ApiError, NetworkError, UnexpectedResponseError } from '../../shared/api/errors';
import { useSession } from '../../shared/auth/session';
import { formatMoney } from '../../shared/money/money';
import { listAllocationAudit, type AllocationAuditEntry } from './allocation-audit-api';
import { listAllocationHistory, type AllocationHistoryItem } from './allocation-history-api';

const defaultPage = 1;
const defaultPageSize = 50;
const auditPageSize = 50;
const pageSizeOptions = [25, 50, 100];
const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const emptyGuid = '00000000-0000-0000-0000-000000000000';

export function ReadOnlyAllocationHistoryPage() {
  const session = useSession();
  const [searchParams, setSearchParams] = useSearchParams();
  const page = readPage(searchParams.get('page'));
  const pageSize = readPageSize(searchParams.get('pageSize'));
  const invoiceFilter = readOptionalGuid(searchParams, 'invoiceId');
  const bankFilter = readOptionalGuid(searchParams, 'bankTransactionId');
  const paginationValid = page !== null && pageSize !== null;
  const filtersValid = invoiceFilter !== 'invalid' && bankFilter !== 'invalid';
  const invoiceId = invoiceFilter === 'absent' || invoiceFilter === 'invalid' ? null : invoiceFilter;
  const bankTransactionId = bankFilter === 'absent' || bankFilter === 'invalid' ? null : bankFilter;
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const readable = session.authorization === 'verified'
    && session.capabilities.includes('allocations.read');
  const canReadAudit = session.authorization === 'verified'
    && session.capabilities.includes('audit.read');

  const query = useQuery({
    queryKey: [
      'allocations',
      'history',
      session.epoch,
      session.readiness,
      page,
      pageSize,
      invoiceId ?? '',
      bankTransactionId ?? '',
    ],
    enabled: readable && paginationValid && filtersValid,
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

  function replaceQuery(
    nextPage: number,
    nextPageSize: number,
    nextInvoiceId: string | null,
    nextBankId: string | null,
  ) {
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
        These are persisted allocations. This browser can only read history
        {canReadAudit ? ' and audit entries' : ''}. Reversal stays unavailable.
      </Typography>
      {filtered && filtersValid ? (
        <Alert
          severity="info"
          action={(
            <Button
              color="inherit"
              onClick={() => replaceQuery(defaultPage, pageSize ?? defaultPageSize, null, null)}
            >
              Clear filters
            </Button>
          )}
        >
          {filterSummary(invoiceId, bankTransactionId)}
        </Alert>
      ) : null}
      {!readable ? <Alert severity="warning">That page is not available.</Alert> : null}
      {readable && (!paginationValid || !filtersValid) ? <InvalidQuery /> : null}
      {readable && paginationValid && filtersValid && query.isPending ? (
        <Alert severity="info" role="status">Loading allocation history</Alert>
      ) : null}
      {readable && paginationValid && filtersValid && query.isError ? (
        <ErrorState error={query.error} onRetry={() => void query.refetch()} retrying={query.isFetching} />
      ) : null}
      {emptyOrganization ? <Alert severity="info">No allocations are stored for this organization.</Alert> : null}
      {emptyFilter ? <Alert severity="info">No allocations match these filters.</Alert> : null}
      {emptyPage ? (
        <Alert
          severity="info"
          action={(
            <Button color="inherit" onClick={() => replaceQuery(defaultPage, list.pageSize, invoiceId, bankTransactionId)}>
              First page
            </Button>
          )}
        >
          This page is empty.
        </Alert>
      ) : null}
      {list !== null && list.totalCount > 0 && !pageOutOfRange ? (
        <HistoryTable
          items={list.items}
          page={list.page}
          pageSize={list.pageSize}
          totalCount={list.totalCount}
          expandedId={expandedId}
          epoch={session.epoch}
          readiness={session.readiness}
          canReadAudit={canReadAudit}
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
  readiness,
  canReadAudit,
  onToggle,
  onPageChange,
}: {
  items: AllocationHistoryItem[];
  page: number;
  pageSize: number;
  totalCount: number;
  expandedId: string | null;
  epoch: number;
  readiness: number;
  canReadAudit: boolean;
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
              {canReadAudit ? <TableCell scope="col">Audit</TableCell> : null}
            </TableRow>
          </TableHead>
          <TableBody>
            {items.map((item) => (
              <HistoryRows
                key={item.allocationId}
                item={item}
                expanded={expandedId === item.allocationId}
                epoch={epoch}
                readiness={readiness}
                canReadAudit={canReadAudit}
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
  readiness,
  canReadAudit,
  onToggle,
}: {
  item: AllocationHistoryItem;
  expanded: boolean;
  epoch: number;
  readiness: number;
  canReadAudit: boolean;
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
          {item.status === 'confirmed'
            ? 'Not reversed'
            : `${item.reversedAt ?? ''}${item.reversalReason !== null ? ` · ${item.reversalReason}` : ''}`}
        </TableCell>
        <TableCell>
          <RecordId id={item.allocationId} label="Allocation" />
        </TableCell>
        {canReadAudit ? (
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
        ) : null}
      </TableRow>
      {canReadAudit && expanded ? (
        <TableRow>
          <TableCell colSpan={8} id={auditId}>
            <AuditPanel epoch={epoch} readiness={readiness} allocationId={item.allocationId} />
          </TableCell>
        </TableRow>
      ) : null}
    </>
  );
}

function AuditPanel({
  epoch,
  readiness,
  allocationId,
}: {
  epoch: number;
  readiness: number;
  allocationId: string;
}) {
  const [page, setPage] = useState(defaultPage);
  const query = useQuery({
    queryKey: ['allocations', 'audit', epoch, readiness, allocationId, page, auditPageSize],
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
            {list.items.map((entry: AllocationAuditEntry) => (
              <TableRow key={entry.id}>
                <TableCell>{entry.recordedAt}</TableCell>
                <TableCell>{entry.action}</TableCell>
                <TableCell>{entry.operation}</TableCell>
                <TableCell><RecordId id={entry.actorId} label="Actor" /></TableCell>
                <TableCell align="right">{formatMoney(entry.amount, entry.currency)}</TableCell>
                <TableCell>{entry.reason ?? 'None'}</TableCell>
                <TableCell>{entry.batchExecutionId ?? 'None'}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      ) : null}
      {list !== null && list.totalCount > 0 ? (
        <TablePagination
          component="div"
          count={list.totalCount}
          page={Math.max(0, list.page - 1)}
          rowsPerPage={list.pageSize}
          rowsPerPageOptions={[auditPageSize]}
          onPageChange={(_event, nextPage) => setPage(nextPage + 1)}
        />
      ) : null}
    </Stack>
  );
}

function RecordId({ id, label }: { id: string; label: string }) {
  return (
    <Tooltip title={`${label} ${id}`}>
      <Typography variant="caption" component="span" sx={{ fontFamily: 'monospace' }}>
        {id}
      </Typography>
    </Tooltip>
  );
}

function InvalidQuery() {
  return <Alert severity="warning">The history query in the address is not valid.</Alert>;
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
  const subject = audit ? 'audit' : 'allocation history';

  if (error instanceof ApiError && error.status === 401) {
    return <Alert severity="warning">Sign in again before reading {subject}.</Alert>;
  }

  if (error instanceof ApiError && error.status === 403) {
    return <Alert severity="warning">{audit ? 'Audit is not available for this organization.' : 'Allocation history is not available for this organization.'}</Alert>;
  }

  if (error instanceof ApiError && error.status === 404) {
    return <Alert severity="info">That allocation was not found.</Alert>;
  }

  if (error instanceof NetworkError) {
    return (
      <Alert
        severity="error"
        action={<Button color="inherit" onClick={onRetry} disabled={retrying}>Try again</Button>}
      >
        The network request for {subject} failed.
      </Alert>
    );
  }

  if (error instanceof UnexpectedResponseError) {
    return <Alert severity="error">The {subject} response could not be read.</Alert>;
  }

  return (
    <Alert
      severity="error"
      action={<Button color="inherit" onClick={onRetry} disabled={retrying}>Try again</Button>}
    >
      {subject} could not be loaded.
    </Alert>
  );
}

function filterSummary(invoiceId: string | null, bankTransactionId: string | null): string {
  if (invoiceId !== null && bankTransactionId !== null) {
    return `Filtered to invoice ${invoiceId} and bank transaction ${bankTransactionId}.`;
  }

  if (invoiceId !== null) {
    return `Filtered to invoice ${invoiceId}.`;
  }

  return `Filtered to bank transaction ${bankTransactionId}.`;
}

function readPage(value: string | null): number | null {
  if (value === null) {
    return defaultPage;
  }

  if (!/^\d+$/.test(value)) {
    return null;
  }

  const page = Number(value);
  return page >= 1 ? page : null;
}

function readPageSize(value: string | null): number | null {
  if (value === null) {
    return defaultPageSize;
  }

  if (!/^\d+$/.test(value)) {
    return null;
  }

  const pageSize = Number(value);
  return pageSizeOptions.includes(pageSize) ? pageSize : null;
}

function readOptionalGuid(
  params: URLSearchParams,
  name: string,
): string | 'absent' | 'invalid' {
  if (!params.has(name)) {
    return 'absent';
  }

  const value = params.get(name);

  if (value === null || !guidPattern.test(value) || value.toLowerCase() === emptyGuid) {
    return 'invalid';
  }

  return value.toLowerCase();
}

function isPageOutOfRange(page: number, pageSize: number, totalCount: number): boolean {
  if (totalCount === 0) {
    return false;
  }

  return (page - 1) * pageSize >= totalCount;
}
