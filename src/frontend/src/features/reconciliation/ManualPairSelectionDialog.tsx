import {
  Alert,
  Box,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
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
import { useQuery } from '@tanstack/react-query';
import { useState, type FormEvent, type ReactNode } from 'react';

import { ApiError } from '../../shared/api/errors';
import { endSession, useSession } from '../../shared/auth/session';
import { formatMoney } from '../../shared/money/money';
import { listBankTransactions, type BankTransactionListItem } from '../bank-transactions/bank-transaction-api';
import { listInvoices, type InvoiceListItem } from '../invoices/invoice-api';

const pageSizeOptions = [25, 50, 100];
const maximumTermLength = 256;

const observation =
  'List balances are a current observation. They do not reserve either record, and selecting both records does not authorize an allocation.';

export function ManualPairSelectionDialog({
  open,
  epoch,
  invoice,
  bankTransaction,
  onSelectInvoice,
  onClearInvoice,
  onSelectBankTransaction,
  onClearBankTransaction,
  onClose,
  onReview,
}: {
  open: boolean;
  epoch: number;
  invoice: InvoiceListItem | null;
  bankTransaction: BankTransactionListItem | null;
  onSelectInvoice: (item: InvoiceListItem) => void;
  onClearInvoice: () => void;
  onSelectBankTransaction: (item: BankTransactionListItem) => void;
  onClearBankTransaction: () => void;
  onClose: () => void;
  onReview: () => void;
}) {
  const session = useSession();
  const [seenEpoch, setSeenEpoch] = useState(epoch);
  const [invoiceLookup, setInvoiceLookup] = useState<LookupState>(emptyLookup);
  const [bankLookup, setBankLookup] = useState<LookupState>(emptyLookup);

  if (epoch !== seenEpoch) {
    setSeenEpoch(epoch);
    setInvoiceLookup(emptyLookup);
    setBankLookup(emptyLookup);
  }

  const invoices = useQuery({
    queryKey: ['invoices', epoch, invoiceLookup.page, invoiceLookup.pageSize, invoiceLookup.applied ?? ''],
    enabled: open && session.token !== null && epoch === session.epoch,
    queryFn: ({ signal }) => listInvoices(
      invoiceLookup.page,
      invoiceLookup.pageSize,
      signal,
      invoiceLookup.applied ?? undefined,
    ),
  });
  const bankTransactions = useQuery({
    queryKey: ['bank-transactions', epoch, bankLookup.page, bankLookup.pageSize, bankLookup.applied ?? ''],
    enabled: open && session.token !== null && epoch === session.epoch,
    queryFn: ({ signal }) => listBankTransactions(
      bankLookup.page,
      bankLookup.pageSize,
      signal,
      bankLookup.applied ?? undefined,
    ),
  });

  return (
    <Dialog
      open={open}
      onClose={onClose}
      aria-labelledby="manual-pair-selection-title"
      fullWidth
      maxWidth="lg"
      transitionDuration={0}
    >
      <DialogTitle id="manual-pair-selection-title">Choose records</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ pt: 1 }}>
          <Typography color="text.secondary">{observation}</Typography>
          <SelectedPair invoice={invoice} bankTransaction={bankTransaction} onReview={onReview} />
          <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} sx={{ alignItems: 'stretch' }}>
            <LookupSection
              title="Invoices"
              inputLabel="Invoice number"
              searchLabel="Search invoices"
              clearLabel="Clear invoice search"
              emptyFiltered="No invoices match that number."
              emptyUnfiltered="No invoices are in this organization yet."
              loadingLabel="Loading invoices"
              tableLabel="Invoices"
              lookup={invoiceLookup}
              onLookupChange={setInvoiceLookup}
              query={invoices}
              renderHead={(
                <TableRow>
                  <TableCell scope="col">Invoice number</TableCell>
                  <TableCell scope="col" align="right">Total</TableCell>
                  <TableCell scope="col" align="right">Allocated</TableCell>
                  <TableCell scope="col" align="right">Outstanding</TableCell>
                  <TableCell scope="col">Select</TableCell>
                </TableRow>
              )}
              renderRow={(item) => (
                <TableRow key={item.id} selected={item.id === invoice?.id}>
                  <TableCell>{item.invoiceNumber}</TableCell>
                  <TableCell align="right">{formatMoney(item.totalAmount, item.totalCurrency)}</TableCell>
                  <TableCell align="right">{formatMoney(item.allocatedAmount, item.allocatedCurrency)}</TableCell>
                  <TableCell align="right">{formatMoney(item.outstandingAmount, item.outstandingCurrency)}</TableCell>
                  <TableCell>
                    <Button
                      type="button"
                      size="small"
                      aria-pressed={item.id === invoice?.id}
                      onClick={() => onSelectInvoice(item)}
                    >
                      {`Select invoice ${item.invoiceNumber} ${item.id}`}
                    </Button>
                  </TableCell>
                </TableRow>
              )}
            />
            <LookupSection
              title="Bank transactions"
              inputLabel="Payment reference"
              searchLabel="Search bank transactions"
              clearLabel="Clear bank transaction search"
              emptyFiltered="No bank transactions match that reference."
              emptyUnfiltered="No bank transactions are in this organization yet."
              loadingLabel="Loading bank transactions"
              tableLabel="Bank transactions"
              lookup={bankLookup}
              onLookupChange={setBankLookup}
              query={bankTransactions}
              renderHead={(
                <TableRow>
                  <TableCell scope="col">Booking date</TableCell>
                  <TableCell scope="col">Reference</TableCell>
                  <TableCell scope="col" align="right">Amount</TableCell>
                  <TableCell scope="col" align="right">Allocated</TableCell>
                  <TableCell scope="col" align="right">Unapplied</TableCell>
                  <TableCell scope="col">Source</TableCell>
                  <TableCell scope="col">Select</TableCell>
                </TableRow>
              )}
              renderRow={(item) => (
                <TableRow key={item.id} selected={item.id === bankTransaction?.id}>
                  <TableCell>{item.bookingDate}</TableCell>
                  <TableCell>{item.reference ?? 'None'}</TableCell>
                  <TableCell align="right">{formatMoney(item.amount, item.currency)}</TableCell>
                  <TableCell align="right">{formatMoney(item.allocatedAmount, item.allocatedCurrency)}</TableCell>
                  <TableCell align="right">{formatMoney(item.unappliedAmount, item.unappliedCurrency)}</TableCell>
                  <TableCell>{sourceLabel(item)}</TableCell>
                  <TableCell>
                    <Button
                      type="button"
                      size="small"
                      aria-pressed={item.id === bankTransaction?.id}
                      onClick={() => onSelectBankTransaction(item)}
                    >
                      {`Select bank transaction ${item.reference ?? 'without a reference'} ${item.id}`}
                    </Button>
                  </TableCell>
                </TableRow>
              )}
            />
          </Stack>
          {invoice !== null ? (
            <Button type="button" onClick={onClearInvoice}>Clear selected invoice</Button>
          ) : null}
          {bankTransaction !== null ? (
            <Button type="button" onClick={onClearBankTransaction}>Clear selected bank transaction</Button>
          ) : null}
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button type="button" onClick={onClose}>Close</Button>
      </DialogActions>
    </Dialog>
  );
}

function SelectedPair({
  invoice,
  bankTransaction,
  onReview,
}: {
  invoice: InvoiceListItem | null;
  bankTransaction: BankTransactionListItem | null;
  onReview: () => void;
}) {
  return (
    <Box component="section" aria-label="Selected pair">
      <Typography component="h2" variant="h6">Selected pair</Typography>
      <Typography>
        {invoice === null
          ? 'No invoice selected.'
          : `Invoice ${invoice.invoiceNumber}, outstanding ${formatMoney(invoice.outstandingAmount, invoice.outstandingCurrency)}.`}
      </Typography>
      <Typography>
        {bankTransaction === null
          ? 'No bank transaction selected.'
          : `Bank reference ${bankTransaction.reference ?? 'None'}, unapplied ${formatMoney(bankTransaction.unappliedAmount, bankTransaction.unappliedCurrency)}.`}
      </Typography>
      {invoice !== null && bankTransaction !== null ? (
        <Button type="button" variant="contained" onClick={onReview} sx={{ mt: 1 }}>
          Review balances
        </Button>
      ) : null}
    </Box>
  );
}

function LookupSection<T extends { id: string }>({
  title,
  inputLabel,
  searchLabel,
  clearLabel,
  emptyFiltered,
  emptyUnfiltered,
  loadingLabel,
  tableLabel,
  lookup,
  onLookupChange,
  query,
  renderHead,
  renderRow,
}: {
  title: string;
  inputLabel: string;
  searchLabel: string;
  clearLabel: string;
  emptyFiltered: string;
  emptyUnfiltered: string;
  loadingLabel: string;
  tableLabel: string;
  lookup: LookupState;
  onLookupChange: (lookup: LookupState) => void;
  query: PagedQuery<T>;
  renderHead: ReactNode;
  renderRow: (item: T) => ReactNode;
}) {
  const list = query.isSuccess ? query.data : undefined;
  const items = list?.items ?? [];
  const empty = list !== undefined && list.totalCount === 0;
  const laterPage = list !== undefined && list.totalCount > 0 && items.length === 0;

  function search(event: FormEvent): void {
    event.preventDefault();
    const decision = normalizeSearch(lookup.draft);
    if (decision.error !== null) {
      onLookupChange({ ...lookup, error: decision.error });
      return;
    }

    onLookupChange({ ...lookup, applied: decision.term, page: 1, error: null });
  }

  return (
    <Stack spacing={1} sx={{ flex: 1, minWidth: 0 }} component="section" aria-label={title}>
      <Typography component="h2" variant="h6">{title}</Typography>
      <Stack component="form" direction="row" spacing={1} onSubmit={search} sx={{ alignItems: 'flex-start', flexWrap: 'wrap' }}>
        <TextField
          label={inputLabel}
          value={lookup.draft}
          onChange={(event) => onLookupChange({ ...lookup, draft: event.target.value, error: null })}
          error={lookup.error !== null}
          helperText={lookup.error ?? 'Search runs when you submit this form.'}
          size="small"
        />
        <Button type="submit">{searchLabel}</Button>
        <Button
          type="button"
          onClick={() => onLookupChange({ ...emptyLookup, pageSize: lookup.pageSize })}
        >
          {clearLabel}
        </Button>
      </Stack>
      {query.isPending ? <Alert severity="info" role="status">{loadingLabel}</Alert> : null}
      {query.isError ? <LookupError error={query.error} onRetry={() => void query.refetch()} retrying={query.isFetching} /> : null}
      {empty ? <Alert severity="info">{lookup.applied === null ? emptyUnfiltered : emptyFiltered}</Alert> : null}
      {laterPage ? (
        <Alert
          severity="info"
          action={<Button color="inherit" onClick={() => onLookupChange({ ...lookup, page: 1 })}>Go to first page</Button>}
        >
          This page has no records.
        </Alert>
      ) : null}
      {list !== undefined && items.length > 0 ? (
        <TableContainer>
          <Table size="small" aria-label={tableLabel}>
            <TableHead>{renderHead}</TableHead>
            <TableBody>{items.map((item) => renderRow(item))}</TableBody>
          </Table>
          <TablePagination
            component="div"
            count={list.totalCount}
            page={Math.max(0, list.page - 1)}
            rowsPerPage={list.pageSize}
            rowsPerPageOptions={pageSizeOptions}
            onPageChange={(_event, nextPage) => onLookupChange({ ...lookup, page: nextPage + 1 })}
            onRowsPerPageChange={(event) => onLookupChange({
              ...lookup,
              page: 1,
              pageSize: Number(event.target.value),
            })}
          />
        </TableContainer>
      ) : null}
    </Stack>
  );
}

function LookupError({
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
      <Alert severity="warning" action={<Button color="inherit" onClick={() => endSession('unauthorized')}>Update access</Button>}>
        Your access has expired or is no longer valid.
      </Alert>
    );
  }

  if (error instanceof ApiError && error.status === 403) {
    return <Alert severity="warning">These records are not available for this organization.</Alert>;
  }

  if (error instanceof ApiError && error.status === 400) {
    return <Alert severity="warning">The search was rejected.</Alert>;
  }

  return (
    <Alert severity="error" action={<Button color="inherit" onClick={onRetry} disabled={retrying}>Retry</Button>}>
      The list could not be loaded.
    </Alert>
  );
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

function normalizeSearch(value: string): { term: string | null; error: string | null } {
  const trimmed = value.trim();
  if (trimmed.length === 0) {
    return { term: null, error: null };
  }

  if (trimmed.length > maximumTermLength) {
    return { term: null, error: 'Use 1 to 256 characters.' };
  }

  return { term: trimmed, error: null };
}

interface PagedQuery<T extends { id: string }> {
  isPending: boolean;
  isSuccess: boolean;
  isError: boolean;
  isFetching: boolean;
  error: unknown;
  data: { items: T[]; page: number; pageSize: number; totalCount: number } | undefined;
  refetch: () => Promise<unknown>;
}

interface LookupState {
  draft: string;
  applied: string | null;
  error: string | null;
  page: number;
  pageSize: number;
}

const emptyLookup: LookupState = {
  draft: '',
  applied: null,
  error: null,
  page: 1,
  pageSize: 25,
};
