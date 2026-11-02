namespace FinOpsDesk.Modules.Access.Application;

public enum ExternalIdentityProvisionStatus
{
    Created = 1,
    Reused = 2,
    Disabled = 3,
}

public sealed class ExternalIdentityProvision
{
    private ExternalIdentityProvision(
        ExternalIdentityProvisionStatus status,
        Guid userId,
        Guid sessionStamp)
    {
        Status = status;
        UserId = userId;
        SessionStamp = sessionStamp;
    }

    public ExternalIdentityProvisionStatus Status { get; }

    public Guid UserId { get; }

    public Guid SessionStamp { get; }

    public bool Allowed =>
        Status is ExternalIdentityProvisionStatus.Created or ExternalIdentityProvisionStatus.Reused;

    public static ExternalIdentityProvision Created(Guid userId, Guid sessionStamp)
    {
        return new ExternalIdentityProvision(
            ExternalIdentityProvisionStatus.Created,
            userId,
            sessionStamp);
    }

    public static ExternalIdentityProvision Reused(Guid userId, Guid sessionStamp)
    {
        return new ExternalIdentityProvision(
            ExternalIdentityProvisionStatus.Reused,
            userId,
            sessionStamp);
    }

    public static ExternalIdentityProvision Disabled(Guid userId, Guid sessionStamp)
    {
        return new ExternalIdentityProvision(
            ExternalIdentityProvisionStatus.Disabled,
            userId,
            sessionStamp);
    }
}