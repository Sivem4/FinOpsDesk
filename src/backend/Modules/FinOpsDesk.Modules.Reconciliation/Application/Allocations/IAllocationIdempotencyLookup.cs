using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public interface IAllocationIdempotencyLookup
{
    Task<ConfirmedAllocationRecord?> FindAsync(
        OrganizationId organizationId,
        Guid idempotencyKey,
        CancellationToken cancellationToken);
}