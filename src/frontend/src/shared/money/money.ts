import { isLosslessNumber, LosslessNumber, stringify } from 'lossless-json';

/**
 * A canonical decimal string. Scale is at most 4 and the value is below 10^15.
 * Zero is allowed for response money. Allocation input uses a stricter parser.
 */
export type DecimalString = string & { readonly __brand: 'DecimalString' };

const SCALE_DIGITS = 4;
const SCALE_FACTOR = 10n ** BigInt(SCALE_DIGITS);

// Values below 10^15 with four fractional digits are integers below 10^19.
const MAX_SCALED_EXCLUSIVE = 10n ** 19n;

const MAX_TOKEN_LENGTH = 64;

export class MoneyParseError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'MoneyParseError';
  }
}

export interface MoneyDto {
  amount: DecimalString;
  currency: string;
}

export function readMoneyAmount(token: string): DecimalString {
  const scaled = parseScaled(token, { allowExponent: true, allowComma: false });

  if (scaled < 0n) {
    throw new MoneyParseError('Monetary amount cannot be negative.');
  }

  if (scaled >= MAX_SCALED_EXCLUSIVE) {
    throw new MoneyParseError('Monetary amount must be below 1000000000000000.');
  }

  return canonicalFromScaled(scaled);
}

export function parseAllocationAmount(input: string): DecimalString {
  const scaled = parseScaled(input.trim(), { allowExponent: false, allowComma: true });

  if (scaled <= 0n || scaled >= MAX_SCALED_EXCLUSIVE) {
    throw new MoneyParseError(
      'Allocation amount must be greater than zero and below 1000000000000000.',
    );
  }

  return canonicalFromScaled(scaled);
}

export function readMoney(value: unknown): MoneyDto {
  if (!isRecord(value)) {
    throw new MoneyParseError('Money must be an object.');
  }

  if (!isLosslessNumber(value.amount)) {
    throw new MoneyParseError('Money amount must be a JSON number.');
  }

  if (typeof value.currency !== 'string' || !/^[A-Z]{3}$/.test(value.currency)) {
    throw new MoneyParseError('Money currency must be three letters.');
  }

  return {
    amount: readMoneyAmount(value.amount.toString()),
    currency: value.currency,
  };
}

export function readSafeInt(
  value: unknown,
  name: string,
  minimum: number,
  maximum: number,
): number {
  if (!isLosslessNumber(value)) {
    throw new MoneyParseError(`${name} must be a JSON number.`);
  }

  const token = value.toString();

  if (!/^(0|[1-9]\d*)$/.test(token)) {
    throw new MoneyParseError(`${name} must be an integer.`);
  }

  const parsed = BigInt(token);
  const lower = BigInt(minimum);
  const upper = BigInt(maximum);

  if (parsed < lower || parsed > upper || parsed > BigInt(Number.MAX_SAFE_INTEGER)) {
    throw new MoneyParseError(`${name} is outside the supported range.`);
  }

  return Number(parsed);
}

export function formatMoney(amount: DecimalString, currency: string): string {
  const negative = amount.startsWith('-');
  const unsigned = negative ? amount.slice(1) : amount;
  const [whole, fraction = ''] = unsigned.split('.');
  const grouped = whole.replace(/\B(?=(\d{3})+(?!\d))/g, ',');
  const visibleFraction = (fraction + '00').slice(0, Math.max(2, fraction.length));
  return `${negative ? '-' : ''}${grouped}.${visibleFraction} ${currency}`;
}

export function serializeAllocationBody(body: {
  invoiceId: string;
  bankTransactionId: string;
  currency: string;
  amount: string;
}): string {
  if (!isGuid(body.invoiceId) || !isGuid(body.bankTransactionId)) {
    throw new MoneyParseError('Allocation identifiers must be GUIDs.');
  }

  if (!/^[A-Z]{3}$/.test(body.currency)) {
    throw new MoneyParseError('Allocation currency must be three letters.');
  }

  const amount = parseAllocationAmount(body.amount);
  const json = stringify({
    invoiceId: body.invoiceId,
    bankTransactionId: body.bankTransactionId,
    amount: {
      amount: new LosslessNumber(amount),
      currency: body.currency,
    },
  });

  if (json === undefined) {
    throw new MoneyParseError('Allocation body could not be serialized.');
  }

  return json;
}

function parseScaled(
  raw: string,
  options: { allowExponent: boolean; allowComma: boolean },
): bigint {
  if (raw.length === 0 || raw.length > MAX_TOKEN_LENGTH) {
    throw new MoneyParseError('Monetary amount is empty or too long.');
  }

  let text = raw;

  if (options.allowComma && text.includes(',') && !text.includes('.')) {
    text = text.replace(',', '.');
  }

  if (text.includes(',') || /\s/u.test(text)) {
    throw new MoneyParseError('Monetary amount contains an unsupported character.');
  }

  let negative = false;

  if (text.startsWith('-')) {
    negative = true;
    text = text.slice(1);
  } else if (text.startsWith('+')) {
    throw new MoneyParseError('Monetary amount cannot use a leading plus.');
  }

  let exponent = 0;
  const exponentIndex = findExponent(text);

  if (exponentIndex >= 0) {
    if (!options.allowExponent) {
      throw new MoneyParseError('Allocation amount cannot use exponent notation.');
    }

    exponent = readExponent(text.slice(exponentIndex + 1));
    text = text.slice(0, exponentIndex);
  }

  const pieces = text.split('.');

  if (pieces.length > 2 || pieces[0].length === 0) {
    throw new MoneyParseError('Monetary amount is not a decimal number.');
  }

  const integerPart = pieces[0];
  const fractionPart = pieces[1] ?? '';

  if (!/^\d+$/.test(integerPart) || (fractionPart.length > 0 && !/^\d+$/.test(fractionPart))) {
    throw new MoneyParseError('Monetary amount is not a decimal number.');
  }

  if (integerPart.length > 1 && integerPart.startsWith('0')) {
    throw new MoneyParseError('Monetary amount cannot have leading zeros.');
  }

  if (integerPart.length + fractionPart.length > 48) {
    throw new MoneyParseError('Monetary amount has too many digits.');
  }

  const digits = BigInt(`${integerPart}${fractionPart}`);
  const power = exponent - fractionPart.length + SCALE_DIGITS;

  if (power > 40 || power < -40) {
    throw new MoneyParseError('Monetary amount scale is not supported.');
  }

  let scaled = digits;

  if (power >= 0) {
    scaled *= 10n ** BigInt(power);
  } else {
    const divisor = 10n ** BigInt(-power);

    if (scaled % divisor !== 0n) {
      throw new MoneyParseError('Monetary amount has more than four fractional digits.');
    }

    scaled /= divisor;
  }

  if (negative && scaled !== 0n) {
    scaled = -scaled;
  }

  return scaled;
}

function findExponent(text: string): number {
  const lower = text.lastIndexOf('e');
  const upper = text.lastIndexOf('E');
  return Math.max(lower, upper);
}

function readExponent(text: string): number {
  if (!/^[+-]?\d{1,3}$/.test(text)) {
    throw new MoneyParseError('Monetary exponent is invalid.');
  }

  const parsed = Number(text);

  if (!Number.isSafeInteger(parsed)) {
    throw new MoneyParseError('Monetary exponent is invalid.');
  }

  return parsed;
}

function canonicalFromScaled(scaled: bigint): DecimalString {
  const negative = scaled < 0n;
  const absolute = negative ? -scaled : scaled;
  const whole = absolute / SCALE_FACTOR;
  const fraction = (absolute % SCALE_FACTOR).toString().padStart(SCALE_DIGITS, '0').replace(/0+$/u, '');
  const body = fraction.length > 0 ? `${whole.toString()}.${fraction}` : whole.toString();
  return (negative ? `-${body}` : body) as DecimalString;
}

function isGuid(value: string): boolean {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
