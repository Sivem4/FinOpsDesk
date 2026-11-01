namespace FinOpsDesk.Modules.Access.Application;

public enum ExternalIdentityProvisionStatus
{
    Created = 1,
    Reused = 2,
    Disabled = 3,
}

public sealed class ExternalIdentityProvision
{
    private ExternalIdentityProvision(ExternalIdentityProvisionStatus status, Guid userId)
    {
        Status = status;
        UserId = userId;
    }

    public ExternalIdentityProvisionStatus Status { get; }

    public Guid UserId { get; }

    public bool Allowed =>
        Status is ExternalIdentityProvisionStatus.Created or ExternalIdentityProvisionStatus.Reused;

    public static ExternalIdentityProvision Created(Guid userId)
    {
        return new ExternalIdentityProvision(ExternalIdentityProvisionStatus.Created, userId);
    }

    public static ExternalIdentityProvision Reused(Guid userId)
    {
        return new ExternalIdentityProvision(ExternalIdentityProvisionStatus.Reused, userId);
    }

    public static ExternalIdentityProvision Disabled(Guid userId)
    {
        return new ExternalIdentityProvision(ExternalIdentityProvisionStatus.Disabled, userId);
    }
}