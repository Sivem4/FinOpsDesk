namespace FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;

public enum ReviewCaseAuditAction
{
    // Stored values. The unique indexes and check constraints use these numbers.
    Opened = 1,
    Resolved = 2,
    Dismissed = 3,
}