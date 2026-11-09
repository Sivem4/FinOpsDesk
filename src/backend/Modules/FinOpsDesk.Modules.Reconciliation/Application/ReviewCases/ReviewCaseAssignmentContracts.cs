using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;

public sealed record ClaimReviewCaseCommand(
    ReviewCaseId ReviewCaseId,
    byte[] RowVersion,
    string CanonicalConcurrencyToken,
    Guid IdempotencyKey);

public sealed record ReleaseReviewCaseCommand(
    ReviewCaseId ReviewCaseId,
    byte[] RowVersion,
    string CanonicalConcurrencyToken,
    Guid IdempotencyKey);

public sealed record DelegateReviewCaseCommand(
    ReviewCaseId ReviewCaseId,
    byte[] RowVersion,
    string CanonicalConcurrencyToken,
    Guid IdempotencyKey,
    string RecipientActorId);

public sealed record GetReviewCaseAssignmentQuery(ReviewCaseId ReviewCaseId);

public sealed record GetReviewCaseDelegationRecipientsQuery;

public sealed record ReviewCaseDelegationRecipientsResponse(IReadOnlyList<string> ActorIds);

public sealed record ReviewCaseDelegationResponse(
    Guid ReviewCaseId,
    string Operation,
    string ActorSource,
    string ActorId,
    string RecipientActorId,
    DateTimeOffset RecordedAt);

public sealed record ChangeReviewCaseDelegationResponse(
    ReviewCaseDelegationResponse Delegation,
    bool Created);

internal sealed record ReviewCaseAssignmentExecution(
    ReviewCaseAssignmentEntry Entry,
    bool Created);

public sealed record ReviewCaseAssigneeResponse(
    string ActorSource,
    string ActorId,
    DateTimeOffset AssignedAt);

public sealed record ReviewCaseAssignmentResponse(
    Guid ReviewCaseId,
    string Operation,
    ReviewCaseAssigneeResponse? Assignee,
    DateTimeOffset RecordedAt);

public sealed record ChangeReviewCaseAssignmentResponse(
    ReviewCaseAssignmentResponse Assignment,
    bool Created);

public sealed record ReviewCaseAssignmentStateResponse(
    Guid ReviewCaseId,
    ReviewCaseAssigneeResponse? Assignee,
    string ConcurrencyToken);

public interface IReviewCaseAssignmentLookup
{
    Task<ReviewCaseAssignmentEntry?> FindByIdempotencyKeyAsync(
        OrganizationId organizationId,
        Guid idempotencyKey,
        CancellationToken cancellationToken);
}

public interface IReviewCaseAssignmentWriter
{
    void Add(ReviewCaseAssignmentEntry entry);
}

public interface IReviewCaseAssignmentConflict
{
    bool IsIdempotencyKeyConflict(Exception exception);
}

public abstract record ReviewCaseDelegationDirectoryRead
{
    private ReviewCaseDelegationDirectoryRead()
    {
    }

    public sealed record Unavailable : ReviewCaseDelegationDirectoryRead
    {
        public static Unavailable Instance { get; } = new();
    }

    public sealed record Available(IReadOnlyList<string> ActorIds) : ReviewCaseDelegationDirectoryRead;
}

public interface IReviewCaseDelegationDirectory
{
    ReviewCaseDelegationDirectoryRead ReadCurrentOrganization();

    bool Allows(OrganizationId organizationId, string normalizedRecipientActorId);
}