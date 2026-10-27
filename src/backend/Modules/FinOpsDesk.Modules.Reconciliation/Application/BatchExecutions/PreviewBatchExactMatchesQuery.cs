namespace FinOpsDesk.Modules.Reconciliation.Application.BatchExecutions;

public sealed record PreviewBatchExactMatchesQuery(
    int Page = 1,
    int PageSize = BatchExecutionLimits.DefaultPreviewPageSize);