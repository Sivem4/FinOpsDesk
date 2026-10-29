import { zodResolver } from '@hookform/resolvers/zod';
import {
  Accordion,
  AccordionDetails,
  AccordionSummary,
  Alert,
  AlertTitle,
  Button,
  FormControl,
  FormControlLabel,
  FormLabel,
  IconButton,
  InputAdornment,
  Paper,
  Radio,
  RadioGroup,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  TextField,
  Typography,
  Tooltip,
} from '@mui/material';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { useController, useForm, useWatch } from 'react-hook-form';
import { Link } from 'react-router';

import { ApiError, StaleSessionError, UnexpectedResponseError, UnknownImportOutcomeError } from '../../shared/api/errors';
import { sessionStore } from '../../shared/api/session-store';
import { endSession, useSession } from '../../shared/auth/session';
import { headersFor, importFormSchema, type ImportFormValues } from './import-form';
import { postImport, UnreadableImportResultError, type ImportCsvResult } from './import-api';

interface ImportAttempt {
  importType: ImportFormValues['importType'];
  sourceSystem: string;
  file: File;
  epoch: number;
}

const errorPageSize = 25;

const sourceSystemHelp =
  'Where is this file from? For example, SAP or your bank.';

const sourceSystemRule =
  'Spaces at the ends are removed. The name is stored in capitals, so sap and SAP are the same source.';

const completedWithErrorsSummary =
  'Some rows were not added. The counts show what was added and what was already here.';

export function ImportWorkspacePage() {
  const queryClient = useQueryClient();
  const session = useSession();
  const uploadController = useRef<AbortController | null>(null);
  const attemptRef = useRef<ImportAttempt | null>(null);
  const [operationEpoch, setOperationEpoch] = useState<number | null>(null);
  const [errorPage, setErrorPage] = useState(0);
  const [showRequirements, setShowRequirements] = useState(false);
  const form = useForm<ImportFormValues>({
    resolver: zodResolver(importFormSchema),
    defaultValues: {
      importType: 'invoices',
      sourceSystem: '',
      file: undefined as unknown as File,
    },
  });
  const importType = useWatch({ control: form.control, name: 'importType' });
  const selectedFile = useWatch({ control: form.control, name: 'file' });
  const fileField = useController({ control: form.control, name: 'file' });
  const typeField = useController({ control: form.control, name: 'importType' });

  const mutation = useMutation({
    networkMode: 'always',
    retry: false,
    onMutate: (attempt: ImportAttempt) => {
      setOperationEpoch(attempt.epoch);
      return attempt.epoch;
    },
    mutationFn: (attempt: ImportAttempt) => {
      const controller = new AbortController();
      uploadController.current = controller;
      return postImport(
        attempt.importType,
        attempt.sourceSystem,
        attempt.file,
        controller.signal,
        attempt.epoch,
      ).finally(
        () => {
          if (uploadController.current === controller) {
            uploadController.current = null;
          }
        },
      );
    },
    onSuccess: (_result, attempt, epoch) => {
      if (attempt.importType !== 'invoices' || sessionStore.getSnapshot().epoch !== epoch) {
        return;
      }

      void queryClient.invalidateQueries({
        queryKey: ['invoices', epoch],
      });
    },
  });

  useEffect(() => {
    return () => {
      uploadController.current?.abort();
    };
  }, []);

  const sameSession = operationEpoch === session.epoch;
  const result = mutation.isSuccess && sameSession ? mutation.data : null;
  const failure = mutation.isError && (sameSession || mutation.error instanceof StaleSessionError) ? mutation.error : null;

  return (
    <Stack spacing={3} component="form" sx={{ maxWidth: 960 }} onSubmit={form.handleSubmit((values) => submit(values))}>
      <Typography variant="h4" component="h1">
        Imports
      </Typography>
      <Typography color="text.secondary">
        Add invoices or bank transactions from a CSV file.
      </Typography>
      <Paper variant="outlined" sx={{ p: { xs: 2, sm: 3 } }}>
        <Stack spacing={3}>
          <FormControl>
            <FormLabel id="import-type-label">What would you like to add?</FormLabel>
            <RadioGroup
              aria-labelledby="import-type-label"
              row
              value={typeField.field.value}
              onChange={(event) => typeField.field.onChange(event.target.value)}
            >
              <FormControlLabel value="invoices" control={<Radio />} label="Invoices" />
              <FormControlLabel value="bank-transactions" control={<Radio />} label="Bank transactions" />
            </RadioGroup>
          </FormControl>
          <TextField
            {...form.register('sourceSystem')}
            label="File source"
            placeholder="e.g. SAP"
            error={form.formState.errors.sourceSystem !== undefined}
            helperText={form.formState.errors.sourceSystem ? (form.formState.errors.sourceSystem.message === 'Enter a source system.' ? 'Enter where this file came from.' : 'Use a source name of 64 characters or fewer.') : sourceSystemHelp}
            fullWidth
            slotProps={{
              input: {
                endAdornment: (
                  <InputAdornment position="end">
                    <Tooltip describeChild title={sourceSystemRule}>
                      <IconButton type="button" edge="end" size="small" aria-label="How the file source is stored">
                        <InfoMark />
                      </IconButton>
                    </Tooltip>
                  </InputAdornment>
                ),
              },
            }}
          />
          <Stack spacing={1}>
            <Stack direction="row" sx={{ alignItems: 'center', gap: 1 }}>
            <Typography component="label" htmlFor="import-file" sx={{ fontSize: '1rem', lineHeight: 1.5, fontWeight: 500 }}>
              CSV file
            </Typography>
            <Tooltip title={showRequirements ? '' : 'File requirements'}>
              <IconButton type="button" aria-label="File requirements" aria-expanded={showRequirements} aria-controls="file-requirements" onClick={() => setShowRequirements((open) => !open)}>
                <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden="true"><circle cx="12" cy="12" r="9" /><path d="M12 11v6M12 7v1" /></svg>
              </IconButton>
            </Tooltip>
            </Stack>
            {showRequirements ? (
              <Paper id="file-requirements" component="section" aria-labelledby="file-requirements-title" variant="outlined" sx={{ p: 2 }}>
                <Typography id="file-requirements-title" component="h2" variant="subtitle2">Prepare your CSV</Typography>
                <Typography variant="body2" sx={{ overflowWrap: 'anywhere', my: 1 }}>Include these column names: {headersFor(importType)}</Typography>
                <Typography variant="body2">Columns can be in any order. Give each row an ExternalId from your source.</Typography>
                <Typography variant="body2">Use positive amounts with a decimal point and up to four decimal places, and a three-letter currency such as PLN.</Typography>
                {importType === 'bank-transactions' ? <Typography variant="body2">Use dates like 2026-10-29 (year-month-day) for BookingDate.</Typography> : null}
              </Paper>
            ) : null}
            <input
              id="import-file"
              type="file"
              accept=".csv,text/csv"
              aria-describedby="import-file-help import-file-error"
              aria-invalid={fileField.fieldState.error !== undefined}
              style={{ maxWidth: '100%', font: 'inherit' }}
              onChange={(event) => {
                fileField.field.onChange(event.target.files?.[0]);
              }}
            />
            <Typography id="import-file-help" variant="body2" color="text.secondary">
              CSV, up to 2 MiB.
            </Typography>
            <Typography id="import-file-error" color="error" role={fileField.fieldState.error ? 'alert' : undefined}>
              {fileField.fieldState.error?.message ? (selectedFile instanceof File ? 'Choose a file smaller than 2 MiB.' : 'Choose a CSV file to continue.') : null}
            </Typography>
          </Stack>
          {selectedFile instanceof File ? (
            <Typography>
              {selectedFile.name} · {formatFileSize(selectedFile.size)}
            </Typography>
          ) : null}
          <Button type="submit" variant="contained" sx={{ alignSelf: 'flex-start', minHeight: 44, px: 4 }} disabled={mutation.isPending}>
            Import file
          </Button>
        </Stack>
      </Paper>
      {mutation.isPending ? <Alert severity="info" role="status">Import in progress</Alert> : null}
      {failure ? (
        <ImportFailure
          error={failure}
          onRetry={retryCapturedAttempt}
        />
      ) : null}
      {result ? <ImportResult result={result} errorPage={errorPage} onErrorPage={setErrorPage} /> : null}
    </Stack>
  );

  function submit(values: ImportFormValues): void {
    if (mutation.isPending) {
      return;
    }

    const attempt: ImportAttempt = {
      importType: values.importType,
      sourceSystem: values.sourceSystem.trim(),
      file: values.file,
      epoch: sessionStore.getSnapshot().epoch,
    };
    attemptRef.current = attempt;
    setErrorPage(0);
    mutation.mutate(attempt);
  }

  function retryCapturedAttempt(): void {
    const attempt = attemptRef.current;

    if (attempt === null || mutation.isPending || sessionStore.getSnapshot().epoch !== attempt.epoch) {
      return;
    }

    setErrorPage(0);
    mutation.mutate(attempt);
  }
}

function ImportFailure({ error, onRetry }: { error: unknown; onRetry: () => void }) {
  if (error instanceof UnknownImportOutcomeError || error instanceof UnreadableImportResultError) {
    return (
      <Alert
        severity="warning"
        action={
          <Button type="button" color="inherit" onClick={onRetry}>
            Send the same file again
          </Button>
        }
      >
        <AlertTitle>We couldn’t confirm the result</AlertTitle>
        We don’t know whether this file was saved. Send the same file again. If it was saved, you’ll see the earlier result. If it wasn’t, the import will run.
      </Alert>
    );
  }

  if (error instanceof StaleSessionError) {
    return (
      <Alert severity="warning">
        Your session changed. Open Imports again to continue.
      </Alert>
    );
  }

  if (error instanceof ApiError && error.status === 401) {
    return (
      <Alert
        severity="warning"
        action={
          <Button type="button" color="inherit" onClick={() => endSession('unauthorized')}>
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
          <Button type="button" color="inherit" onClick={() => endSession('forbidden')}>
            Update access
          </Button>
        }
      >
        This sign-in isn’t connected to an organization.
      </Alert>
    );
  }

  if (error instanceof ApiError && error.status === 400) {
    return (
      <Alert severity="error">
        <AlertTitle>File not accepted</AlertTitle>
        Nothing was imported. {error.detail || 'Check the file requirements and choose a corrected CSV.'}
        <ApiDiagnostics error={error} shown={error.detail || 'Check the file requirements and choose a corrected CSV.'} />
      </Alert>
    );
  }

  if (error instanceof ApiError && error.status === 409) {
    return (
      <Alert severity="error">
        <AlertTitle>Please try again shortly</AlertTitle>
        The import was not saved.
        <ApiDiagnostics error={error} shown="The import was not saved." />
      </Alert>
    );
  }

  if (error instanceof ApiError && error.status === 413) {
    return (
      <Alert severity="error">
        <AlertTitle>Upload too large</AlertTitle>
        Choose a smaller CSV file, up to 2 MiB.
        <ApiDiagnostics error={error} shown="Choose a smaller CSV file, up to 2 MiB." />
      </Alert>
    );
  }

  if (error instanceof UnexpectedResponseError) {
    return <Alert severity="error">We couldn’t show the result. Please try again later.</Alert>;
  }

  if (error instanceof ApiError) {
    return (
      <Alert severity="error">
        We couldn’t confirm the import. Try the same file again later.
        <ApiDiagnostics error={error} shown="We couldn’t confirm the import. Try the same file again later." />
      </Alert>
    );
  }

  return <Alert severity="error">We couldn’t show the result. Please try again later.</Alert>;
}

function ImportResult({
  result,
  errorPage,
  onErrorPage,
}: {
  result: ImportCsvResult;
  errorPage: number;
  onErrorPage: (page: number) => void;
}) {
  const pageCount = Math.max(1, Math.ceil(result.rowErrors.length / errorPageSize));
  const safePage = Math.min(errorPage, pageCount - 1);
  const visibleErrors = result.rowErrors.slice(safePage * errorPageSize, (safePage + 1) * errorPageSize);
  const firstError = result.rowErrors.length === 0 ? 0 : safePage * errorPageSize + 1;
  const lastError = safePage * errorPageSize + visibleErrors.length;

  return (
    <Stack spacing={2}>
      <Typography variant="h5" component="h2">
        Import result
      </Typography>
      <Typography sx={{ overflowWrap: 'anywhere' }}>
        {result.fileName} · {result.importType === 'Invoice' ? 'Invoices' : 'Bank transactions'} · Source: {result.sourceSystem}
      </Typography>
      <Alert severity={severityFor(result.status)}>
        <AlertTitle>{result.status === 'Completed' ? 'Import complete' : result.status === 'CompletedWithErrors' ? 'Some rows need attention' : 'No rows added'}</AlertTitle>
        {summaryFor(result.status)}
      </Alert>
      {result.replayed ? (
        <Alert severity="info">
          <AlertTitle>You’ve already imported this file</AlertTitle>
          Here’s the earlier result. Nothing was added again.
        </Alert>
      ) : null}
      <ImportCounts result={result} />
      {result.alreadyExistsRows > 0 ? (
        <Typography variant="body2" color="text.secondary">Rows already in your workspace were left unchanged.</Typography>
      ) : null}
      {canReviewInvoices(result) ? (
        <Button component={Link} to="/invoices" variant="outlined">
          Review invoices
        </Button>
      ) : null}
      {result.rowErrors.length > 0 ? (
        <Stack spacing={1}>
          <Typography component="h3" variant="subtitle1" sx={{ fontWeight: 600 }}>
            {result.rejectedRows === 1 ? '1 row needs attention' : `${result.rejectedRows} rows need attention`}
          </Typography>
          <Typography variant="body2" color="text.secondary">
            Fix these rows in your file, then import it again using the same file source. Existing records won’t be added twice.
          </Typography>
          <TableContainer>
            <Table size="small" aria-label="Rows to fix">
              <TableHead>
                <TableRow>
                  <TableCell scope="col">Row</TableCell>
                  <TableCell scope="col">
                    <Tooltip describeChild title="The first line of this record in the file.">
                      <span tabIndex={0}>Line</span>
                    </Tooltip>
                  </TableCell>
                  <TableCell scope="col">Field</TableCell>
                  <TableCell scope="col">What to change</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {visibleErrors.map((error) => (
                  <TableRow key={`${error.dataRowIndex}-${error.errorCode}`}>
                    <TableCell>{error.dataRowIndex}</TableCell>
                    <TableCell>{error.sourceLineNumber ?? '—'}</TableCell>
                    <TableCell>{error.columnName ?? '—'}</TableCell>
                    <TableCell>
                      <Typography component="p" variant="body2" sx={{ m: 0 }}>
                        {error.message}
                      </Typography>
                      <Typography component="p" variant="body2" color="text.secondary" sx={{ m: 0 }}>
                        {error.errorCode}
                      </Typography>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </TableContainer>
          {pageCount > 1 ? (
            <Stack spacing={1}>
              <Typography variant="body2" color="text.secondary">
                Showing {firstError === lastError ? `row ${firstError}` : `rows ${firstError} to ${lastError}`} of {result.rowErrors.length}
              </Typography>
              <Stack direction="row" spacing={1}>
                <Button type="button" onClick={() => onErrorPage(safePage - 1)} disabled={safePage === 0}>
                  Previous errors
                </Button>
                <Button type="button" onClick={() => onErrorPage(safePage + 1)} disabled={safePage >= pageCount - 1}>
                  Next errors
                </Button>
              </Stack>
            </Stack>
          ) : null}
        </Stack>
      ) : null}
      <TechnicalDetails result={result} />
    </Stack>
  );
}

function ImportCounts({ result }: { result: ImportCsvResult }) {
  return (
    <Stack component="dl" direction="row" spacing={1} useFlexGap sx={{ flexWrap: 'wrap', m: 0 }}>
      <ImportCount label="Added" value={result.importedRows} />
      <ImportCount label="Already in workspace" value={result.alreadyExistsRows} />
      <ImportCount label="Needs attention" value={result.rejectedRows} />
      <ImportCount label="Rows in file" value={result.totalRows} />
    </Stack>
  );
}

function ImportCount({ label, value }: { label: string; value: number }) {
  return (
    <Paper variant="outlined" sx={{ px: 2, py: 1, flex: '1 1 140px' }}>
      <Typography component="dt" variant="body2" color="text.secondary">
        {label}
      </Typography>
      <Typography component="dd" variant="h6" sx={{ m: 0 }}>
        {value}
      </Typography>
    </Paper>
  );
}

function TechnicalDetails({ result }: { result: ImportCsvResult }) {
  return (
    <Accordion disableGutters elevation={0} variant="outlined" sx={{ overflowWrap: 'anywhere' }}>
      <AccordionSummary
        id="import-technical-details"
        aria-controls="import-technical-details-content"
        type="button"
        expandIcon={<span aria-hidden="true">⌄</span>}
      >
        <Typography component="span">Technical details</Typography>
      </AccordionSummary>
      <AccordionDetails id="import-technical-details-content">
        <Stack spacing={0.5}>
          <Typography>Import type: {result.importType}</Typography>
          <Typography>Status: {result.status}</Typography>
          <Typography>Batch ID: {result.importBatchId}</Typography>
          <Typography>Source system: {result.sourceSystem}</Typography>
          <Typography>File name: {result.fileName}</Typography>
          <Typography>SHA-256: {result.fileHash}</Typography>
          <Typography>Created: {result.createdAt}</Typography>
          <Typography>Completed: {result.completedAt}</Typography>
        </Stack>
      </AccordionDetails>
    </Accordion>
  );
}

function canReviewInvoices(result: ImportCsvResult): boolean {
  return result.importType === 'Invoice' && (result.importedRows > 0 || result.alreadyExistsRows > 0);
}

function severityFor(status: ImportCsvResult['status']): 'success' | 'warning' | 'error' {
  if (status === 'Completed') {
    return 'success';
  }

  if (status === 'CompletedWithErrors') {
    return 'warning';
  }

  return 'error';
}

function summaryFor(status: ImportCsvResult['status']): string {
  if (status === 'Completed') {
    return 'All rows have been checked. Review the totals below.';
  }

  if (status === 'CompletedWithErrors') {
    return completedWithErrorsSummary;
  }

  return 'Fix the rows listed below, then import your file again.';
}

function ApiDiagnostics({ error, shown }: { error: ApiError; shown: string }) {
  const text = diagnosticText(error, shown);
  if (text === null) {
    return null;
  }

  return (
    <Typography component="p" variant="body2" sx={{ mt: 1, overflowWrap: 'anywhere' }}>
      {text}
    </Typography>
  );
}

function diagnosticText(error: ApiError, shown: string): string | null {
  const genericTitles = new Set([
    'Unauthorized',
    'Forbidden',
    'The request was rejected.',
    'The import conflict was not resolved.',
    'The file is too large.',
    'The API could not complete the request.',
  ]);
  const parts: string[] = [];
  const detail = error.detail.trim();
  const title = error.title.trim();

  if (detail.length > 0 && !shown.includes(detail)) {
    parts.push(detail);
  }

  if (title.length > 0 && !shown.includes(title) && !parts.includes(title) && !genericTitles.has(title)) {
    parts.push(title);
  }

  return parts.length === 0 ? null : parts.join(' ');
}

function InfoMark() {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden="true">
      <circle cx="12" cy="12" r="9" />
      <path d="M12 11v6M12 7v1" />
    </svg>
  );
}

function formatFileSize(bytes: number): string {
  if (!Number.isSafeInteger(bytes) || bytes < 0) {
    return `${bytes} bytes`;
  }

  if (bytes < 1024) {
    return bytes === 1 ? '1 byte' : `${bytes} bytes`;
  }

  const kibibyte = 1024;
  const mebibyte = kibibyte * 1024;

  if (bytes < mebibyte) {
    return `${formatBinaryUnit(bytes, kibibyte)} KiB`;
  }

  return `${formatBinaryUnit(bytes, mebibyte)} MiB`;
}

function formatBinaryUnit(bytes: number, unit: number): string {
  const value = bytes / unit;

  if (Number.isInteger(value)) {
    return String(value);
  }

  return (Math.round(value * 10) / 10).toFixed(1);
}
