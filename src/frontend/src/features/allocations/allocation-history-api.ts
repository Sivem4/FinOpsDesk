import { isLosslessNumber } from 'lossless-json';

import { apiGet } from '../../shared/api/http';
import { UnexpectedResponseError } from '../../shared/api/errors';
import {
  compareDecimalAmounts,
  readMoney,
  readMoneyAmount,
  readSafeInt,
  type DecimalString,
} from '../../shared/money/money';

const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const emptyGuid = '00000000-0000-0000-0000-000000000000';
const timestampPattern = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$/;
const zero = readMoneyAmount('0');

export type AllocationHistoryStatus = 'confirmed' | 'reversed';
export type AllocationHistoryOperation = 'exactMatch' | 'explicitAmount' | 'batchExactMatch';

export interface AllocationHistoryItem {
  allocationId: string;
  invoiceId: string;
  bankTransactionId: string;
  invoiceNumber: string;
  reference: string | null;
  amount: DecimalString;
  currency: string;
  status: AllocationHistoryStatus;
  confirmedAt: string;
  reversedAt: string | null;
  reversalReason: string | null;
  idempotencyOperation: AllocationHistoryOperation | null;
}

export interface AllocationHistoryList {
  items: AllocationHistoryItem[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export async function listAllocationHistory(
  page: number,
  pageSize: number,
  invoiceId: string | null,
  bankTransactionId: string | null,
  signal: AbortSignal,
): Promise<AllocationHistoryList> {
  const params = new URLSearchParams({
    page: String(page),
    pageSize: String(pageSize),
  });
  if (invoiceId !== null) {
    params.set('invoiceId', invoiceId);
  }
  if (bankTransactionId !== null) {
    params.set('bankTransactionId', bankTransactionId);
  }

  const body = await apiGet(`/api/allocations?${params.toString()}`, signal);

  try {
    return mapHistoryList(body);
  } catch (error) {
    if (error instanceof UnexpectedResponseError) {
      throw error;
    }

    throw new UnexpectedResponseError();
  }
}

function mapHistoryList(body: unknown): AllocationHistoryList {
  if (!isRecord(body) || !Array.isArray(body.items)) {
    throw new UnexpectedResponseError();
  }

  return {
    items: body.items.map(mapHistoryItem),
    page: readSafeInt(body.page, 'page', 1, 2_147_483_647),
    pageSize: readSafeInt(body.pageSize, 'pageSize', 1, 100),
    totalCount: readSafeInt(body.totalCount, 'totalCount', 0, Number.MAX_SAFE_INTEGER),
  };
}

function mapHistoryItem(value: unknown): AllocationHistoryItem {
  if (!isRecord(value)) {
    throw new UnexpectedResponseError();
  }

  const allocationId = readGuid(value.allocationId);
  const invoiceId = readGuid(value.invoiceId);
  const bankTransactionId = readGuid(value.bankTransactionId);
  if (typeof value.invoiceNumber !== 'string' || value.invoiceNumber.trim().length === 0 || value.invoiceNumber.length > 4000) {
    throw new UnexpectedResponseError();
  }

  const reference = readOptionalText(value.reference, 4000);
  const money = readMoney(value.amount);
  if (compareDecimalAmounts(money.amount, zero) !== 1) {
    throw new UnexpectedResponseError();
  }

  const status = readStatus(value.status);
  const confirmedAt = readTimestamp(value.confirmedAt);
  const reversedAt = value.reversedAt === null ? null : readTimestamp(value.reversedAt);
  const reversalReason = readOptionalText(value.reversalReason, 500);
  const idempotencyOperation = readHistoryOperation(value.idempotencyOperation);

  if (status === 'confirmed' && (reversedAt !== null || reversalReason !== null)) {
    throw new UnexpectedResponseError();
  }

  if (status === 'reversed' && reversedAt === null) {
    throw new UnexpectedResponseError();
  }

  return {
    allocationId,
    invoiceId,
    bankTransactionId,
    invoiceNumber: value.invoiceNumber,
    reference,
    amount: money.amount,
    currency: money.currency,
    status,
    confirmedAt,
    reversedAt,
    reversalReason,
    idempotencyOperation,
  };
}

function readHistoryOperation(value: unknown): AllocationHistoryOperation | null {
  if (value === null) {
    return null;
  }

  if (value === 'exactMatch' || value === 'explicitAmount' || value === 'batchExactMatch') {
    return value;
  }

  throw new UnexpectedResponseError();
}

function readStatus(value: unknown): AllocationHistoryStatus {
  if (value === 'confirmed' || value === 'reversed') {
    return value;
  }

  throw new UnexpectedResponseError();
}

function readGuid(value: unknown): string {
  if (typeof value !== 'string' || !guidPattern.test(value) || value.toLowerCase() === emptyGuid) {
    throw new UnexpectedResponseError();
  }

  return value;
}

function readOptionalText(value: unknown, maximum: number): string | null {
  if (value === null) {
    return null;
  }

  if (typeof value !== 'string' || value.length > maximum) {
    throw new UnexpectedResponseError();
  }

  return value;
}

function readTimestamp(value: unknown): string {
  if (typeof value !== 'string' || !timestampPattern.test(value) || Number.isNaN(Date.parse(value))) {
    throw new UnexpectedResponseError();
  }

  return value;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value) && !isLosslessNumber(value);
}
