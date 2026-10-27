using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;

public sealed class ListReviewCasesHandler
{
    private readonly ICurrentOrganizationContext _currentOrganizationContext;
    private readonly IReviewCaseQueries _reviewCaseQueries;

    public ListReviewCasesHandler(
        ICurrentOrganizationContext currentOrganizationContext,
        IReviewCaseQueries reviewCaseQueries)
    {
        ArgumentNullException.ThrowIfNull(currentOrganizationContext);
        ArgumentNullException.ThrowIfNull(reviewCaseQueries);

        _currentOrganizationContext = currentOrganizationContext;
        _reviewCaseQueries = reviewCaseQueries;
    }

    public async Task<Result<ReviewCaseListResponse>> Handle(
        ListReviewCasesQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Status is not null && !Enum.IsDefined(query.Status.Value))
        {
            return Result<ReviewCaseListResponse>.Failure(
                ReconciliationApplicationErrors.ReviewCaseInvalidRequest);
        }

        if (!ReviewCaseListPagination.IsValid(query.Page, query.PageSize))
        {
            return Result<ReviewCaseListResponse>.Failure(
                ReconciliationApplicationErrors.ReviewCaseInvalidPagination);
        }

        OrganizationId organizationId = _currentOrganizationContext.OrganizationId;
        ReviewCaseListResponse response = await _reviewCaseQueries.ListAsync(
            organizationId,
            query.Status,
            query.InvoiceId,
            query.BankTransactionId,
            query.Page,
            query.PageSize,
            cancellationToken);
        return Result<ReviewCaseListResponse>.Success(response);
    }
}

public sealed class GetReviewCaseHandler
{
    private readonly ICurrentOrganizationContext _currentOrganizationContext;
    private readonly IReviewCaseQueries _reviewCaseQueries;

    public GetReviewCaseHandler(
        ICurrentOrganizationContext currentOrganizationContext,
        IReviewCaseQueries reviewCaseQueries)
    {
        ArgumentNullException.ThrowIfNull(currentOrganizationContext);
        ArgumentNullException.ThrowIfNull(reviewCaseQueries);

        _currentOrganizationContext = currentOrganizationContext;
        _reviewCaseQueries = reviewCaseQueries;
    }

    public async Task<Result<ReviewCaseDetailResponse>> Handle(
        GetReviewCaseQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        OrganizationId organizationId = _currentOrganizationContext.OrganizationId;
        return await _reviewCaseQueries.GetAsync(
            organizationId,
            query.ReviewCaseId,
            cancellationToken);
    }
}