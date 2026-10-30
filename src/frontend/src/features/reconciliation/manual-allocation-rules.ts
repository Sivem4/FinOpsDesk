import {
  compareDecimalAmounts,
  MoneyParseError,
  parseAllocationAmount,
  readMoneyAmount,
  type DecimalString,
} from '../../shared/money/money';
import type { AllocationContext } from './allocation-context-api';

const zeroAmount = readMoneyAmount('0');

export type AllocationBalanceView =
  | {
    status: 'allocatable';
    currency: string;
    maximum: DecimalString;
    outstanding: DecimalString;
    unapplied: DecimalString;
  }
  | {
    status: 'fully-allocated';
    currency: string;
    outstanding: DecimalString;
    unapplied: DecimalString;
  }
  | { status: 'currency-mismatch' };

export function describeAllocationBalances(context: AllocationContext): AllocationBalanceView {
  const invoiceCurrencies = [
    context.invoice.totalAmount.currency,
    context.invoice.allocatedAmount.currency,
    context.invoice.outstandingAmount.currency,
  ];
  const bankCurrencies = [
    context.bankTransaction.amount.currency,
    context.bankTransaction.allocatedAmount.currency,
    context.bankTransaction.unappliedAmount.currency,
  ];
  const invoiceCurrency = invoiceCurrencies[0];
  const bankCurrency = bankCurrencies[0];

  if (invoiceCurrency === undefined || bankCurrency === undefined || invoiceCurrencies.some((currency) => currency !== invoiceCurrency) || bankCurrencies.some((currency) => currency !== bankCurrency) || invoiceCurrency !== bankCurrency) {
    return { status: 'currency-mismatch' };
  }

  const outstanding = context.invoice.outstandingAmount.amount;
  const unapplied = context.bankTransaction.unappliedAmount.amount;

  if (compareDecimalAmounts(outstanding, zeroAmount) <= 0 || compareDecimalAmounts(unapplied, zeroAmount) <= 0) {
    return { status: 'fully-allocated', currency: invoiceCurrency, outstanding, unapplied };
  }

  const maximum = compareDecimalAmounts(outstanding, unapplied) <= 0 ? outstanding : unapplied;
  return { status: 'allocatable', currency: invoiceCurrency, maximum, outstanding, unapplied };
}

export function assessAllocationAmount(
  maximum: DecimalString,
  amountText: string,
): { ok: true; amount: DecimalString } | { ok: false; message: string } {
  let amount: DecimalString;

  try {
    amount = parseAllocationAmount(amountText);
  } catch (error) {
    return { ok: false, message: error instanceof MoneyParseError ? error.message : 'Enter a valid allocation amount.' };
  }

  if (compareDecimalAmounts(amount, maximum) > 0) {
    return { ok: false, message: 'This amount is larger than the smaller remaining balance in this read.' };
  }

  return { ok: true, amount };
}
