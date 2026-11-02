using FinOpsDesk.Modules.Access.Domain;

namespace FinOpsDesk.Modules.Access.Application;

public sealed class OrganizationRoleAdministration : IOrganizationRoleAdministration
{
    private readonly IOrganizationRoleStore _store;
    private readonly TimeProvider _timeProvider;

    public OrganizationRoleAdministration(IOrganizationRoleStore store, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _store = store;
        _timeProvider = timeProvider;
    }

    public Task<OrganizationRoleMutationStatus> GrantAsync(
        Guid actorUserId,
        Guid organizationId,
        Guid membershipId,
        OrganizationRole role,
        CancellationToken cancellationToken)
    {
        if (!OrganizationRolePermissions.IsDefined(role))
        {
            return Task.FromResult(OrganizationRoleMutationStatus.InvalidRole);
        }

        if (actorUserId == Guid.Empty || organizationId == Guid.Empty || membershipId == Guid.Empty)
        {
            return Task.FromResult(OrganizationRoleMutationStatus.Forbidden);
        }

        return _store.GrantForAdministratorAsync(
            actorUserId,
            organizationId,
            membershipId,
            role,
            _timeProvider.GetUtcNow(),
            cancellationToken);
    }

    public Task<OrganizationRoleMutationStatus> RevokeAsync(
        Guid actorUserId,
        Guid organizationId,
        Guid assignmentId,
        byte[] expectedRowVersion,
        CancellationToken cancellationToken)
    {
        if (actorUserId == Guid.Empty || organizationId == Guid.Empty)
        {
            return Task.FromResult(OrganizationRoleMutationStatus.Forbidden);
        }

        if (assignmentId == Guid.Empty)
        {
            return Task.FromResult(OrganizationRoleMutationStatus.NotActive);
        }

        if (expectedRowVersion is not { Length: 8 })
        {
            return Task.FromResult(OrganizationRoleMutationStatus.ConcurrencyConflict);
        }

        return _store.RevokeForAdministratorAsync(
            actorUserId,
            organizationId,
            assignmentId,
            expectedRowVersion,
            _timeProvider.GetUtcNow(),
            cancellationToken);
    }
}