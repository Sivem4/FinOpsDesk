using FinOpsDesk.Modules.Access.Domain;

namespace FinOpsDesk.Modules.Access.Application;

public interface IDeploymentRoleBootstrap
{
    Task<DeploymentCommandStatus> CreateOrganizationAsync(
        Guid organizationId,
        string displayName,
        CancellationToken cancellationToken);

    Task<DeploymentCommandStatus> GrantMembershipAsync(
        string issuer,
        string subject,
        Guid organizationId,
        CancellationToken cancellationToken);

    Task<DeploymentCommandStatus> GrantRoleAsync(
        string issuer,
        string subject,
        Guid organizationId,
        OrganizationRole role,
        string operatorName,
        CancellationToken cancellationToken);

    Task<DeploymentCommandStatus> RevokeRoleAsync(
        string issuer,
        string subject,
        Guid organizationId,
        OrganizationRole role,
        string operatorName,
        CancellationToken cancellationToken);
}