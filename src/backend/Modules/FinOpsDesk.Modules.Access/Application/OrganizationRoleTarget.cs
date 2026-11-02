namespace FinOpsDesk.Modules.Access.Application;

public sealed record OrganizationRoleTarget(
    Guid MembershipId,
    Guid OrganizationId,
    Guid UserId,
    bool MembershipActive,
    bool UserActive,
    bool OrganizationActive);