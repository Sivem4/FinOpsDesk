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

        return await _dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(membership => membership.UserId == user.Id
                && membership.Status == OrganizationMembershipStatus.Active
                && membership.Organization!.Status == OrganizationStatus.Active)
            .OrderBy(membership => membership.OrganizationId)
            .Select(membership => new OrganizationChoice(
                membership.OrganizationId,
                membership.Organization!.DisplayName))
            .ToListAsync(cancellationToken);
    }
}