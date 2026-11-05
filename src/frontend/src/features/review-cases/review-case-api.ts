import { isLosslessNumber } from 'lossless-json';

import { apiGet } from '../../shared/api/http';
import { UnexpectedResponseError } from '../../shared/api/errors';
import { readMoney, readSafeInt, type DecimalString } from '../../shared/money/money';

const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const emptyGuid = '00000000-0000-0000-0000-000000000000';
const timestampPattern = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$/;

export type ReviewCaseReasonName = 'CompetingMatch' | 'NeedsInvestigation';
export type ReviewCaseStatusName = 'Open' | 'Resolved' | 'Dismissed';
export type ReviewCaseOutcomeName = 'AllocationRecorded' | 'NoActionRequired' | 'HandledOutside';
export type ReviewCaseSourceName = 'manual' | 'exact-1' | 'smart-1';

export interface ReviewCaseListItem {
  reviewCaseId: string;
  reason: ReviewCaseReasonName;
  status: ReviewCaseStatusName;
  invoiceId: string | null;
  bankTransactionId: string | null;
  sourceLabel: ReviewCaseSourceName | null;
  note: string | null;
  outcome: ReviewCaseOutcomeName | null;
  resolutionNote: string | null;
  allocationId: string | null;
  openedAt: string;
  closedAt: string | null;
}

export interface ReviewCaseList {
  items: ReviewCaseListItem[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export interface ReviewCaseObservation {
  found: boolean;
  amount: DecimalString | null;
  currency: string | null;
}

export interface ReviewCaseDetail {
  reviewCase: ReviewCaseListItem;
  invoice: ReviewCaseObservation;
  bankTransaction: ReviewCaseObservation;
}

export async function listReviewCases(
  page: number,
  pageSize: number,
  status: ReviewCaseStatusName | null,
  invoiceId: string | null,
  bankTransactionId: string | null,
  signal: AbortSignal,
): Promise<ReviewCaseList> {
  const params = new URLSearchParams({
    page: String(page),
    pageSize: String(pageSize),
  });
  if (status !== null) {
    params.set('status', status);
  }
  if (invoiceId !== null) {
    params.set('invoiceId', invoiceId);
  }
  if (bankTransactionId !== null) {
    params.set('bankTransactionId', bankTransactionId);
  }

  const body = await apiGet(`/api/review-cases?${params.toString()}`, signal);
  return mapList(body);
}

export async function getReviewCase(reviewCaseId: string, signal: AbortSignal): Promise<ReviewCaseDetail> {
  if (!isGuid(reviewCaseId)) {
    throw new UnexpectedResponseError('The review case response could not be read.');
  }

  const body = await apiGet(`/api/review-cases/${reviewCaseId}`, signal);
  return mapDetail(body);
}

function mapList(body: unknown): ReviewCaseList {
  try {
    if (!isRecord(body) || !Array.isArray(body.items)) {
      throw new UnexpectedResponseError('The review case response could not be read.');
    }

    return {
      items: body.items.map(mapReviewCase),
      page: readSafeInt(body.page, 'page', 1, 2_147_483_647),
      pageSize: readSafeInt(body.pageSize, 'pageSize', 1, 100),
      totalCount: readSafeInt(body.totalCount, 'totalCount', 0, Number.MAX_SAFE_INTEGER),
    };
  } catch (error) {
    if (error instanceof UnexpectedResponseError) {
      throw error;
    }

    throw new UnexpectedResponseError('The review case response could not be read.');
  }
}

function mapDetail(body: unknown): ReviewCaseDetail {
  try {
    if (!isRecord(body) || !isRecord(body.linkedFinancials)) {
      throw new UnexpectedResponseError('The review case response could not be read.');
    }

    const reviewCase = mapReviewCase(body.reviewCase);
    return {
      reviewCase,
      invoice: mapObservation(
        body.linkedFinancials.invoiceFound,
        body.linkedFinancials.invoiceOutstanding,
        reviewCase.invoiceId,
      ),
      bankTransaction: mapObservation(
        body.linkedFinancials.bankTransactionFound,
        body.linkedFinancials.bankTransactionUnapplied,
        reviewCase.bankTransactionId,
      ),
    };
  } catch (error) {
    if (error instanceof UnexpectedResponseError) {
      throw error;
    }

    throw new UnexpectedResponseError('The review case response could not be read.');
  }
}

function mapObservation(found: unknown, money: unknown, namedId: string | null): ReviewCaseObservation {
  if (typeof found !== 'boolean') {
    throw new UnexpectedResponseError('The review case response could not be read.');
  }

  if (found) {
    if (namedId === null || money === null) {
      throw new UnexpectedResponseError('The review case response could not be read.');
    }

    const read = readMoney(money);
    return { found: true, amount: read.amount, currency: read.currency };
  }

  if (money !== null) {
    throw new UnexpectedResponseError('The review case response could not be read.');
  }

  return { found: false, amount: null, currency: null };
}

function mapReviewCase(value: unknown): ReviewCaseListItem {
  if (!isRecord(value)) {
    throw new UnexpectedResponseError('The review case response could not be read.');
  }

  const reviewCaseId = readGuid(value.reviewCaseId, true);
  const reason = readEnum(value.reason, ['CompetingMatch', 'NeedsInvestigation'] as const);
  const status = readEnum(value.status, ['Open', 'Resolved', 'Dismissed'] as const);
  const invoiceId = readGuid(value.invoiceId, false);
  const bankTransactionId = readGuid(value.bankTransactionId, false);
  const sourceLabel = readNullableEnum(value.sourceLabel, ['manual', 'exact-1', 'smart-1'] as const);
  const note = readNullableString(value.note);
  const outcome = readNullableEnum(value.outcome, ['AllocationRecorded', 'NoActionRequired', 'HandledOutside'] as const);
  const resolutionNote = readNullableString(value.resolutionNote);
  const allocationId = readGuid(value.allocationId, false);
  const openedAt = readTimestamp(value.openedAt, true);
  const closedAt = readTimestamp(value.closedAt, false);

  if (reviewCaseId === null || reason === null || status === null || openedAt === null) {
    throw new UnexpectedResponseError('The review case response could not be read.');
  }

  return {
    reviewCaseId,
    reason,
    status,
    invoiceId,
    bankTransactionId,
    sourceLabel,
    note,
    outcome,
    resolutionNote,
    allocationId,
    openedAt,
    closedAt,
  };
}

function readEnum<T extends string>(value: unknown, allowed: readonly T[]): T | null {
  if (typeof value !== 'string' || !allowed.includes(value as T)) {
    return null;
  }

  return value as T;
}

function readNullableEnum<T extends string>(value: unknown, allowed: readonly T[]): T | null {
  if (value === null) {
    return null;
  }

  const parsed = readEnum(value, allowed);
  if (parsed === null) {
    throw new UnexpectedResponseError('The review case response could not be read.');
  }

  return parsed;
}

function readNullableString(value: unknown): string | null {
  if (value === null) {
    return null;
  }

  if (typeof value !== 'string') {
    throw new UnexpectedResponseError('The review case response could not be read.');
  }

  return value;
}

function readTimestamp(value: unknown, required: true): string | null;
function readTimestamp(value: unknown, required: false): string | null;
function readTimestamp(value: unknown, required: boolean): string | null {
  if (value === null && !required) {
    return null;
  }

  if (typeof value !== 'string' || !timestampPattern.test(value)) {
    if (required) {
      return null;
    }

    throw new UnexpectedResponseError('The review case response could not be read.');
  }

  return value;
}

function readGuid(value: unknown, required: boolean): string | null {
  if (value === null && !required) {
    return null;
  }

  if (typeof value !== 'string' || !isGuid(value)) {
    if (required) {
      return null;
    }

    throw new UnexpectedResponseError('The review case response could not be read.');
  }

  return value.toLowerCase();
}

function isGuid(value: string): boolean {
  return guidPattern.test(value) && value.toLowerCase() !== emptyGuid;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value) && !isLosslessNumber(value);
}
