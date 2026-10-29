namespace FinOpsDesk.Modules.Reconciliation.Domain.Allocations;

public enum AllocationIdempotencyOperation
{
    ExactMatch = 1,
    ExplicitAmount = 2,
    BatchExactMatch = 3,

    // Audit reversal rows only. Allocation confirmation rows reject this value.
    Reversal = 4,
}