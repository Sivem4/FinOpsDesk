import { isLosslessNumber } from 'lossless-json';

import { apiGet } from '../../shared/api/http';
import { UnexpectedResponseError } from '../../shared/api/errors';
import { readMoney, readSafeInt, type DecimalString } from '../../shared/money/money';

export interface InvoiceListItem {
  id: string;
  invoiceNumber: string;
  totalAmount: DecimalString;
  totalCurrency: string;
  allocatedAmount: DecimalString;
  allocatedCurrency: string;
  outstandingAmount: DecimalString;
  outstandingCurrency: string;
}

export interface InvoiceListResponse {
  items: InvoiceListItem[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export async function listInvoices(
  page: number,
  pageSize: number,
  signal: AbortSignal,
  invoiceNumber?: string,
): Promise<InvoiceListResponse> {
  const params = new URLSearchParams({
    page: String(page),
    pageSize: String(pageSize),
  });
  if (invoiceNumber !== undefined) {
    params.set('invoiceNumber', invoiceNumber);
  }
  const body = await apiGet(`/api/invoices?${params.toString()}`, signal);

  try {
    return mapInvoiceList(body);
  } catch (error) {
    if (error instanceof UnexpectedResponseError) {
      throw error;
    }

    throw new UnexpectedResponseError();
  }
}

function mapInvoiceList(body: unknown): InvoiceListResponse {
  if (!isRecord(body) || !Array.isArray(body.items)) {
    throw new UnexpectedResponseError();
  }

  return {
    items: body.items.map(mapInvoice),
    page: readSafeInt(body.page, 'page', 1, 2_147_483_647),
    pageSize: readSafeInt(body.pageSize, 'pageSize', 1, 100),
    totalCount: readSafeInt(body.totalCount, 'totalCount', 0, Number.MAX_SAFE_INTEGER),
  };
}

function mapInvoice(value: unknown): InvoiceListItem {
  if (!isRecord(value) || typeof value.id !== 'string' || typeof value.invoiceNumber !== 'string') {
    throw new UnexpectedResponseError();
  }

  if (!isGuid(value.id) || value.invoiceNumber.trim().length === 0) {
    throw new UnexpectedResponseError();
  }

  const total = readMoney(value.totalAmount);
  const allocated = readMoney(value.allocatedAmount);
  const outstanding = readMoney(value.outstandingAmount);

  return {
    id: value.id,
    invoiceNumber: value.invoiceNumber,
    totalAmount: total.amount,
    totalCurrency: total.currency,
    allocatedAmount: allocated.amount,
    allocatedCurrency: allocated.currency,
    outstandingAmount: outstanding.amount,
    outstandingCurrency: outstanding.currency,
  };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value) && !isLosslessNumber(value);
}

function isGuid(value: string): boolean {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);
}
