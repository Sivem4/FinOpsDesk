import { parse } from 'lossless-json';

import {
  StaleSessionError,
  UnexpectedResponseError,
  UnknownImportOutcomeError,
} from '../../shared/api/errors';
import { toApiError } from '../../shared/api/problem';
import { sessionStore } from '../../shared/api/session-store';
import { readSafeInt } from '../../shared/money/money';
import type { ImportFormValues } from './import-form';

const importResponseMessage = 'The API returned a response the import workspace cannot read.';

export class UnreadableImportResultError extends Error {
  constructor() {
    super(
      'The server returned a result this screen cannot read. The import may already be stored.',
    );
    this.name = 'UnreadableImportResultError';
  }
}

export type ImportRoute = ImportFormValues['importType'];

export type ImportBatchStatus = 'Completed' | 'CompletedWithErrors' | 'Rejected';

export interface ImportRowError {
  dataRowIndex: number;
  sourceLineNumber: number | null;
  errorCode: string;
  message: string;
  columnName: string | null;
}

export interface ImportCsvResult {
  importBatchId: string;
  importType: 'Invoice' | 'BankTransaction';
  sourceSystem: string;
  fileHash: string;
  fileName: string;
  status: ImportBatchStatus;
  totalRows: number;
  importedRows: number;
  alreadyExistsRows: number;
  rejectedRows: number;
  createdAt: string;
  completedAt: string;
  replayed: boolean;
  rowErrors: ImportRowError[];
}

export async function postImport(
  importType: ImportRoute,
  sourceSystem: string,
  file: File,
  signal: AbortSignal,
  expectedEpoch: number,
): Promise<ImportCsvResult> {
  const session = sessionStore.getSnapshot();

  if (session.epoch !== expectedEpoch) {
    throw new StaleSessionError();
  }

  if (session.token === null) {
    throw toApiError(401, '');
  }

  const epoch = expectedEpoch;
  const body = new FormData();
  body.append('file', file, file.name);
  body.append('sourceSystem', sourceSystem);

  let response: Response;

  try {
    response = await fetch(`/api/imports/${importType}`, {
      method: 'POST',
      headers: {
        Accept: 'application/json',
        Authorization: `Bearer ${session.token}`,
      },
      body,
      signal: AbortSignal.any([signal, session.signal]),
    });
  } catch {
    throw transportOutcome(epoch);
  }

  assertCurrentEpoch(epoch);

  let text: string;

  try {
    text = await response.text();
  } catch {
    if (!response.ok) {
      throw toApiError(response.status, '');
    }

    assertCurrentEpoch(epoch);
    throw new UnreadableImportResultError();
  }

  assertCurrentEpoch(epoch);

  if (!response.ok) {
    throw toApiError(response.status, text);
  }

  try {
    return mapImportCsvResponse(parse(text), importType);
  } catch (error) {
    if (error instanceof StaleSessionError) {
      throw error;
    }

    assertCurrentEpoch(epoch);
    throw new UnreadableImportResultError();
  }
}

function transportOutcome(epoch: number): Error {
  if (sessionStore.getSnapshot().epoch !== epoch) {
    return new StaleSessionError();
  }

  return new UnknownImportOutcomeError();
}

function assertCurrentEpoch(epoch: number): void {
  if (sessionStore.getSnapshot().epoch !== epoch) {
    throw new StaleSessionError();
  }
}

function mapImportCsvResponse(value: unknown, route: ImportRoute): ImportCsvResult {
  if (!isRecord(value) || !Array.isArray(value.rowErrors)) {
    throw new UnexpectedResponseError(importResponseMessage);
  }

  const importType = route === 'invoices' ? 'Invoice' : 'BankTransaction';

  if (value.importType !== importType) {
    throw new UnexpectedResponseError(importResponseMessage);
  }

  if (typeof value.replayed !== 'boolean') {
    throw new UnexpectedResponseError(importResponseMessage);
  }

  const status = readStatus(value.status);
  const totalRows = readSafeInt(value.totalRows, 'totalRows', 0, Number.MAX_SAFE_INTEGER);
  const importedRows = readSafeInt(value.importedRows, 'importedRows', 0, Number.MAX_SAFE_INTEGER);
  const alreadyExistsRows = readSafeInt(
    value.alreadyExistsRows,
    'alreadyExistsRows',
    0,
    Number.MAX_SAFE_INTEGER,
  );
  const rejectedRows = readSafeInt(value.rejectedRows, 'rejectedRows', 0, Number.MAX_SAFE_INTEGER);

  if (importedRows + alreadyExistsRows + rejectedRows !== totalRows) {
    throw new UnexpectedResponseError(importResponseMessage);
  }

  if (!statusMatchesCounts(status, totalRows, importedRows + alreadyExistsRows, rejectedRows)) {
    throw new UnexpectedResponseError(importResponseMessage);
  }

  const rowErrors = value.rowErrors.map(mapRowError);

  if (rowErrors.length !== rejectedRows) {
    throw new UnexpectedResponseError(importResponseMessage);
  }

  return {
    importBatchId: readGuid(value.importBatchId),
    importType,
    sourceSystem: readText(value.sourceSystem),
    fileHash: readFileHash(value.fileHash),
    fileName: readText(value.fileName),
    status,
    totalRows,
    importedRows,
    alreadyExistsRows,
    rejectedRows,
    createdAt: readText(value.createdAt),
    completedAt: readText(value.completedAt),
    replayed: value.replayed,
    rowErrors,
  };
}

function mapRowError(value: unknown): ImportRowError {
  if (!isRecord(value)) {
    throw new UnexpectedResponseError(importResponseMessage);
  }

  return {
    dataRowIndex: readSafeInt(value.dataRowIndex, 'dataRowIndex', 1, Number.MAX_SAFE_INTEGER),
    sourceLineNumber: readOptionalLine(value.sourceLineNumber),
    errorCode: readText(value.errorCode),
    message: readText(value.message),
    columnName: readOptionalText(value.columnName),
  };
}

function statusMatchesCounts(
  status: ImportBatchStatus,
  totalRows: number,
  acceptedRows: number,
  rejectedRows: number,
): boolean {
  if (status === 'Completed') {
    return rejectedRows === 0;
  }

  if (status === 'CompletedWithErrors') {
    return rejectedRows > 0 && acceptedRows > 0;
  }

  return rejectedRows === totalRows && acceptedRows === 0 && totalRows > 0;
}

function readStatus(value: unknown): ImportBatchStatus {
  if (value === 'Completed' || value === 'CompletedWithErrors' || value === 'Rejected') {
    return value;
  }

  throw new UnexpectedResponseError(importResponseMessage);
}

function readOptionalLine(value: unknown): number | null {
  if (value === null) {
    return null;
  }

  return readSafeInt(value, 'sourceLineNumber', 1, Number.MAX_SAFE_INTEGER);
}

function readOptionalText(value: unknown): string | null {
  if (value === null) {
    return null;
  }

  return readText(value);
}

function readText(value: unknown): string {
  if (typeof value !== 'string' || value.trim().length === 0) {
    throw new UnexpectedResponseError(importResponseMessage);
  }

  return value;
}

function readGuid(value: unknown): string {
  if (typeof value !== 'string' || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value)) {
    throw new UnexpectedResponseError(importResponseMessage);
  }

  return value;
}

function readFileHash(value: unknown): string {
  if (typeof value !== 'string' || !/^[0-9a-fA-F]{64}$/.test(value)) {
    throw new UnexpectedResponseError(importResponseMessage);
  }

  return value;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
