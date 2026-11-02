namespace FinOpsDesk.Modules.Access.Application;

public enum MembershipResolutionKind
{
    Active = 1,
    Disabled = 2,
    Missing = 3,
}

public sealed class MembershipResolution
{
    private MembershipResolution(
        MembershipResolutionKind kind,
        Guid? userId,
        Guid? sessionStamp,
        IReadOnlyList<OrganizationChoice> organizations)
    {
        Kind = kind;
        UserId = userId;
        SessionStamp = sessionStamp;
        Organizations = organizations;
    }

    public MembershipResolutionKind Kind { get; }

    public Guid? UserId { get; }

    public Guid? SessionStamp { get; }

    public IReadOnlyList<OrganizationChoice> Organizations { get; }

    public static MembershipResolution Active(
        Guid userId,
        Guid sessionStamp,
        IReadOnlyList<OrganizationChoice> organizations)
    {
        ArgumentNullException.ThrowIfNull(organizations);

        return new MembershipResolution(
            MembershipResolutionKind.Active,
            userId,
            sessionStamp,
            organizations);
    }

    public static MembershipResolution Disabled(Guid userId, Guid sessionStamp)
    {
        return new MembershipResolution(
            MembershipResolutionKind.Disabled,
            userId,
            sessionStamp,
            []);
    }

    public static MembershipResolution Missing()
    {
        return new MembershipResolution(MembershipResolutionKind.Missing, null, null, []);
    }
}