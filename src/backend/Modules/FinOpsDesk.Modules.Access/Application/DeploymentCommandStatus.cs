namespace FinOpsDesk.Modules.Access.Application;

public enum DeploymentCommandStatus
{
    OrganizationCreated = 1,
    OrganizationAlreadyExists = 2,
    InvalidOrganizationName = 3,
    MembershipGranted = 4,
    MembershipAlreadyActive = 5,
    UserNotFound = 6,
    OrganizationNotFound = 7,
    MembershipInactive = 8,
    RoleGranted = 9,
    RoleAlreadyActive = 10,
    RoleRevoked = 11,
    RoleNotActive = 12,
    LastOrganizationAdmin = 13,
    DisabledUser = 14,
    DisabledOrganization = 15,
    InvalidRole = 16,
    InvalidOperator = 17,
    ConcurrencyConflict = 18,
    MembershipNotFound = 19,
}