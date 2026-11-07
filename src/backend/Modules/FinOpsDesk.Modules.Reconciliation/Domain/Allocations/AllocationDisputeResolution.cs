using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Domain.Allocations;

public enum AllocationDisputeResolutionOutcome
{
    Upheld = 1,
    NotUpheld = 2,
}

public enum AllocationDisputeResolutionReasonIssue
{
    Required = 1,
    TooLong = 2,
}

public sealed class AllocationDisputeResolution
{
    public const int MaxReasonLength = 500;

    public OrganizationId OrganizationId { get; }

    public AllocationDisputeId AllocationDisputeId { get; }

    public AllocationDisputeResolutionOutcome Outcome { get; }

    public string Reason { get; }

    public ReviewCaseActorSource ActorSource { get; }

    public string ActorId { get; }

    public DateTimeOffset DecidedAt { get; }

    public Guid IdempotencyKey { get; }

#pragma warning disable CS8618
    private AllocationDisputeResolution()
    {
    }
#pragma warning restore CS8618

    private AllocationDisputeResolution(
        OrganizationId organizationId,
        AllocationDisputeId allocationDisputeId,
        AllocationDisputeResolutionOutcome outcome,
        string reason,
        ReviewCaseActorSource actorSource,
        string actorId,
        DateTimeOffset decidedAt,
        Guid idempotencyKey)
    {
        OrganizationId = organizationId;
        AllocationDisputeId = allocationDisputeId;
        Outcome = outcome;
        Reason = reason;
        ActorSource = actorSource;
        ActorId = actorId;
        DecidedAt = decidedAt;
        IdempotencyKey = idempotencyKey;
    }

    public static AllocationDisputeResolution Create(
        OrganizationId organizationId,
        AllocationDisputeId allocationDisputeId,
        AllocationDisputeResolutionOutcome outcome,
        string reason,
        ReviewCaseActorSource actorSource,
        string actorId,
        DateTimeOffset decidedAt,
        Guid idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(allocationDisputeId);

        if (idempotencyKey == Guid.Empty)
        {
            throw new ArgumentException(
                "Idempotency key cannot be empty.",
                nameof(idempotencyKey));
        }

        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(
                nameof(outcome),
                "Resolution outcome is not defined.");
        }

        if (!Enum.IsDefined(actorSource))
        {
            throw new ArgumentOutOfRangeException(
                nameof(actorSource),
                "Actor source is not defined.");
        }

        if (decidedAt == default)
        {
            throw new ArgumentOutOfRangeException(
                nameof(decidedAt),
                "Decision time cannot be the default value.");
        }

        if (!TryNormalizeReason(reason, out string normalized, out _))
        {
            throw new ArgumentException(
                "Allocation dispute resolution reason is not valid.",
                nameof(reason));
        }

        return new AllocationDisputeResolution(
            organizationId,
            allocationDisputeId,
            outcome,
            normalized,
            actorSource,
            NormalizeActorId(actorId),
            decidedAt,
            idempotencyKey);
    }

    public bool Matches(
        AllocationDisputeId allocationDisputeId,
        AllocationDisputeResolutionOutcome outcome,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(allocationDisputeId);

        return AllocationDisputeId == allocationDisputeId
            && Outcome == outcome
            && string.Equals(Reason, reason, StringComparison.Ordinal);
    }

    public static bool TryNormalizeReason(
        string? reason,
        out string normalized,
        out AllocationDisputeResolutionReasonIssue issue)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(reason))
        {
            issue = AllocationDisputeResolutionReasonIssue.Required;
            return false;
        }

        string trimmed = reason.Trim();
        if (trimmed.Length < 1)
        {
            issue = AllocationDisputeResolutionReasonIssue.Required;
            return false;
        }

        if (trimmed.Length > MaxReasonLength)
        {
            issue = AllocationDisputeResolutionReasonIssue.TooLong;
            return false;
        }

        normalized = trimmed;
        issue = default;
        return true;
    }

    public static bool TryParseOutcome(
        string? value,
        out AllocationDisputeResolutionOutcome outcome)
    {
        if (string.Equals(value, "upheld", StringComparison.Ordinal))
        {
            outcome = AllocationDisputeResolutionOutcome.Upheld;
            return true;
        }

        if (string.Equals(value, "not_upheld", StringComparison.Ordinal))
        {
            outcome = AllocationDisputeResolutionOutcome.NotUpheld;
            return true;
        }

        outcome = default;
        return false;
    }

    public static string OutcomeName(AllocationDisputeResolutionOutcome outcome)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), "Resolution outcome is not defined.");
        }

        return outcome == AllocationDisputeResolutionOutcome.Upheld
            ? "upheld"
            : "not_upheld";
    }

    private static string NormalizeActorId(string actorId)
    {
        ArgumentNullException.ThrowIfNull(actorId);

        if (actorId.Length == 0
            || actorId.Length > AllocationDispute.MaxActorIdLength
            || actorId != actorId.Trim())
        {
            throw new ArgumentException(
                "Actor id must be a trimmed non-blank value of at most 256 characters.",
                nameof(actorId));
        }

        return actorId;
    }
}