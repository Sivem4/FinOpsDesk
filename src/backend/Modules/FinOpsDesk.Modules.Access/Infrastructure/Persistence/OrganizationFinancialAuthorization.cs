using System.Data;
using System.Data.Common;

using FinOpsDesk.Modules.Access.Application;
using FinOpsDesk.Modules.Access.Domain;

using Microsoft.Data.SqlClient;

namespace FinOpsDesk.Modules.Access.Infrastructure.Persistence;

internal sealed class OrganizationFinancialAuthorization : IOrganizationFinancialAuthorization
{
    private readonly SqlApplicationLockPolicy _policy;
    private readonly TimeProvider _timeProvider;

    public OrganizationFinancialAuthorization(
        SqlApplicationLockPolicy? policy = null,
        TimeProvider? timeProvider = null)
    {
        _policy = policy ?? new SqlApplicationLockPolicy();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task AuthorizeSharedAsync(
        DbConnection connection,
        DbTransaction transaction,
        FinancialCommitIdentity identity,
        string permission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        await AcquireOrThrowAsync(connection, transaction, identity.OrganizationId, "Shared", cancellationToken);

        try
        {
            await AuthorizeRowsAsync(connection, transaction, identity, permission, cancellationToken);
        }
        catch (OrganizationAuthorizationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await RollbackQuietlyAsync(transaction, cancellationToken);
            throw new OrganizationAuthorizationException(OrganizationAuthorizationFailure.Unavailable, 0);
        }
    }

    public Task AcquireExclusiveAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        return AcquireOrThrowAsync(connection, transaction, organizationId, "Exclusive", cancellationToken);
    }

    private async Task AcquireOrThrowAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid organizationId,
        string mode,
        CancellationToken cancellationToken)
    {
        int code;
        try
        {
            code = await SqlApplicationLock.AcquireAsync(
                connection,
                transaction,
                organizationId,
                mode,
                _policy,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await RollbackQuietlyAsync(transaction, cancellationToken);
            throw new OrganizationAuthorizationException(OrganizationAuthorizationFailure.Unavailable, 0);
        }

        if (SqlApplicationLock.IsAcquired(code))
        {
            return;
        }

        await RollbackQuietlyAsync(transaction, cancellationToken);
        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        throw new OrganizationAuthorizationException(OrganizationAuthorizationFailure.LockNotAcquired, code);
    }

    private async Task AuthorizeRowsAsync(
        DbConnection connection,
        DbTransaction transaction,
        FinancialCommitIdentity identity,
        string permission,
        CancellationToken cancellationToken)
    {
        byte[] identityKey;
        try
        {
            identityKey = ExternalIdentityKey.Compute(identity.Issuer, identity.Subject);
        }
        catch (ArgumentException)
        {
            await RollbackQuietlyAsync(transaction, cancellationToken);
            throw new OrganizationAuthorizationException(OrganizationAuthorizationFailure.Unauthenticated, 0);
        }

        UserRow? user = await ReadUserAsync(connection, transaction, identityKey, cancellationToken);
        if (user is null)
        {
            await RollbackQuietlyAsync(transaction, cancellationToken);
            throw new OrganizationAuthorizationException(OrganizationAuthorizationFailure.Unauthenticated, 0);
        }

        if (!ExternalIdentityKey.Same(user.Issuer, user.Subject, identity.Issuer, identity.Subject))
        {
            await RollbackQuietlyAsync(transaction, cancellationToken);
            throw new OrganizationAuthorizationException(OrganizationAuthorizationFailure.Unavailable, 0);
        }

        if (user.Status != ApplicationUserStatus.Active
            || user.SessionStamp == Guid.Empty
            || user.SessionStamp != identity.SessionStamp)
        {
            await RollbackQuietlyAsync(transaction, cancellationToken);
            throw new OrganizationAuthorizationException(OrganizationAuthorizationFailure.Unauthenticated, 0);
        }

        SessionRow? session = await ReadSessionAsync(connection, transaction, identity.BrowserSessionId, cancellationToken);
        // The eight-hour absolute expiry is compared only after the session row
        // is locked. A clock reading taken before sp_getapplock would accept a
        // session that expired while this transaction waited.
        DateTimeOffset sessionValidatedAt = _timeProvider.GetUtcNow();
        if (session is null
            || session.UserId != user.Id
            || session.RevokedAt is not null
            || session.ExpiresAt <= sessionValidatedAt)
        {
            await RollbackQuietlyAsync(transaction, cancellationToken);
            throw new OrganizationAuthorizationException(OrganizationAuthorizationFailure.Unauthenticated, 0);
        }

        MembershipRow? membership = await ReadMembershipAsync(
            connection,
            transaction,
            user.Id,
            identity.OrganizationId,
            cancellationToken);
        if (membership is null || membership.Status != OrganizationMembershipStatus.Active)
        {
            await RollbackQuietlyAsync(transaction, cancellationToken);
            throw new OrganizationAuthorizationException(OrganizationAuthorizationFailure.Forbidden, 0);
        }

        OrganizationStatus? organization = await ReadOrganizationAsync(
            connection,
            transaction,
            identity.OrganizationId,
            cancellationToken);
        if (organization is not OrganizationStatus.Active)
        {
            await RollbackQuietlyAsync(transaction, cancellationToken);
            throw new OrganizationAuthorizationException(OrganizationAuthorizationFailure.Forbidden, 0);
        }

        IReadOnlyList<byte> roles = await ReadRolesAsync(connection, transaction, membership.Id, cancellationToken);
        List<OrganizationRole> defined = new(roles.Count);
        foreach (byte role in roles)
        {
            if (role is < 1 or > 4)
            {
                await RollbackQuietlyAsync(transaction, cancellationToken);
                throw new OrganizationAuthorizationException(OrganizationAuthorizationFailure.Unavailable, 0);
            }

            defined.Add(OrganizationRolePermissions.Require(role));
        }

        if (!OrganizationRolePermissions.Union(defined).Any(candidate => candidate.Value == permission))
        {
            await RollbackQuietlyAsync(transaction, cancellationToken);
            throw new OrganizationAuthorizationException(OrganizationAuthorizationFailure.Forbidden, 0);
        }
    }

    private static async Task<UserRow?> ReadUserAsync(
        DbConnection connection,
        DbTransaction transaction,
        byte[] identityKey,
        CancellationToken cancellationToken)
    {
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

        return new UserRow(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            (ApplicationUserStatus)reader.GetByte(3),
            reader.GetGuid(4));
    }

    private static async Task<SessionRow?> ReadSessionAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT UserId, ExpiresAt, RevokedAt
            FROM access.BrowserSessions WITH (UPDLOCK, HOLDLOCK)
            WHERE Id = @id
            """;
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = sessionId });
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SessionRow(
            reader.GetGuid(0),
            reader.GetFieldValue<DateTimeOffset>(1),
            reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2));
    }

    private static async Task<MembershipRow?> ReadMembershipAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
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

        return new MembershipRow(reader.GetGuid(0), (OrganizationMembershipStatus)reader.GetByte(1));
    }

    private static async Task<OrganizationStatus?> ReadOrganizationAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT Status
            FROM access.Organizations WITH (UPDLOCK, HOLDLOCK)
            WHERE Id = @id
            """;
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = organizationId });
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (OrganizationStatus)reader.GetByte(0);
    }

    private static async Task<IReadOnlyList<byte>> ReadRolesAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid membershipId,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT Role
            FROM access.OrganizationRoleAssignments WITH (UPDLOCK, HOLDLOCK)
            WHERE MembershipId = @membershipId AND RevokedAt IS NULL
            ORDER BY Role
            """;
        command.Parameters.Add(new SqlParameter("@membershipId", SqlDbType.UniqueIdentifier) { Value = membershipId });
        List<byte> roles = [];
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            roles.Add(reader.GetByte(0));
        }

        return roles;
    }

    private static async Task RollbackQuietlyAsync(DbTransaction transaction, CancellationToken cancellationToken)
    {
        try
        {
            await transaction.RollbackAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed record UserRow(
        Guid Id,
        string Issuer,
        string Subject,
        ApplicationUserStatus Status,
        Guid SessionStamp);

    private sealed record SessionRow(Guid UserId, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt);

    private sealed record MembershipRow(Guid Id, OrganizationMembershipStatus Status);
}