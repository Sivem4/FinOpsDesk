using FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;

namespace FinOpsDesk.Modules.Reconciliation.Application.BatchExecutions;

public sealed record ExecuteBatchExactMatchesCommand(
    Guid IdempotencyKey,
    IReadOnlyList<BatchExactMatchPair> Pairs);