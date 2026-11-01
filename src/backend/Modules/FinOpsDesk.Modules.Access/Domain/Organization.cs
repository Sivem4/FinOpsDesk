namespace FinOpsDesk.Modules.Access.Domain;

public sealed class Organization
{
    public const int DisplayNameMaxCharacters = 200;

    public Guid Id { get; private set; }

    public string DisplayName { get; private set; } = string.Empty;

    public OrganizationStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private Organization()
    {
    }

    public static Organization Create(Guid id, string displayName, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Organization id cannot be empty.", nameof(id));
        }

        if (displayName is null
            || displayName.Length == 0
            || displayName.Length > DisplayNameMaxCharacters)
        {
            throw new ArgumentException("Organization display name length is not valid.", nameof(displayName));
        }

        return new Organization
        {
            Id = id,
            DisplayName = displayName,
            Status = OrganizationStatus.Active,
            CreatedAt = createdAt,
        };
    }

    internal void Disable()
    {
        Status = OrganizationStatus.Disabled;
    }
}