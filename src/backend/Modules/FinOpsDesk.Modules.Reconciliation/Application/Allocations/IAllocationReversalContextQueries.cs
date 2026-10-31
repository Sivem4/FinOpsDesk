using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public interface IAllocationReversalContextQueries
{
    Task<AllocationReversalContextResponse?> FindAsync(
        OrganizationId organizationId,
        AllocationId allocationId,
        CancellationToken cancellationToken);
}