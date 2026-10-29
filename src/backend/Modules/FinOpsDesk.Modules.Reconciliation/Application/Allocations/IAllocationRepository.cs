using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public interface IAllocationRepository
{
    void Add(Allocation allocation);

    Task<Allocation?> GetByIdAsync(
        OrganizationId organizationId,
        AllocationId allocationId,
        CancellationToken cancellationToken);

    void AssignReversalIdempotencyKey(Allocation allocation, Guid reversalIdempotencyKey);
}