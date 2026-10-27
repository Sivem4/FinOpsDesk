namespace FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;

public enum BatchExecutionItemOutcome
{
    Confirmed = 1,
    SkippedAmbiguous = 2,
    SkippedCoverage = 3,
    SkippedReview = 4,
    Stale = 5,
    Conflict = 6,
    NotFound = 7,
    Interrupted = 8
}