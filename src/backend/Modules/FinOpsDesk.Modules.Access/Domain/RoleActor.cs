namespace FinOpsDesk.Modules.Access.Domain;

public readonly record struct RoleActor
{
    public const int OperatorNameMaxCharacters = 128;

    private RoleActor(RoleActorKind kind, Guid? userId, string? operatorName)
    {
        Kind = kind;
        UserId = userId;
        OperatorName = operatorName;
    }

    public RoleActorKind Kind { get; }

    public Guid? UserId { get; }

    public string? OperatorName { get; }

    public static RoleActor ApplicationUser(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("Granting user id cannot be empty.", nameof(userId));
        }

        return new RoleActor(RoleActorKind.ApplicationUser, userId, null);
    }

    public static RoleActor DeploymentBootstrap(string operatorName)
    {
        if (string.IsNullOrWhiteSpace(operatorName)
            || operatorName.Trim().Length != operatorName.Length
            || operatorName.Length > OperatorNameMaxCharacters)
        {
            throw new ArgumentException(
                "Deployment operator name must be nonblank and at most 128 characters.",
                nameof(operatorName));
        }

        return new RoleActor(RoleActorKind.DeploymentBootstrap, null, operatorName);
    }
}