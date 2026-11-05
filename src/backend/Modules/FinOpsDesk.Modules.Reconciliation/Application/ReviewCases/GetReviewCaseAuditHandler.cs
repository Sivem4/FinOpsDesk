using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.SharedKernel.Results;

namespace FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;

public sealed class GetReviewCaseAuditHandler
{
    private readonly ICurrentOrganizationContext _currentOrganizationContext;
    private readonly IReviewCaseAuditQueries _reviewCaseAuditQueries;

    public GetReviewCaseAuditHandler(
        ICurrentOrganizationContext currentOrganizationContext,
        IReviewCaseAuditQueries reviewCaseAuditQueries)
    {
        ArgumentNullException.ThrowIfNull(currentOrganizationContext);
        ArgumentNullException.ThrowIfNull(reviewCaseAuditQueries);

        _currentOrganizationContext = currentOrganizationContext;
        _reviewCaseAuditQueries = reviewCaseAuditQueries;
    }

    public async Task<Result<ReviewCaseAuditListResponse>> Handle(
        GetReviewCaseAuditQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!ReviewCaseAuditListPagination.IsValid(query.Page, query.PageSize))
        {
            return Result<ReviewCaseAuditListResponse>.Failure(
                ReconciliationApplicationErrors.ReviewCaseAuditInvalidPagination);
        }

        return await _reviewCaseAuditQueries.GetForReviewCaseAsync(
            _currentOrganizationContext.OrganizationId,
            query.ReviewCaseId,
            query.Page,
            query.PageSize,
            cancellationToken);
    }
}