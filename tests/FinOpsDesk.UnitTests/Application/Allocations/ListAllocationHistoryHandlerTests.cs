using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.Reconciliation.Application.Allocations;

public sealed class ListAllocationHistoryHandlerTests
{
    private static readonly OrganizationId TestOrganizationId =
        new(new Guid("11111111-1111-1111-1111-111111111111"));

    [Theory]
    [InlineData(0, 50)]
    [InlineData(-1, 50)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 101)]
    public async Task Handle_should_reject_invalid_pagination(int page, int pageSize)
    {
        RecordingOrganizationContext organizationContext = new(TestOrganizationId);
        FakeAllocationHistoryQueries queries = new();
        ListAllocationHistoryHandler handler = new(organizationContext, queries);

        Result<AllocationHistoryListResponse> result = await handler.Handle(
            new ListAllocationHistoryQuery(page, pageSize, null, null),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ReconciliationApplicationErrors.AllocationHistoryInvalidPagination, result.Error);
        Assert.Equal(0, organizationContext.ReadCount);
        Assert.Equal(0, queries.CallCount);
    }

    [Fact]
    public async Task Handle_should_reject_a_page_whose_offset_exceeds_int()
    {
        RecordingOrganizationContext organizationContext = new(TestOrganizationId);
        FakeAllocationHistoryQueries queries = new();
        ListAllocationHistoryHandler handler = new(organizationContext, queries);

        Result<AllocationHistoryListResponse> result = await handler.Handle(
            new ListAllocationHistoryQuery(int.MaxValue, 100, null, null),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            ReconciliationApplicationErrors.AllocationHistoryInvalidPagination.Code,
            result.Error.Code);
        Assert.Equal(0, queries.CallCount);
    }

    [Fact]
    public async Task Handle_should_forward_the_current_organization_filters_and_cancellation_token()
    {
        RecordingOrganizationContext organizationContext = new(TestOrganizationId);
        AllocationHistoryListResponse response = new([], 2, 10, 0);
        FakeAllocationHistoryQueries queries = new(response);
        ListAllocationHistoryHandler handler = new(organizationContext, queries);
        InvoiceId invoiceId = InvoiceId.New();
        BankTransactionId bankTransactionId = BankTransactionId.New();
        using CancellationTokenSource cancellationTokenSource = new();

        Result<AllocationHistoryListResponse> result = await handler.Handle(
            new ListAllocationHistoryQuery(2, 10, invoiceId, bankTransactionId),
            cancellationTokenSource.Token);

        Assert.True(result.IsSuccess);
        Assert.Same(response, result.Value);
        Assert.Equal(1, organizationContext.ReadCount);
        Assert.Equal(TestOrganizationId, queries.OrganizationId);
        Assert.Equal(2, queries.Page);
        Assert.Equal(10, queries.PageSize);
        Assert.Equal(invoiceId, queries.InvoiceId);
        Assert.Equal(bankTransactionId, queries.BankTransactionId);
        Assert.Equal(cancellationTokenSource.Token, queries.CancellationToken);
    }

    [Fact]
    public async Task Handle_should_forward_an_unfiltered_query()
    {
        RecordingOrganizationContext organizationContext = new(TestOrganizationId);
        FakeAllocationHistoryQueries queries = new();
        ListAllocationHistoryHandler handler = new(organizationContext, queries);

        Result<AllocationHistoryListResponse> result = await handler.Handle(
            new ListAllocationHistoryQuery(1, 50, null, null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(queries.InvoiceId);
        Assert.Null(queries.BankTransactionId);
        Assert.Equal(1, queries.CallCount);
    }

    [Fact]
    public async Task Handle_should_not_query_when_the_organization_context_fails()
    {
        FakeAllocationHistoryQueries queries = new();
        ListAllocationHistoryHandler handler = new(new ThrowingOrganizationContext(), queries);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(new ListAllocationHistoryQuery(1, 50, null, null), CancellationToken.None));

        Assert.Equal(0, queries.CallCount);
    }

    private sealed class RecordingOrganizationContext : ICurrentOrganizationContext
    {
        private readonly OrganizationId _organizationId;

        public RecordingOrganizationContext(OrganizationId organizationId)
        {
            _organizationId = organizationId;
        }

        public int ReadCount { get; private set; }

        public OrganizationId OrganizationId
        {
            get
            {
                ReadCount++;
                return _organizationId;
            }
        }
    }

    private sealed class ThrowingOrganizationContext : ICurrentOrganizationContext
    {
        public OrganizationId OrganizationId =>
            throw new InvalidOperationException(
                "A valid organization claim is required on the authenticated user.");
    }

    private sealed class FakeAllocationHistoryQueries : IAllocationHistoryQueries
    {
        private readonly AllocationHistoryListResponse _response;

        public FakeAllocationHistoryQueries(AllocationHistoryListResponse? response = null)
        {
            _response = response ?? new AllocationHistoryListResponse([], 1, 50, 0);
        }

        public int CallCount { get; private set; }

        public OrganizationId? OrganizationId { get; private set; }

        public int Page { get; private set; }

        public int PageSize { get; private set; }

        public InvoiceId? InvoiceId { get; private set; }

        public BankTransactionId? BankTransactionId { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<AllocationHistoryListResponse> ListAsync(
            OrganizationId organizationId,
            int page,
            int pageSize,
            InvoiceId? invoiceId,
            BankTransactionId? bankTransactionId,
            CancellationToken cancellationToken)
        {
            CallCount++;
            OrganizationId = organizationId;
            Page = page;
            PageSize = pageSize;
            InvoiceId = invoiceId;
            BankTransactionId = bankTransactionId;
            CancellationToken = cancellationToken;
            return Task.FromResult(_response);
        }
    }
}