using FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;

namespace FinOpsDesk.Modules.Reconciliation.Application.BatchExecutions;

public interface IBatchExecutionLease : IAsyncDisposable
{
    BatchExecutionSnapshot Snapshot { get; }

    int OwnerSessionId { get; }

    Task<bool> TryCompleteAsync(
        BatchExecutionResult result,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken);

    Task CommitAsync(CancellationToken cancellationToken);
}