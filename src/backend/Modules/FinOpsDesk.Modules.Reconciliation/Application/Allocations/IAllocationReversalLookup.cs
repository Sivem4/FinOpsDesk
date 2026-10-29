using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public interface IAllocationReversalLookup
{
    Task<AllocationReversalKeyRecord?> FindByKeyAsync(
        OrganizationId organizationId,
        Guid reversalIdempotencyKey,
        CancellationToken cancellationToken);

    Task<AllocationReversalState?> FindAllocationStateAsync(
        OrganizationId organizationId,
        AllocationId allocationId,
        CancellationToken cancellationToken);
}