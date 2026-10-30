import { parse } from 'lossless-json';

import { StaleSessionError } from '../../shared/api/errors';
import { toApiError } from '../../shared/api/problem';
import { sessionStore } from '../../shared/api/session-store';
import { readMoney, type DecimalString } from '../../shared/money/money';

const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const emptyGuid = '00000000-0000-0000-0000-000000000000';

export class UnknownConfirmationOutcomeError extends Error {
  constructor() {
    super('The confirmation request ended before a server result was received.');
    this.name = 'UnknownConfirmationOutcomeError';
  }
}

export class UnreadableConfirmationResultError extends Error {
  constructor() {
    super('The server returned a confirmation result this screen cannot read. The allocation may already be stored.');
    this.name = 'UnreadableConfirmationResultError';
  }
}

export interface ConfirmationAttempt {
  invoiceId: string;
  bankTransactionId: string;
  idempotencyKey: string;
  epoch: number;
}

export interface ExactConfirmationResult {
  allocationId: string;
  invoiceId: string;
  bankTransactionId: string;
  amount: DecimalString;
  currency: string;
  replayed: boolean;
}

export function createConfirmationAttempt(
  invoiceId: string,
  bankTransactionId: string,
  epoch: number,
): ConfirmationAttempt {
  if (!isGuid(invoiceId) || !isGuid(bankTransactionId)) {
    throw new Error('Confirmation identifiers must be GUIDs.');
  }

  const idempotencyKey = crypto.randomUUID();

  if (!isGuid(idempotencyKey)) {
    throw new Error('Idempotency key could not be created.');
  }

  return {
    invoiceId,
    bankTransactionId,
    idempotencyKey,
    epoch,
  };
}

export async function postExactConfirmation(
  attempt: ConfirmationAttempt,
  signal: AbortSignal,
): Promise<ExactConfirmationResult> {
  const session = sessionStore.getSnapshot();

  if (session.epoch !== attempt.epoch) {
    throw new StaleSessionError();
  }

  if (session.token === null) {
    throw toApiError(401, '');
  }

  const epoch = attempt.epoch;
  let response: Response;

  try {
    response = await fetch('/api/matches/confirm', {
      method: 'POST',
      headers: {
        Accept: 'application/json',
        'Content-Type': 'application/json',
        Authorization: `Bearer ${session.token}`,
        'Idempotency-Key': attempt.idempotencyKey,
      },
      body: confirmationBody(attempt),
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
    throw new UnreadableConfirmationResultError();
  }

  assertCurrentEpoch(epoch);

  if (!response.ok) {
    throw toApiError(response.status, text);
  }

  try {
    return mapConfirmation(parse(text), attempt, response.status);
  } catch (error) {
    if (error instanceof StaleSessionError) {
      throw error;
    }

    assertCurrentEpoch(epoch);
    throw new UnreadableConfirmationResultError();
  }
}

function confirmationBody(attempt: ConfirmationAttempt): string {
  return `{"invoiceId":${JSON.stringify(attempt.invoiceId)},"bankTransactionId":${JSON.stringify(attempt.bankTransactionId)}}`;
}

function mapConfirmation(
  value: unknown,
  attempt: ConfirmationAttempt,
  status: number,
): ExactConfirmationResult {
  if (!isRecord(value) || typeof value.replayed !== 'boolean') {
    throw new UnreadableConfirmationResultError();
  }

  if (status === 201 && value.replayed) {
    throw new UnreadableConfirmationResultError();
  }

  if (status === 200 && !value.replayed) {
    throw new UnreadableConfirmationResultError();
  }

  if (status !== 200 && status !== 201) {
    throw new UnreadableConfirmationResultError();
  }

  if (typeof value.invoiceId !== 'string' || typeof value.bankTransactionId !== 'string' || typeof value.allocationId !== 'string') {
    throw new UnreadableConfirmationResultError();
  }

  if (!isGuid(value.allocationId) || !isGuid(value.invoiceId) || !isGuid(value.bankTransactionId)) {
    throw new UnreadableConfirmationResultError();
  }

  if (!sameGuid(value.invoiceId, attempt.invoiceId) || !sameGuid(value.bankTransactionId, attempt.bankTransactionId)) {
    throw new UnreadableConfirmationResultError();
  }

  let amount: ReturnType<typeof readMoney>;

  try {
    amount = readMoney(value.amount);
  } catch {
    throw new UnreadableConfirmationResultError();
  }

  if (!isStrictlyPositiveAmount(amount.amount)) {
    throw new UnreadableConfirmationResultError();
  }

  return {
    allocationId: value.allocationId,
    invoiceId: value.invoiceId,
    bankTransactionId: value.bankTransactionId,
    amount: amount.amount,
    currency: amount.currency,
    replayed: value.replayed,
  };
}

function transportOutcome(epoch: number): Error {
  if (sessionStore.getSnapshot().epoch !== epoch) {
    return new StaleSessionError();
  }

  return new UnknownConfirmationOutcomeError();
}

function assertCurrentEpoch(epoch: number): void {
  if (sessionStore.getSnapshot().epoch !== epoch) {
    throw new StaleSessionError();
  }
}

function sameGuid(left: string, right: string): boolean {
  return left.toLowerCase() === right.toLowerCase();
}

function isStrictlyPositiveAmount(amount: string): boolean {
  const match = /^(\d+)(?:\.(\d{1,4}))?$/.exec(amount);

  if (match === null) {
    return false;
  }

  const fraction = (match[2] ?? '').padEnd(4, '0');
  return BigInt(`${match[1]}${fraction}`) > 0n;
}

function isGuid(value: string): boolean {
  return guidPattern.test(value) && value.toLowerCase() !== emptyGuid;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
