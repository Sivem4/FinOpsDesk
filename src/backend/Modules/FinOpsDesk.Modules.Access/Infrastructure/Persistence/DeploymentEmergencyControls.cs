using System.Data;
using System.Data.Common;

using FinOpsDesk.Modules.Access.Application;
using FinOpsDesk.Modules.Access.Domain;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FinOpsDesk.Modules.Access.Infrastructure.Persistence;

internal sealed class DeploymentEmergencyControls : IDeploymentEmergencyControls
{
    private readonly AccessDbContext _dbContext;
    private readonly IOrganizationFinancialAuthorization _authorization;
    private readonly IDeploymentEmergencySync _sync;
    private readonly TimeProvider _timeProvider;

    public DeploymentEmergencyControls(
        AccessDbContext dbContext,
        IOrganizationFinancialAuthorization? authorization = null,
        IDeploymentEmergencySync? sync = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
        _authorization = authorization ?? new OrganizationFinancialAuthorization();
        _sync = sync ?? NoDeploymentEmergencySync.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<DeploymentCommandStatus> DisableUserAsync(
        string issuer,
        string subject,
        string operatorName,
        CancellationToken cancellationToken)
    {
        if (!TryOperator(operatorName, out string declared))
        {
            return DeploymentCommandStatus.InvalidOperator;
        }

        byte[] identityKey;
        try
        {
            identityKey = ExternalIdentityKey.Compute(issuer, subject);
        }
        catch (ArgumentException)
        {
            return DeploymentCommandStatus.UserNotFound;
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        LockedUser? locked = await LockUserAsync(transaction.GetDbTransaction(), identityKey, cancellationToken);
        if (locked is null || !ExternalIdentityKey.Same(locked.Issuer, locked.Subject, issuer, subject))
        {
            await transaction.RollbackAsync(cancellationToken);
            return DeploymentCommandStatus.UserNotFound;
        }

        await _sync.AfterAuthoritativeLockAsync(cancellationToken);
        bool already = locked.Status == ApplicationUserStatus.Disabled;
        if (!already)
        {
            ApplicationUser user = await _dbContext.ApplicationUsers
                .SingleAsync(candidate => candidate.Id == locked.Id, cancellationToken);
            user.Disable();
        }

        AddEvent(
            OperationalSecurityOperation.DisableUser,
            already ? OperationalSecurityOutcome.AlreadyApplied : OperationalSecurityOutcome.Applied,
            locked.Id,
            null,
            null,
            declared);
        await _sync.BeforeCommitAsync(cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return already
            ? DeploymentCommandStatus.UserAlreadyDisabled
            : DeploymentCommandStatus.UserDisabled;
    }

    public async Task<DeploymentCommandStatus> RevokeMembershipAsync(
        string issuer,
        string subject,
        Guid organizationId,
        string operatorName,
        CancellationToken cancellationToken)
    {
        if (!TryOperator(operatorName, out string declared))
        {
            return DeploymentCommandStatus.InvalidOperator;
        }

        if (organizationId == Guid.Empty)
        {
            return DeploymentCommandStatus.OrganizationNotFound;
        }

        ApplicationUser? user = await FindUserAsync(issuer, subject, cancellationToken);
        if (user is null)
        {
            return DeploymentCommandStatus.UserNotFound;
        }

        if (!await _dbContext.Organizations.AnyAsync(organization => organization.Id == organizationId, cancellationToken))
        {
            return DeploymentCommandStatus.OrganizationNotFound;
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await _authorization.AcquireExclusiveAsync(
                _dbContext.Database.GetDbConnection(),
                transaction.GetDbTransaction(),
                organizationId,
                cancellationToken);
        }
        catch (OrganizationAuthorizationException exception) when (exception.Failure == OrganizationAuthorizationFailure.LockNotAcquired)
        {
            return DeploymentCommandStatus.LockNotAcquired;
        }

        await _sync.AfterAuthoritativeLockAsync(cancellationToken);
        LockedMembership? locked = await LockMembershipAsync(transaction.GetDbTransaction(), user.Id, organizationId, cancellationToken);
        if (locked is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return DeploymentCommandStatus.MembershipNotFound;
        }

        bool already = locked.Status == OrganizationMembershipStatus.Revoked;
        if (!already)
        {
            OrganizationMembership membership = await _dbContext.OrganizationMemberships
                .SingleAsync(candidate => candidate.Id == locked.Id, cancellationToken);
            membership.Revoke(_timeProvider.GetUtcNow());
        }

        AddEvent(
            OperationalSecurityOperation.RevokeMembership,
            already ? OperationalSecurityOutcome.AlreadyApplied : OperationalSecurityOutcome.Applied,
            user.Id,
            organizationId,
            locked.Id,
            declared);
        await _sync.BeforeCommitAsync(cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return already
            ? DeploymentCommandStatus.MembershipAlreadyRevoked
            : DeploymentCommandStatus.MembershipRevoked;
    }

    public async Task<DeploymentCommandStatus> DisableOrganizationAsync(
        Guid organizationId,
        string operatorName,
        CancellationToken cancellationToken)
    {
        if (!TryOperator(operatorName, out string declared))
        {
            return DeploymentCommandStatus.InvalidOperator;
        }

        if (organizationId == Guid.Empty)
        {
            return DeploymentCommandStatus.OrganizationNotFound;
        }

        if (!await _dbContext.Organizations.AnyAsync(organization => organization.Id == organizationId, cancellationToken))
        {
            return DeploymentCommandStatus.OrganizationNotFound;
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await _authorization.AcquireExclusiveAsync(
                _dbContext.Database.GetDbConnection(),
                transaction.GetDbTransaction(),
                organizationId,
                cancellationToken);
        }
        catch (OrganizationAuthorizationException exception) when (exception.Failure == OrganizationAuthorizationFailure.LockNotAcquired)
        {
            return DeploymentCommandStatus.LockNotAcquired;
        }

        await _sync.AfterAuthoritativeLockAsync(cancellationToken);
        OrganizationStatus? status = await LockOrganizationRowAsync(transaction.GetDbTransaction(), organizationId, cancellationToken);
        if (status is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return DeploymentCommandStatus.OrganizationNotFound;
        }

        bool already = status == OrganizationStatus.Disabled;
        if (!already)
        {
            Organization organization = await _dbContext.Organizations
                .SingleAsync(candidate => candidate.Id == organizationId, cancellationToken);
            organization.Disable();
        }

        AddEvent(
            OperationalSecurityOperation.DisableOrganization,
            already ? OperationalSecurityOutcome.AlreadyApplied : OperationalSecurityOutcome.Applied,
            null,
            organizationId,
            null,
            declared);
        await _sync.BeforeCommitAsync(cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return already
            ? DeploymentCommandStatus.OrganizationAlreadyDisabled
            : DeploymentCommandStatus.OrganizationDisabled;
    }

    private void AddEvent(
        OperationalSecurityOperation operation,
        OperationalSecurityOutcome outcome,
        Guid? targetUserId,
        Guid? targetOrganizationId,
        Guid? targetMembershipId,
        string declaredOperator)
    {
        _dbContext.OperationalSecurityEvents.Add(OperationalSecurityEvent.Record(
            Guid.NewGuid(),
            operation,
            outcome,
            targetUserId,
            targetOrganizationId,
            targetMembershipId,
            declaredOperator,
            _timeProvider.GetUtcNow()));
    }

    private async Task<LockedUser?> LockUserAsync(
        DbTransaction transaction,
        byte[] identityKey,
        CancellationToken cancellationToken)
    {
        DbConnection connection = _dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT Id, Issuer, Subject, Status, SessionStamp
            FROM access.ApplicationUsers WITH (UPDLOCK, HOLDLOCK)
            WHERE IdentityKey = @identityKey
            """;
        command.Parameters.Add(new SqlParameter("@identityKey", SqlDbType.Binary, 32) { Value = identityKey });
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new LockedUser(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            (ApplicationUserStatus)reader.GetByte(3),
            reader.GetGuid(4));
    }

    private async Task<OrganizationStatus?> LockOrganizationRowAsync(
        DbTransaction transaction,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        DbConnection connection = _dbContext.Database.GetDbConnection();
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT Status
            FROM access.Organizations WITH (UPDLOCK, HOLDLOCK)
            WHERE Id = @id
            """;
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = organizationId });
        object? status = await command.ExecuteScalarAsync(cancellationToken);
        return status is byte value ? (OrganizationStatus)value : null;
    }

    private async Task<LockedMembership?> LockMembershipAsync(
        DbTransaction transaction,
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        DbConnection connection = _dbContext.Database.GetDbConnection();
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT Id, Status
            FROM access.OrganizationMemberships WITH (UPDLOCK, HOLDLOCK)
            WHERE UserId = @userId AND OrganizationId = @organizationId
            """;
        command.Parameters.Add(new SqlParameter("@userId", SqlDbType.UniqueIdentifier) { Value = userId });
        command.Parameters.Add(new SqlParameter("@organizationId", SqlDbType.UniqueIdentifier) { Value = organizationId });
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new LockedMembership(reader.GetGuid(0), (OrganizationMembershipStatus)reader.GetByte(1));
    }

    private async Task<ApplicationUser?> FindUserAsync(
        string issuer,
        string subject,
        CancellationToken cancellationToken)
    {
        byte[] identityKey;
        try
        {
            identityKey = ExternalIdentityKey.Compute(issuer, subject);
        }
        catch (ArgumentException)
        {
            return null;
        }

        List<ApplicationUser> rows = await _dbContext.ApplicationUsers
            .AsNoTracking()
            .Where(user => user.IdentityKey == identityKey)
            .ToListAsync(cancellationToken);
        return rows.SingleOrDefault(user => ExternalIdentityKey.Same(user.Issuer, user.Subject, issuer, subject));
    }

    private static bool TryOperator(string operatorName, out string declared)
    {
        try
        {
            declared = RoleActor.DeploymentBootstrap(operatorName).OperatorName
                ?? throw new ArgumentException("Declared operator is missing.");
            return true;
        }
        catch (ArgumentException)
        {
            declared = string.Empty;
            return false;
        }
    }

    private sealed record LockedMembership(Guid Id, OrganizationMembershipStatus Status);

    private sealed record LockedUser(
        Guid Id,
        string Issuer,
        string Subject,
        ApplicationUserStatus Status,
        Guid SessionStamp);
}