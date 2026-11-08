using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Domain.Allocations;

public enum AllocationDisputeCorrectionReasonIssue
{
    Required = 1,
    TooLong = 2,
}

public sealed class AllocationDisputeCorrection
{
    public const int MaxReasonLength = 500;

    public OrganizationId OrganizationId { get; }

    public AllocationDisputeId AllocationDisputeId { get; }

    public AllocationId AllocationId { get; }

    public string Reason { get; }

    public ReviewCaseActorSource ActorSource { get; }

    public string ActorId { get; }

    public DateTimeOffset CorrectedAt { get; }

    public Guid IdempotencyKey { get; }

#pragma warning disable CS8618
    private AllocationDisputeCorrection()
    {
    }
#pragma warning restore CS8618

    private AllocationDisputeCorrection(
        OrganizationId organizationId,
        AllocationDisputeId allocationDisputeId,
        AllocationId allocationId,
        string reason,
        ReviewCaseActorSource actorSource,
        string actorId,
        DateTimeOffset correctedAt,
        Guid idempotencyKey)
    {
        OrganizationId = organizationId;
        AllocationDisputeId = allocationDisputeId;
        AllocationId = allocationId;
        Reason = reason;
        ActorSource = actorSource;
        ActorId = actorId;
        CorrectedAt = correctedAt;
        IdempotencyKey = idempotencyKey;
    }

    public static AllocationDisputeCorrection Create(
        OrganizationId organizationId,
        AllocationDisputeId allocationDisputeId,
        AllocationId allocationId,
        string reason,
        ReviewCaseActorSource actorSource,
        string actorId,
        DateTimeOffset correctedAt,
        Guid idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(allocationDisputeId);
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

        if (correctedAt == default)
        {
            throw new ArgumentOutOfRangeException(
                nameof(correctedAt),
                "Correction time cannot be the default value.");
        }

        if (!TryNormalizeReason(reason, out string normalized, out _))
        {
            throw new ArgumentException(
                "Allocation dispute correction reason is not valid.",
                nameof(reason));
        }

        return new AllocationDisputeCorrection(
            organizationId,
            allocationDisputeId,
            allocationId,
            normalized,
            actorSource,
            NormalizeActorId(actorId),
            correctedAt,
            idempotencyKey);
    }

    public static bool TryNormalizeReason(
        string? reason,
        out string normalized,
        out AllocationDisputeCorrectionReasonIssue issue)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(reason))
        {
            issue = AllocationDisputeCorrectionReasonIssue.Required;
            return false;
        }

        string trimmed = reason.Trim();
        if (trimmed.Length < 1)
        {
            issue = AllocationDisputeCorrectionReasonIssue.Required;
            return false;
        }

        if (trimmed.Length > MaxReasonLength)
        {
            issue = AllocationDisputeCorrectionReasonIssue.TooLong;
            return false;
        }

        normalized = trimmed;
        issue = default;
        return true;
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