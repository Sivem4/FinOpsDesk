namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public interface IAllocationIdempotencyConflict
{
    bool IsIdempotencyIndexViolation(Exception exception);
}