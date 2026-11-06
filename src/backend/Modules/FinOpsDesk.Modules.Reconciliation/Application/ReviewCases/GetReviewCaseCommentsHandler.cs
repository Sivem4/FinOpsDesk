using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.SharedKernel.Results;

namespace FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;

public sealed class GetReviewCaseCommentsHandler
{
    private readonly ICurrentOrganizationContext _currentOrganizationContext;
    private readonly IReviewCaseCommentQueries _commentQueries;

    public GetReviewCaseCommentsHandler(
        ICurrentOrganizationContext currentOrganizationContext,
        IReviewCaseCommentQueries commentQueries)
    {
        ArgumentNullException.ThrowIfNull(currentOrganizationContext);
        ArgumentNullException.ThrowIfNull(commentQueries);

        _currentOrganizationContext = currentOrganizationContext;
        _commentQueries = commentQueries;
    }

    public async Task<Result<ReviewCaseCommentListResponse>> Handle(
        GetReviewCaseCommentsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!ReviewCaseCommentListPagination.IsValid(query.Page, query.PageSize))
        {
            return Result<ReviewCaseCommentListResponse>.Failure(
                ReconciliationApplicationErrors.ReviewCaseCommentInvalidPagination);
        }

        return await _commentQueries.GetForReviewCaseAsync(
            _currentOrganizationContext.OrganizationId,
            query.ReviewCaseId,
            query.Page,
            query.PageSize,
            cancellationToken);
    }
}