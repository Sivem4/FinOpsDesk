using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Repositories;

internal sealed class AllocationRepository : IAllocationRepository
{
    private readonly ReconciliationDbContext _dbContext;

    public AllocationRepository(ReconciliationDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    public void Add(Allocation allocation)
    {
        ArgumentNullException.ThrowIfNull(allocation);

        _dbContext.Allocations.Add(allocation);
    }

    public Task<Allocation?> GetByIdAsync(
        OrganizationId organizationId,
        AllocationId allocationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(allocationId);

        return _dbContext.Allocations.SingleOrDefaultAsync(
            allocation => allocation.OrganizationId == organizationId
                && allocation.Id == allocationId,
            cancellationToken);
    }

    public void AssignReversalIdempotencyKey(Allocation allocation, Guid reversalIdempotencyKey)
    {
        AllocationReversalIdempotency.Assign(_dbContext, allocation, reversalIdempotencyKey);
    }
}