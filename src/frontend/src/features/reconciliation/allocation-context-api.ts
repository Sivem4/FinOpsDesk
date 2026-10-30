import { apiGet } from '../../shared/api/http';
import { UnexpectedResponseError } from '../../shared/api/errors';
import { readMoney, type MoneyDto } from '../../shared/money/money';

const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const datePattern = /^\d{4}-\d{2}-\d{2}$/;

export interface AllocationContextInvoice {
  invoiceId: string;
  invoiceNumber: string;
  totalAmount: MoneyDto;
  allocatedAmount: MoneyDto;
  outstandingAmount: MoneyDto;
  sourceSystem: string | null;
  externalId: string | null;
}

export interface AllocationContextBankTransaction {
  bankTransactionId: string;
  reference: string | null;
  bookingDate: string;
  amount: MoneyDto;
  allocatedAmount: MoneyDto;
  unappliedAmount: MoneyDto;
  sourceSystem: string | null;
  externalId: string | null;
}

export interface AllocationContext {
  invoice: AllocationContextInvoice;
  bankTransaction: AllocationContextBankTransaction;
}

export async function getAllocationContext(
  invoiceId: string,
  bankTransactionId: string,
  signal: AbortSignal,
): Promise<AllocationContext> {
  const params = new URLSearchParams({
    invoiceId,
    bankTransactionId,
  });
  return mapAllocationContext(
    await apiGet(`/api/allocations/context?${params.toString()}`, signal),
    { invoiceId, bankTransactionId },
  );
}

export function mapAllocationContext(
  body: unknown,
  requested?: { invoiceId: string; bankTransactionId: string },
): AllocationContext {
  if (!isRecord(body) || !isRecord(body.invoice) || !isRecord(body.bankTransaction)) {
    throw unreadable();
  }

  const bookingDate = body.bankTransaction.bookingDate;

  if (typeof bookingDate !== 'string' || !datePattern.test(bookingDate)) {
    throw unreadable();
  }

  const context = {
    invoice: {
      invoiceId: readGuid(body.invoice.invoiceId),
      invoiceNumber: readRequiredText(body.invoice.invoiceNumber),
      totalAmount: readMoney(body.invoice.totalAmount),
      allocatedAmount: readMoney(body.invoice.allocatedAmount),
      outstandingAmount: readMoney(body.invoice.outstandingAmount),
      sourceSystem: readOptionalText(body.invoice.sourceSystem),
      externalId: readOptionalText(body.invoice.externalId),
    },
    bankTransaction: {
      bankTransactionId: readGuid(body.bankTransaction.bankTransactionId),
      reference: readOptionalText(body.bankTransaction.reference),
      bookingDate,
      amount: readMoney(body.bankTransaction.amount),
      allocatedAmount: readMoney(body.bankTransaction.allocatedAmount),
      unappliedAmount: readMoney(body.bankTransaction.unappliedAmount),
      sourceSystem: readOptionalText(body.bankTransaction.sourceSystem),
      externalId: readOptionalText(body.bankTransaction.externalId),
    },
  };

  if (requested !== undefined && (!sameGuid(context.invoice.invoiceId, requested.invoiceId) || !sameGuid(context.bankTransaction.bankTransactionId, requested.bankTransactionId))) {
    throw unreadable();
  }

  return context;
}

function readGuid(value: unknown): string {
  if (typeof value !== 'string' || !guidPattern.test(value) || value === '00000000-0000-0000-0000-000000000000') {
    throw unreadable();
  }

  return value;
}

function readRequiredText(value: unknown): string {
  if (typeof value !== 'string' || value.trim().length === 0) {
    throw unreadable();
  }

  return value;
}

function readOptionalText(value: unknown): string | null {
  if (value === null) {
    return null;
  }

  if (typeof value !== 'string') {
    throw unreadable();
  }

  const trimmed = value.trim();
  return trimmed.length === 0 ? null : trimmed;
}

function sameGuid(left: string, right: string): boolean {
  return left.toLowerCase() === right.toLowerCase();
}

function unreadable(): UnexpectedResponseError {
  return new UnexpectedResponseError('The allocation context could not be read.');
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
