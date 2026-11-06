using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.Reconciliation.Application.ReviewCases;

public sealed class ReviewCaseQueryHandlerTests
{
    private static readonly OrganizationId OrganizationId =
        new(new Guid("11111111-1111-1111-1111-111111111111"));

    [Theory]
    [InlineData(0, 50)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    [InlineData(int.MaxValue, 100)]
    public async Task List_should_reject_invalid_pagination_before_reading_the_organization(
        int page,
        int pageSize)
    {
        RecordingOrganizationContext organization = new(OrganizationId);
        FakeReviewCaseQueries queries = new();
        ListReviewCasesHandler handler = new(organization, queries);

        Result<ReviewCaseListResponse> result = await handler.Handle(
            new ListReviewCasesQuery(null, null, null, page, pageSize),
            CancellationToken.None);

        Assert.Equal(
            ReconciliationApplicationErrors.ReviewCaseInvalidPagination.Code,
            result.Error!.Code);
        Assert.Equal(0, organization.ReadCount);
        Assert.Equal(0, queries.ListCount);
    }

    [Fact]
    public async Task List_should_query_only_the_current_organization()
    {
        RecordingOrganizationContext organization = new(OrganizationId);
        ReviewCaseListResponse response = new([], 1, 50, 0);
        FakeReviewCaseQueries queries = new(response);
        ListReviewCasesHandler handler = new(organization, queries);
        InvoiceId invoiceId = InvoiceId.New();
        BankTransactionId bankTransactionId = BankTransactionId.New();
        using CancellationTokenSource cancellation = new();

        Result<ReviewCaseListResponse> result = await handler.Handle(
            new ListReviewCasesQuery(
                ReviewCaseStatus.Open,
                invoiceId,
                bankTransactionId,
                ReviewCaseListPagination.DefaultPage,
                ReviewCaseListPagination.DefaultPageSize),
            cancellation.Token);

        Assert.True(result.IsSuccess);
        Assert.Same(response, result.Value);
        Assert.Equal(OrganizationId, queries.OrganizationId);
        Assert.Equal(ReviewCaseStatus.Open, queries.Status);
        Assert.Equal(invoiceId, queries.InvoiceId);
        Assert.Equal(bankTransactionId, queries.BankTransactionId);
        Assert.Equal(1, queries.Page);
        Assert.Equal(50, queries.PageSize);
        Assert.Equal(cancellation.Token, queries.CancellationToken);
    }

    [Fact]
    public async Task List_should_allow_the_maximum_page_size()
    {
        FakeReviewCaseQueries queries = new();
        ListReviewCasesHandler handler = new(new RecordingOrganizationContext(OrganizationId), queries);

        Result<ReviewCaseListResponse> result = await handler.Handle(
            new ListReviewCasesQuery(null, null, null, 1, ReviewCaseListPagination.MaximumPageSize),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(100, queries.PageSize);
    }

    [Fact]
    public async Task Get_should_return_not_found_without_another_tenants_detail()
    {
        RecordingOrganizationContext organization = new(OrganizationId);
        FakeReviewCaseQueries queries = new();
        GetReviewCaseHandler handler = new(organization, queries);
        using CancellationTokenSource cancellation = new();

        Result<ReviewCaseDetailResponse> result = await handler.Handle(
            new GetReviewCaseQuery(ReviewCaseId.New()),
            cancellation.Token);

        Assert.Equal(ReconciliationApplicationErrors.ReviewCaseNotFound.Code, result.Error!.Code);
        Assert.Equal(OrganizationId, queries.OrganizationId);
        Assert.Equal(cancellation.Token, queries.CancellationToken);
    }

    [Fact]
    public async Task Get_should_return_the_persisted_case_and_current_linked_balances()
    {
        Guid reviewCaseId = Guid.NewGuid();
        ReviewCaseDetailResponse detail = new(
            new ReviewCaseResponse(
                reviewCaseId,
                "NeedsInvestigation",
                "Open",
                Guid.NewGuid(),
                null,
                "manual",
                null,
                null,
                null,
                null,
                ReviewCaseHandlerTestDoubles.OpenedAt,
                null,
                ReviewCaseConcurrencyToken.Encode(ReviewCaseHandlerTestDoubles.CurrentVersion)),
            new ReviewCaseLinkedFinancials(
                true,
                new MoneyDto(70m, "PLN"),
                false,
                null));
        FakeReviewCaseQueries queries = new(detail: detail);
        GetReviewCaseHandler handler = new(new RecordingOrganizationContext(OrganizationId), queries);

        Result<ReviewCaseDetailResponse> result = await handler.Handle(
            new GetReviewCaseQuery(new ReviewCaseId(reviewCaseId)),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(70m, result.Value.LinkedFinancials.InvoiceOutstanding!.Amount);
        Assert.False(result.Value.LinkedFinancials.BankTransactionFound);
        Assert.Equal(OrganizationId, queries.OrganizationId);
    }

    [Fact]
    public async Task Summary_should_query_only_the_current_organization()
    {
        RecordingOrganizationContext organization = new(OrganizationId);
        ReviewCaseSummaryResponse response = new(4, 3, 1, 0, 2, 1);
        FakeReviewCaseQueries queries = new(summary: response);
        GetReviewCaseSummaryHandler handler = new(organization, queries);
        using CancellationTokenSource cancellation = new();

        Result<ReviewCaseSummaryResponse> result = await handler.Handle(cancellation.Token);

        Assert.True(result.IsSuccess);
        Assert.Equal(4, result.Value.TotalCount);
        Assert.Equal(3, result.Value.OpenCount);
        Assert.Equal(1, organization.ReadCount);
        Assert.Equal(OrganizationId, queries.OrganizationId);
        Assert.Equal(cancellation.Token, queries.CancellationToken);
        Assert.Equal(1, queries.SummarizeCount);
        Assert.Equal(0, queries.ListCount);
        Assert.Equal(0, queries.GetCount);
    }
}