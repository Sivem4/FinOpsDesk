using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;

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
}