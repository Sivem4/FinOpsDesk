using FinOpsDesk.Modules.Access.Application;
using FinOpsDesk.Modules.Access.Domain;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.Modules.Access.Infrastructure.Persistence;

internal sealed class OrganizationMembershipReader : IOrganizationMembershipReader
{
    private readonly AccessDbContext _dbContext;

    public OrganizationMembershipReader(AccessDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<OrganizationChoice>> ListActiveAsync(
        string issuer,
        string subject,
        CancellationToken cancellationToken)
    {
        byte[] identityKey = ExternalIdentityKey.Compute(issuer, subject);
        ApplicationUser? user = await _dbContext.ApplicationUsers
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.IdentityKey == identityKey, cancellationToken);

        if (user is null)
        {
            return [];
        }

        if (!ExternalIdentityKey.Same(user.Issuer, user.Subject, issuer, subject))
        {
            throw new InvalidOperationException("Identity key collision.");
        }

        if (user.Status != ApplicationUserStatus.Active)
        {
            return [];
        }

        return await ListActiveChoicesAsync(user.Id, cancellationToken);
    }

    public async Task<MembershipResolution> ResolveAsync(
        string issuer,
        string subject,
        CancellationToken cancellationToken)
    {
        byte[] identityKey = ExternalIdentityKey.Compute(issuer, subject);
        ApplicationUser? user = await _dbContext.ApplicationUsers
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.IdentityKey == identityKey, cancellationToken);

        if (user is null)
        {
            return MembershipResolution.Missing();
        }

        if (!ExternalIdentityKey.Same(user.Issuer, user.Subject, issuer, subject))
        {
            throw new InvalidOperationException("Identity key collision.");
        }

        if (user.Status != ApplicationUserStatus.Active)
        {
            return MembershipResolution.Disabled(user.Id, user.SessionStamp);
        }

        if (user.SessionStamp == Guid.Empty)
        {
            throw new InvalidOperationException("Application user session stamp is missing.");
        }

        IReadOnlyList<OrganizationChoice> organizations = await ListActiveChoicesAsync(
            user.Id,
            cancellationToken);
        return MembershipResolution.Active(user.Id, user.SessionStamp, organizations);
    }

    public Task RetireDisabledSessionAsync(
        Guid userId,
        Guid observedStamp,
        CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty || observedStamp == Guid.Empty)
        {
            return Task.CompletedTask;
        }

        // Disable already rotates the stamp. This update remains for a disabled
        // row that still carries the presented stamp, and it does not replace a newer one.
        Guid replacement = Guid.NewGuid();
        return _dbContext.ApplicationUsers
            .Where(user => user.Id == userId
                && user.Status == ApplicationUserStatus.Disabled
                && user.SessionStamp == observedStamp)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(user => user.SessionStamp, replacement),
                cancellationToken);
    }

    private async Task<IReadOnlyList<OrganizationChoice>> ListActiveChoicesAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {

        return await _dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(membership => membership.UserId == userId
                && membership.Status == OrganizationMembershipStatus.Active
                && membership.Organization!.Status == OrganizationStatus.Active)
            .OrderBy(membership => membership.OrganizationId)
            .Select(membership => new OrganizationChoice(
                membership.OrganizationId,
                membership.Organization!.DisplayName))
            .ToListAsync(cancellationToken);
    }
}