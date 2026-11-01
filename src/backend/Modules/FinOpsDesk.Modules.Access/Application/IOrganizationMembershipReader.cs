namespace FinOpsDesk.Modules.Access.Application;

public interface IOrganizationMembershipReader
{
    Task<IReadOnlyList<OrganizationChoice>> ListActiveAsync(
        string issuer,
        string subject,
        CancellationToken cancellationToken);
}