using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;
using FinOpsDesk.SharedKernel.Results;

namespace FinOpsDesk.Modules.Reconciliation.Application.BatchExecutions;

internal abstract record BatchItemAttempt
{
    private BatchItemAttempt()
    {
    }

    internal sealed record Confirmed(AllocationId AllocationId, Money Amount) : BatchItemAttempt;

    internal sealed record Rejected(BatchExecutionItemOutcome Outcome) : BatchItemAttempt;

    internal sealed record Stopped(Error Error) : BatchItemAttempt;
}