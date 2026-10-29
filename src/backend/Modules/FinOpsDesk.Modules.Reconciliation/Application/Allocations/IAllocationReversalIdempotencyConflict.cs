namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public interface IAllocationReversalIdempotencyConflict
{
    bool IsReversalIdempotencyIndexViolation(Exception exception);
}