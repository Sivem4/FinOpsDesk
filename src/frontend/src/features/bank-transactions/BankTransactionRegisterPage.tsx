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
  Typography,
  Tooltip,
} from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import { Link, useSearchParams } from 'react-router';

import { ApiError, NetworkError, UnexpectedResponseError } from '../../shared/api/errors';
import { endSession, useSession } from '../../shared/auth/session';
import { compareDecimalAmounts, formatMoney, readMoneyAmount, type DecimalString } from '../../shared/money/money';
import { listBankTransactions, type BankTransactionListItem } from './bank-transaction-api';

const defaultPage = 1;
const defaultPageSize = 50;
const pageSizeOptions = [25, 50, 100];
const zero = readMoneyAmount('0');

export function BankTransactionRegisterPage() {
  const session = useSession();
  const [searchParams, setSearchParams] = useSearchParams();
  const page = readPage(searchParams.get('page'));
  const pageSize = readPageSize(searchParams.get('pageSize'));
  const paginationValid = page !== null && pageSize !== null;

  const query = useQuery({
    queryKey: ['bank-transactions', session.epoch, page, pageSize],
    enabled: session.token !== null && paginationValid,
    queryFn: ({ signal }) => listBankTransactions(page ?? defaultPage, pageSize ?? defaultPageSize, signal),
  });

  const list = paginationValid && query.isSuccess ? query.data : null;
  const emptyOrganization = list !== null && list.totalCount === 0;
  const pageOutOfRange = list !== null && isPageOutOfRange(list.page, list.pageSize, list.totalCount);
  const emptyPage = list !== null && list.totalCount > 0 && list.items.length === 0;

  return (
    <Stack spacing={2}>
      <Stack direction="row" spacing={2} sx={{ alignItems: 'center', justifyContent: 'space-between' }}>
        <Typography variant="h4" component="h1">
          Bank transactions
        </Typography>
        <Stack direction="row" spacing={1}>
          <Button component={Link} to="/imports" variant="outlined">Import bank transactions</Button>
          {list !== null ? (
            <Button onClick={() => void query.refetch()} disabled={query.isFetching}>
              Refresh
            </Button>
          ) : null}
        </Stack>
      </Stack>
      <Typography color="text.secondary">
        These balances are a current observation. They do not reserve a bank transaction.
      </Typography>
      {!paginationValid ? <InvalidPagination /> : null}
      {paginationValid && query.isPending ? <LoadingState /> : null}
      {paginationValid && query.isError ? (
        <ErrorState
          error={query.error}
          onRetry={() => void query.refetch()}
          retrying={query.isFetching}
        />
      ) : null}
      {emptyOrganization ? <EmptyState /> : null}
      {emptyPage ? (
        <EmptyPage
          onFirstPage={() => {
            setSearchParams({
              page: '1',
              pageSize: String(list.pageSize),
            });
          }}
        />
      ) : null}
      {list !== null && list.totalCount > 0 && !pageOutOfRange ? (
        <BankTransactionTable
          items={list.items}
          page={list.page}
          pageSize={list.pageSize}
          totalCount={list.totalCount}
          onPageChange={(nextPage, nextPageSize) => {
            setSearchParams({
              page: String(nextPage),
              pageSize: String(nextPageSize),
            });
          }}
        />
      ) : null}
    </Stack>
  );
}

function BankTransactionTable({
  items,
  page,
  pageSize,
  totalCount,
  onPageChange,
}: {
  items: BankTransactionListItem[];
  page: number;
  pageSize: number;
  totalCount: number;
  onPageChange: (page: number, pageSize: number) => void;
}) {
  return (
    <Paper>
      <TableContainer>
        <Table size="small" aria-label="Bank transactions">
          <TableHead>
            <TableRow>
              <TableCell scope="col">Booking date</TableCell>
              <TableCell scope="col">Reference</TableCell>
              <TableCell scope="col" align="right">
                <Tooltip describeChild title="The original bank transaction amount">
                  <span tabIndex={0}>Original</span>
                </Tooltip>
              </TableCell>
              <TableCell scope="col" align="right">
                <Tooltip describeChild title="The amount already applied from this bank transaction">
                  <span tabIndex={0}>Allocated</span>
                </Tooltip>
              </TableCell>
              <TableCell scope="col" align="right">
                <Tooltip describeChild title="The amount still available from this read">
                  <span tabIndex={0}>Unapplied</span>
                </Tooltip>
              </TableCell>
              <TableCell scope="col">Source</TableCell>
              <TableCell scope="col">State</TableCell>
              <TableCell scope="col">Allocations</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {items.map((item) => (
              <TableRow key={item.id} hover>
                <TableCell>{item.bookingDate}</TableCell>
                <TableCell>{item.reference ?? 'None'}</TableCell>
                <TableCell align="right">{formatMoney(item.amount, item.currency)}</TableCell>
                <TableCell align="right">{formatMoney(item.allocatedAmount, item.allocatedCurrency)}</TableCell>
                <TableCell align="right">{formatMoney(item.unappliedAmount, item.unappliedCurrency)}</TableCell>
                <TableCell>{sourceLabel(item)}</TableCell>
                <TableCell>{allocationState(item.allocatedAmount, item.unappliedAmount)}</TableCell>
                <TableCell>
                  <Button
                    component={Link}
                    to={`/allocations?bankTransactionId=${item.id}`}
                    variant="text"
                  >
                    {`View allocations for bank transaction ${item.id}`}
                  </Button>
                </TableCell>
              </TableRow>
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

function allocationState(allocatedAmount: DecimalString, unappliedAmount: DecimalString): string {
  if (compareDecimalAmounts(unappliedAmount, zero) === 0) {
    return 'Fully allocated';
  }

  if (compareDecimalAmounts(allocatedAmount, zero) === 0) {
    return 'Unallocated';
  }

  return 'Partially allocated';
}

function sourceLabel(item: BankTransactionListItem): string {
  if (item.sourceSystem === null && item.externalId === null) {
    return 'None';
  }

  if (item.sourceSystem !== null && item.externalId !== null) {
    return `${item.sourceSystem} · ${item.externalId}`;
  }

  return item.sourceSystem ?? item.externalId ?? 'None';
}

function LoadingState() {
  return (
    <Alert severity="info" role="status">
      Loading bank transactions
    </Alert>
  );
}

function EmptyState() {
  return (
    <Alert severity="info">
      No bank transactions are in this organization yet. Import a CSV file to add bank transactions.
    </Alert>
  );
}

function EmptyPage({ onFirstPage }: { onFirstPage: () => void }) {
  return (
    <Alert
      severity="info"
      action={
        <Button color="inherit" onClick={onFirstPage}>
          Go to first page
        </Button>
      }
    >
      This page has no bank transactions. Other pages still contain bank transactions.
    </Alert>
  );
}

function InvalidPagination() {
  return <Alert severity="warning">This page can’t be opened. Open Bank transactions again from the menu.</Alert>;
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
        action={
          <Button color="inherit" onClick={() => endSession('unauthorized')}>
            Update access
          </Button>
        }
      >
        Your access has expired or is no longer valid. Update it to continue.
      </Alert>
    );
  }

  if (error instanceof ApiError && error.status === 403) {
    return (
      <Alert
        severity="warning"
        action={
          <Button color="inherit" onClick={() => endSession('forbidden')}>
            Update access
          </Button>
        }
      >
        You don’t have access to this workspace. Check your access details.
      </Alert>
    );
  }

  if (error instanceof ApiError && error.code === 'reconciliation.bank_transactions.invalid_pagination') {
    return <InvalidPagination />;
  }

  if (error instanceof ApiError && error.code === 'reconciliation.bank_transactions.inconsistent_money') {
    return (
      <Alert severity="error" action={<RetryButton onRetry={onRetry} retrying={retrying} />}>
        Stored amounts on this page are inconsistent. The page was not shown.
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
        We couldn’t load your bank transactions. Please try again.
      </Alert>
    );
  }

  return (
    <Alert severity="error" action={<RetryButton onRetry={onRetry} retrying={retrying} />}>
      The bank transaction list could not be loaded.
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

  return readBoundedInt(value, 1, 100);
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
