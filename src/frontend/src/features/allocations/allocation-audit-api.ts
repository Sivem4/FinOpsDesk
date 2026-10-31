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

export type AllocationAuditAction = 'confirmation' | 'reversal';
export type AllocationAuditOperation = 'exactMatch' | 'explicitAmount' | 'batchExactMatch' | 'reversal';

export interface AllocationAuditEntry {
  id: string;
  allocationId: string;
  invoiceId: string;
  bankTransactionId: string;
  batchExecutionId: string | null;
  action: AllocationAuditAction;
  operation: AllocationAuditOperation;
  actorId: string;
  amount: DecimalString;
  currency: string;
  recordedAt: string;
  reason: string | null;
}

export interface AllocationAuditList {
  items: AllocationAuditEntry[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export async function listAllocationAudit(
  allocationId: string,
  page: number,
  pageSize: number,
  signal: AbortSignal,
): Promise<AllocationAuditList> {
  const params = new URLSearchParams({
    page: String(page),
    pageSize: String(pageSize),
  });
  const body = await apiGet(
    `/api/allocations/${allocationId}/audit?${params.toString()}`,
    signal,
  );

  try {
    return mapAuditList(body);
  } catch (error) {
    if (error instanceof UnexpectedResponseError) {
      throw error;
    }

    throw new UnexpectedResponseError();
  }
}

function mapAuditList(body: unknown): AllocationAuditList {
  if (!isRecord(body) || !Array.isArray(body.items)) {
    throw new UnexpectedResponseError();
  }

  return {
    items: body.items.map(mapAuditEntry),
    page: readSafeInt(body.page, 'page', 1, 2_147_483_647),
    pageSize: readSafeInt(body.pageSize, 'pageSize', 1, 100),
    totalCount: readSafeInt(body.totalCount, 'totalCount', 0, Number.MAX_SAFE_INTEGER),
  };
}

function mapAuditEntry(value: unknown): AllocationAuditEntry {
  if (!isRecord(value)) {
    throw new UnexpectedResponseError();
  }

  const money = readMoney(value.amount);
  if (compareDecimalAmounts(money.amount, zero) !== 1) {
    throw new UnexpectedResponseError();
  }

  const actorId = value.actorId;
  if (typeof actorId !== 'string' || actorId.trim().length === 0 || actorId.length > 256) {
    throw new UnexpectedResponseError();
  }

  return {
    id: readGuid(value.id),
    allocationId: readGuid(value.allocationId),
    invoiceId: readGuid(value.invoiceId),
    bankTransactionId: readGuid(value.bankTransactionId),
    batchExecutionId: value.batchExecutionId === null ? null : readGuid(value.batchExecutionId),
    action: readAction(value.action),
    operation: readOperation(value.operation),
    actorId,
    amount: money.amount,
    currency: money.currency,
    recordedAt: readTimestamp(value.recordedAt),
    reason: readReason(value.reason),
  };
}

function readAction(value: unknown): AllocationAuditAction {
  if (value === 'confirmation' || value === 'reversal') {
    return value;
  }

  throw new UnexpectedResponseError();
}

function readOperation(value: unknown): AllocationAuditOperation {
  if (value === 'exactMatch' || value === 'explicitAmount' || value === 'batchExactMatch' || value === 'reversal') {
    return value;
  }

  throw new UnexpectedResponseError();
}

function readReason(value: unknown): string | null {
  if (value === null) {
    return null;
  }

  if (typeof value !== 'string' || value.length > 500) {
    throw new UnexpectedResponseError();
  }

  return value;
}

function readGuid(value: unknown): string {
  if (typeof value !== 'string' || !guidPattern.test(value) || value.toLowerCase() === emptyGuid) {
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
