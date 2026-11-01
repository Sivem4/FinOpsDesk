namespace FinOpsDesk.Modules.Access.Domain;

public sealed class ApplicationUser
{
    public const int IssuerMaxCharacters = 500;

    public const int SubjectMaxCharacters = 256;

    public const int DisplayNameMaxCharacters = 256;

    public Guid Id { get; private set; }

    public byte[] IdentityKey { get; private set; } = [];

    public string Issuer { get; private set; } = string.Empty;

    public string Subject { get; private set; } = string.Empty;

    public string? DisplayName { get; private set; }

    public ApplicationUserStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private ApplicationUser()
    {
    }

    public static ApplicationUser Create(
        Guid id,
        string issuer,
        string subject,
        string? displayName,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("User id cannot be empty.", nameof(id));
        }

        return new ApplicationUser
        {
            Id = id,
            IdentityKey = ExternalIdentityKey.Compute(issuer, subject),
            Issuer = issuer,
            Subject = subject,
            DisplayName = AcceptDisplayName(displayName),
            Status = ApplicationUserStatus.Active,
            CreatedAt = createdAt,
        };
    }

    internal void Disable()
    {
        Status = ApplicationUserStatus.Disabled;
    }

    internal void Restore()
    {
        Status = ApplicationUserStatus.Active;
    }

    private static string? AcceptDisplayName(string? displayName)
    {
        if (displayName is null || displayName.Length == 0 || displayName.Length > DisplayNameMaxCharacters)
        {
            return null;
        }

        return displayName;
    }
}