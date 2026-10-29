using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;

internal static class AllocationReversalIdempotency
{
    public static void Assign(
        ReconciliationDbContext context,
        Allocation allocation,
        Guid reversalIdempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(allocation);

        if (reversalIdempotencyKey == Guid.Empty)
        {
            throw new ArgumentException(
                "Reversal idempotency key cannot be empty.",
                nameof(reversalIdempotencyKey));
        }

        context.Entry(allocation)
            .Property<Guid?>(AllocationConfiguration.ReversalIdempotencyKeyPropertyName)
            .CurrentValue = reversalIdempotencyKey;
    }
}