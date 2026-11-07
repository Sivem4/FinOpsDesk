using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Domain.Allocations;

public enum AllocationDisputeStatementIssue
{
    Required = 1,
    TooLong = 2,
}

public sealed class AllocationDispute
{
    public const int MaxStatementLength = 500;

    public const int MaxActorIdLength = 256;

    public AllocationDisputeId Id { get; }

    public OrganizationId OrganizationId { get; }

    public AllocationId AllocationId { get; }

    public string Statement { get; }

    public ReviewCaseActorSource ActorSource { get; }

    public string ActorId { get; }

    public DateTimeOffset RecordedAt { get; }

    public Guid IdempotencyKey { get; }

#pragma warning disable CS8618
    private AllocationDispute()
    {
    }
#pragma warning restore CS8618

    private AllocationDispute(
        AllocationDisputeId id,
        OrganizationId organizationId,
        AllocationId allocationId,
        string statement,
        ReviewCaseActorSource actorSource,
        string actorId,
        DateTimeOffset recordedAt,
        Guid idempotencyKey)
    {
        Id = id;
        OrganizationId = organizationId;
        AllocationId = allocationId;
        Statement = statement;
        ActorSource = actorSource;
        ActorId = actorId;
        RecordedAt = recordedAt;
        IdempotencyKey = idempotencyKey;
    }

    public static AllocationDispute Create(
        AllocationDisputeId id,
        OrganizationId organizationId,
        AllocationId allocationId,
        string statement,
        ReviewCaseActorSource actorSource,
        string actorId,
        DateTimeOffset recordedAt,
        Guid idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(allocationId);

        if (idempotencyKey == Guid.Empty)
        {
            throw new ArgumentException(
                "Idempotency key cannot be empty.",
                nameof(idempotencyKey));
        }

        if (!Enum.IsDefined(actorSource))
        {
            throw new ArgumentOutOfRangeException(
                nameof(actorSource),
                "Actor source is not defined.");
        }

        if (recordedAt == default)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recordedAt),
                "Dispute time cannot be the default value.");
        }

        if (!TryNormalizeStatement(statement, out string normalized, out _))
        {
            throw new ArgumentException(
                "Allocation dispute statement is not valid.",
                nameof(statement));
        }

        return new AllocationDispute(
            id,
            organizationId,
            allocationId,
            normalized,
            actorSource,
            NormalizeActorId(actorId),
            recordedAt,
            idempotencyKey);
    }

    public bool Matches(AllocationId allocationId, string statement)
    {
        ArgumentNullException.ThrowIfNull(allocationId);

        return AllocationId == allocationId
            && string.Equals(Statement, statement, StringComparison.Ordinal);
    }

    public static bool TryNormalizeStatement(
        string? statement,
        out string normalized,
        out AllocationDisputeStatementIssue issue)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(statement))
        {
            issue = AllocationDisputeStatementIssue.Required;
            return false;
        }

        string trimmed = statement.Trim();
        if (trimmed.Length < 1)
        {
            issue = AllocationDisputeStatementIssue.Required;
            return false;
        }

        if (trimmed.Length > MaxStatementLength)
        {
            issue = AllocationDisputeStatementIssue.TooLong;
            return false;
        }

        normalized = trimmed;
        issue = default;
        return true;
    }

    public static string ActorSourceName(ReviewCaseActorSource source)
    {
        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source), "Actor source is not defined.");
        }

        return source == ReviewCaseActorSource.DevelopmentJwt
            ? "developmentJwt"
            : "productionCookie";
    }

    private static string NormalizeActorId(string actorId)
    {
        ArgumentNullException.ThrowIfNull(actorId);

        if (actorId.Length == 0
            || actorId.Length > MaxActorIdLength
            || actorId != actorId.Trim())
        {
            throw new ArgumentException(
                "Actor id must be a trimmed non-blank value of at most 256 characters.",
                nameof(actorId));
        }

        return actorId;
    }
}