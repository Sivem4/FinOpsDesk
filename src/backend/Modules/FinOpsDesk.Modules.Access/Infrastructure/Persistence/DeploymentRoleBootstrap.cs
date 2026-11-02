using FinOpsDesk.Modules.Access.Application;
using FinOpsDesk.Modules.Access.Domain;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.Modules.Access.Infrastructure.Persistence;

internal sealed class DeploymentRoleBootstrap : IDeploymentRoleBootstrap
{
    private readonly AccessDbContext _dbContext;
    private readonly IOrganizationRoleStore _roles;
    private readonly TimeProvider _timeProvider;

    public DeploymentRoleBootstrap(
        AccessDbContext dbContext,
        IOrganizationRoleStore roles,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _dbContext = dbContext;
        _roles = roles;
        _timeProvider = timeProvider;
    }

    public async Task<DeploymentCommandStatus> CreateOrganizationAsync(
        Guid organizationId,
        string displayName,
        CancellationToken cancellationToken)
    {
        if (organizationId == Guid.Empty)
        {
            return DeploymentCommandStatus.OrganizationNotFound;
        }

        if (await _dbContext.Organizations.AnyAsync(organization => organization.Id == organizationId, cancellationToken))
        {
            return DeploymentCommandStatus.OrganizationAlreadyExists;
        }

        Organization organization;
        try
        {
            organization = Organization.Create(organizationId, displayName, _timeProvider.GetUtcNow());
        }
        catch (ArgumentException)
        {
            return DeploymentCommandStatus.InvalidOrganizationName;
        }

        _dbContext.Organizations.Add(organization);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return DeploymentCommandStatus.OrganizationCreated;
    }

    public async Task<DeploymentCommandStatus> GrantMembershipAsync(
        string issuer,
        string subject,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        ApplicationUser? user = await FindUserAsync(issuer, subject, cancellationToken);
        if (user is null)
        {
            return DeploymentCommandStatus.UserNotFound;
        }

        if (user.Status != ApplicationUserStatus.Active)
        {
            return DeploymentCommandStatus.DisabledUser;
        }

        Organization? organization = await _dbContext.Organizations
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == organizationId, cancellationToken);
        if (organization is null)
        {
            return DeploymentCommandStatus.OrganizationNotFound;
        }

        if (organization.Status != OrganizationStatus.Active)
        {
            return DeploymentCommandStatus.DisabledOrganization;
        }

        OrganizationMembership? membership = await _dbContext.OrganizationMemberships
            .SingleOrDefaultAsync(
                candidate => candidate.UserId == user.Id && candidate.OrganizationId == organizationId,
                cancellationToken);
        if (membership is not null)
        {
            return membership.Status == OrganizationMembershipStatus.Active
                ? DeploymentCommandStatus.MembershipAlreadyActive
                : DeploymentCommandStatus.MembershipInactive;
        }

        _dbContext.OrganizationMemberships.Add(OrganizationMembership.Grant(
            Guid.NewGuid(),
            user.Id,
            organizationId,
            _timeProvider.GetUtcNow()));
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return DeploymentCommandStatus.MembershipGranted;
        }
        catch (DbUpdateException)
        {
            _dbContext.ChangeTracker.Clear();
            OrganizationMembership? raced = await _dbContext.OrganizationMemberships
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.UserId == user.Id && candidate.OrganizationId == organizationId,
                    cancellationToken);
            if (raced is null)
            {
                throw;
            }

            return raced.Status == OrganizationMembershipStatus.Active
                ? DeploymentCommandStatus.MembershipAlreadyActive
                : DeploymentCommandStatus.MembershipInactive;
        }
    }

    public Task<DeploymentCommandStatus> GrantRoleAsync(
        string issuer,
        string subject,
        Guid organizationId,
        OrganizationRole role,
        string operatorName,
        CancellationToken cancellationToken)
    {
        return ChangeRoleAsync(issuer, subject, organizationId, role, operatorName, grant: true, cancellationToken);
    }

    public Task<DeploymentCommandStatus> RevokeRoleAsync(
        string issuer,
        string subject,
        Guid organizationId,
        OrganizationRole role,
        string operatorName,
        CancellationToken cancellationToken)
    {
        return ChangeRoleAsync(issuer, subject, organizationId, role, operatorName, grant: false, cancellationToken);
    }

    private async Task<DeploymentCommandStatus> ChangeRoleAsync(
        string issuer,
        string subject,
        Guid organizationId,
        OrganizationRole role,
        string operatorName,
        bool grant,
        CancellationToken cancellationToken)
    {
        RoleActor actor;
        try
        {
            actor = RoleActor.DeploymentBootstrap(operatorName);
        }
        catch (ArgumentException)
        {
            return DeploymentCommandStatus.InvalidOperator;
        }

        if (!OrganizationRolePermissions.IsDefined(role))
        {
            return DeploymentCommandStatus.InvalidRole;
        }

        ApplicationUser? user = await FindUserAsync(issuer, subject, cancellationToken);
        if (user is null)
        {
            return DeploymentCommandStatus.UserNotFound;
        }

        Guid? membershipId = await _dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(membership => membership.UserId == user.Id && membership.OrganizationId == organizationId)
            .Select(membership => (Guid?)membership.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (membershipId is null)
        {
            return DeploymentCommandStatus.MembershipNotFound;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        OrganizationRoleMutationStatus status = grant
            ? await _roles.GrantAsync(membershipId.Value, role, now, actor, cancellationToken)
            : await _roles.RevokeAsync(membershipId.Value, role, now, actor, cancellationToken);
        return status switch
        {
            OrganizationRoleMutationStatus.Granted => DeploymentCommandStatus.RoleGranted,
            OrganizationRoleMutationStatus.AlreadyActive => DeploymentCommandStatus.RoleAlreadyActive,
            OrganizationRoleMutationStatus.Revoked => DeploymentCommandStatus.RoleRevoked,
            OrganizationRoleMutationStatus.NotActive => DeploymentCommandStatus.RoleNotActive,
            OrganizationRoleMutationStatus.LastOrganizationAdmin => DeploymentCommandStatus.LastOrganizationAdmin,
            OrganizationRoleMutationStatus.InactiveMembership => DeploymentCommandStatus.MembershipInactive,
            OrganizationRoleMutationStatus.DisabledUser => DeploymentCommandStatus.DisabledUser,
            OrganizationRoleMutationStatus.DisabledOrganization => DeploymentCommandStatus.DisabledOrganization,
            OrganizationRoleMutationStatus.InvalidRole => DeploymentCommandStatus.InvalidRole,
            OrganizationRoleMutationStatus.MissingMembership => DeploymentCommandStatus.OrganizationNotFound,
            OrganizationRoleMutationStatus.ConcurrencyConflict => DeploymentCommandStatus.ConcurrencyConflict,
            _ => DeploymentCommandStatus.ConcurrencyConflict,
        };
    }

    private async Task<ApplicationUser?> FindUserAsync(
        string issuer,
        string subject,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(issuer) || string.IsNullOrEmpty(subject))
        {
            return null;
        }

        byte[] identityKey = ExternalIdentityKey.Compute(issuer, subject);
        List<ApplicationUser> rows = await _dbContext.ApplicationUsers
            .AsNoTracking()
            .Where(user => user.IdentityKey == identityKey)
            .ToListAsync(cancellationToken);
        return rows.SingleOrDefault(user => ExternalIdentityKey.Same(user.Issuer, user.Subject, issuer, subject));
    }
}