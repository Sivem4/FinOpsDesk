namespace FinOpsDesk.Modules.Access.Domain;

// One grant of one role on one membership. RevokedAt is the only revocation mark.
// A later grant is a new row. Revocation never clears this row.
public sealed class OrganizationRoleAssignment
{
    public Guid Id { get; private set; }

    public Guid MembershipId { get; private set; }

    public OrganizationRole Role { get; private set; }

    public DateTimeOffset GrantedAt { get; private set; }

    public RoleActorKind GrantActorKind { get; private set; }

    public Guid? GrantActorUserId { get; private set; }

    public string? GrantOperatorName { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public RoleActorKind? RevokeActorKind { get; private set; }

    public Guid? RevokeActorUserId { get; private set; }

    public string? RevokeOperatorName { get; private set; }

    private OrganizationRoleAssignment()
    {
    }

    public static OrganizationRoleAssignment Create(
        Guid id,
        Guid membershipId,
        OrganizationRole role,
        DateTimeOffset grantedAt,
        RoleActor grantedBy)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Assignment id cannot be empty.", nameof(id));
        }

        if (membershipId == Guid.Empty)
        {
            throw new ArgumentException("Membership id cannot be empty.", nameof(membershipId));
        }

        if (!OrganizationRolePermissions.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "Organization role is not defined.");
        }

        return new OrganizationRoleAssignment
        {
            Id = id,
            MembershipId = membershipId,
            Role = role,
            GrantedAt = grantedAt.ToUniversalTime(),
            GrantActorKind = grantedBy.Kind,
            GrantActorUserId = grantedBy.UserId,
            GrantOperatorName = grantedBy.OperatorName,
        };
    }

    // datetimeoffset stores 100-nanosecond ticks. An equal clock value cannot
    // satisfy RevokedAt > GrantedAt, so that single instant moves forward one tick.
    // An earlier clock value is rejected.
    public static DateTimeOffset ResolveRevokedAt(DateTimeOffset grantedAt, DateTimeOffset revokedAt)
    {
        DateTimeOffset granted = grantedAt.ToUniversalTime();
        DateTimeOffset revoked = revokedAt.ToUniversalTime();
        if (revoked < granted)
        {
            throw new ArgumentException("Revocation is earlier than the grant.", nameof(revokedAt));
        }

        if (revoked == granted)
        {
            return granted.AddTicks(1);
        }

        return revoked;
    }

    public void Revoke(DateTimeOffset revokedAt, RoleActor revokedBy)
    {
        if (RevokedAt is not null)
        {
            throw new InvalidOperationException("A revoked role assignment cannot be revoked again.");
        }

        RevokedAt = ResolveRevokedAt(GrantedAt, revokedAt);
        RevokeActorKind = revokedBy.Kind;
        RevokeActorUserId = revokedBy.UserId;
        RevokeOperatorName = revokedBy.OperatorName;
    }
}