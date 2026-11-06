import { zodResolver } from '@hookform/resolvers/zod';
import {
  Alert,
  Button,
  MenuItem,
  Paper,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TablePagination,
  TableRow,
  TextField,
  Typography,
} from '@mui/material';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useContext, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { UNSAFE_DataRouterContext, useNavigate, useSearchParams } from 'react-router';

import { ApiError, NetworkError, StaleSessionError, UnexpectedResponseError } from '../../shared/api/errors';
import { browserAuthMode } from '../../shared/auth/auth-mode';
import { endSession, useSession } from '../../shared/auth/session';
import { formatMoney } from '../../shared/money/money';
import {
  listReviewCaseAudit,
  type ReviewCaseAuditEntry,
} from './review-case-audit-api';
import {
  getReviewCase,
  listReviewCases,
  type ReviewCaseDetail,
  type ReviewCaseList,
  type ReviewCaseObservation,
  type ReviewCaseStatusName,
} from './review-case-api';
import { reviewCaseFilterSchema, type ReviewCaseFilterValues } from './review-case-filters';
import { ReviewCaseDecisionPanel } from './ReviewCaseDecisionPanel';
import {
  ReviewCaseCommentComposer,
  ReviewCaseCommentRecovery,
  ReviewCaseCommentsHost,
} from './ReviewCaseCommentPanel';
import { developmentActorId, developmentOrganizationId } from './review-case-open-request';
import { ReviewCaseOpenPanel } from './ReviewCaseOpenPanel';
import { ReviewCaseSummaryPanel } from './ReviewCaseSummaryPanel';

const defaultPage = 1;
const defaultPageSize = 50;
const pageSizeOptions = [25, 50, 100];
const statuses: readonly ReviewCaseStatusName[] = ['Open', 'Resolved', 'Dismissed'];
const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const emptyGuid = '00000000-0000-0000-0000-000000000000';

export function ReviewCasePage() {
  const session = useSession();
  const queryClient = useQueryClient();
  const cookie = browserAuthMode() === 'cookie';
  const [searchParams, setSearchParams] = useSearchParams();
  const dataRouter = useContext(UNSAFE_DataRouterContext);
  const navigate = useNavigate();
  const searchParamsRef = useRef(searchParams);
  const setSearchParamsRef = useRef(setSearchParams);
  const pageMounted = useRef(false);
  useLayoutEffect(() => {
    searchParamsRef.current = searchParams;
    setSearchParamsRef.current = setSearchParams;
  }, [searchParams, setSearchParams]);
  useEffect(() => {
    pageMounted.current = true;
    return () => {
      pageMounted.current = false;
    };
  }, []);
  const page = readPage(searchParams.get('page'));
  const pageSize = readPageSize(searchParams.get('pageSize'));
  const status = readStatus(searchParams.get('status'));
  const invoiceFilter = readOptionalGuid(searchParams, 'invoiceId');
  const bankFilter = readOptionalGuid(searchParams, 'bankTransactionId');
  const selected = readOptionalGuid(searchParams, 'selected');
  const paginationValid = page !== null && pageSize !== null;
  const filtersValid = status !== 'invalid' && invoiceFilter !== 'invalid' && bankFilter !== 'invalid';
  const selectedValid = selected !== 'invalid';
  const readable = cookie
    ? session.authorization === 'verified' && session.capabilities.includes('review-cases.read')
    : session.token !== null;
  const invoiceId = invoiceFilter === 'absent' || invoiceFilter === 'invalid' ? null : invoiceFilter;
  const bankTransactionId = bankFilter === 'absent' || bankFilter === 'invalid' ? null : bankFilter;
  const statusFilter = status === 'absent' || status === 'invalid' ? null : status;
  const selectedId = selected === 'absent' || selected === 'invalid' ? null : selected;

  const listQuery = useQuery({
    queryKey: cookie
      ? ['review-cases', 'list', session.epoch, session.readiness, page, pageSize, statusFilter ?? '', invoiceId ?? '', bankTransactionId ?? '']
      : ['review-cases', 'list', session.epoch, page, pageSize, statusFilter ?? '', invoiceId ?? '', bankTransactionId ?? ''],
    enabled: readable && paginationValid && filtersValid,
    queryFn: ({ signal }) => listReviewCases(
      page ?? defaultPage,
      pageSize ?? defaultPageSize,
      statusFilter,
      invoiceId,
      bankTransactionId,
      signal,
    ),
  });

  const detailQuery = useQuery({
    queryKey: cookie
      ? ['review-cases', 'detail', session.epoch, session.readiness, selectedId ?? '']
      : ['review-cases', 'detail', session.epoch, selectedId ?? ''],
    enabled: readable && paginationValid && filtersValid && selectedValid && selectedId !== null,
    queryFn: ({ signal }) => getReviewCase(selectedId ?? '', session.epoch, signal),
  });

  const list = readable && paginationValid && filtersValid && listQuery.isSuccess ? listQuery.data : null;
  const detailRead = readable && selectedId !== null && detailQuery.isSuccess ? detailQuery.data : null;
  const detail = detailRead?.detail ?? null;
  const filtered = statusFilter !== null || invoiceId !== null || bankTransactionId !== null;
  const emptyOrganization = list !== null && list.totalCount === 0 && !filtered;
  const emptyFilter = list !== null && list.totalCount === 0 && filtered;
  const pageOutOfRange = list !== null && isPageOutOfRange(list.page, list.pageSize, list.totalCount);
  const emptyPage = list !== null && list.totalCount > 0 && list.items.length === 0;

  function writeList(nextPage: number, nextPageSize: number, nextStatus: ReviewCaseStatusName | null, nextInvoiceId: string | null, nextBankId: string | null, nextSelected: string | null) {
    const next = new URLSearchParams();
    next.set('page', String(nextPage));
    next.set('pageSize', String(nextPageSize));
    if (nextStatus !== null) {
      next.set('status', nextStatus);
    }
    if (nextInvoiceId !== null) {
      next.set('invoiceId', nextInvoiceId);
    }
    if (nextBankId !== null) {
      next.set('bankTransactionId', nextBankId);
    }
    if (nextSelected !== null) {
      next.set('selected', nextSelected);
    }
    setSearchParams(next);
  }

  function currentSearch(): string {
    if (dataRouter !== null) {
      return dataRouter.router.state.location.search;
    }

    if (window.location.pathname === '/review-cases') {
      return window.location.search;
    }

    return searchParamsRef.current.toString();
  }

  function selectOpenedCase(reviewCaseId: string) {
    if (!pageMounted.current) {
      return;
    }

    const next = new URLSearchParams(currentSearch());
    next.set('selected', reviewCaseId);
    if (dataRouter !== null) {
      void navigate({
        pathname: dataRouter.router.state.location.pathname,
        search: `?${next.toString()}`,
      });
      return;
    }

    setSearchParamsRef.current(next);
  }

  return (
    <Stack spacing={2}>
      <Typography variant="h4" component="h1">Review cases</Typography>
      <Typography color="text.secondary">
        {cookie
          ? 'These are existing reconciliation exceptions. This screen only reads them.'
          : 'These are existing reconciliation exceptions. You can open one case for this organization.'}
      </Typography>
      {readable && !cookie ? (
        <ReviewCaseOpenPanel
          onSelect={selectOpenedCase}
        />
      ) : null}
      {readable && !cookie ? <ReviewCaseCommentRecovery key={session.epoch} /> : null}
      {readable && !cookie ? (
        <ReviewCaseDecisionPanel
          selectedCaseId={selectedId}
          caseStatus={detail?.reviewCase.status ?? null}
          command={detailRead?.command ?? null}
          onReadAgain={async () => {
            if (selectedId === null) {
              return;
            }

            const queryKey = ['review-cases', 'detail', session.epoch, selectedId];
            await queryClient.cancelQueries({ queryKey });
            await queryClient.invalidateQueries({ queryKey }, { throwOnError: true });
          }}
        />
      ) : null}
      {!readable ? <Alert severity="warning">Review cases are not available for this session.</Alert> : null}
      <ReviewCaseSummaryPanel />
      {readable && (!paginationValid || !filtersValid) ? <InvalidAddress /> : null}
      {readable && paginationValid && filtersValid && !selectedValid ? (
        <Alert severity="warning">The selected review case address is not valid. The list can still be read.</Alert>
      ) : null}
      {readable && paginationValid && filtersValid ? (
        <FilterForm
          status={statusFilter}
          invoiceId={invoiceId}
          bankTransactionId={bankTransactionId}
          onApply={(nextStatus, nextInvoiceId, nextBankId) => {
            writeList(defaultPage, pageSize ?? defaultPageSize, nextStatus, nextInvoiceId, nextBankId, null);
          }}
        />
      ) : null}
      {readable && paginationValid && filtersValid && listQuery.isPending ? <LoadingList /> : null}
      {readable && paginationValid && filtersValid && listQuery.isError ? (
        <ErrorState error={listQuery.error} onRetry={() => void listQuery.refetch()} retrying={listQuery.isFetching} />
      ) : null}
      {emptyOrganization ? <Alert severity="info">No review cases are in this organization yet.</Alert> : null}
      {emptyFilter ? <Alert severity="info">No review cases match these filters.</Alert> : null}
      {emptyPage && list !== null ? (
        <Alert
          severity="info"
          action={(
            <Button color="inherit" onClick={() => writeList(defaultPage, list.pageSize, statusFilter, invoiceId, bankTransactionId, null)}>
              Go to first page
            </Button>
          )}
        >
          This page has no review cases. Other pages still contain review cases.
        </Alert>
      ) : null}
      {list !== null && list.totalCount > 0 && !pageOutOfRange ? (
        <ReviewCaseTable
          list={list}
          selectedId={selectedId}
          onSelect={(id) => writeList(list.page, list.pageSize, statusFilter, invoiceId, bankTransactionId, id)}
          onPageChange={(nextPage, nextPageSize) => writeList(nextPage, nextPageSize, statusFilter, invoiceId, bankTransactionId, null)}
        />
      ) : null}
      {readable && selectedId !== null && paginationValid && filtersValid && selectedValid && detailQuery.isPending ? (
        <Alert severity="info" role="status">Loading the selected review case</Alert>
      ) : null}
      {readable && selectedId !== null && detailQuery.isError ? (
        <ErrorState error={detailQuery.error} onRetry={() => void detailQuery.refetch()} retrying={detailQuery.isFetching} detail />
      ) : null}
      {detail !== null ? (
        <SelectedReviewCase detail={detail} onRefresh={() => void detailQuery.refetch()} refreshing={detailQuery.isFetching} />
      ) : null}
    </Stack>
  );
}

function FilterForm({
  status,
  invoiceId,
  bankTransactionId,
  onApply,
}: {
  status: ReviewCaseStatusName | null;
  invoiceId: string | null;
  bankTransactionId: string | null;
  onApply: (status: ReviewCaseStatusName | null, invoiceId: string | null, bankTransactionId: string | null) => void;
}) {
  const applied = filterValues(status, invoiceId, bankTransactionId);
  const form = useForm<ReviewCaseFilterValues>({
    resolver: zodResolver(reviewCaseFilterSchema),
    defaultValues: applied,
  });

  useEffect(() => {
    form.reset({
      status: status ?? '',
      invoiceId: invoiceId ?? '',
      bankTransactionId: bankTransactionId ?? '',
    });
  }, [form, status, invoiceId, bankTransactionId]);

  return (
    <Stack
      direction="row"
      spacing={1}
      sx={{ alignItems: 'flex-start', flexWrap: 'wrap' }}
      component="form"
      onSubmit={form.handleSubmit((values) => {
        onApply(
          values.status === '' ? null : values.status,
          values.invoiceId === '' ? null : values.invoiceId.toLowerCase(),
          values.bankTransactionId === '' ? null : values.bankTransactionId.toLowerCase(),
        );
      })}
    >
      <Controller
        name="status"
        control={form.control}
        render={({ field }) => (
          <TextField select label="Status" value={field.value} onChange={field.onChange} onBlur={field.onBlur} sx={{ minWidth: 160 }}>
            <MenuItem value="">All</MenuItem>
            {statuses.map((item) => <MenuItem key={item} value={item}>{item}</MenuItem>)}
          </TextField>
        )}
      />
      <Controller
        name="invoiceId"
        control={form.control}
        render={({ field, fieldState }) => (
          <TextField
            label="Invoice id"
            value={field.value}
            onChange={field.onChange}
            onBlur={field.onBlur}
            error={fieldState.error !== undefined}
            helperText={fieldState.error?.message}
          />
        )}
      />
      <Controller
        name="bankTransactionId"
        control={form.control}
        render={({ field, fieldState }) => (
          <TextField
            label="Bank transaction id"
            value={field.value}
            onChange={field.onChange}
            onBlur={field.onBlur}
            error={fieldState.error !== undefined}
            helperText={fieldState.error?.message}
          />
        )}
      />
      <Button type="submit" variant="outlined">Apply filters</Button>
    </Stack>
  );
}

function filterValues(
  status: ReviewCaseStatusName | null,
  invoiceId: string | null,
  bankTransactionId: string | null,
): ReviewCaseFilterValues {
  return {
    status: status ?? '',
    invoiceId: invoiceId ?? '',
    bankTransactionId: bankTransactionId ?? '',
  };
}

function ReviewCaseTable({
  list,
  selectedId,
  onSelect,
  onPageChange,
}: {
  list: ReviewCaseList;
  selectedId: string | null;
  onSelect: (id: string) => void;
  onPageChange: (page: number, pageSize: number) => void;
}) {
  return (
    <Paper>
      <TableContainer>
        <Table size="small" aria-label="Review cases">
          <TableHead>
            <TableRow>
              <TableCell scope="col">Case</TableCell>
              <TableCell scope="col">Reason</TableCell>
              <TableCell scope="col">Status</TableCell>
              <TableCell scope="col">Opened</TableCell>
              <TableCell scope="col">Note</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {list.items.map((item) => (
              <TableRow key={item.reviewCaseId} selected={item.reviewCaseId === selectedId} hover>
                <TableCell>
                  <Button
                    variant="text"
                    onClick={() => onSelect(item.reviewCaseId)}
                    aria-pressed={item.reviewCaseId === selectedId}
                  >
                    {`Show review case ${item.reviewCaseId}`}
                  </Button>
                </TableCell>
                <TableCell>{item.reason}</TableCell>
                <TableCell>{item.status}</TableCell>
                <TableCell>{item.openedAt}</TableCell>
                <TableCell>{item.note ?? 'None'}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </TableContainer>
      <TablePagination
        component="div"
        count={list.totalCount}
        page={Math.max(0, list.page - 1)}
        rowsPerPage={list.pageSize}
        rowsPerPageOptions={pageSizeOptions}
        onPageChange={(_event, nextPage) => onPageChange(nextPage + 1, list.pageSize)}
        onRowsPerPageChange={(event) => onPageChange(1, Number(event.target.value))}
      />
    </Paper>
  );
}

function SelectedReviewCase({
  detail,
  onRefresh,
  refreshing,
}: {
  detail: ReviewCaseDetail;
  onRefresh: () => void;
  refreshing: boolean;
}) {
  const session = useSession();
  const cookie = browserAuthMode() === 'cookie';
  const item = detail.reviewCase;
  const canReadAudit = cookie
    ? session.authorization === 'verified' && session.capabilities.includes('audit.read')
    : session.token !== null;
  const canReadComments = canReadAudit;
  const canComposeComment = !cookie
    && session.token !== null
    && developmentOrganizationId(session.token) !== null
    && developmentActorId(session.token) !== null;
  const auditKey = cookie
    ? `${session.epoch}:${session.readiness}:${item.reviewCaseId}`
    : `${session.epoch}:${item.reviewCaseId}`;
  const commentsKey = `comments:${auditKey}`;
  return (
    <Paper component="section" aria-label="Selected review case" sx={{ p: 2 }}>
      <Stack direction="row" spacing={1} sx={{ alignItems: 'center', justifyContent: 'space-between' }}>
        <Typography variant="h6" component="h2">Selected review case</Typography>
        <Button onClick={onRefresh} disabled={refreshing}>Refresh selected case</Button>
      </Stack>
      <Typography color="text.secondary" sx={{ mt: 1 }}>
        These figures are the current linked balances. Each figure is its own observation.
        They are not one atomic snapshot, not a reservation of funds, and not permission to allocate.
      </Typography>
      <Typography>{`Case ${item.reviewCaseId}`}</Typography>
      <Typography>{`${item.reason} · ${item.status}`}</Typography>
      <Typography>{`Source ${absent(item.sourceLabel)}`}</Typography>
      <Typography>{`Opening note ${absent(item.note)}`}</Typography>
      <Typography>{`Resolution note ${absent(item.resolutionNote)}`}</Typography>
      <Typography>{`Outcome ${absent(item.outcome)}`}</Typography>
      <Typography>{`Allocation ${absent(item.allocationId)}`}</Typography>
      <Typography>{`Opened ${item.openedAt}`}</Typography>
      <Typography>{`Closed ${absent(item.closedAt)}`}</Typography>
      <DocumentId label="Invoice" documentId={item.invoiceId} />
      <DocumentId label="Bank transaction" documentId={item.bankTransactionId} />
      <Observation label="Invoice" named={item.invoiceId !== null} observation={detail.invoice} kind="outstanding" />
      <Observation label="Bank transaction" named={item.bankTransactionId !== null} observation={detail.bankTransaction} kind="unapplied" />
      {canReadAudit ? <ReviewCaseAuditHost key={auditKey} reviewCaseId={item.reviewCaseId} /> : null}
      {canReadComments ? <ReviewCaseCommentsHost key={commentsKey} reviewCaseId={item.reviewCaseId} /> : null}
      {canComposeComment ? (
        <ReviewCaseCommentComposer key={`comment-form:${session.epoch}:${item.reviewCaseId}`} reviewCaseId={item.reviewCaseId} />
      ) : null}
    </Paper>
  );
}

function ReviewCaseAuditHost({ reviewCaseId }: { reviewCaseId: string }) {
  const [open, setOpen] = useState(false);
  const panelId = `review-case-audit-${reviewCaseId}`;

  return (
    <Stack spacing={1} sx={{ mt: 2 }}>
      <Button
        type="button"
        aria-expanded={open}
        aria-controls={open ? panelId : undefined}
        onClick={() => setOpen((current) => !current)}
      >
        {open ? 'Hide audit' : 'View audit'}
      </Button>
      {open ? <ReviewCaseAuditPanel id={panelId} reviewCaseId={reviewCaseId} /> : null}
    </Stack>
  );
}

function ReviewCaseAuditPanel({ id, reviewCaseId }: { id: string; reviewCaseId: string }) {
  const session = useSession();
  const cookie = browserAuthMode() === 'cookie';
  const canReadAudit = cookie
    ? session.authorization === 'verified' && session.capabilities.includes('audit.read')
    : session.token !== null;
  const query = useQuery({
    queryKey: cookie
      ? ['review-cases', 'audit', session.epoch, session.readiness, reviewCaseId]
      : ['review-cases', 'audit', session.epoch, reviewCaseId],
    enabled: canReadAudit,
    queryFn: ({ signal }) => listReviewCaseAudit(reviewCaseId, signal),
  });
  const history = query.isSuccess ? query.data : null;
  const openingMissing = history !== null
    && history.items.length > 0
    && history.items.every((entry) => entry.action !== 'Opened');

  return (
    <Stack id={id} spacing={1} component="section" aria-label="Stored history">
      <Typography color="text.secondary">
        Stored history is separate from the current case and from the balances above.
      </Typography>
      {query.isPending ? <Alert severity="info" role="status">Loading stored history</Alert> : null}
      {query.isError ? (
        <ReviewCaseAuditError error={query.error} onRetry={() => void query.refetch()} retrying={query.isFetching} />
      ) : null}
      {history !== null && history.items.length === 0 ? (
        <Alert severity="info">
          No stored history was found for this case. Earlier actions may not have been recorded.
        </Alert>
      ) : null}
      {history !== null && history.items.length > 0 ? (
        <Stack component="ol" aria-label="Stored history entries" spacing={1} sx={{ m: 0, pl: 0, listStyle: 'none' }}>
          {history.items.map((entry) => <AuditEntry key={entry.id} entry={entry} />)}
        </Stack>
      ) : null}
      {openingMissing ? (
        <Typography color="text.secondary">The opening entry is not in the stored history.</Typography>
      ) : null}
    </Stack>
  );
}

function AuditEntry({ entry }: { entry: ReviewCaseAuditEntry }) {
  return (
    <Stack component="li" spacing={0.5}>
      <Typography>{entry.action}</Typography>
      <Typography>{`Recorded ${entry.recordedAt}`}</Typography>
      <Typography sx={{ fontFamily: 'monospace' }}>{`Actor ${entry.actorId}`}</Typography>
      {entry.action === 'Opened' ? <OpenedPayload entry={entry} /> : null}
      {entry.action === 'Resolved' ? <ResolvedPayload entry={entry} /> : null}
      {entry.action === 'Dismissed' ? <DismissedPayload entry={entry} /> : null}
    </Stack>
  );
}

function OpenedPayload({ entry }: { entry: ReviewCaseAuditEntry }) {
  return (
    <>
      <Typography>{`Reason ${entry.reason ?? 'none'}`}</Typography>
      <Typography>{`Invoice id ${entry.invoiceId ?? 'none'}`}</Typography>
      <Typography>{`Bank transaction id ${entry.bankTransactionId ?? 'none'}`}</Typography>
      <Typography>{`Source ${entry.sourceLabel ?? 'none'}`}</Typography>
      <Typography>{`Note ${entry.note ?? 'none'}`}</Typography>
    </>
  );
}

function ResolvedPayload({ entry }: { entry: ReviewCaseAuditEntry }) {
  return (
    <>
      <Typography>{`Outcome ${entry.outcome ?? 'none'}`}</Typography>
      <Typography>{`Allocation ${entry.allocationId ?? 'none'}`}</Typography>
      <Typography>{`Resolution note ${entry.resolutionNote ?? 'none'}`}</Typography>
    </>
  );
}

function DismissedPayload({ entry }: { entry: ReviewCaseAuditEntry }) {
  return <Typography>{`Resolution note ${entry.resolutionNote ?? 'none'}`}</Typography>;
}

function ReviewCaseAuditError({
  error,
  onRetry,
  retrying,
}: {
  error: unknown;
  onRetry: () => void;
  retrying: boolean;
}) {
  if (error instanceof StaleSessionError) {
    return null;
  }

  if (error instanceof ApiError && error.status === 401) {
    if (browserAuthMode() === 'cookie') {
      return <Alert severity="warning">The browser session needs to be checked again. Stored history stays hidden until that check succeeds.</Alert>;
    }

    return (
      <Alert severity="warning" action={<Button color="inherit" onClick={() => endSession('unauthorized')}>Update access</Button>}>
        Your access has expired or is no longer valid. Update it to continue.
      </Alert>
    );
  }

  if (error instanceof ApiError && error.status === 403) {
    if (browserAuthMode() === 'cookie') {
      return (
        <Alert severity="warning" action={<RetryButton onRetry={onRetry} retrying={retrying} />}>
          Stored history is not available for this organization.
        </Alert>
      );
    }

    return <Alert severity="warning">You don’t have access to this stored history.</Alert>;
  }

  if (error instanceof ApiError && error.status === 404) {
    return <Alert severity="warning">That review case was not found.</Alert>;
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
        We couldn’t read the stored history.
      </Alert>
    );
  }

  return (
    <Alert severity="error" action={<RetryButton onRetry={onRetry} retrying={retrying} />}>
      Stored history could not be loaded.
    </Alert>
  );
}

function DocumentId({ label, documentId }: { label: string; documentId: string | null }) {
  if (documentId === null) {
    return <Typography>{`${label} id: this case does not name one.`}</Typography>;
  }

  return <Typography>{`${label} id ${documentId}`}</Typography>;
}

function absent(value: string | null): string {
  return value ?? 'none';
}

function Observation({
  label,
  named,
  observation,
  kind,
}: {
  label: string;
  named: boolean;
  observation: ReviewCaseObservation;
  kind: 'outstanding' | 'unapplied';
}) {
  if (!named) {
    return <Typography>{`${label}: this case does not name one.`}</Typography>;
  }

  if (!observation.found || observation.amount === null || observation.currency === null) {
    return <Typography>{`${label}: the named document was not found.`}</Typography>;
  }

  const figure = kind === 'outstanding' ? 'outstanding' : 'unapplied';
  return <Typography>{`${label} ${figure} ${formatMoney(observation.amount, observation.currency)}`}</Typography>;
}

function LoadingList() {
  return <Alert severity="info" role="status">Loading review cases</Alert>;
}

function InvalidAddress() {
  return <Alert severity="warning">This review case address can’t be opened. Open Review cases again from the menu.</Alert>;
}

function ErrorState({
  error,
  onRetry,
  retrying,
  detail = false,
}: {
  error: unknown;
  onRetry: () => void;
  retrying: boolean;
  detail?: boolean;
}) {
  const subject = detail ? 'selected review case' : 'review cases';
  if (error instanceof ApiError && error.status === 401) {
    if (browserAuthMode() === 'cookie') {
      return <Alert severity="warning">The browser session needs to be checked again. Review cases stay hidden until that check succeeds.</Alert>;
    }

    return (
      <Alert severity="warning" action={<Button color="inherit" onClick={() => endSession('unauthorized')}>Update access</Button>}>
        Your access has expired or is no longer valid. Update it to continue.
      </Alert>
    );
  }

  if (error instanceof ApiError && error.status === 403) {
    if (browserAuthMode() === 'cookie') {
      return (
        <Alert severity="warning" action={<RetryButton onRetry={onRetry} retrying={retrying} />}>
          Review cases are not available for this organization.
        </Alert>
      );
    }

    return (
      <Alert severity="warning" action={<Button color="inherit" onClick={() => endSession('forbidden')}>Update access</Button>}>
        You don’t have access to review cases. Check your access details.
      </Alert>
    );
  }

  if (error instanceof ApiError && error.status === 404) {
    return <Alert severity="warning">That review case was not found.</Alert>;
  }

  if (error instanceof ApiError && (error.code === 'reconciliation.review_cases.invalid_pagination' || error.code === 'reconciliation.review_case.invalid_request')) {
    return <InvalidAddress />;
  }

  if (error instanceof ApiError && error.code === 'reconciliation.review_case.inconsistent_linked_money') {
    return (
      <Alert severity="error" action={<RetryButton onRetry={onRetry} retrying={retrying} />}>
        The linked balances could not be read. No amounts from that response are shown.
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
        {`We couldn’t read the ${subject}. No amounts from that response are shown.`}
      </Alert>
    );
  }

  return (
    <Alert severity="error" action={<RetryButton onRetry={onRetry} retrying={retrying} />}>
      {`The ${subject} could not be loaded. No amounts from that response are shown.`}
    </Alert>
  );
}

function RetryButton({ onRetry, retrying }: { onRetry: () => void; retrying: boolean }) {
  return <Button color="inherit" onClick={onRetry} disabled={retrying}>Try again</Button>;
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

function readStatus(value: string | null): ReviewCaseStatusName | 'absent' | 'invalid' {
  if (value === null) {
    return 'absent';
  }

  return statuses.includes(value as ReviewCaseStatusName) ? value as ReviewCaseStatusName : 'invalid';
}

function readOptionalGuid(params: URLSearchParams, name: string): string | 'absent' | 'invalid' {
  if (!params.has(name)) {
    return 'absent';
  }

  const value = params.get(name);
  if (value === null || !isGuid(value)) {
    return 'invalid';
  }

  return value.toLowerCase();
}

function isGuid(value: string): boolean {
  return guidPattern.test(value) && value.toLowerCase() !== emptyGuid;
}

function isPageOutOfRange(page: number, pageSize: number, totalCount: number): boolean {
  if (totalCount === 0) {
    return false;
  }

  return (page - 1) * pageSize >= totalCount;
}
