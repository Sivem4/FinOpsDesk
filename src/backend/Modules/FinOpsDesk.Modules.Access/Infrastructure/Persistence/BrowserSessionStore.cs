using System.Data;
using System.Data.Common;

using FinOpsDesk.Modules.Access.Application;
using FinOpsDesk.Modules.Access.Domain;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FinOpsDesk.Modules.Access.Infrastructure.Persistence;

internal sealed class BrowserSessionStore : IBrowserSessionStore
{
    private readonly AccessDbContext _dbContext;

    public BrowserSessionStore(AccessDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    public async Task<Guid> CreateAsync(
        Guid userId,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        BrowserSession session = BrowserSession.Open(BrowserSession.CreateId(), userId, createdAt, expiresAt);
        _dbContext.BrowserSessions.Add(session);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return session.Id;
    }

    public async Task<BrowserSessionState?> FindAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        return await _dbContext.BrowserSessions
            .AsNoTracking()
            .Where(session => session.Id == sessionId)
            .Select(session => new BrowserSessionState(
                session.Id,
                session.UserId,
                session.CreatedAt,
                session.ExpiresAt,
                session.RevokedAt))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<BrowserSessionRevocation> RevokeAsync(
        Guid sessionId,
        Guid userId,
        DateTimeOffset revokedAt,
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
            SELECT UserId, RevokedAt
            FROM access.BrowserSessions WITH (UPDLOCK, HOLDLOCK)
            WHERE Id = @id
            """;
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = sessionId });
        Guid lockedUser = Guid.Empty;
        bool found = false;
        bool alreadyRevoked = false;
        await using (DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                found = true;
                lockedUser = reader.GetGuid(0);
                alreadyRevoked = !reader.IsDBNull(1);
            }
        }

        if (!found)
        {
            await transaction.RollbackAsync(cancellationToken);
            return BrowserSessionRevocation.Missing;
        }

        if (lockedUser != userId)
        {
            await transaction.RollbackAsync(cancellationToken);
            return BrowserSessionRevocation.UserMismatch;
        }

        if (alreadyRevoked)
        {
            await transaction.RollbackAsync(cancellationToken);
            return BrowserSessionRevocation.AlreadyRevoked;
        }

        await using DbCommand update = connection.CreateCommand();
        update.Transaction = transaction.GetDbTransaction();
        update.CommandText =
            """
            UPDATE access.BrowserSessions
            SET RevokedAt = @revokedAt
            WHERE Id = @id AND UserId = @userId AND RevokedAt IS NULL
            """;
        update.Parameters.Add(new SqlParameter("@revokedAt", SqlDbType.DateTimeOffset) { Value = revokedAt });
        update.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = sessionId });
        update.Parameters.Add(new SqlParameter("@userId", SqlDbType.UniqueIdentifier) { Value = userId });
        int updated = await update.ExecuteNonQueryAsync(cancellationToken);
        if (updated == 1)
        {
            await transaction.CommitAsync(cancellationToken);
            return BrowserSessionRevocation.Revoked;
        }

        await transaction.RollbackAsync(cancellationToken);

        BrowserSessionState? current = await FindAsync(sessionId, cancellationToken);
        if (current is null)
        {
            return BrowserSessionRevocation.Missing;
        }

        if (current.UserId != userId)
        {
            return BrowserSessionRevocation.UserMismatch;
        }

        if (current.RevokedAt is not null)
        {
            return BrowserSessionRevocation.AlreadyRevoked;
        }

        return BrowserSessionRevocation.Missing;
    }
}