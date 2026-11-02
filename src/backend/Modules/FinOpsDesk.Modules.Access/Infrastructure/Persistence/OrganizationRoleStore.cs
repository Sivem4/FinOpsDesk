using System.Data;
using System.Data.Common;

using FinOpsDesk.Modules.Access.Application;
using FinOpsDesk.Modules.Access.Domain;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FinOpsDesk.Modules.Access.Infrastructure.Persistence;

internal sealed class OrganizationRoleStore : IOrganizationRoleStore, IOrganizationRoleReader
{
    private readonly AccessDbContext _dbContext;
    private readonly IOrganizationRoleMutationSync _sync;

    public OrganizationRoleStore(AccessDbContext dbContext, IOrganizationRoleMutationSync? sync = null)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
        _sync = sync ?? NoOrganizationRoleMutationSync.Instance;
    }

    public async Task<IReadOnlyList<OrganizationRole>> ListActiveAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        List<OrganizationRole> roles = await ActiveRoleQuery(userId, organizationId)
            .ToListAsync(cancellationToken);
        OrganizationRole[] defined = new OrganizationRole[roles.Count];
        for (int index = 0; index < roles.Count; index++)
        {
            defined[index] = OrganizationRolePermissions.Require((byte)roles[index]);
        }

        return defined;
    }

    public async Task<OrganizationRoleMutationStatus> GrantAsync(
        Guid membershipId,
        OrganizationRole role,
        DateTimeOffset grantedAt,
        RoleActor grantedBy,
        CancellationToken cancellationToken)
    {
        if (!OrganizationRolePermissions.IsDefined(role))
        {
            return OrganizationRoleMutationStatus.InvalidRole;
        }

        Guid? organizationId = await PeekOrganizationIdAsync(membershipId, cancellationToken);
        await using var transaction = await BeginAsync(cancellationToken);
        if (role == OrganizationRole.OrganizationAdmin && organizationId is Guid organization)
        {
            await LockActiveAdministratorsAsync(organization, cancellationToken);
        }

        LockedMembership? membership = await LockMembershipByIdAsync(membershipId, cancellationToken);
        OrganizationRoleMutationStatus? blocked = TargetStatus(membership, expectedOrganizationId: null);
        if (blocked is not null)
        {
            return await RefuseAsync(transaction, blocked.Value, cancellationToken);
        }

        return await InsertGrantAsync(transaction, membershipId, role, grantedAt, grantedBy, cancellationToken);
    }

    public async Task<OrganizationRoleMutationStatus> RevokeAsync(
        Guid membershipId,
        OrganizationRole role,
        DateTimeOffset revokedAt,
        RoleActor revokedBy,
        CancellationToken cancellationToken)
    {
        if (!OrganizationRolePermissions.IsDefined(role))
        {
            return OrganizationRoleMutationStatus.InvalidRole;
        }

        Guid? organizationId = await PeekOrganizationIdAsync(membershipId, cancellationToken);
        await using var transaction = await BeginAsync(cancellationToken);
        IReadOnlyList<LockedAdministrator> administrators = [];
        if (role == OrganizationRole.OrganizationAdmin && organizationId is Guid organization)
        {
            administrators = await LockActiveAdministratorsAsync(organization, cancellationToken);
        }

        LockedMembership? membership = await LockMembershipByIdAsync(membershipId, cancellationToken);
        OrganizationRoleMutationStatus? blocked = TargetStatus(membership, expectedOrganizationId: null);
        if (blocked is not null)
        {
            return await RefuseAsync(transaction, blocked.Value, cancellationToken);
        }

        LockedAssignment? active = await LockActiveAssignmentAsync(membershipId, role, cancellationToken);
        if (active is null)
        {
            return await RefuseAsync(transaction, OrganizationRoleMutationStatus.NotActive, cancellationToken);
        }

        if (role == OrganizationRole.OrganizationAdmin && administrators.Count <= 1)
        {
            return await RefuseAsync(transaction, OrganizationRoleMutationStatus.LastOrganizationAdmin, cancellationToken);
        }

        return await CompleteRevokeAsync(transaction, active, revokedAt, revokedBy, cancellationToken);
    }

    public async Task<OrganizationRoleMutationStatus> GrantForAdministratorAsync(
        Guid actorUserId,
        Guid organizationId,
        Guid membershipId,
        OrganizationRole role,
        DateTimeOffset grantedAt,
        CancellationToken cancellationToken)
    {
        if (!OrganizationRolePermissions.IsDefined(role))
        {
            return OrganizationRoleMutationStatus.InvalidRole;
        }

        if (actorUserId == Guid.Empty || organizationId == Guid.Empty || membershipId == Guid.Empty)
        {
            return OrganizationRoleMutationStatus.Forbidden;
        }

        await _sync.BeforeAuthoritativeLockAsync(cancellationToken);
        await using var transaction = await BeginAsync(cancellationToken);
        // Administrator rows first, then membership rows. Revoking OrganizationAdmin uses the same order.
        IReadOnlyList<LockedAdministrator> administrators = await LockActiveAdministratorsAsync(
            organizationId,
            cancellationToken);
        await _sync.AfterAuthoritativeLockAsync(cancellationToken);
        LockedMembership? actor = await LockActorMembershipAsync(actorUserId, organizationId, cancellationToken);
        LockedMembership? target = await LockMembershipByIdAsync(membershipId, cancellationToken);
        OrganizationRoleMutationStatus? blocked = TargetStatus(target, organizationId);
        if (blocked is not null)
        {
            return await RefuseAsync(transaction, blocked.Value, cancellationToken);
        }

        if (!HasRolesManage(actor, administrators))
        {
            return await RefuseAsync(transaction, OrganizationRoleMutationStatus.Forbidden, cancellationToken);
        }

        return await InsertGrantAsync(
            transaction,
            membershipId,
            role,
            grantedAt,
            RoleActor.ApplicationUser(actorUserId),
            cancellationToken);
    }

    public async Task<OrganizationRoleMutationStatus> RevokeForAdministratorAsync(
        Guid actorUserId,
        Guid organizationId,
        Guid assignmentId,
        byte[] expectedRowVersion,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken)
    {
        if (actorUserId == Guid.Empty || organizationId == Guid.Empty)
        {
            return OrganizationRoleMutationStatus.Forbidden;
        }

        if (assignmentId == Guid.Empty)
        {
            return OrganizationRoleMutationStatus.NotActive;
        }

        if (expectedRowVersion is not { Length: 8 })
        {
            return OrganizationRoleMutationStatus.ConcurrencyConflict;
        }

        await _sync.BeforeAuthoritativeLockAsync(cancellationToken);
        await using var transaction = await BeginAsync(cancellationToken);
        IReadOnlyList<LockedAdministrator> administrators = await LockActiveAdministratorsAsync(
            organizationId,
            cancellationToken);
        await _sync.AfterAuthoritativeLockAsync(cancellationToken);
        LockedAssignment? assignment = await LockAssignmentAsync(assignmentId, cancellationToken);
        if (assignment is null)
        {
            return await RefuseAsync(transaction, OrganizationRoleMutationStatus.NotActive, cancellationToken);
        }

        LockedMembership? target = await LockMembershipByIdAsync(assignment.MembershipId, cancellationToken);
        OrganizationRoleMutationStatus? blocked = TargetStatus(target, organizationId);
        if (blocked is not null)
        {
            return await RefuseAsync(transaction, blocked.Value, cancellationToken);
        }

        LockedMembership? actor = await LockActorMembershipAsync(actorUserId, organizationId, cancellationToken);
        if (!HasRolesManage(actor, administrators))
        {
            return await RefuseAsync(transaction, OrganizationRoleMutationStatus.Forbidden, cancellationToken);
        }

        if (!assignment.Active)
        {
            return await RefuseAsync(transaction, OrganizationRoleMutationStatus.NotActive, cancellationToken);
        }

        if (!assignment.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            return await RefuseAsync(transaction, OrganizationRoleMutationStatus.ConcurrencyConflict, cancellationToken);
        }

        if (assignment.Role == OrganizationRole.OrganizationAdmin && administrators.Count <= 1)
        {
            return await RefuseAsync(transaction, OrganizationRoleMutationStatus.LastOrganizationAdmin, cancellationToken);
        }

        return await CompleteRevokeAsync(
            transaction,
            assignment,
            revokedAt,
            RoleActor.ApplicationUser(actorUserId),
            cancellationToken);
    }

    private async Task<OrganizationRoleMutationStatus> InsertGrantAsync(
        IDbContextTransaction transaction,
        Guid membershipId,
        OrganizationRole role,
        DateTimeOffset grantedAt,
        RoleActor grantedBy,
        CancellationToken cancellationToken)
    {
        if (await ActiveAssignmentExistsAsync(membershipId, role, cancellationToken))
        {
            return await RefuseAsync(transaction, OrganizationRoleMutationStatus.AlreadyActive, cancellationToken);
        }

        _dbContext.OrganizationRoleAssignments.Add(OrganizationRoleAssignment.Create(
            Guid.NewGuid(),
            membershipId,
            role,
            grantedAt,
            grantedBy));
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return OrganizationRoleMutationStatus.Granted;
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            _dbContext.ChangeTracker.Clear();
            if (await ActiveAssignmentExistsAsync(membershipId, role, cancellationToken))
            {
                return OrganizationRoleMutationStatus.AlreadyActive;
            }

            throw;
        }
    }

    private async Task<OrganizationRoleMutationStatus> CompleteRevokeAsync(
        IDbContextTransaction transaction,
        LockedAssignment assignment,
        DateTimeOffset revokedAt,
        RoleActor revokedBy,
        CancellationToken cancellationToken)
    {
        DateTimeOffset storedRevokedAt = OrganizationRoleAssignment.ResolveRevokedAt(assignment.GrantedAt, revokedAt);
        int updated = await ExecuteRevokeAsync(assignment.Id, assignment.RowVersion, storedRevokedAt, revokedBy, cancellationToken);
        if (updated == 1)
        {
            await transaction.CommitAsync(cancellationToken);
            return OrganizationRoleMutationStatus.Revoked;
        }

        OrganizationRoleMutationStatus status = await ClassifyExactMissAsync(assignment.Id, cancellationToken);
        return await RefuseAsync(transaction, status, cancellationToken);
    }

    private async Task<int> ExecuteRevokeAsync(
        Guid assignmentId,
        byte[] rowVersion,
        DateTimeOffset revokedAt,
        RoleActor revokedBy,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = await CreateCommandAsync(cancellationToken);
        command.CommandText =
            """
            UPDATE access.OrganizationRoleAssignments
            SET RevokedAt = @revokedAt,
                RevokeActorKind = @kind,
                RevokeActorUserId = @userId,
                RevokeOperatorName = @operatorName
            WHERE Id = @id
              AND RevokedAt IS NULL
              AND RowVersion = @version
            """;
        command.Parameters.Add(new SqlParameter("@revokedAt", SqlDbType.DateTimeOffset) { Value = revokedAt });
        command.Parameters.Add(new SqlParameter("@kind", SqlDbType.TinyInt) { Value = (byte)revokedBy.Kind });
        command.Parameters.Add(new SqlParameter("@userId", SqlDbType.UniqueIdentifier)
        {
            Value = revokedBy.UserId.HasValue ? revokedBy.UserId.Value : DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@operatorName", SqlDbType.NVarChar, RoleActor.OperatorNameMaxCharacters)
        {
            Value = revokedBy.OperatorName is null ? DBNull.Value : revokedBy.OperatorName,
        });
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = assignmentId });
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.VarBinary, 8) { Value = rowVersion });
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<OrganizationRoleMutationStatus> ClassifyExactMissAsync(
        Guid assignmentId,
        CancellationToken cancellationToken)
    {
        bool? revoked = await _dbContext.OrganizationRoleAssignments
            .AsNoTracking()
            .Where(candidate => candidate.Id == assignmentId)
            .Select(candidate => (bool?)(candidate.RevokedAt != null))
            .SingleOrDefaultAsync(cancellationToken);
        if (revoked is null or true)
        {
            return OrganizationRoleMutationStatus.NotActive;
        }

        return OrganizationRoleMutationStatus.ConcurrencyConflict;
    }

    private async Task<Guid?> PeekOrganizationIdAsync(Guid membershipId, CancellationToken cancellationToken)
    {
        return await _dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(membership => membership.Id == membershipId)
            .Select(membership => (Guid?)membership.OrganizationId)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private Task<IDbContextTransaction> BeginAsync(CancellationToken cancellationToken)
    {
        return _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
    }

    private static async Task<OrganizationRoleMutationStatus> RefuseAsync(
        IDbContextTransaction transaction,
        OrganizationRoleMutationStatus status,
        CancellationToken cancellationToken)
    {
        await transaction.RollbackAsync(cancellationToken);
        return status;
    }

    private static bool HasRolesManage(LockedMembership? actor, IReadOnlyList<LockedAdministrator> administrators)
    {
        return actor is not null
            && actor.MembershipActive
            && actor.UserActive
            && actor.OrganizationActive
            && administrators.Any(administrator => administrator.MembershipId == actor.Id);
    }

    private static OrganizationRoleMutationStatus? TargetStatus(
        LockedMembership? membership,
        Guid? expectedOrganizationId)
    {
        if (membership is null)
        {
            return OrganizationRoleMutationStatus.MissingMembership;
        }

        if (expectedOrganizationId is Guid organizationId && membership.OrganizationId != organizationId)
        {
            return OrganizationRoleMutationStatus.OutsideOrganization;
        }

        if (!membership.MembershipActive)
        {
            return OrganizationRoleMutationStatus.InactiveMembership;
        }

        if (!membership.UserActive)
        {
            return OrganizationRoleMutationStatus.DisabledUser;
        }

        if (!membership.OrganizationActive)
        {
            return OrganizationRoleMutationStatus.DisabledOrganization;
        }

        return null;
    }

    private Task<bool> ActiveAssignmentExistsAsync(
        Guid membershipId,
        OrganizationRole role,
        CancellationToken cancellationToken)
    {
        return _dbContext.OrganizationRoleAssignments
            .AsNoTracking()
            .AnyAsync(
                assignment => assignment.MembershipId == membershipId
                    && assignment.Role == role
                    && assignment.RevokedAt == null,
                cancellationToken);
    }

    private IQueryable<OrganizationRole> ActiveRoleQuery(Guid userId, Guid organizationId)
    {
        return
            from assignment in _dbContext.OrganizationRoleAssignments.AsNoTracking()
            join membership in _dbContext.OrganizationMemberships.AsNoTracking()
                on assignment.MembershipId equals membership.Id
            join user in _dbContext.ApplicationUsers.AsNoTracking()
                on membership.UserId equals user.Id
            join organization in _dbContext.Organizations.AsNoTracking()
                on membership.OrganizationId equals organization.Id
            where assignment.RevokedAt == null
                && membership.UserId == userId
                && membership.OrganizationId == organizationId
                && membership.Status == OrganizationMembershipStatus.Active
                && user.Status == ApplicationUserStatus.Active
                && organization.Status == OrganizationStatus.Active
            select assignment.Role;
    }

    private async Task<IReadOnlyList<LockedAdministrator>> LockActiveAdministratorsAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = await CreateCommandAsync(cancellationToken);
        command.CommandText =
            """
            SELECT a.Id, a.MembershipId
            FROM access.OrganizationRoleAssignments AS a WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN access.OrganizationMemberships AS m ON m.Id = a.MembershipId
            WHERE m.OrganizationId = @organizationId
              AND a.Role = @role
              AND a.RevokedAt IS NULL
            ORDER BY a.Id
            """;
        command.Parameters.Add(new SqlParameter("@organizationId", SqlDbType.UniqueIdentifier) { Value = organizationId });
        command.Parameters.Add(new SqlParameter("@role", SqlDbType.TinyInt) { Value = (byte)OrganizationRole.OrganizationAdmin });
        List<LockedAdministrator> administrators = [];
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            administrators.Add(new LockedAdministrator(reader.GetGuid(0), reader.GetGuid(1)));
        }

        return administrators;
    }

    private async Task<LockedMembership?> LockMembershipByIdAsync(Guid membershipId, CancellationToken cancellationToken)
    {
        await using DbCommand command = await CreateCommandAsync(cancellationToken);
        command.CommandText = MembershipLockSql + " WHERE m.Id = @id";
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = membershipId });
        return await ReadMembershipAsync(command, cancellationToken);
    }

    private async Task<LockedMembership?> LockActorMembershipAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = await CreateCommandAsync(cancellationToken);
        command.CommandText = MembershipLockSql + " WHERE m.UserId = @userId AND m.OrganizationId = @organizationId";
        command.Parameters.Add(new SqlParameter("@userId", SqlDbType.UniqueIdentifier) { Value = userId });
        command.Parameters.Add(new SqlParameter("@organizationId", SqlDbType.UniqueIdentifier) { Value = organizationId });
        return await ReadMembershipAsync(command, cancellationToken);
    }

    private async Task<LockedAssignment?> LockAssignmentAsync(Guid assignmentId, CancellationToken cancellationToken)
    {
        await using DbCommand command = await CreateCommandAsync(cancellationToken);
        command.CommandText = AssignmentLockSql + " WHERE a.Id = @id";
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = assignmentId });
        return await ReadAssignmentAsync(command, cancellationToken);
    }

    private async Task<LockedAssignment?> LockActiveAssignmentAsync(
        Guid membershipId,
        OrganizationRole role,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = await CreateCommandAsync(cancellationToken);
        command.CommandText = AssignmentLockSql + " WHERE a.MembershipId = @membershipId AND a.Role = @role AND a.RevokedAt IS NULL";
        command.Parameters.Add(new SqlParameter("@membershipId", SqlDbType.UniqueIdentifier) { Value = membershipId });
        command.Parameters.Add(new SqlParameter("@role", SqlDbType.TinyInt) { Value = (byte)role });
        return await ReadAssignmentAsync(command, cancellationToken);
    }

    private static async Task<LockedMembership?> ReadMembershipAsync(DbCommand command, CancellationToken cancellationToken)
    {
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new LockedMembership(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetByte(3) == (byte)OrganizationMembershipStatus.Active,
            reader.GetByte(4) == (byte)ApplicationUserStatus.Active,
            reader.GetByte(5) == (byte)OrganizationStatus.Active);
    }

    private static async Task<LockedAssignment?> ReadAssignmentAsync(DbCommand command, CancellationToken cancellationToken)
    {
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        byte[] version = ((byte[])reader.GetValue(5)).ToArray();
        return new LockedAssignment(
            reader.GetGuid(0),
            reader.GetGuid(1),
            OrganizationRolePermissions.Require(reader.GetByte(2)),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.IsDBNull(4),
            version);
    }

    private async Task<DbCommand> CreateCommandAsync(CancellationToken cancellationToken)
    {
        DbConnection connection = _dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        DbCommand command = connection.CreateCommand();
        command.Transaction = _dbContext.Database.CurrentTransaction?.GetDbTransaction();
        return command;
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

    private const string MembershipLockSql =
        """
        SELECT m.Id, m.OrganizationId, m.UserId, m.Status, u.Status, o.Status
        FROM access.OrganizationMemberships AS m WITH (UPDLOCK, HOLDLOCK)
        INNER JOIN access.ApplicationUsers AS u ON u.Id = m.UserId
        INNER JOIN access.Organizations AS o ON o.Id = m.OrganizationId
        """;

    private const string AssignmentLockSql =
        """
        SELECT a.Id, a.MembershipId, a.Role, a.GrantedAt, a.RevokedAt, a.RowVersion
        FROM access.OrganizationRoleAssignments AS a WITH (UPDLOCK, HOLDLOCK)
        """;

    private sealed record LockedAdministrator(Guid Id, Guid MembershipId);

    private sealed record LockedMembership(
        Guid Id,
        Guid OrganizationId,
        Guid UserId,
        bool MembershipActive,
        bool UserActive,
        bool OrganizationActive);

    private sealed record LockedAssignment(
        Guid Id,
        Guid MembershipId,
        OrganizationRole Role,
        DateTimeOffset GrantedAt,
        bool Active,
        byte[] RowVersion);
}