namespace FinOpsDesk.Modules.Access.Application;

public enum OrganizationRoleMutationStatus
{
    Granted = 1,
    AlreadyActive = 2,
    Revoked = 3,
    NotActive = 4,
    MissingMembership = 5,
    InactiveMembership = 6,
    DisabledOrganization = 7,
    DisabledUser = 8,
    InvalidRole = 9,
    LastOrganizationAdmin = 10,
    ConcurrencyConflict = 11,
    Forbidden = 12,
    OutsideOrganization = 13,
}