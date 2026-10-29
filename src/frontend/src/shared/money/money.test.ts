import { isLosslessNumber, parse } from 'lossless-json';
import { describe, expect, it } from 'vitest';

import {
  formatMoney,
  parseAllocationAmount,
  readMoney,
  readMoneyAmount,
  readSafeInt,
  serializeAllocationBody,
} from './money';

const invoiceId = '11111111-1111-1111-1111-111111111111';
const bankTransactionId = '22222222-2222-2222-2222-222222222222';

describe('monetary amounts', () => {
  it('canonicalizes ordinary decimals without using Number', () => {
    expect(readMoneyAmount('10')).toBe('10');
    expect(readMoneyAmount('10.5')).toBe('10.5');
    expect(readMoneyAmount('10.50')).toBe('10.5');
    expect(readMoneyAmount('400.0000')).toBe('400');
    expect(parseAllocationAmount('10,5')).toBe('10.5');
  });

  it('accepts a zero incoming money amount and rejects it as an allocation', () => {
    const money = readMoney(parse('{"amount":0,"currency":"PLN"}'));

    expect(money.amount).toBe('0');
    expect(money.currency).toBe('PLN');
    expect(() => parseAllocationAmount('0')).toThrow(/greater than zero/);
    expect(() => parseAllocationAmount('0.0000')).toThrow(/greater than zero/);
  });

  it('accepts one ten-thousandth and rejects an extra fractional digit', () => {
    expect(readMoneyAmount('0.0001')).toBe('0.0001');
    expect(parseAllocationAmount('0.0001')).toBe('0.0001');
    expect(() => parseAllocationAmount('0.00001')).toThrow(/four fractional digits/);
    expect(() => readMoneyAmount('0.00001')).toThrow(/four fractional digits/);
    expect(() => parseAllocationAmount('10.50001')).toThrow(/four fractional digits/);
  });

  it('accepts the largest amount below 10^15 and rejects 10^15', () => {
    expect(parseAllocationAmount('999999999999999.9999')).toBe('999999999999999.9999');
    expect(readMoneyAmount('999999999999999.9999')).toBe('999999999999999.9999');
    expect(() => parseAllocationAmount('1000000000000000')).toThrow(/below 1000000000000000/);
    expect(() => readMoneyAmount('1000000000000000')).toThrow(/below 1000000000000000/);
  });

  it('rejects negative and malformed allocation input', () => {
    expect(() => parseAllocationAmount('-1')).toThrow(/greater than zero/);
    expect(() => readMoneyAmount('-1')).toThrow(/negative/);
    expect(() => parseAllocationAmount('')).toThrow();
    expect(() => parseAllocationAmount('abc')).toThrow();
    expect(() => parseAllocationAmount('1e-4')).toThrow(/exponent/);
    expect(() => parseAllocationAmount('+10')).toThrow();
  });

  it('maps pagination numbers to safe integers', () => {
    const body = parse('{"page":2,"pageSize":50,"totalCount":0}') as Record<string, unknown>;

    expect(readSafeInt(body.page, 'page', 1, 100)).toBe(2);
    expect(readSafeInt(body.pageSize, 'pageSize', 1, 100)).toBe(50);
    expect(readSafeInt(body.totalCount, 'totalCount', 0, Number.MAX_SAFE_INTEGER)).toBe(0);
    expect(() => readSafeInt(parse('1.5'), 'page', 1, 100)).toThrow(/integer/);
  });

  it('preserves a large JSON money literal exactly', () => {
    const money = readMoney(parse('{"amount":999999999999999.9999,"currency":"PLN"}'));

    expect(money.amount).toBe('999999999999999.9999');
    expect(Number('999999999999999.9999').toString()).not.toBe('999999999999999.9999');
  });

  it('serializes an allocation amount as an unquoted JSON number', () => {
    const json = serializeAllocationBody({
      invoiceId,
      bankTransactionId,
      currency: 'PLN',
      amount: '10.50',
    });

    expect(json).toContain('"amount":10.5');
    expect(json).not.toContain('"amount":"10.5"');
    expect(json).toContain('"currency":"PLN"');
  });

  it('serializes the largest permitted amount as an exact unquoted JSON number', () => {
    const amount = '999999999999999.9999';
    const json = serializeAllocationBody({
      invoiceId,
      bankTransactionId,
      currency: 'PLN',
      amount,
    });

    expect(json).toContain(`"amount":${amount}`);
    expect(json).not.toContain(`"amount":"${amount}"`);

    const parsed = parse(json);
    const amountValue =
      typeof parsed === 'object' &&
      parsed !== null &&
      'amount' in parsed &&
      typeof parsed.amount === 'object' &&
      parsed.amount !== null &&
      'amount' in parsed.amount
        ? parsed.amount.amount
        : null;

    expect(isLosslessNumber(amountValue)).toBe(true);
    if (!isLosslessNumber(amountValue)) {
      return;
    }

    expect(amountValue.toString()).toBe(amount);
  });

  it('formats zero and scale-4 amounts from the canonical string', () => {
    expect(formatMoney(readMoneyAmount('0'), 'PLN')).toBe('0.00 PLN');
    expect(formatMoney(readMoneyAmount('0.0001'), 'EUR')).toBe('0.0001 EUR');
    expect(formatMoney(readMoneyAmount('999999999999999.9999'), 'PLN')).toBe(
      '999,999,999,999,999.9999 PLN',
    );
  });
});
