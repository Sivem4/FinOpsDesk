namespace FinOpsDesk.Modules.Access.Application;

public interface IBrowserSessionStore
{
    Task<Guid> CreateAsync(
        Guid userId,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken);

    Task<BrowserSessionState?> FindAsync(Guid sessionId, CancellationToken cancellationToken);

    Task<BrowserSessionRevocation> RevokeAsync(
        Guid sessionId,
        Guid userId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken);
}

public sealed record BrowserSessionState(
    Guid Id,
    Guid UserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt);

public enum BrowserSessionRevocation
{
    Revoked = 1,
    AlreadyRevoked = 2,
    Missing = 3,
    UserMismatch = 4,
}