namespace FinOpsDesk.Modules.Reconciliation.Application.BatchExecutions;

public static class BatchExecutionLimits
{
    public const int DefaultPreviewPageSize = 50;

    public const int MaxPreviewPageSize = 100;

    public const int MaxAmbiguousSummaries = 100;

    public static readonly TimeSpan MaxResumeAge = TimeSpan.FromMinutes(15);
}