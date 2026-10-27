namespace FinOpsDesk.Modules.Reconciliation.Domain.Allocations;

public enum AllocationIdempotencyOperation
{
    ExactMatch = 1,
    ExplicitAmount = 2,
    BatchExactMatch = 3,
}