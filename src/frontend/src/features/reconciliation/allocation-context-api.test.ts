import { parse } from 'lossless-json';
import { describe, expect, it } from 'vitest';

import { mapAllocationContext } from './allocation-context-api';

const invoiceId = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const bankTransactionId = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';

function contextDocument(invoice: string, bankTransaction: string) {
  return parse(`{
    "invoice": {
      "invoiceId": "${invoice}",
      "invoiceNumber": "INV-100",
      "totalAmount": { "amount": 100.5, "currency": "PLN" },
      "allocatedAmount": { "amount": 0, "currency": "PLN" },
      "outstandingAmount": { "amount": 100.5, "currency": "PLN" },
      "sourceSystem": null,
      "externalId": null
    },
    "bankTransaction": {
      "bankTransactionId": "${bankTransaction}",
      "reference": "PAY-9",
      "bookingDate": "2026-03-15",
      "amount": { "amount": 100.5, "currency": "PLN" },
      "allocatedAmount": { "amount": 0, "currency": "PLN" },
      "unappliedAmount": { "amount": 100.5, "currency": "PLN" },
      "sourceSystem": null,
      "externalId": null
    }
  }`);
}

describe('allocation context mapping', () => {
  it('keeps the booking date as a date-only string and reads amounts losslessly', () => {
    const context = mapAllocationContext(parse(`{
      "invoice": {
        "invoiceId": "${invoiceId}",
        "invoiceNumber": "INV-100",
        "totalAmount": { "amount": 100.5, "currency": "PLN" },
        "allocatedAmount": { "amount": 0, "currency": "PLN" },
        "outstandingAmount": { "amount": 100.5, "currency": "PLN" },
        "sourceSystem": "DEMO",
        "externalId": null
      },
      "bankTransaction": {
        "bankTransactionId": "${bankTransactionId}",
        "reference": null,
        "bookingDate": "2026-03-15",
        "amount": { "amount": 100.5, "currency": "PLN" },
        "allocatedAmount": { "amount": 0, "currency": "PLN" },
        "unappliedAmount": { "amount": 100.5, "currency": "PLN" },
        "sourceSystem": null,
        "externalId": "pay-1"
      }
    }`));

    expect(context.bankTransaction.bookingDate).toBe('2026-03-15');
    expect(context.bankTransaction.reference).toBeNull();
    expect(context.invoice.outstandingAmount.amount).toBe('100.5');
    expect(context.invoice.externalId).toBeNull();
  });

  it('accepts a matching pair and ignores GUID casing', () => {
    const context = mapAllocationContext(contextDocument(invoiceId.toUpperCase(), bankTransactionId.toUpperCase()), {
      invoiceId,
      bankTransactionId,
    });

    expect(context.invoice.invoiceId).toBe(invoiceId.toUpperCase());
    expect(context.bankTransaction.bankTransactionId).toBe(bankTransactionId.toUpperCase());
  });

  it('rejects a response for a different invoice or bank transaction', () => {
    const otherInvoiceId = 'cccccccc-cccc-cccc-cccc-cccccccccccc';
    const otherBankTransactionId = 'dddddddd-dddd-dddd-dddd-dddddddddddd';

    expect(() => mapAllocationContext(contextDocument(otherInvoiceId, bankTransactionId), {
      invoiceId,
      bankTransactionId,
    })).toThrow(/could not be read/);
    expect(() => mapAllocationContext(contextDocument(invoiceId, otherBankTransactionId), {
      invoiceId,
      bankTransactionId,
    })).toThrow(/could not be read/);
  });

  it('rejects a booking date that is not a date-only string', () => {
    expect(() => mapAllocationContext({
      invoice: { invoiceId, invoiceNumber: 'INV-100' },
      bankTransaction: { bookingDate: '2026-03-15T00:00:00.000Z' },
    })).toThrow(/could not be read/);
  });
});
