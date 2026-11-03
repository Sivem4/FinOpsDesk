namespace FinOpsDesk.Modules.Reconciliation.Application.BatchExecutions;

// Production execution does not wait. Tests replace this so two financial
// transactions reach SaveChanges together without sleeping.
internal interface IBatchExactMatchItemSynchronization
{
    Task OnItemReadyToCommitAsync(Guid itemKey, CancellationToken cancellationToken);

    Task BeforeItemAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

internal sealed class ImmediateBatchItemSynchronization : IBatchExactMatchItemSynchronization
{
    public Task OnItemReadyToCommitAsync(Guid itemKey, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}