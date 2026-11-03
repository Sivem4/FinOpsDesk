using System.Data;
using System.Data.Common;

using FinOpsDesk.Modules.Access.Application;
using FinOpsDesk.Modules.Access.Domain;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

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
        return RetireLockedAsync(userId, observedStamp, cancellationToken);
    }

    private async Task RetireLockedAsync(
        Guid userId,
        Guid observedStamp,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        DbConnection connection = _dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText =
            """
            SELECT Status, SessionStamp
            FROM access.ApplicationUsers WITH (UPDLOCK, HOLDLOCK)
            WHERE Id = @id
            """;
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = userId });
        bool matches = false;
        await using (DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                matches = reader.GetByte(0) == (byte)ApplicationUserStatus.Disabled
                    && reader.GetGuid(1) == observedStamp;
            }
        }

        if (!matches)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        await using DbCommand update = connection.CreateCommand();
        update.Transaction = transaction.GetDbTransaction();
        update.CommandText =
            """
            UPDATE access.ApplicationUsers
            SET SessionStamp = @stamp
            WHERE Id = @id AND Status = @status AND SessionStamp = @observed
            """;
        update.Parameters.Add(new SqlParameter("@stamp", SqlDbType.UniqueIdentifier) { Value = Guid.NewGuid() });
        update.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = userId });
        update.Parameters.Add(new SqlParameter("@status", SqlDbType.TinyInt) { Value = (byte)ApplicationUserStatus.Disabled });
        update.Parameters.Add(new SqlParameter("@observed", SqlDbType.UniqueIdentifier) { Value = observedStamp });
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
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