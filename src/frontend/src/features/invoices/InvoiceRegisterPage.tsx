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
import { useState } from 'react';
import { Link, useSearchParams } from 'react-router';

import { ApiError, NetworkError, UnexpectedResponseError } from '../../shared/api/errors';
import { endSession, useSession } from '../../shared/auth/session';
import { formatMoney } from '../../shared/money/money';
import { listInvoices, type InvoiceListItem } from './invoice-api';

const defaultPage = 1;
const defaultPageSize = 50;
const pageSizeOptions = [25, 50, 100];

export function InvoiceRegisterPage() {
  const session = useSession();
  const [searchParams, setSearchParams] = useSearchParams();
  const page = readPage(searchParams.get('page'));
  const pageSize = readPageSize(searchParams.get('pageSize'));
  const paginationValid = page !== null && pageSize !== null;
  const [selectedId, setSelectedId] = useState<string | null>(null);

  const query = useQuery({
    queryKey: ['invoices', session.epoch, page, pageSize],
    enabled: session.token !== null && paginationValid,
    queryFn: ({ signal }) => listInvoices(page ?? defaultPage, pageSize ?? defaultPageSize, signal),
  });

  const selected = query.data?.items.find((item) => item.id === selectedId) ?? null;

  const list = paginationValid && query.isSuccess ? query.data : null;
  const emptyOrganization = list !== null && list.totalCount === 0;
  const pageOutOfRange = list !== null && isPageOutOfRange(list.page, list.pageSize, list.totalCount);
  const emptyPage = list !== null && list.totalCount > 0 && list.items.length === 0;

  return (
    <Stack spacing={2}>
      <Stack direction="row" spacing={2} sx={{ alignItems: 'center', justifyContent: 'space-between' }}>
        <Typography variant="h4" component="h1">
          Invoices
        </Typography>
        <Stack direction="row" spacing={1}>
          <Button component={Link} to="/imports" variant="outlined">Import invoices</Button>
          {list !== null ? (
            <Button onClick={() => void query.refetch()} disabled={query.isFetching}>
              Refresh
            </Button>
          ) : null}
        </Stack>
      </Stack>
      <Typography color="text.secondary">
        Review your invoices and the amounts still outstanding. Select an invoice number to see its balances.
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
            setSelectedId(null);
            setSearchParams({
              page: '1',
              pageSize: String(list.pageSize),
            });
          }}
        />
      ) : null}
      {list !== null && list.totalCount > 0 && !pageOutOfRange ? (
        <InvoiceTable
          items={list.items}
          page={list.page}
          pageSize={list.pageSize}
          totalCount={list.totalCount}
          selectedId={selected?.id ?? null}
          onSelect={setSelectedId}
          onPageChange={(nextPage, nextPageSize) => {
            setSelectedId(null);
            setSearchParams({
              page: String(nextPage),
              pageSize: String(nextPageSize),
            });
          }}
        />
      ) : null}
      {selected ? <SelectedInvoice item={selected} /> : null}
    </Stack>
  );
}

function InvoiceTable({
  items,
  page,
  pageSize,
  totalCount,
  selectedId,
  onSelect,
  onPageChange,
}: {
  items: InvoiceListItem[];
  page: number;
  pageSize: number;
  totalCount: number;
  selectedId: string | null;
  onSelect: (id: string) => void;
  onPageChange: (page: number, pageSize: number) => void;
}) {
  return (
    <Paper>
      <TableContainer>
        <Table size="small" aria-label="Invoices">
          <TableHead>
            <TableRow>
              <TableCell scope="col">Invoice number</TableCell>
              <TableCell scope="col" align="right"><Tooltip describeChild title="The original invoice amount"><span tabIndex={0}>Total</span></Tooltip></TableCell>
              <TableCell scope="col" align="right"><Tooltip describeChild title="The amount already applied to this invoice"><span tabIndex={0}>Allocated</span></Tooltip></TableCell>
              <TableCell scope="col" align="right"><Tooltip describeChild title="The amount still to be covered"><span tabIndex={0}>Outstanding</span></Tooltip></TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {items.map((item) => (
              <TableRow key={item.id} selected={item.id === selectedId} hover>
                <TableCell>
                  <Button
                    variant="text"
                    onClick={() => onSelect(item.id)}
                    aria-pressed={item.id === selectedId}
                    aria-controls={item.id === selectedId ? 'selected-invoice' : undefined}
                  >
                    {item.invoiceNumber}
                  </Button>
                </TableCell>
                <TableCell align="right">{formatMoney(item.totalAmount, item.totalCurrency)}</TableCell>
                <TableCell align="right">
                  {formatMoney(item.allocatedAmount, item.allocatedCurrency)}
                </TableCell>
                <TableCell align="right">
                  {formatMoney(item.outstandingAmount, item.outstandingCurrency)}
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

function SelectedInvoice({ item }: { item: InvoiceListItem }) {
  return (
    <Paper id="selected-invoice" component="section" aria-labelledby="selected-invoice-heading" sx={{ p: 2 }}>
      <Typography id="selected-invoice-heading" variant="h6" component="h2" gutterBottom>
        Selected invoice
      </Typography>
      <Typography color="text.secondary" gutterBottom>
        These are the balances for the selected invoice.
      </Typography>
      <Typography>{item.invoiceNumber}</Typography>
      <Typography>Total {formatMoney(item.totalAmount, item.totalCurrency)}</Typography>
      <Typography>Allocated {formatMoney(item.allocatedAmount, item.allocatedCurrency)}</Typography>
      <Typography>
        Outstanding {formatMoney(item.outstandingAmount, item.outstandingCurrency)}
      </Typography>
      <Button
        component={Link}
        to={`/allocations?invoiceId=${item.id}`}
        variant="outlined"
        sx={{ mt: 1 }}
      >
        {`View allocations for invoice ${item.id}`}
      </Button>
    </Paper>
  );
}

function LoadingState() {
  return (
    <Alert severity="info" role="status">
      Loading invoices
    </Alert>
  );
}

function EmptyState() {
  return (
    <Alert severity="info">No invoices are in this organization yet. Import a CSV file to add invoices.</Alert>
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
      This page has no invoices. Other pages still contain invoices.
    </Alert>
  );
}

function InvalidPagination() {
  return <Alert severity="warning">This page can’t be opened. Open Invoices again from the menu.</Alert>;
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

  if (error instanceof ApiError && error.code === 'reconciliation.invoices.invalid_pagination') {
    return <InvalidPagination />;
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
        We couldn’t load your invoices. Please try again.
      </Alert>
    );
  }

  return (
    <Alert severity="error" action={<RetryButton onRetry={onRetry} retrying={retrying} />}>
      The invoice list could not be loaded.
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
