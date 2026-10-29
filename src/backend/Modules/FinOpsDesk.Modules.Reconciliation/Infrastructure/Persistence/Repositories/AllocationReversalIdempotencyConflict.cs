using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Repositories;

internal sealed class AllocationReversalIdempotencyConflict : IAllocationReversalIdempotencyConflict
{
    public bool IsReversalIdempotencyIndexViolation(Exception exception)
    {
        return SqlServerUniqueIndexes.IsViolation(
            exception,
            AllocationConfiguration.ReversalIdempotencyIndexName);
    }
}