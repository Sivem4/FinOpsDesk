namespace FinOpsDesk.Modules.Access.Application;

public interface IExternalIdentityProvisioner
{
    Task<ExternalIdentityProvision> ProvisionAsync(
        string issuer,
        string subject,
        string? displayName,
        CancellationToken cancellationToken);
}