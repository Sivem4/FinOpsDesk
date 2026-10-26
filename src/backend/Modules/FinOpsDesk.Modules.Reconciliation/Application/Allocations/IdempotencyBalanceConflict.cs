using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.SharedKernel.Results;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

internal static class IdempotencyBalanceConflict
{
    public static bool IsPossibleLostRace(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return error.Code == ReconciliationErrors.MatchStale.Code
            || error.Code == ReconciliationErrors.InvoiceAllocationExceedsOutstanding.Code
            || error.Code == ReconciliationErrors.BankTransactionAllocationExceedsUnapplied.Code;
    }
}