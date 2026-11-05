import { z } from 'zod';

const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const emptyGuid = '00000000-0000-0000-0000-000000000000';

export const reviewCaseFilterSchema = z.object({
  status: z.enum(['', 'Open', 'Resolved', 'Dismissed']),
  invoiceId: z.string().trim().refine(
    isOptionalGuid,
    'Enter a real invoice id, or leave the field empty.',
  ),
  bankTransactionId: z.string().trim().refine(
    isOptionalGuid,
    'Enter a real bank transaction id, or leave the field empty.',
  ),
});

export type ReviewCaseFilterValues = z.infer<typeof reviewCaseFilterSchema>;

function isOptionalGuid(value: string): boolean {
  return value === '' || (guidPattern.test(value) && value.toLowerCase() !== emptyGuid);
}
