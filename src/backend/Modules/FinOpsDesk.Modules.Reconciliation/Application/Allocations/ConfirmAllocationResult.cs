using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public sealed class ConfirmAllocationResult
{
    public AllocationId AllocationId { get; }

    public ConfirmAllocationResult(AllocationId allocationId)
    {
        ArgumentNullException.ThrowIfNull(allocationId);

        AllocationId = allocationId;
    }
}