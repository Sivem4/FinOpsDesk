using FinOpsDesk.Modules.Access.Domain;

namespace FinOpsDesk.Modules.Access.Application;

public interface IOrganizationRoleAdministration
{
    Task<OrganizationRoleMutationStatus> GrantAsync(
        Guid actorUserId,
        Guid organizationId,
        Guid membershipId,
        OrganizationRole role,
        CancellationToken cancellationToken);

    Task<OrganizationRoleMutationStatus> RevokeAsync(
        Guid actorUserId,
        Guid organizationId,
        Guid assignmentId,
        byte[] expectedRowVersion,
        CancellationToken cancellationToken);
}