import { z } from 'zod';

export const maxImportFileBytes = 2 * 1024 * 1024;

export const invoiceCsvHeaders = 'ExternalId,InvoiceNumber,Amount,Currency';

export const bankTransactionCsvHeaders = 'ExternalId,BookingDate,Amount,Currency,Reference';

export const importFormSchema = z.object({
  importType: z.enum(['invoices', 'bank-transactions']),
  sourceSystem: z
    .string()
    .trim()
    .min(1, 'Enter a source system.')
    .max(64, 'Source system must be 64 characters or fewer after trimming.'),
  file: z
    .instanceof(File, { message: 'Select a file.' })
    .refine((file) => file.size <= maxImportFileBytes, 'The file exceeds 2 MiB.'),
});

export type ImportFormValues = z.infer<typeof importFormSchema>;

export function headersFor(importType: ImportFormValues['importType']): string {
  return importType === 'invoices' ? invoiceCsvHeaders : bankTransactionCsvHeaders;
}
