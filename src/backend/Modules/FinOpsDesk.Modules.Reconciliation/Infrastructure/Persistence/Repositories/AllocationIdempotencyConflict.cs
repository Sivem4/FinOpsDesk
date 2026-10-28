using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Repositories;

internal sealed class AllocationIdempotencyConflict : IAllocationIdempotencyConflict
{
    public bool IsIdempotencyIndexViolation(Exception exception)
    {
        return SqlServerUniqueIndexes.IsViolation(
            exception,
            AllocationConfiguration.IdempotencyIndexName);
    }
}