using FinOpsDesk.Modules.Access.Domain;

namespace FinOpsDesk.Modules.Access.Application;

public interface IOrganizationRoleStore
{
    Task<OrganizationRoleMutationStatus> GrantAsync(
        Guid membershipId,
        OrganizationRole role,
        DateTimeOffset grantedAt,
        RoleActor grantedBy,
        CancellationToken cancellationToken);

    Task<OrganizationRoleMutationStatus> RevokeAsync(
        Guid membershipId,
        OrganizationRole role,
        DateTimeOffset revokedAt,
        RoleActor revokedBy,
        CancellationToken cancellationToken);

    Task<OrganizationRoleMutationStatus> GrantForAdministratorAsync(
        Guid actorUserId,
        Guid organizationId,
        Guid membershipId,
        OrganizationRole role,
        DateTimeOffset grantedAt,
        CancellationToken cancellationToken);

    Task<OrganizationRoleMutationStatus> RevokeForAdministratorAsync(
        Guid actorUserId,
        Guid organizationId,
        Guid assignmentId,
        byte[] expectedRowVersion,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken);
}