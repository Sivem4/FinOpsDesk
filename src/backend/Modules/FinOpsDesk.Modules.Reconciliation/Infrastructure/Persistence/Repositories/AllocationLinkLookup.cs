using FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Repositories;

internal sealed class AllocationLinkLookup : IAllocationLinkLookup
{
    private readonly ReconciliationDbContext _dbContext;

    public AllocationLinkLookup(ReconciliationDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    public async Task<AllocationLink?> FindAsync(
        OrganizationId organizationId,
        AllocationId allocationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(allocationId);

        Allocation? allocation = await _dbContext.Allocations
            .AsNoTracking()
            .SingleOrDefaultAsync(
                stored => stored.OrganizationId == organizationId
                    && stored.Id == allocationId,
                cancellationToken);
        if (allocation is null)
        {
            return null;
        }

        return new AllocationLink(
            allocation.Id,
            allocation.InvoiceId,
            allocation.BankTransactionId,
            allocation.Status);
    }
}