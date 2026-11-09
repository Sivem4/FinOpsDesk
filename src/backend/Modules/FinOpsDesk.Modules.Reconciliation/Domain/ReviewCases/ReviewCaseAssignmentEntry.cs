using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;

public sealed class ReviewCaseAssignmentEntry
{
    public const int MaxActorIdLength = 256;

    public const int CanonicalTokenLength = 12;

    public ReviewCaseAssignmentEntryId Id { get; }

    public OrganizationId OrganizationId { get; }

    public ReviewCaseId ReviewCaseId { get; }

    public ReviewCaseAssignmentOperation Operation { get; }

    public ReviewCaseActorSource ActorSource { get; }

    public string ActorId { get; }

    public string RequestConcurrencyToken { get; }

    public Guid IdempotencyKey { get; }

    public DateTimeOffset RecordedAt { get; }

    public DateTimeOffset? AssignedAt { get; }

    public string? RecipientActorId { get; }

#pragma warning disable CS8618
    private ReviewCaseAssignmentEntry()
    {
    }
#pragma warning restore CS8618

    private ReviewCaseAssignmentEntry(
        ReviewCaseAssignmentEntryId id,
        OrganizationId organizationId,
        ReviewCaseId reviewCaseId,
        ReviewCaseAssignmentOperation operation,
        ReviewCaseActorSource actorSource,
        string actorId,
        string requestConcurrencyToken,
        Guid idempotencyKey,
        DateTimeOffset recordedAt,
        DateTimeOffset? assignedAt,
        string? recipientActorId)
    {
        Id = id;
        OrganizationId = organizationId;
        ReviewCaseId = reviewCaseId;
        Operation = operation;
        ActorSource = actorSource;
        ActorId = actorId;
        RequestConcurrencyToken = requestConcurrencyToken;
        IdempotencyKey = idempotencyKey;
        RecordedAt = recordedAt;
        AssignedAt = assignedAt;
        RecipientActorId = recipientActorId;
    }

    public static ReviewCaseAssignmentEntry Create(
        ReviewCaseAssignmentEntryId id,
        OrganizationId organizationId,
        ReviewCaseId reviewCaseId,
        ReviewCaseAssignmentOperation operation,
        ReviewCaseActorSource actorSource,
        string actorId,
        string requestConcurrencyToken,
        Guid idempotencyKey,
        DateTimeOffset recordedAt,
        DateTimeOffset? assignedAt,
        string? recipientActorId = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(reviewCaseId);
        ArgumentNullException.ThrowIfNull(actorId);
        ArgumentNullException.ThrowIfNull(requestConcurrencyToken);

        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation), "Assignment operation is not defined.");
        }

        if (!Enum.IsDefined(actorSource))
        {
            throw new ArgumentOutOfRangeException(nameof(actorSource), "Actor source is not defined.");
        }

        if (idempotencyKey == Guid.Empty)
        {
            throw new ArgumentException("Idempotency key cannot be empty.", nameof(idempotencyKey));
        }

        if (recordedAt == default)
        {
            throw new ArgumentOutOfRangeException(nameof(recordedAt), "Recorded time cannot be the default value.");
        }

        if (actorId.Length == 0
            || actorId.Length > MaxActorIdLength
            || actorId != actorId.Trim())
        {
            throw new ArgumentException(
                "Actor id must be a trimmed non-blank value of at most 256 characters.",
                nameof(actorId));
        }

        if (requestConcurrencyToken.Length != CanonicalTokenLength
            || requestConcurrencyToken != requestConcurrencyToken.Trim())
        {
            throw new ArgumentException(
                "Request concurrency token must be the canonical 12-character encoding.",
                nameof(requestConcurrencyToken));
        }

        if (operation == ReviewCaseAssignmentOperation.Claim)
        {
            if (assignedAt is null || assignedAt.Value == default)
            {
                throw new ArgumentException("A claim records the assignment time.", nameof(assignedAt));
            }

            if (recipientActorId is not null)
            {
                throw new ArgumentException("A claim does not record a recipient.", nameof(recipientActorId));
            }
        }
        else if (operation == ReviewCaseAssignmentOperation.Release)
        {
            if (assignedAt is not null)
            {
                throw new ArgumentException("A release does not record an assignment time.", nameof(assignedAt));
            }

            if (recipientActorId is not null)
            {
                throw new ArgumentException("A release does not record a recipient.", nameof(recipientActorId));
            }
        }
        else
        {
            if (assignedAt is null || assignedAt.Value == default)
            {
                throw new ArgumentException("A delegation records the assignment time.", nameof(assignedAt));
            }

            if (recipientActorId is null
                || recipientActorId.Length == 0
                || recipientActorId.Length > MaxActorIdLength
                || recipientActorId != recipientActorId.Trim()
                || string.Equals(recipientActorId, actorId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "A delegation records a different trimmed recipient of at most 256 characters.",
                    nameof(recipientActorId));
            }
        }

        return new ReviewCaseAssignmentEntry(
            id,
            organizationId,
            reviewCaseId,
            operation,
            actorSource,
            actorId,
            requestConcurrencyToken,
            idempotencyKey,
            recordedAt,
            assignedAt,
            recipientActorId);
    }

    public bool Matches(
        ReviewCaseId reviewCaseId,
        ReviewCaseAssignmentOperation operation,
        ReviewCaseActorSource actorSource,
        string actorId,
        string requestConcurrencyToken,
        string? recipientActorId = null)
    {
        ArgumentNullException.ThrowIfNull(reviewCaseId);
        ArgumentNullException.ThrowIfNull(actorId);
        ArgumentNullException.ThrowIfNull(requestConcurrencyToken);

        return ReviewCaseId == reviewCaseId
            && Operation == operation
            && ActorSource == actorSource
            && string.Equals(ActorId, actorId, StringComparison.Ordinal)
            && string.Equals(RequestConcurrencyToken, requestConcurrencyToken, StringComparison.Ordinal)
            && string.Equals(RecipientActorId, recipientActorId, StringComparison.Ordinal);
    }
}