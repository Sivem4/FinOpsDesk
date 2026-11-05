import { isLosslessNumber } from 'lossless-json';

import { apiGet } from '../../shared/api/http';
import { UnexpectedResponseError } from '../../shared/api/errors';
import { readSafeInt } from '../../shared/money/money';

const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const emptyGuid = '00000000-0000-0000-0000-000000000000';
const timestampPattern = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$/;
const maxActorIdLength = 256;
const maxNoteLength = 500;
const auditPage = 1;
const auditPageSize = 50;

export type ReviewCaseAuditActionName = 'Opened' | 'Resolved' | 'Dismissed';
export type ReviewCaseAuditReasonName = 'CompetingMatch' | 'NeedsInvestigation';
export type ReviewCaseAuditOutcomeName = 'AllocationRecorded' | 'NoActionRequired' | 'HandledOutside';
export type ReviewCaseAuditSourceName = 'manual' | 'exact-1' | 'smart-1';

export interface ReviewCaseAuditEntry {
  id: string;
  action: ReviewCaseAuditActionName;
  actorId: string;
  recordedAt: string;
  reason: ReviewCaseAuditReasonName | null;
  invoiceId: string | null;
  bankTransactionId: string | null;
  sourceLabel: ReviewCaseAuditSourceName | null;
  note: string | null;
  outcome: ReviewCaseAuditOutcomeName | null;
  allocationId: string | null;
  resolutionNote: string | null;
}

export interface ReviewCaseAuditList {
  items: ReviewCaseAuditEntry[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export async function listReviewCaseAudit(reviewCaseId: string, signal: AbortSignal): Promise<ReviewCaseAuditList> {
  if (!isGuid(reviewCaseId)) {
    throw new UnexpectedResponseError('The stored history response could not be read.');
  }

  const body = await apiGet(
    `/api/review-cases/${reviewCaseId}/audit?page=${auditPage}&pageSize=${auditPageSize}`,
    signal,
  );

  try {
    return mapAuditList(body);
  } catch (error) {
    if (error instanceof UnexpectedResponseError) {
      throw error;
    }

    throw new UnexpectedResponseError('The stored history response could not be read.');
  }
}

function mapAuditList(body: unknown): ReviewCaseAuditList {
  if (!isRecord(body) || !Array.isArray(body.items)) {
    throw new UnexpectedResponseError('The stored history response could not be read.');
  }

  // The API reads the count and the page separately. A mismatch is not a corrupt payload.
  return {
    items: body.items.map(mapAuditEntry),
    page: readSafeInt(body.page, 'page', 1, 2_147_483_647),
    pageSize: readSafeInt(body.pageSize, 'pageSize', 1, 100),
    totalCount: readSafeInt(body.totalCount, 'totalCount', 0, Number.MAX_SAFE_INTEGER),
  };
}

function mapAuditEntry(value: unknown): ReviewCaseAuditEntry {
  if (!isRecord(value)) {
    throw new UnexpectedResponseError('The stored history response could not be read.');
  }

  const entry: ReviewCaseAuditEntry = {
    id: readGuid(value.id),
    action: readAction(value.action),
    actorId: readActorId(value.actorId),
    recordedAt: readTimestamp(value.recordedAt),
    reason: readNullableEnum(value.reason, ['CompetingMatch', 'NeedsInvestigation'] as const),
    invoiceId: readOptionalGuid(value.invoiceId),
    bankTransactionId: readOptionalGuid(value.bankTransactionId),
    sourceLabel: readNullableEnum(value.sourceLabel, ['manual', 'exact-1', 'smart-1'] as const),
    note: readNote(value.note),
    outcome: readNullableEnum(value.outcome, ['AllocationRecorded', 'NoActionRequired', 'HandledOutside'] as const),
    allocationId: readOptionalGuid(value.allocationId),
    resolutionNote: readNote(value.resolutionNote),
  };
  assertActionShape(entry);
  return entry;
}

function assertActionShape(entry: ReviewCaseAuditEntry): void {
  if (entry.action === 'Opened') {
    if (
      entry.reason === null
      || entry.outcome !== null
      || entry.allocationId !== null
      || entry.resolutionNote !== null
      || !openedDocumentsMatch(entry)
    ) {
      throw new UnexpectedResponseError('The stored history response could not be read.');
    }

    return;
  }

  if (entry.action === 'Resolved') {
    if (
      entry.reason !== null
      || entry.invoiceId !== null
      || entry.bankTransactionId !== null
      || entry.sourceLabel !== null
      || entry.note !== null
      || entry.outcome === null
      || !resolvedAllocationMatches(entry)
    ) {
      throw new UnexpectedResponseError('The stored history response could not be read.');
    }

    return;
  }

  if (
    entry.reason !== null
    || entry.invoiceId !== null
    || entry.bankTransactionId !== null
    || entry.sourceLabel !== null
    || entry.note !== null
    || entry.outcome !== null
    || entry.allocationId !== null
  ) {
    throw new UnexpectedResponseError('The stored history response could not be read.');
  }
}

function openedDocumentsMatch(entry: ReviewCaseAuditEntry): boolean {
  if (entry.reason === 'CompetingMatch') {
    return entry.invoiceId !== null && entry.bankTransactionId !== null;
  }

  return (entry.invoiceId === null) !== (entry.bankTransactionId === null);
}

function resolvedAllocationMatches(entry: ReviewCaseAuditEntry): boolean {
  if (entry.outcome === 'AllocationRecorded') {
    return entry.allocationId !== null;
  }

  return entry.allocationId === null;
}

function readAction(value: unknown): ReviewCaseAuditActionName {
  if (value === 'Opened' || value === 'Resolved' || value === 'Dismissed') {
    return value;
  }

  throw new UnexpectedResponseError('The stored history response could not be read.');
}

function readActorId(value: unknown): string {
  if (typeof value !== 'string' || !isPreservedDotNetText(value, maxActorIdLength)) {
    throw new UnexpectedResponseError('The stored history response could not be read.');
  }

  return value;
}

function readNote(value: unknown): string | null {
  if (value === null) {
    return null;
  }

  if (typeof value !== 'string' || !isPreservedDotNetText(value, maxNoteLength)) {
    throw new UnexpectedResponseError('The stored history response could not be read.');
  }

  return value;
}

// string.Trim and IsNullOrWhiteSpace use Char.IsWhiteSpace. JavaScript trim also
// removes U+FEFF, which that classification keeps.
function isPreservedDotNetText(value: string, maximumLength: number): boolean {
  if (value.length === 0 || value.length > maximumLength) {
    return false;
  }

  const trimmed = trimDotNetWhiteSpace(value);
  return trimmed.length > 0 && trimmed === value;
}

function trimDotNetWhiteSpace(value: string): string {
  let start = 0;
  let end = value.length;

  while (start < end && isDotNetWhiteSpace(value.charCodeAt(start))) {
    start += 1;
  }

  while (end > start && isDotNetWhiteSpace(value.charCodeAt(end - 1))) {
    end -= 1;
  }

  return value.slice(start, end);
}

function isDotNetWhiteSpace(code: number): boolean {
  return code === 0x20
    || (code >= 0x09 && code <= 0x0d)
    || code === 0x85
    || code === 0xa0
    || code === 0x1680
    || (code >= 0x2000 && code <= 0x200a)
    || code === 0x2028
    || code === 0x2029
    || code === 0x202f
    || code === 0x205f
    || code === 0x3000;
}

function readNullableEnum<T extends string>(value: unknown, allowed: readonly T[]): T | null {
  if (value === null) {
    return null;
  }

  if (typeof value !== 'string' || !allowed.includes(value as T)) {
    throw new UnexpectedResponseError('The stored history response could not be read.');
  }

  return value as T;
}

function readOptionalGuid(value: unknown): string | null {
  if (value === null) {
    return null;
  }

  return readGuid(value);
}

function readGuid(value: unknown): string {
  if (typeof value !== 'string' || !isGuid(value)) {
    throw new UnexpectedResponseError('The stored history response could not be read.');
  }

  return value.toLowerCase();
}

function readTimestamp(value: unknown): string {
  if (typeof value !== 'string' || !timestampPattern.test(value) || Number.isNaN(Date.parse(value))) {
    throw new UnexpectedResponseError('The stored history response could not be read.');
  }

  return value;
}

function isGuid(value: string): boolean {
  return guidPattern.test(value) && value.toLowerCase() !== emptyGuid;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value) && !isLosslessNumber(value);
}
