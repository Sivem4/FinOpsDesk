import { isLosslessNumber } from 'lossless-json';

import { apiGet } from '../../shared/api/http';
import { UnexpectedResponseError } from '../../shared/api/errors';
import { readMoney, readSafeInt, type DecimalString } from '../../shared/money/money';

const datePattern = /^\d{4}-\d{2}-\d{2}$/;

export interface BankTransactionListItem {
  id: string;
  bookingDate: string;
  reference: string | null;
  amount: DecimalString;
  currency: string;
  allocatedAmount: DecimalString;
  allocatedCurrency: string;
  unappliedAmount: DecimalString;
  unappliedCurrency: string;
  sourceSystem: string | null;
  externalId: string | null;
}

export interface BankTransactionListResponse {
  items: BankTransactionListItem[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export async function listBankTransactions(
  page: number,
  pageSize: number,
  signal: AbortSignal,
  reference?: string,
): Promise<BankTransactionListResponse> {
  const params = new URLSearchParams({
    page: String(page),
    pageSize: String(pageSize),
  });
  if (reference !== undefined) {
    params.set('reference', reference);
  }
  const body = await apiGet(`/api/bank-transactions?${params.toString()}`, signal);

  try {
    return mapBankTransactionList(body);
  } catch (error) {
    if (error instanceof UnexpectedResponseError) {
      throw error;
    }

    throw new UnexpectedResponseError();
  }
}

function mapBankTransactionList(body: unknown): BankTransactionListResponse {
  if (!isRecord(body) || !Array.isArray(body.items)) {
    throw new UnexpectedResponseError();
  }

  return {
    items: body.items.map(mapBankTransaction),
    page: readSafeInt(body.page, 'page', 1, 2_147_483_647),
    pageSize: readSafeInt(body.pageSize, 'pageSize', 1, 100),
    totalCount: readSafeInt(body.totalCount, 'totalCount', 0, Number.MAX_SAFE_INTEGER),
  };
}

function mapBankTransaction(value: unknown): BankTransactionListItem {
  if (!isRecord(value) || typeof value.id !== 'string' || !isGuid(value.id)) {
    throw new UnexpectedResponseError();
  }

  if (typeof value.bookingDate !== 'string' || !datePattern.test(value.bookingDate)) {
    throw new UnexpectedResponseError();
  }

  const amount = readMoney(value.amount);
  const allocated = readMoney(value.allocatedAmount);
  const unapplied = readMoney(value.unappliedAmount);

  if (amount.currency !== allocated.currency || amount.currency !== unapplied.currency) {
    throw new UnexpectedResponseError();
  }

  return {
    id: value.id,
    bookingDate: value.bookingDate,
    reference: readOptionalText(value.reference),
    amount: amount.amount,
    currency: amount.currency,
    allocatedAmount: allocated.amount,
    allocatedCurrency: allocated.currency,
    unappliedAmount: unapplied.amount,
    unappliedCurrency: unapplied.currency,
    sourceSystem: readOptionalText(value.sourceSystem),
    externalId: readOptionalText(value.externalId),
  };
}

function readOptionalText(value: unknown): string | null {
  if (value === null) {
    return null;
  }

  if (typeof value !== 'string' || value.trim().length === 0) {
    throw new UnexpectedResponseError();
  }

  return value;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value) && !isLosslessNumber(value);
}

function isGuid(value: string): boolean {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);
}
