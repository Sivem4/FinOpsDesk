using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class ReviewCaseLinkedFinancialTests
{
    private static readonly DateTimeOffset OpenedAt = new(2026, 10, 26, 21, 0, 0, TimeSpan.Zero);

    private readonly SqlServerFixture _fixture;

    public ReviewCaseLinkedFinancialTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Get_returns_the_full_invoice_outstanding_amount()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 100m);
        await SeedAsync(invoice);
        OpenReviewCaseResponse opened = await OpenInvoiceAsync(organizationId, invoice.Id);

        ReviewCaseDetailResponse detail = await GetAsync(organizationId, opened);

        Assert.True(detail.LinkedFinancials.InvoiceFound);
        Assert.Equal(100m, detail.LinkedFinancials.InvoiceOutstanding!.Amount);
        Assert.Equal("PLN", detail.LinkedFinancials.InvoiceOutstanding.Currency);
        Assert.False(detail.LinkedFinancials.BankTransactionFound);
        Assert.Null(detail.LinkedFinancials.BankTransactionUnapplied);
    }

    [Fact]
    public async Task Get_returns_the_remaining_amount_of_a_partially_allocated_invoice()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 100m);
        await SeedAsync(invoice);
        await AllocateInvoiceAsync(invoice.Id, 40m);
        OpenReviewCaseResponse opened = await OpenInvoiceAsync(organizationId, invoice.Id);

        ReviewCaseDetailResponse detail = await GetAsync(organizationId, opened);

        Assert.Equal(60m, detail.LinkedFinancials.InvoiceOutstanding!.Amount);
        Assert.Equal("PLN", detail.LinkedFinancials.InvoiceOutstanding.Currency);
    }

    [Fact]
    public async Task Get_returns_zero_outstanding_when_the_invoice_is_fully_allocated()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 100m);
        await SeedAsync(invoice);
        await AllocateInvoiceAsync(invoice.Id, 100m);
        OpenReviewCaseResponse opened = await OpenInvoiceAsync(organizationId, invoice.Id);

        ReviewCaseDetailResponse detail = await GetAsync(organizationId, opened);

        Assert.True(detail.LinkedFinancials.InvoiceFound);
        Assert.Equal(0m, detail.LinkedFinancials.InvoiceOutstanding!.Amount);
        Assert.Equal("PLN", detail.LinkedFinancials.InvoiceOutstanding.Currency);
    }

    [Fact]
    public async Task Get_returns_the_full_bank_transaction_unapplied_amount()
    {
        OrganizationId organizationId = OrganizationId.New();
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 80m);
        await SeedAsync(bankTransaction);
        OpenReviewCaseResponse opened = await OpenBankAsync(organizationId, bankTransaction.Id);

        ReviewCaseDetailResponse detail = await GetAsync(organizationId, opened);

        Assert.False(detail.LinkedFinancials.InvoiceFound);
        Assert.Null(detail.LinkedFinancials.InvoiceOutstanding);
        Assert.True(detail.LinkedFinancials.BankTransactionFound);
        Assert.Equal(80m, detail.LinkedFinancials.BankTransactionUnapplied!.Amount);
        Assert.Equal("PLN", detail.LinkedFinancials.BankTransactionUnapplied.Currency);
    }

    [Fact]
    public async Task Get_returns_the_remaining_amount_of_a_partially_allocated_bank_transaction()
    {
        OrganizationId organizationId = OrganizationId.New();
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 80m);
        await SeedAsync(bankTransaction);
        await AllocateBankAsync(bankTransaction.Id, 25m);
        OpenReviewCaseResponse opened = await OpenBankAsync(organizationId, bankTransaction.Id);

        ReviewCaseDetailResponse detail = await GetAsync(organizationId, opened);

        Assert.Equal(55m, detail.LinkedFinancials.BankTransactionUnapplied!.Amount);
        Assert.Equal("PLN", detail.LinkedFinancials.BankTransactionUnapplied.Currency);
    }

    [Fact]
    public async Task Get_rejects_an_invoice_whose_total_and_allocated_currencies_differ()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 100m);
        await SeedAsync(invoice);
        OpenReviewCaseResponse opened = await OpenInvoiceAsync(organizationId, invoice.Id);
        await ExecuteAsync(
            $"UPDATE [reconciliation].[Invoices] SET [AllocatedAmount] = {{0}}, [AllocatedCurrency] = {{1}} WHERE [Id] = {{2}} AND [OrganizationId] = {{3}}",
            30m,
            "EUR",
            invoice.Id.Value,
            organizationId.Value);

        await AssertCorruptReadLeavesRowsUnchangedAsync(organizationId, opened, invoice.Id, bankTransactionId: null);
    }

    [Fact]
    public async Task Get_rejects_a_bank_transaction_whose_amount_and_allocated_currencies_differ()
    {
        OrganizationId organizationId = OrganizationId.New();
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 80m);
        await SeedAsync(bankTransaction);
        OpenReviewCaseResponse opened = await OpenBankAsync(organizationId, bankTransaction.Id);
        await ExecuteAsync(
            $"UPDATE [reconciliation].[BankTransactions] SET [AllocatedAmount] = {{0}}, [AllocatedCurrency] = {{1}} WHERE [Id] = {{2}} AND [OrganizationId] = {{3}}",
            10m,
            "EUR",
            bankTransaction.Id.Value,
            organizationId.Value);

        await AssertCorruptReadLeavesRowsUnchangedAsync(
            organizationId,
            opened,
            invoiceId: null,
            bankTransaction.Id);
    }

    [Fact]
    public async Task Get_rejects_an_allocated_balance_above_the_invoice_total_or_bank_amount()
    {
        OrganizationId invoiceOrganization = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(invoiceOrganization, 100m);
        await SeedAsync(invoice);
        OpenReviewCaseResponse invoiceCase = await OpenInvoiceAsync(invoiceOrganization, invoice.Id);
        await ExecuteAsync(
            $"UPDATE [reconciliation].[Invoices] SET [AllocatedAmount] = [TotalAmount] + {{0}} WHERE [Id] = {{1}} AND [OrganizationId] = {{2}}",
            1m,
            invoice.Id.Value,
            invoiceOrganization.Value);
        await AssertCorruptReadLeavesRowsUnchangedAsync(
            invoiceOrganization,
            invoiceCase,
            invoice.Id,
            bankTransactionId: null);

        OrganizationId bankOrganization = OrganizationId.New();
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(bankOrganization, 80m);
        await SeedAsync(bankTransaction);
        OpenReviewCaseResponse bankCase = await OpenBankAsync(bankOrganization, bankTransaction.Id);
        await ExecuteAsync(
            $"UPDATE [reconciliation].[BankTransactions] SET [AllocatedAmount] = [Amount] + {{0}} WHERE [Id] = {{1}} AND [OrganizationId] = {{2}}",
            1m,
            bankTransaction.Id.Value,
            bankOrganization.Value);
        await AssertCorruptReadLeavesRowsUnchangedAsync(
            bankOrganization,
            bankCase,
            invoiceId: null,
            bankTransaction.Id);
    }

    [Fact]
    public async Task Get_rejects_a_negative_allocated_balance()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 100m);
        await SeedAsync(invoice);
        OpenReviewCaseResponse opened = await OpenInvoiceAsync(organizationId, invoice.Id);
        await ExecuteAsync(
            $"UPDATE [reconciliation].[Invoices] SET [AllocatedAmount] = {{0}} WHERE [Id] = {{1}} AND [OrganizationId] = {{2}}",
            -5m,
            invoice.Id.Value,
            organizationId.Value);

        Result<ReviewCaseDetailResponse> result = await GetResultAsync(organizationId, opened);

        Assert.True(result.IsFailure);
        Assert.Null(result.Value);
        Assert.Equal(
            ReconciliationApplicationErrors.ReviewCaseInconsistentLinkedMoney.Code,
            result.Error.Code);
    }

    [Fact]
    public async Task Linked_balances_stay_inside_the_authenticated_organization()
    {
        OrganizationId owner = OrganizationId.New();
        OrganizationId other = OrganizationId.New();
        Invoice ownerInvoice = PersistenceTestData.CreateInvoice(owner, 100m);
        Invoice otherInvoice = PersistenceTestData.CreateInvoice(other, 40m, "INV-OTHER");
        await SeedAsync(ownerInvoice, otherInvoice);
        OpenReviewCaseResponse ownerCase = await OpenInvoiceAsync(owner, ownerInvoice.Id);
        OpenReviewCaseResponse otherCase = await OpenInvoiceAsync(other, otherInvoice.Id);

        ReviewCaseDetailResponse ownerDetail = await GetAsync(owner, ownerCase);
        ReviewCaseDetailResponse otherDetail = await GetAsync(other, otherCase);
        Result<ReviewCaseDetailResponse> hiddenFromOther = await GetResultAsync(other, ownerCase);
        Result<ReviewCaseDetailResponse> hiddenFromOwner = await GetResultAsync(owner, otherCase);

        Assert.Equal(100m, ownerDetail.LinkedFinancials.InvoiceOutstanding!.Amount);
        Assert.Equal(40m, otherDetail.LinkedFinancials.InvoiceOutstanding!.Amount);
        Assert.True(hiddenFromOther.IsFailure);
        Assert.Null(hiddenFromOther.Value);
        Assert.Equal(ReconciliationApplicationErrors.ReviewCaseNotFound.Code, hiddenFromOther.Error.Code);
        Assert.True(hiddenFromOwner.IsFailure);
        Assert.Null(hiddenFromOwner.Value);
        Assert.Equal(ReconciliationApplicationErrors.ReviewCaseNotFound.Code, hiddenFromOwner.Error.Code);
    }

    private async Task AssertCorruptReadLeavesRowsUnchangedAsync(
        OrganizationId organizationId,
        OpenReviewCaseResponse opened,
        InvoiceId? invoiceId,
        BankTransactionId? bankTransactionId)
    {
        CaseState caseBefore = await ReadCaseAsync(opened.ReviewCase.ReviewCaseId);
        MoneyState? invoiceBefore = invoiceId is null ? null : await ReadInvoiceAsync(invoiceId);
        MoneyState? bankBefore = bankTransactionId is null
            ? null
            : await ReadBankAsync(bankTransactionId);

        Result<ReviewCaseDetailResponse> result = await GetResultAsync(organizationId, opened);

        Assert.True(result.IsFailure);
        Assert.Null(result.Value);
        Assert.Equal(
            ReconciliationApplicationErrors.ReviewCaseInconsistentLinkedMoney.Code,
            result.Error.Code);
        Assert.DoesNotContain("EUR", result.Error.Message, StringComparison.Ordinal);
        AssertSame(caseBefore, await ReadCaseAsync(opened.ReviewCase.ReviewCaseId));
        if (invoiceBefore is not null)
        {
            AssertSame(invoiceBefore, await ReadInvoiceAsync(invoiceId!));
        }

        if (bankBefore is not null)
        {
            AssertSame(bankBefore, await ReadBankAsync(bankTransactionId!));
        }
    }

    private async Task<OpenReviewCaseResponse> OpenInvoiceAsync(
        OrganizationId organizationId,
        InvoiceId invoiceId)
    {
        return await OpenAsync(
            organizationId,
            new OpenReviewCaseCommand(
                ReviewCaseReason.NeedsInvestigation,
                invoiceId,
                null,
                null,
                null));
    }

    private async Task<OpenReviewCaseResponse> OpenBankAsync(
        OrganizationId organizationId,
        BankTransactionId bankTransactionId)
    {
        return await OpenAsync(
            organizationId,
            new OpenReviewCaseCommand(
                ReviewCaseReason.NeedsInvestigation,
                null,
                bankTransactionId,
                null,
                null));
    }

    private async Task<OpenReviewCaseResponse> OpenAsync(
        OrganizationId organizationId,
        OpenReviewCaseCommand command)
    {
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        OpenReviewCaseHandler handler = new(
            new TestCurrentOrganizationContext(organizationId),
            new TestCurrentActorContext(),
            scope.ServiceProvider.GetRequiredService<IInvoiceRepository>(),
            scope.ServiceProvider.GetRequiredService<IBankTransactionRepository>(),
            scope.ServiceProvider.GetRequiredService<IReviewCaseRepository>(),
            scope.ServiceProvider.GetRequiredService<IReviewCaseSnapshotLookup>(),
            scope.ServiceProvider.GetRequiredService<IReviewCaseOpenConflict>(),
            scope.ServiceProvider.GetRequiredService<IReviewCaseOpenAttemptLookup>(),
            scope.ServiceProvider.GetRequiredService<IReviewCaseOpenAttemptWriter>(),
            scope.ServiceProvider.GetRequiredService<IReviewCaseOpenAttemptConflict>(),
            scope.ServiceProvider.GetRequiredService<IReviewCaseAuditWriter>(),
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            new FixedTimeProvider(OpenedAt));
        Result<OpenReviewCaseResponse> result = await handler.Handle(command, CancellationToken.None);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private async Task<ReviewCaseDetailResponse> GetAsync(
        OrganizationId organizationId,
        OpenReviewCaseResponse opened)
    {
        Result<ReviewCaseDetailResponse> result = await GetResultAsync(organizationId, opened);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private async Task<Result<ReviewCaseDetailResponse>> GetResultAsync(
        OrganizationId organizationId,
        OpenReviewCaseResponse opened)
    {
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        GetReviewCaseHandler handler = new(
            new TestCurrentOrganizationContext(organizationId),
            scope.ServiceProvider.GetRequiredService<IReviewCaseQueries>());
        return await handler.Handle(
            new GetReviewCaseQuery(new ReviewCaseId(opened.ReviewCase.ReviewCaseId)),
            CancellationToken.None);
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        foreach (object entity in entities)
        {
            context.Add(entity);
        }

        await context.SaveChangesAsync();
    }

    private async Task AllocateInvoiceAsync(InvoiceId invoiceId, decimal amount)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Invoice invoice = await context.Invoices.SingleAsync(candidate => candidate.Id == invoiceId);
        Assert.True(invoice.Allocate(new Money(amount, PersistenceTestData.Pln)).IsSuccess);
        await context.SaveChangesAsync();
    }

    private async Task AllocateBankAsync(BankTransactionId bankTransactionId, decimal amount)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        BankTransaction bankTransaction = await context.BankTransactions.SingleAsync(
            candidate => candidate.Id == bankTransactionId);
        Assert.True(bankTransaction.Allocate(new Money(amount, PersistenceTestData.Pln)).IsSuccess);
        await context.SaveChangesAsync();
    }

    private async Task ExecuteAsync(string sql, params object[] parameters)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync(sql, parameters);
    }

    private async Task<CaseState> ReadCaseAsync(Guid reviewCaseId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        ReviewCase reviewCase = await context.ReviewCases.SingleAsync(
            candidate => candidate.Id == new ReviewCaseId(reviewCaseId));
        return new CaseState(
            reviewCase.Status,
            reviewCase.Note,
            reviewCase.Outcome,
            reviewCase.ResolutionNote,
            reviewCase.AllocationId,
            reviewCase.ClosedAt,
            context.Entry(reviewCase).Property<byte[]>("RowVersion").CurrentValue!.ToArray());
    }

    private async Task<MoneyState> ReadInvoiceAsync(InvoiceId invoiceId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Invoice invoice = await context.Invoices.SingleAsync(candidate => candidate.Id == invoiceId);
        return new MoneyState(
            invoice.TotalAmount.Amount,
            invoice.TotalAmount.Currency.Value,
            invoice.AllocatedAmount.Amount,
            invoice.AllocatedAmount.Currency.Value,
            context.Entry(invoice).Property<byte[]>("RowVersion").CurrentValue!.ToArray());
    }

    private async Task<MoneyState> ReadBankAsync(BankTransactionId bankTransactionId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        BankTransaction bankTransaction = await context.BankTransactions.SingleAsync(
            candidate => candidate.Id == bankTransactionId);
        return new MoneyState(
            bankTransaction.Amount.Amount,
            bankTransaction.Amount.Currency.Value,
            bankTransaction.AllocatedAmount.Amount,
            bankTransaction.AllocatedAmount.Currency.Value,
            context.Entry(bankTransaction).Property<byte[]>("RowVersion").CurrentValue!.ToArray());
    }

    private static void AssertSame(CaseState before, CaseState after)
    {
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.Note, after.Note);
        Assert.Equal(before.Outcome, after.Outcome);
        Assert.Equal(before.ResolutionNote, after.ResolutionNote);
        Assert.Equal(before.AllocationId, after.AllocationId);
        Assert.Equal(before.ClosedAt, after.ClosedAt);
        Assert.Equal(before.RowVersion, after.RowVersion);
    }

    private static void AssertSame(MoneyState before, MoneyState after)
    {
        Assert.Equal(before.PrincipalAmount, after.PrincipalAmount);
        Assert.Equal(before.PrincipalCurrency, after.PrincipalCurrency);
        Assert.Equal(before.AllocatedAmount, after.AllocatedAmount);
        Assert.Equal(before.AllocatedCurrency, after.AllocatedCurrency);
        Assert.Equal(before.RowVersion, after.RowVersion);
    }

    private sealed record CaseState(
        ReviewCaseStatus Status,
        string? Note,
        ReviewCaseOutcome? Outcome,
        string? ResolutionNote,
        AllocationId? AllocationId,
        DateTimeOffset? ClosedAt,
        byte[] RowVersion);

    private sealed record MoneyState(
        decimal PrincipalAmount,
        string PrincipalCurrency,
        decimal AllocatedAmount,
        string AllocatedCurrency,
        byte[] RowVersion);

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }
    }
}