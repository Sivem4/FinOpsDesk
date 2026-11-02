using FinOpsDesk.Modules.Access.Application;
using FinOpsDesk.Modules.Access.Domain;

using Microsoft.EntityFrameworkCore;

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
        int updated = await _dbContext.BrowserSessions
            .Where(session => session.Id == sessionId
                && session.UserId == userId
                && session.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(session => session.RevokedAt, revokedAt),
                cancellationToken);
        if (updated == 1)
        {
            return BrowserSessionRevocation.Revoked;
        }

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