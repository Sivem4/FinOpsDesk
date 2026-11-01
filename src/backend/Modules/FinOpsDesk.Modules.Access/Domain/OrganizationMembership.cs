namespace FinOpsDesk.Modules.Access.Domain;

public sealed class OrganizationMembership
{
    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public Guid OrganizationId { get; private set; }

    public OrganizationMembershipStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    internal ApplicationUser? User { get; private set; }

    internal Organization? Organization { get; private set; }

    private OrganizationMembership()
    {
    }

    public static OrganizationMembership Grant(
        Guid id,
        Guid userId,
        Guid organizationId,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Membership id cannot be empty.", nameof(id));
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id cannot be empty.", nameof(userId));
        }

        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id cannot be empty.", nameof(organizationId));
        }

        return new OrganizationMembership
        {
            Id = id,
            UserId = userId,
            OrganizationId = organizationId,
            Status = OrganizationMembershipStatus.Active,
            CreatedAt = createdAt,
        };
    }

    public void Revoke(DateTimeOffset revokedAt)
    {
        Status = OrganizationMembershipStatus.Revoked;
        RevokedAt = revokedAt;
    }
}