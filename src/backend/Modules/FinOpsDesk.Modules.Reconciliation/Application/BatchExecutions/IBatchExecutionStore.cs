using FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.BatchExecutions;

public interface IBatchExecutionStore
{
    Task<BatchExecutionSnapshot?> FindAsync(
        OrganizationId organizationId,
        Guid idempotencyKey,
        CancellationToken cancellationToken);

    Task AddRunningAsync(
        BatchExecution execution,
        CancellationToken cancellationToken);

    // True when the running receipt was inserted. False only when
    // UX_BatchExecutions_OrganizationId_IdempotencyKey rejected the row.
    // Any other database failure propagates and the caller must discard the context.
    Task<bool> TryAddRunningAsync(
        BatchExecution execution,
        CancellationToken cancellationToken);

    Task<bool> TryCompleteAsync(
        BatchExecutionId id,
        OrganizationId organizationId,
        BatchExecutionResult result,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken);
}