using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.Reconciliation.Application.Invoices;

public sealed class ListInvoicesHandlerTests
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
        FakeInvoiceQueries invoiceQueries = new();
        ListInvoicesHandler handler = CreateHandler(organizationContext, invoiceQueries);
        using CancellationTokenSource cancellationTokenSource = new();

        Result<InvoiceListResponse> result = await handler.Handle(
            new ListInvoicesQuery(page, pageSize),
            cancellationTokenSource.Token);

        Assert.True(result.IsFailure);
        Assert.Equal(
            ReconciliationApplicationErrors.InvalidInvoicePagination,
            result.Error);
        Assert.Equal(0, organizationContext.ReadCount);
        Assert.Equal(0, invoiceQueries.CallCount);
    }

    [Fact]
    public async Task Handle_should_reject_a_page_whose_offset_exceeds_int()
    {
        RecordingOrganizationContext organizationContext = new(TestOrganizationId);
        FakeInvoiceQueries invoiceQueries = new();
        ListInvoicesHandler handler = CreateHandler(organizationContext, invoiceQueries);

        Result<InvoiceListResponse> result = await handler.Handle(
            new ListInvoicesQuery(page: int.MaxValue, pageSize: 100),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            ReconciliationApplicationErrors.InvalidInvoicePagination.Code,
            result.Error.Code);
        Assert.Equal(0, organizationContext.ReadCount);
        Assert.Equal(0, invoiceQueries.CallCount);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 100)]
    [InlineData(4, 50)]
    [InlineData(int.MaxValue, 1)]
    public async Task Handle_should_list_invoices_for_the_current_organization(
        int page,
        int pageSize)
    {
        RecordingOrganizationContext organizationContext = new(TestOrganizationId);
        InvoiceListResponse response = new([], page, pageSize, 0);
        FakeInvoiceQueries invoiceQueries = new(response);
        ListInvoicesHandler handler = CreateHandler(organizationContext, invoiceQueries);
        using CancellationTokenSource cancellationTokenSource = new();
        CancellationToken cancellationToken = cancellationTokenSource.Token;

        Result<InvoiceListResponse> result = await handler.Handle(
            new ListInvoicesQuery(page, pageSize),
            cancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Same(response, result.Value);
        Assert.Equal(1, organizationContext.ReadCount);
        Assert.Equal(1, invoiceQueries.CallCount);
        Assert.Equal(TestOrganizationId, invoiceQueries.OrganizationId);
        Assert.Equal(page, invoiceQueries.Page);
        Assert.Equal(pageSize, invoiceQueries.PageSize);
        Assert.Equal(cancellationToken, invoiceQueries.CancellationToken);
    }

    [Fact]
    public async Task Handle_should_not_query_when_the_organization_context_fails()
    {
        FakeInvoiceQueries invoiceQueries = new();
        ListInvoicesHandler handler = CreateHandler(
            new ThrowingOrganizationContext(),
            invoiceQueries);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(new ListInvoicesQuery(1, 50), CancellationToken.None));

        Assert.Equal(0, invoiceQueries.CallCount);
    }

    private static ListInvoicesHandler CreateHandler(
        ICurrentOrganizationContext organizationContext,
        IInvoiceQueries invoiceQueries)
    {
        return new ListInvoicesHandler(organizationContext, invoiceQueries);
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

    private sealed class FakeInvoiceQueries : IInvoiceQueries
    {
        private readonly InvoiceListResponse _response;

        public FakeInvoiceQueries(InvoiceListResponse? response = null)
        {
            _response = response ?? new InvoiceListResponse([], 1, 50, 0);
        }

        public int CallCount { get; private set; }

        public OrganizationId? OrganizationId { get; private set; }

        public int Page { get; private set; }

        public int PageSize { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<InvoiceListResponse> ListAsync(
            OrganizationId organizationId,
            int page,
            int pageSize,
            CancellationToken cancellationToken)
        {
            CallCount++;
            OrganizationId = organizationId;
            Page = page;
            PageSize = pageSize;
            CancellationToken = cancellationToken;
            return Task.FromResult(_response);
        }
    }
}