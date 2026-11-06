using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;

public sealed class ReviewCaseComment
{
    public const int MaxTextLength = 500;

    public const int MaxActorIdLength = 256;

    public ReviewCaseCommentId Id { get; }

    public OrganizationId OrganizationId { get; }

    public ReviewCaseId ReviewCaseId { get; }

    public string Text { get; }

    public string ActorId { get; }

    public DateTimeOffset RecordedAt { get; }

    public Guid IdempotencyKey { get; }

#pragma warning disable CS8618
    private ReviewCaseComment()
    {
    }
#pragma warning restore CS8618

    private ReviewCaseComment(
        ReviewCaseCommentId id,
        OrganizationId organizationId,
        ReviewCaseId reviewCaseId,
        string text,
        string actorId,
        DateTimeOffset recordedAt,
        Guid idempotencyKey)
    {
        Id = id;
        OrganizationId = organizationId;
        ReviewCaseId = reviewCaseId;
        Text = text;
        ActorId = actorId;
        RecordedAt = recordedAt;
        IdempotencyKey = idempotencyKey;
    }

    public static ReviewCaseComment Create(
        ReviewCaseCommentId id,
        OrganizationId organizationId,
        ReviewCaseId reviewCaseId,
        string text,
        string actorId,
        DateTimeOffset recordedAt,
        Guid idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(reviewCaseId);

        if (idempotencyKey == Guid.Empty)
        {
            throw new ArgumentException(
                "Idempotency key cannot be empty.",
                nameof(idempotencyKey));
        }

        if (recordedAt == default)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recordedAt),
                "Comment time cannot be the default value.");
        }

        return new ReviewCaseComment(
            id,
            organizationId,
            reviewCaseId,
            NormalizeText(text),
            NormalizeActorId(actorId),
            recordedAt,
            idempotencyKey);
    }

    public bool Matches(ReviewCaseId reviewCaseId, string text)
    {
        ArgumentNullException.ThrowIfNull(reviewCaseId);

        return ReviewCaseId == reviewCaseId
            && string.Equals(Text, text, StringComparison.Ordinal);
    }

    public static string NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException(
                "Review case comment text is required.",
                nameof(text));
        }

        string trimmed = text.Trim();
        if (trimmed.Length is < 1 or > MaxTextLength)
        {
            throw new ArgumentException(
                $"Review case comment text must contain 1 to {MaxTextLength} characters after trim.",
                nameof(text));
        }

        return trimmed;
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