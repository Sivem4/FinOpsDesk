import { apiGet } from '../../shared/api/http';
import { UnexpectedResponseError } from '../../shared/api/errors';
import { compareDecimalAmounts, readMoney, readMoneyAmount, type DecimalString } from '../../shared/money/money';

const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const emptyGuid = '00000000-0000-0000-0000-000000000000';
const timestampPattern = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$/;
const zero = readMoneyAmount('0');

export type ReversalContextStatus = 'confirmed' | 'reversed';

export interface ReversalContext {
  allocationId: string;
  invoiceId: string;
  bankTransactionId: string;
  invoiceNumber: string;
  reference: string | null;
  amount: DecimalString;
  currency: string;
  status: ReversalContextStatus;
  confirmedAt: string;
  reversedAt: string | null;
  reversalReason: string | null;
}

export async function getReversalContext(allocationId: string, signal: AbortSignal): Promise<ReversalContext> {
  if (!isGuid(allocationId)) {
    throw new UnexpectedResponseError('The reversal context response could not be read.');
  }

  const body = await apiGet(`/api/allocations/${allocationId}/reversal-context`, signal);

  try {
    const context = mapReversalContext(body);

    if (context.allocationId.toLowerCase() !== allocationId.toLowerCase()) {
      throw new UnexpectedResponseError('The reversal context response could not be read.');
    }

    return context;
  } catch (error) {
    if (error instanceof UnexpectedResponseError) {
      throw error;
    }

    throw new UnexpectedResponseError('The reversal context response could not be read.');
  }
}

function mapReversalContext(body: unknown): ReversalContext {
  if (!isRecord(body)) {
    throw new UnexpectedResponseError('The reversal context response could not be read.');
  }

  const money = readMoney(body.amount);

  if (compareDecimalAmounts(money.amount, zero) !== 1) {
    throw new UnexpectedResponseError('The reversal context response could not be read.');
  }

  const invoiceNumber = readText(body.invoiceNumber, 4000);
  const status = readStatus(body.status);

  return {
    allocationId: readGuid(body.allocationId),
    invoiceId: readGuid(body.invoiceId),
    bankTransactionId: readGuid(body.bankTransactionId),
    invoiceNumber,
    reference: readOptionalText(body.reference, 4000),
    amount: money.amount,
    currency: money.currency,
    status,
    confirmedAt: readTimestamp(body.confirmedAt),
    reversedAt: body.reversedAt === null ? null : readTimestamp(body.reversedAt),
    reversalReason: readOptionalText(body.reversalReason, 500),
  };
}

function readStatus(value: unknown): ReversalContextStatus {
  if (value === 'confirmed' || value === 'reversed') {
    return value;
  }

  throw new UnexpectedResponseError('The reversal context response could not be read.');
}

function readGuid(value: unknown): string {
  if (!isGuid(value)) {
    throw new UnexpectedResponseError('The reversal context response could not be read.');
  }

  return value;
}

function readText(value: unknown, maximum: number): string {
  if (typeof value !== 'string' || value.trim().length === 0 || value.length > maximum) {
    throw new UnexpectedResponseError('The reversal context response could not be read.');
  }

  return value;
}

function readOptionalText(value: unknown, maximum: number): string | null {
  if (value === null) {
    return null;
  }

  if (typeof value !== 'string' || value.length > maximum) {
    throw new UnexpectedResponseError('The reversal context response could not be read.');
  }

  return value;
}

function readTimestamp(value: unknown): string {
  if (typeof value !== 'string' || !timestampPattern.test(value) || Number.isNaN(Date.parse(value))) {
    throw new UnexpectedResponseError('The reversal context response could not be read.');
  }

  return value;
}

function isGuid(value: unknown): value is string {
  return typeof value === 'string' && guidPattern.test(value) && value.toLowerCase() !== emptyGuid;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
