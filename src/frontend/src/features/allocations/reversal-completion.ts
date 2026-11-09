import { queryClient } from '../../shared/api/query-client';
import { sessionStore } from '../../shared/api/session-store';
import type { VerifiedReversal } from './reversal-attempt';

export function invalidateVerifiedReversal(result: VerifiedReversal): void {
  if (sessionStore.getSnapshot().epoch !== result.epoch) {
    return;
  }

  const epoch = result.epoch;
  const keys = [
    ['allocations', 'history', epoch],
    ['allocations', 'audit', epoch, result.allocationId],
    ['invoices', epoch],
    ['bank-transactions', epoch],
    ['matches', epoch],
    ['allocations', 'context', epoch, result.invoiceId, result.bankTransactionId],
    ['allocations', 'reversal-context', epoch, result.allocationId],
    ['allocation-disputes', 'work-summary', epoch],
    ['allocation-disputes', 'queue', epoch, 'needs-correction'],
  ] as const;

  for (const queryKey of keys) {
    void queryClient.invalidateQueries({ queryKey: [...queryKey] }).catch(() => {
      // A failed refresh does not undo the reversal.
    });
  }
}
