import { z } from 'zod';

import { isExactGuid } from './allocation-dispute-api';

export const allocationDisputeFilterSchema = z.object({
  allocationId: z.string().refine(
    (value) => value === '' || isExactGuid(value),
    'Enter one allocation id, or leave the field empty.',
  ),
});

export type AllocationDisputeFilterValues = z.infer<typeof allocationDisputeFilterSchema>;
