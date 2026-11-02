namespace FinOpsDesk.Modules.Access.Domain;

// One application-cookie lifetime. RevokedAt is the only revocation mark.
// Natural expiry is ExpiresAt and is never rewritten by revocation.
public sealed class BrowserSession
{
    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    private BrowserSession()
    {
    }

    public static Guid CreateId()
    {
        // Guid.NewGuid uses a cryptographic RNG and returns an RFC 4122 version-4 identifier.
        return Guid.NewGuid();
    }

    public static BrowserSession Open(
        Guid id,
        Guid userId,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Session id cannot be empty.", nameof(id));
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id cannot be empty.", nameof(userId));
        }

        if (expiresAt <= createdAt)
        {
            throw new ArgumentException("Session expiry must be later than its creation.", nameof(expiresAt));
        }

        return new BrowserSession
        {
            Id = id,
            UserId = userId,
            CreatedAt = createdAt,
            ExpiresAt = expiresAt,
        };
    }
}