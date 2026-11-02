using FinOpsDesk.Modules.Access.Application;
using FinOpsDesk.Modules.Access.Domain;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.Modules.Access.Infrastructure.Persistence;

internal sealed class ExternalIdentityProvisioner : IExternalIdentityProvisioner
{
    private readonly AccessDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public ExternalIdentityProvisioner(AccessDbContext dbContext, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    public async Task<ExternalIdentityProvision> ProvisionAsync(
        string issuer,
        string subject,
        string? displayName,
        CancellationToken cancellationToken)
    {
        byte[] identityKey = ExternalIdentityKey.Compute(issuer, subject);
        ApplicationUser? existing = await FindAsync(identityKey, cancellationToken);
        if (existing is not null)
        {
            if (existing.Status == ApplicationUserStatus.Active && existing.SessionStamp == Guid.Empty)
            {
                existing = await ReplaceEmptyStampAsync(existing.Id, cancellationToken);
            }

            return ResolveExisting(existing, issuer, subject);
        }

        ApplicationUser created = ApplicationUser.Create(
            Guid.NewGuid(),
            issuer,
            subject,
            displayName,
            _timeProvider.GetUtcNow());
        _dbContext.ApplicationUsers.Add(created);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return ExternalIdentityProvision.Created(created.Id, created.SessionStamp);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            _dbContext.Entry(created).State = EntityState.Detached;
            ApplicationUser? winner = await FindAsync(identityKey, cancellationToken);
            if (winner is null)
            {
                throw;
            }

            if (winner.Status == ApplicationUserStatus.Active && winner.SessionStamp == Guid.Empty)
            {
                winner = await ReplaceEmptyStampAsync(winner.Id, cancellationToken);
            }

            return ResolveExisting(winner, issuer, subject);
        }
    }

    internal static ExternalIdentityProvision ResolveExisting(
        ApplicationUser existing,
        string issuer,
        string subject)
    {
        ArgumentNullException.ThrowIfNull(existing);

        if (!ExternalIdentityKey.Same(existing.Issuer, existing.Subject, issuer, subject))
        {
            throw new InvalidOperationException("Identity key collision.");
        }

        if (existing.Status != ApplicationUserStatus.Active)
        {
            return ExternalIdentityProvision.Disabled(existing.Id, existing.SessionStamp);
        }

        return ExternalIdentityProvision.Reused(existing.Id, existing.SessionStamp);
    }

    private async Task<ApplicationUser> ReplaceEmptyStampAsync(Guid userId, CancellationToken cancellationToken)
    {
        ApplicationUser tracked = await _dbContext.ApplicationUsers
            .SingleAsync(user => user.Id == userId, cancellationToken);
        tracked.RotateSessionStamp();
        await _dbContext.SaveChangesAsync(cancellationToken);
        return tracked;
    }

    private Task<ApplicationUser?> FindAsync(byte[] identityKey, CancellationToken cancellationToken)
    {
        return _dbContext.ApplicationUsers
            .AsNoTracking()
            .SingleOrDefaultAsync(user => user.IdentityKey == identityKey, cancellationToken);
    }

    private static bool IsUniqueViolation(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException)
            {
                return sqlException.Number is 2601 or 2627;
            }
        }

        return false;
    }
}