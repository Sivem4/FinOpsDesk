import { isLosslessNumber } from 'lossless-json';

import { apiGet } from '../../shared/api/http';
import { UnexpectedResponseError } from '../../shared/api/errors';
import { readMoney, readSafeInt, type DecimalString, type MoneyDto } from '../../shared/money/money';

const exactAlgorithmVersion = 'exact-1';
const smartAlgorithmVersion = 'smart-1';
const scoreScale = 'points';

const exactReason = 'ExactReferenceCurrencyAndOpenAmount';
const unmatchedReason = 'NoCounterpart';
const ineligibleReasons = new Set(['MissingReference', 'NonPositiveOpenAmount']);

const referenceSignals = new Set([
  'ReferenceExact',
  'ReferenceTokenSetEqual',
  'CompactFormEqual',
  'NumericIdentifierEqual',
]);

const knownSignalPoints: Record<string, readonly number[]> = {
  ReferenceExact: [70, 0],
  ReferenceTokenSetEqual: [60, 0],
  CompactFormEqual: [60, 0],
  NumericIdentifierEqual: [60, 0],
  AmountExact: [25],
  AmountDiffers: [0],
  ContestedCandidates: [0],
  ExactMatchAmbiguous: [0],
};

export interface ExactMatchItem {
  invoiceId: string;
  bankTransactionId: string;
  amount: DecimalString;
  currency: string;
  normalizedReference: string;
  reason: string;
}

export interface AmbiguousMatchGroup {
  normalizedReference: string;
  openAmount: DecimalString;
  currency: string;
  reason: string;
  invoiceIds: string[];
  bankTransactionIds: string[];
}

export interface ClassifiedMatchRecord {
  classification: 'NoMatch' | 'Ineligible';
  reason: string;
  invoiceId: string | null;
  bankTransactionId: string | null;
  normalizedReference: string | null;
  openAmount: DecimalString;
  currency: string;
}

export interface ExactMatchResponse {
  algorithmVersion: typeof exactAlgorithmVersion;
  exactMatches: ExactMatchItem[];
  ambiguousGroups: AmbiguousMatchGroup[];
  unmatched: ClassifiedMatchRecord[];
  ineligible: ClassifiedMatchRecord[];
}

export interface SmartMatchSignal {
  code: string;
  points: number;
}

export interface SmartMatchSuggestion {
  rank: number;
  invoiceId: string;
  bankTransactionId: string;
  score: number;
  scoreScale: typeof scoreScale;
  contested: boolean;
  exactMatchAmbiguous: boolean;
  invoiceOutstanding: DecimalString;
  invoiceOutstandingCurrency: string;
  bankTransactionUnapplied: DecimalString;
  bankTransactionUnappliedCurrency: string;
  suggestedAmount: DecimalString;
  suggestedAmountCurrency: string;
  signals: SmartMatchSignal[];
}

export interface SmartMatchResponse {
  algorithmVersion: typeof smartAlgorithmVersion;
  advisory: true;
  truncated: boolean;
  coverageLimited: boolean;
  suggestions: SmartMatchSuggestion[];
}

export async function getExactMatches(signal: AbortSignal): Promise<ExactMatchResponse> {
  return mapExact(await apiGet('/api/matches', signal));
}

export async function getSmartMatches(signal: AbortSignal): Promise<SmartMatchResponse> {
  return mapSmart(await apiGet('/api/matches/smart', signal));
}

export function mapExact(body: unknown): ExactMatchResponse {
  if (!isRecord(body) || body.algorithmVersion !== exactAlgorithmVersion) {
    throw unreadable();
  }

  return {
    algorithmVersion: exactAlgorithmVersion,
    exactMatches: readArray(body.exactMatches, readExactMatch),
    ambiguousGroups: readArray(body.ambiguousGroups, readAmbiguousGroup),
    unmatched: readArray(body.unmatched, (value) => readClassified(value, 'NoMatch')),
    ineligible: readArray(body.ineligible, (value) => readClassified(value, 'Ineligible')),
  };
}

export function mapSmart(body: unknown): SmartMatchResponse {
  if (!isRecord(body) || body.algorithmVersion !== smartAlgorithmVersion || body.advisory !== true) {
    throw unreadable();
  }

  if (typeof body.truncated !== 'boolean' || typeof body.coverageLimited !== 'boolean') {
    throw unreadable();
  }

  return {
    algorithmVersion: smartAlgorithmVersion,
    advisory: true,
    truncated: body.truncated,
    coverageLimited: body.coverageLimited,
    suggestions: readArray(body.suggestions, (value, index) => readSuggestion(value, index)),
  };
}

function readExactMatch(value: unknown): ExactMatchItem {
  if (!isRecord(value) || value.reason !== exactReason) {
    throw unreadable();
  }

  const amount = readRequiredMoney(value.amount);
  if (!isPositive(amount.amount)) {
    throw unreadable();
  }

  return {
    invoiceId: readGuid(value.invoiceId),
    bankTransactionId: readGuid(value.bankTransactionId),
    amount: amount.amount,
    currency: amount.currency,
    normalizedReference: readReference(value.normalizedReference),
    reason: exactReason,
  };
}

function readAmbiguousGroup(value: unknown): AmbiguousMatchGroup {
  if (!isRecord(value)) {
    throw unreadable();
  }

  const openAmount = readRequiredMoney(value.openAmount);
  const invoiceIds = readGuidList(value.invoiceIds);
  const bankTransactionIds = readGuidList(value.bankTransactionIds);

  if (!isPositive(openAmount.amount) || invoiceIds.length === 0 || bankTransactionIds.length === 0) {
    throw unreadable();
  }

  if (invoiceIds.length === 1 && bankTransactionIds.length === 1) {
    throw unreadable();
  }

  const reason = expectedAmbiguousReason(invoiceIds.length, bankTransactionIds.length);
  if (value.reason !== reason) {
    throw unreadable();
  }

  return {
    normalizedReference: readReference(value.normalizedReference),
    openAmount: openAmount.amount,
    currency: openAmount.currency,
    reason,
    invoiceIds,
    bankTransactionIds,
  };
}

function readClassified(value: unknown, classification: 'NoMatch' | 'Ineligible'): ClassifiedMatchRecord {
  if (!isRecord(value) || value.classification !== classification) {
    throw unreadable();
  }

  const invoiceId = readOptionalGuid(value.invoiceId);
  const bankTransactionId = readOptionalGuid(value.bankTransactionId);
  if ((invoiceId === null) === (bankTransactionId === null)) {
    throw unreadable();
  }

  const openAmount = readRequiredMoney(value.openAmount);
  const reason = value.reason;

  if (classification === 'NoMatch') {
    if (reason !== unmatchedReason || !isPositive(openAmount.amount)) {
      throw unreadable();
    }
  } else if (typeof reason !== 'string' || !ineligibleReasons.has(reason)) {
    throw unreadable();
  }

  return {
    classification,
    reason,
    invoiceId,
    bankTransactionId,
    normalizedReference: classification === 'NoMatch'
      ? readReference(value.normalizedReference)
      : readOptionalReference(value.normalizedReference),
    openAmount: openAmount.amount,
    currency: openAmount.currency,
  };
}

function readSuggestion(value: unknown, index: number): SmartMatchSuggestion {
  if (!isRecord(value) || value.scoreScale !== scoreScale) {
    throw unreadable();
  }

  if (typeof value.contested !== 'boolean' || typeof value.exactMatchAmbiguous !== 'boolean') {
    throw unreadable();
  }

  const rank = readInt(value.rank, 'rank', 1, Number.MAX_SAFE_INTEGER);
  if (rank !== index + 1) {
    throw unreadable();
  }

  const outstanding = readRequiredMoney(value.invoiceOutstanding);
  const unapplied = readRequiredMoney(value.bankTransactionUnapplied);
  const suggested = readRequiredMoney(value.suggestedAmount);
  if (
    outstanding.currency !== unapplied.currency
    || outstanding.currency !== suggested.currency
    || !isSmallerOpenAmount(outstanding.amount, unapplied.amount, suggested.amount)
  ) {
    throw unreadable();
  }

  const signals = readArray(value.signals, readSignal);
  if (
    signals.length === 0
    || !signalsExplain(
      signals,
      value.contested,
      value.exactMatchAmbiguous,
      outstanding.amount,
      unapplied.amount,
    )
  ) {
    throw unreadable();
  }

  const score = readInt(value.score, 'score', 0, Number.MAX_SAFE_INTEGER);
  if (score !== signals.reduce((sum, signal) => sum + signal.points, 0)) {
    throw unreadable();
  }

  return {
    rank,
    invoiceId: readGuid(value.invoiceId),
    bankTransactionId: readGuid(value.bankTransactionId),
    score,
    scoreScale,
    contested: value.contested,
    exactMatchAmbiguous: value.exactMatchAmbiguous,
    invoiceOutstanding: outstanding.amount,
    invoiceOutstandingCurrency: outstanding.currency,
    bankTransactionUnapplied: unapplied.amount,
    bankTransactionUnappliedCurrency: unapplied.currency,
    suggestedAmount: suggested.amount,
    suggestedAmountCurrency: suggested.currency,
    signals,
  };
}

function readSignal(value: unknown): SmartMatchSignal {
  if (!isRecord(value) || typeof value.code !== 'string' || value.code.trim().length === 0) {
    throw unreadable();
  }

  const points = readInt(value.points, 'points', 0, Number.MAX_SAFE_INTEGER);
  const allowed = knownSignalPoints[value.code];
  if (allowed !== undefined && !allowed.includes(points)) {
    throw unreadable();
  }

  return { code: value.code, points };
}

function signalsExplain(
  signals: SmartMatchSignal[],
  contested: boolean,
  exactMatchAmbiguous: boolean,
  outstanding: DecimalString,
  unapplied: DecimalString,
): boolean {
  const seen = new Set<string>();
  let positiveReferenceSignals = 0;
  let unknownSignals = 0;
  let amountSignals = 0;

  for (const signal of signals) {
    if (seen.has(signal.code)) {
      return false;
    }

    seen.add(signal.code);

    if (referenceSignals.has(signal.code)) {
      if (signal.points > 0) {
        positiveReferenceSignals += 1;
      }
    } else if (signal.code === 'AmountExact' || signal.code === 'AmountDiffers') {
      amountSignals += 1;
    } else if (signal.code !== 'ContestedCandidates' && signal.code !== 'ExactMatchAmbiguous') {
      unknownSignals += 1;
    }
  }

  if (positiveReferenceSignals > 1 || amountSignals !== 1) {
    return false;
  }

  if (positiveReferenceSignals === 0 && unknownSignals === 0) {
    return false;
  }

  const amountsEqual = scaled(outstanding) === scaled(unapplied);
  const amountSignal = signals.find((signal) => signal.code === 'AmountExact' || signal.code === 'AmountDiffers');
  if (amountSignal?.code === 'AmountExact' && !amountsEqual) {
    return false;
  }

  if (amountSignal?.code === 'AmountDiffers' && amountsEqual) {
    return false;
  }

  const contestedSignal = signals.find((signal) => signal.code === 'ContestedCandidates');
  const ambiguousSignal = signals.find((signal) => signal.code === 'ExactMatchAmbiguous');

  return (contestedSignal !== undefined) === contested
    && (ambiguousSignal !== undefined) === exactMatchAmbiguous;
}

function expectedAmbiguousReason(invoiceCount: number, bankTransactionCount: number): string {
  if (invoiceCount > 1 && bankTransactionCount > 1) {
    return 'CompetingInvoicesAndTransactions';
  }

  if (invoiceCount > 1) {
    return 'CompetingInvoices';
  }

  return 'CompetingTransactions';
}

function readArray<T>(value: unknown, readItem: (item: unknown, index: number) => T): T[] {
  if (!Array.isArray(value)) {
    throw unreadable();
  }

  return value.map((item, index) => readItem(item, index));
}

function readGuidList(value: unknown): string[] {
  const ids = readArray(value, (item) => readGuid(item));
  if (new Set(ids).size !== ids.length) {
    throw unreadable();
  }

  return ids;
}

function readOptionalGuid(value: unknown): string | null {
  if (value === null) {
    return null;
  }

  return readGuid(value);
}

function readGuid(value: unknown): string {
  if (typeof value !== 'string' || !isGuid(value)) {
    throw unreadable();
  }

  return value;
}

function readReference(value: unknown): string {
  if (typeof value !== 'string' || value.trim().length === 0) {
    throw unreadable();
  }

  return value;
}

function readOptionalReference(value: unknown): string | null {
  if (value === null) {
    return null;
  }

  return readReference(value);
}

function readRequiredMoney(value: unknown): MoneyDto {
  try {
    return readMoney(value);
  } catch {
    throw unreadable();
  }
}

function readInt(value: unknown, name: string, minimum: number, maximum: number): number {
  try {
    return readSafeInt(value, name, minimum, maximum);
  } catch {
    throw unreadable();
  }
}

function isPositive(amount: DecimalString): boolean {
  return scaled(amount) > 0n;
}

function isSmallerOpenAmount(outstanding: DecimalString, unapplied: DecimalString, suggested: DecimalString): boolean {
  const outstandingScaled = scaled(outstanding);
  const unappliedScaled = scaled(unapplied);
  const suggestedScaled = scaled(suggested);
  const smaller = outstandingScaled < unappliedScaled ? outstandingScaled : unappliedScaled;
  return suggestedScaled > 0n && suggestedScaled === smaller;
}

function scaled(amount: DecimalString): bigint {
  const [whole, fraction = ''] = amount.split('.');
  return BigInt(`${whole}${fraction.padEnd(4, '0')}`);
}

function unreadable(): UnexpectedResponseError {
  return new UnexpectedResponseError('The API returned a response the reconciliation workspace cannot read.');
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value) && !isLosslessNumber(value);
}

function isGuid(value: string): boolean {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);
}
