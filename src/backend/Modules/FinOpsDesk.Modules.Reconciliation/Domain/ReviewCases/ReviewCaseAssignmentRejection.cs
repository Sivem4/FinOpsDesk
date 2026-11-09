namespace FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;

public enum ReviewCaseAssignmentRejection
{
    Terminal = 1,
    AlreadyAssigned = 2,
    NotAssigned = 3,
    NotAssignee = 4,
    DelegateeUnavailable = 5,
}