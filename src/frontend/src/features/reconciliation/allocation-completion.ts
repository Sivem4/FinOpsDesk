import { queryClient } from '../../shared/api/query-client';
import { sessionStore } from '../../shared/api/session-store';
import type { VerifiedExplicitAmount } from './allocation-attempt';

export function invalidateVerifiedAllocation(result: VerifiedExplicitAmount): void {
  if (sessionStore.getSnapshot().epoch !== result.epoch) {
    return;
  }

  const epoch = result.epoch;
  const keys = [
    ['invoices', epoch],
    ['bank-transactions', epoch],
    ['matches', epoch],
    ['allocations', 'history', epoch],
    ['allocations', 'context', epoch, result.invoiceId, result.bankTransactionId],
  ] as const;

  for (const queryKey of keys) {
    void queryClient.invalidateQueries({ queryKey: [...queryKey] }).catch(() => {
      // A failed refresh does not undo the financial write.
    });
  }
}
