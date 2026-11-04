namespace FinOpsDesk.Modules.Access.Application;

public interface IDeploymentEmergencyControls
{
    Task<DeploymentCommandStatus> DisableUserAsync(
        string issuer,
        string subject,
        string operatorName,
        CancellationToken cancellationToken);

    Task<DeploymentCommandStatus> RevokeMembershipAsync(
        string issuer,
        string subject,
        Guid organizationId,
        string operatorName,
        CancellationToken cancellationToken);

    Task<DeploymentCommandStatus> DisableOrganizationAsync(
        Guid organizationId,
        string operatorName,
        CancellationToken cancellationToken);
}