using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.BankTransactions;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.Reconciliation.Application.BankTransactions;

public sealed class ListBankTransactionsHandlerTests
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
        FakeBankTransactionQueries queries = new();
        ListBankTransactionsHandler handler = CreateHandler(organizationContext, queries);
        using CancellationTokenSource cancellationTokenSource = new();

        Result<BankTransactionListResponse> result = await handler.Handle(
            new ListBankTransactionsQuery(page, pageSize),
            cancellationTokenSource.Token);

        Assert.True(result.IsFailure);
        Assert.Equal(
            ReconciliationApplicationErrors.InvalidBankTransactionPagination,
            result.Error);
        Assert.Equal(0, organizationContext.ReadCount);
        Assert.Equal(0, queries.CallCount);
    }

    [Fact]
    public async Task Handle_should_reject_a_page_whose_offset_exceeds_int()
    {
        RecordingOrganizationContext organizationContext = new(TestOrganizationId);
        FakeBankTransactionQueries queries = new();
        ListBankTransactionsHandler handler = CreateHandler(organizationContext, queries);

        Result<BankTransactionListResponse> result = await handler.Handle(
            new ListBankTransactionsQuery(page: int.MaxValue, pageSize: 100),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            ReconciliationApplicationErrors.InvalidBankTransactionPagination.Code,
            result.Error.Code);
        Assert.Equal(0, organizationContext.ReadCount);
        Assert.Equal(0, queries.CallCount);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 100)]
    [InlineData(4, 50)]
    [InlineData(int.MaxValue, 1)]
    public async Task Handle_should_list_bank_transactions_for_the_current_organization(
        int page,
        int pageSize)
    {
        RecordingOrganizationContext organizationContext = new(TestOrganizationId);
        BankTransactionListRead read = new(0, []);
        FakeBankTransactionQueries queries = new(read);
        ListBankTransactionsHandler handler = CreateHandler(organizationContext, queries);
        using CancellationTokenSource cancellationTokenSource = new();
        CancellationToken cancellationToken = cancellationTokenSource.Token;

        Result<BankTransactionListResponse> result = await handler.Handle(
            new ListBankTransactionsQuery(page, pageSize),
            cancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Items);
        Assert.Equal(page, result.Value.Page);
        Assert.Equal(pageSize, result.Value.PageSize);
        Assert.Equal(0, result.Value.TotalCount);
        Assert.Equal(1, organizationContext.ReadCount);
        Assert.Equal(1, queries.CallCount);
        Assert.Equal(TestOrganizationId, queries.OrganizationId);
        Assert.Equal(page, queries.Page);
        Assert.Equal(pageSize, queries.PageSize);
        Assert.Equal(cancellationToken, queries.CancellationToken);
    }

    [Fact]
    public async Task Handle_should_fail_the_page_when_any_selected_row_has_inconsistent_money()
    {
        RecordingOrganizationContext organizationContext = new(TestOrganizationId);
        Guid validId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Guid brokenId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        FakeBankTransactionQueries queries = new(new BankTransactionListRead(
            2,
            [
                Row(validId, 40m, "PLN", 0m, "PLN"),
                Row(brokenId, 40m, "PLN", 10m, "USD"),
            ]));
        ListBankTransactionsHandler handler = CreateHandler(organizationContext, queries);

        Result<BankTransactionListResponse> result = await handler.Handle(
            new ListBankTransactionsQuery(1, 50),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            ReconciliationApplicationErrors.BankTransactionListInconsistentMoney,
            result.Error);
        Assert.Equal(1, queries.CallCount);
    }

    [Fact]
    public async Task Handle_should_not_query_when_the_organization_context_fails()
    {
        FakeBankTransactionQueries queries = new();
        ListBankTransactionsHandler handler = CreateHandler(
            new ThrowingOrganizationContext(),
            queries);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(new ListBankTransactionsQuery(1, 50), CancellationToken.None));

        Assert.Equal(0, queries.CallCount);
    }

    private static ListBankTransactionsHandler CreateHandler(
        ICurrentOrganizationContext organizationContext,
        IBankTransactionQueries queries)
    {
        return new ListBankTransactionsHandler(organizationContext, queries);
    }

    private static BankTransactionListColumns Row(
        Guid id,
        decimal amount,
        string currency,
        decimal allocatedAmount,
        string allocatedCurrency)
    {
        return new BankTransactionListColumns(
            id,
            "PAY-1",
            new DateOnly(2026, 3, 15),
            amount,
            currency,
            allocatedAmount,
            allocatedCurrency,
            null,
            null);
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

    private sealed class FakeBankTransactionQueries : IBankTransactionQueries
    {
        private readonly BankTransactionListRead _read;

        public FakeBankTransactionQueries(BankTransactionListRead? read = null)
        {
            _read = read ?? new BankTransactionListRead(0, []);
        }

        public int CallCount { get; private set; }

        public OrganizationId? OrganizationId { get; private set; }

        public int Page { get; private set; }

        public int PageSize { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<BankTransactionListRead> ListAsync(
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
            return Task.FromResult(_read);
        }
    }
}