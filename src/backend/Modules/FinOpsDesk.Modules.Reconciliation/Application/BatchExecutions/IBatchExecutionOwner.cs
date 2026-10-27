using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.BatchExecutions;

public interface IBatchExecutionOwner
{
    Task<Result<IBatchExecutionLease>> TryAcquireAsync(
        OrganizationId organizationId,
        Guid idempotencyKey,
        CancellationToken cancellationToken);
}