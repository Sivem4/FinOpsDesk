namespace FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;

public enum ReviewCaseStatus
{
    // The filtered unique index UX_ReviewCases_OrganizationId_SubjectKey_Open
    // stores this value. Do not renumber it without a new migration.
    Open = 1,
    Resolved = 2,
    Dismissed = 3
}