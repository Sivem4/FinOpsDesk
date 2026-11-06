using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;

public sealed record AddReviewCaseCommentCommand(
    ReviewCaseId ReviewCaseId,
    string? Text,
    Guid IdempotencyKey);

public sealed record ReviewCaseCommentResponse(
    Guid Id,
    string Text,
    string ActorId,
    DateTimeOffset RecordedAt);

public sealed record AddReviewCaseCommentResponse(
    ReviewCaseCommentResponse Comment,
    bool Created);

public sealed record GetReviewCaseCommentsQuery(
    ReviewCaseId ReviewCaseId,
    int Page,
    int PageSize);

public sealed record ReviewCaseCommentListResponse(
    IReadOnlyList<ReviewCaseCommentResponse> Items,
    int Page,
    int PageSize,
    long TotalCount);

public interface IReviewCaseCommentLookup
{
    Task<ReviewCaseComment?> FindByIdempotencyKeyAsync(
        OrganizationId organizationId,
        Guid idempotencyKey,
        CancellationToken cancellationToken);
}

public interface IReviewCaseCommentWriter
{
    void Add(ReviewCaseComment comment);
}

public interface IReviewCaseCommentConflict
{
    bool IsIdempotencyKeyConflict(Exception exception);
}

public interface IReviewCaseCommentQueries
{
    Task<bool> ExistsAsync(
        OrganizationId organizationId,
        ReviewCaseId reviewCaseId,
        CancellationToken cancellationToken);

    Task<Result<ReviewCaseCommentListResponse>> GetForReviewCaseAsync(
        OrganizationId organizationId,
        ReviewCaseId reviewCaseId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
}