namespace FinOpsDesk.Modules.Access.Domain;

// One append-only record of an emergency console command. The application does
// not update or delete a committed row. A database administrator can still
// change the table; this type does not claim tamper-proof storage.
public sealed class OperationalSecurityEvent
{
    public Guid Id { get; private set; }

    public OperationalSecurityOperation Operation { get; private set; }

    public OperationalSecurityOutcome Outcome { get; private set; }

    public Guid? TargetUserId { get; private set; }

    public Guid? TargetOrganizationId { get; private set; }

    public Guid? TargetMembershipId { get; private set; }

    public string DeclaredOperator { get; private set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; private set; }

    private OperationalSecurityEvent()
    {
    }

    public static OperationalSecurityEvent Record(
        Guid id,
        OperationalSecurityOperation operation,
        OperationalSecurityOutcome outcome,
        Guid? targetUserId,
        Guid? targetOrganizationId,
        Guid? targetMembershipId,
        string declaredOperator,
        DateTimeOffset occurredAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Event id cannot be empty.", nameof(id));
        }

        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "Operational security operation is not defined.");
        }

        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Operational security outcome is not defined.");
        }

        if (!TargetsMatch(operation, targetUserId, targetOrganizationId, targetMembershipId))
        {
            throw new ArgumentException("Operational security event targets do not match the operation.");
        }

        string op = RoleActor.DeploymentBootstrap(declaredOperator).OperatorName
            ?? throw new ArgumentException("Declared operator is missing.", nameof(declaredOperator));

        return new OperationalSecurityEvent
        {
            Id = id,
            Operation = operation,
            Outcome = outcome,
            TargetUserId = targetUserId,
            TargetOrganizationId = targetOrganizationId,
            TargetMembershipId = targetMembershipId,
            DeclaredOperator = op,
            OccurredAt = occurredAt,
        };
    }

    private static bool TargetsMatch(
        OperationalSecurityOperation operation,
        Guid? targetUserId,
        Guid? targetOrganizationId,
        Guid? targetMembershipId)
    {
        bool user = targetUserId is Guid userId && userId != Guid.Empty;
        bool organization = targetOrganizationId is Guid organizationId && organizationId != Guid.Empty;
        bool membership = targetMembershipId is Guid membershipId && membershipId != Guid.Empty;
        return operation switch
        {
            OperationalSecurityOperation.DisableUser => user && !organization && !membership,
            OperationalSecurityOperation.RevokeMembership => user && organization && membership,
            OperationalSecurityOperation.DisableOrganization => !user && organization && !membership,
            _ => false,
        };
    }
}