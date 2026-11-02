namespace FinOpsDesk.Modules.Access.Domain;

public enum OrganizationRole : byte
{
    Viewer = 1,
    Operator = 2,
    ReversalApprover = 3,
    OrganizationAdmin = 4,
}