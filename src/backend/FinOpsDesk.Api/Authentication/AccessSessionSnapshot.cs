using FinOpsDesk.Modules.Access.Application;

namespace FinOpsDesk.Api.Authentication;

internal enum AccessSessionKind
{
    Active = 1,
    Rejected = 2,
    InvalidIdentity = 3,
    Unavailable = 4,
}

internal sealed class AccessSessionSnapshot
{
    private AccessSessionSnapshot(
        AccessSessionKind kind,
        string reason,
        string? issuer,
        string? subject,
        string? displayName,
        Guid? userId,
        Guid? sessionStamp,
        IReadOnlyList<OrganizationChoice> organizations,
        OrganizationChoice? activeOrganization)
    {
        Kind = kind;
        Reason = reason;
        Issuer = issuer;
        Subject = subject;
        DisplayName = displayName;
        UserId = userId;
        SessionStamp = sessionStamp;
        Organizations = organizations;
        ActiveOrganization = activeOrganization;
    }

    public AccessSessionKind Kind { get; }

    public string Reason { get; }

    public string? Issuer { get; }

    public string? Subject { get; }

    public string? DisplayName { get; }

    public Guid? UserId { get; }

    public Guid? SessionStamp { get; }

    public IReadOnlyList<OrganizationChoice> Organizations { get; }

    public OrganizationChoice? ActiveOrganization { get; }

    public static AccessSessionSnapshot Active(
        string issuer,
        string subject,
        string? displayName,
        Guid userId,
        Guid sessionStamp,
        IReadOnlyList<OrganizationChoice> organizations,
        OrganizationChoice? activeOrganization)
    {
        return new AccessSessionSnapshot(
            AccessSessionKind.Active,
            "active",
            issuer,
            subject,
            displayName,
            userId,
            sessionStamp,
            organizations,
            activeOrganization);
    }

    public static AccessSessionSnapshot Rejected(string reason)
    {
        return new AccessSessionSnapshot(
            AccessSessionKind.Rejected,
            reason,
            null,
            null,
            null,
            null,
            null,
            [],
            null);
    }

    public static AccessSessionSnapshot InvalidIdentity()
    {
        return new AccessSessionSnapshot(
            AccessSessionKind.InvalidIdentity,
            "invalid-identity",
            null,
            null,
            null,
            null,
            null,
            [],
            null);
    }

    public static AccessSessionSnapshot Unavailable(string reason)
    {
        return new AccessSessionSnapshot(
            AccessSessionKind.Unavailable,
            reason,
            null,
            null,
            null,
            null,
            null,
            [],
            null);
    }
}