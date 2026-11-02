using FinOpsDesk.Modules.Access.Domain;

namespace FinOpsDesk.Modules.Access.Application;

public interface IOrganizationRoleReader
{
    Task<IReadOnlyList<OrganizationRole>> ListActiveAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken);
}