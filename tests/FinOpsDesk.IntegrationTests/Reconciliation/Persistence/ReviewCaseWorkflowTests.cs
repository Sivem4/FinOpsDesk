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
public sealed class ReviewCaseWorkflowTests
{
    private static readonly DateTimeOffset OpenedAt = new(2026, 10, 26, 18, 0, 0, TimeSpan.Zero);

    private readonly SqlServerFixture _fixture;

    public ReviewCaseWorkflowTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Cross_tenant_cases_and_financial_records_stay_hidden()
    {
        OrganizationId owner = OrganizationId.New();
        OrganizationId other = OrganizationId.New();
        Invoice ownerInvoice = PersistenceTestData.CreateInvoice(owner, 40m);
        Invoice otherInvoice = PersistenceTestData.CreateInvoice(other, 40m, "INV-OTHER");
        BankTransaction otherBank = PersistenceTestData.CreateBankTransaction(other, 40m, reference: "OTHER");
        await SeedAsync(ownerInvoice, otherInvoice, otherBank);
        OpenReviewCaseResponse opened = await OpenAsync(
            owner,
            new OpenReviewCaseCommand(
                ReviewCaseReason.NeedsInvestigation,
                ownerInvoice.Id,
                null,
                "manual",
                "owner"));

        Result<ReviewCaseDetailResponse> foreignGet = await GetAsync(
            other,
            new ReviewCaseId(opened.ReviewCase.ReviewCaseId));
        Result<ReviewCaseListResponse> foreignList = await ListAsync(
            other,
            new ListReviewCasesQuery(null, null, null, 1, 50));
        Result<OpenReviewCaseResponse> foreignInvoice = await OpenResultAsync(
            owner,
            new OpenReviewCaseCommand(
                ReviewCaseReason.NeedsInvestigation,
                otherInvoice.Id,
                null,
                null,
                null));
        Result<OpenReviewCaseResponse> foreignBank = await OpenResultAsync(
            owner,
            new OpenReviewCaseCommand(
                ReviewCaseReason.NeedsInvestigation,
                null,
                otherBank.Id,
                null,
                null));

        Assert.Equal("reconciliation.review_case.not_found", foreignGet.Error!.Code);
        Assert.True(foreignList.IsSuccess);
        Assert.Empty(foreignList.Value.Items);
        Assert.Equal("reconciliation.invoice.not_found", foreignInvoice.Error!.Code);
        Assert.Equal("reconciliation.bank_transaction.not_found", foreignBank.Error!.Code);
    }

    [Fact]
    public async Task Repeated_open_returns_the_same_case_and_a_terminal_case_can_be_replaced()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 25m);
        await SeedAsync(invoice);
        OpenReviewCaseCommand command = new(
            ReviewCaseReason.NeedsInvestigation,
            invoice.Id,
            null,
            "manual",
            "first");

        OpenReviewCaseResponse first = await OpenAsync(organizationId, command);
        OpenReviewCaseResponse second = await OpenAsync(
            organizationId,
            command with { Note = "second", SourceLabel = "smart-1" });
        ReviewCaseResponse dismissed = await DismissAsync(
            organizationId,
            new DismissReviewCaseCommand(
                new ReviewCaseId(first.ReviewCase.ReviewCaseId),
                "done",
                first.ReviewCase.ConcurrencyToken));
        OpenReviewCaseResponse third = await OpenAsync(
            organizationId,
            command with { Note = "after close" });

        Assert.False(second.Created);
        Assert.Equal(first.ReviewCase.ReviewCaseId, second.ReviewCase.ReviewCaseId);
        Assert.Equal("first", second.ReviewCase.Note);
        Assert.Equal("manual", second.ReviewCase.SourceLabel);
        Assert.Equal(first.ReviewCase.ConcurrencyToken, second.ReviewCase.ConcurrencyToken);
        Assert.Equal("Dismissed", dismissed.Status);
        Assert.True(third.Created);
        Assert.NotEqual(first.ReviewCase.ReviewCaseId, third.ReviewCase.ReviewCaseId);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Assert.Equal(
            1,
            await context.ReviewCases.CountAsync(reviewCase =>
                reviewCase.OrganizationId == organizationId
                && reviewCase.Status == ReviewCaseStatus.Open));
    }

    [Fact]
    public async Task Concurrent_open_commands_create_one_active_case()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 30m);
        await SeedAsync(invoice);

        Result<OpenReviewCaseResponse>[] results = await OpenTogetherAsync(
            organizationId,
            invoice.Id,
            "alpha",
            "beta");

        OpenReviewCaseResponse firstResult = Require(results[0]);
        OpenReviewCaseResponse secondResult = Require(results[1]);
        Assert.Equal(1, new[] { firstResult, secondResult }.Count(result => result.Created));
        Assert.Equal(firstResult.ReviewCase.ReviewCaseId, secondResult.ReviewCase.ReviewCaseId);
        Assert.Equal(firstResult.ReviewCase.Note, secondResult.ReviewCase.Note);
        Assert.Contains(firstResult.ReviewCase.Note, new[] { "alpha", "beta" });
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Assert.Equal(
            1,
            await context.ReviewCases.CountAsync(reviewCase =>
                reviewCase.OrganizationId == organizationId
                && reviewCase.InvoiceId == invoice.Id));
    }

    [Fact]
    public async Task A_stale_token_cannot_dismiss_a_case_another_operator_resolved()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 15m);
        await SeedAsync(invoice);
        OpenReviewCaseResponse opened = await OpenAsync(
            organizationId,
            new OpenReviewCaseCommand(
                ReviewCaseReason.NeedsInvestigation,
                invoice.Id,
                null,
                null,
                null));
        string originalToken = opened.ReviewCase.ConcurrencyToken;

        ReviewCaseResponse resolved = await ResolveAsync(
            organizationId,
            new ResolveReviewCaseCommand(
                new ReviewCaseId(opened.ReviewCase.ReviewCaseId),
                ReviewCaseOutcome.NoActionRequired,
                null,
                "kept",
                originalToken));
        Result<ReviewCaseResponse> dismissed = await DismissResultAsync(
            organizationId,
            new DismissReviewCaseCommand(
                new ReviewCaseId(opened.ReviewCase.ReviewCaseId),
                "other",
                originalToken));
        string persistedToken = await RowVersionTokenAsync(
            new ReviewCaseId(opened.ReviewCase.ReviewCaseId));

        Assert.NotEqual(originalToken, resolved.ConcurrencyToken);
        Assert.Equal("reconciliation.review_case.terminal_conflict", dismissed.Error!.Code);
        Assert.Equal(resolved.ConcurrencyToken, persistedToken);
        Assert.Equal("kept", resolved.ResolutionNote);
    }

    [Fact]
    public async Task Identical_replay_preserves_row_version_and_a_different_note_conflicts()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 12m);
        await SeedAsync(invoice);
        OpenReviewCaseResponse opened = await OpenAsync(
            organizationId,
            new OpenReviewCaseCommand(
                ReviewCaseReason.NeedsInvestigation,
                invoice.Id,
                null,
                null,
                null));
        ReviewCaseResponse resolved = await ResolveAsync(
            organizationId,
            new ResolveReviewCaseCommand(
                new ReviewCaseId(opened.ReviewCase.ReviewCaseId),
                ReviewCaseOutcome.HandledOutside,
                null,
                "outside",
                opened.ReviewCase.ConcurrencyToken));

        ReviewCaseResponse replay = await ResolveAsync(
            organizationId,
            new ResolveReviewCaseCommand(
                new ReviewCaseId(opened.ReviewCase.ReviewCaseId),
                ReviewCaseOutcome.HandledOutside,
                null,
                "  outside  ",
                opened.ReviewCase.ConcurrencyToken));
        Result<ReviewCaseResponse> conflict = await ResolveResultAsync(
            organizationId,
            new ResolveReviewCaseCommand(
                new ReviewCaseId(opened.ReviewCase.ReviewCaseId),
                ReviewCaseOutcome.HandledOutside,
                null,
                "different",
                replay.ConcurrencyToken));
        string persistedToken = await RowVersionTokenAsync(
            new ReviewCaseId(opened.ReviewCase.ReviewCaseId));

        Assert.Equal(resolved.ConcurrencyToken, replay.ConcurrencyToken);
        Assert.Equal(resolved.ClosedAt, replay.ClosedAt);
        Assert.Equal(resolved.ConcurrencyToken, persistedToken);
        Assert.Equal("reconciliation.review_case.terminal_conflict", conflict.Error!.Code);
    }

    [Fact]
    public async Task A_stale_token_does_not_change_an_open_case()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 18m);
        await SeedAsync(invoice);
        OpenReviewCaseResponse opened = await OpenAsync(
            organizationId,
            new OpenReviewCaseCommand(
                ReviewCaseReason.NeedsInvestigation,
                invoice.Id,
                null,
                null,
                "stay"));
        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [reconciliation].[ReviewCases] SET [Note] = [Note] WHERE [Id] = {opened.ReviewCase.ReviewCaseId}");
        }

        string bumpedToken = await RowVersionTokenAsync(
            new ReviewCaseId(opened.ReviewCase.ReviewCaseId));
        Result<ReviewCaseResponse> dismissed = await DismissResultAsync(
            organizationId,
            new DismissReviewCaseCommand(
                new ReviewCaseId(opened.ReviewCase.ReviewCaseId),
                "too late",
                opened.ReviewCase.ConcurrencyToken));
        string afterToken = await RowVersionTokenAsync(
            new ReviewCaseId(opened.ReviewCase.ReviewCaseId));
        Result<ReviewCaseDetailResponse> detail = await GetAsync(
            organizationId,
            new ReviewCaseId(opened.ReviewCase.ReviewCaseId));

        Assert.NotEqual(opened.ReviewCase.ConcurrencyToken, bumpedToken);
        Assert.Equal("reconciliation.review_case.concurrency_conflict", dismissed.Error!.Code);
        Assert.Equal(bumpedToken, afterToken);
        Assert.True(detail.IsSuccess);
        Assert.Equal("Open", detail.Value.ReviewCase.Status);
        Assert.Equal("stay", detail.Value.ReviewCase.Note);
    }

    [Fact]
    public async Task Concurrent_decisions_do_not_reapply_the_losing_command()
    {
        OrganizationId identicalOrganization = OrganizationId.New();
        OrganizationId differentOrganization = OrganizationId.New();
        Invoice identicalInvoice = PersistenceTestData.CreateInvoice(identicalOrganization, 21m);
        Invoice differentInvoice = PersistenceTestData.CreateInvoice(differentOrganization, 22m);
        await SeedAsync(identicalInvoice, differentInvoice);
        OpenReviewCaseResponse identical = await OpenAsync(
            identicalOrganization,
            new OpenReviewCaseCommand(
                ReviewCaseReason.NeedsInvestigation,
                identicalInvoice.Id,
                null,
                null,
                null));
        OpenReviewCaseResponse different = await OpenAsync(
            differentOrganization,
            new OpenReviewCaseCommand(
                ReviewCaseReason.NeedsInvestigation,
                differentInvoice.Id,
                null,
                null,
                null));

        Result<ReviewCaseResponse>[] sameDecision = await ResolveTogetherAsync(
            identicalOrganization,
            new ReviewCaseId(identical.ReviewCase.ReviewCaseId),
            identical.ReviewCase.ConcurrencyToken,
            "shared",
            "shared");
        Result<ReviewCaseResponse>[] differentDecision = await DecideTogetherAsync(
            differentOrganization,
            new ReviewCaseId(different.ReviewCase.ReviewCaseId),
            different.ReviewCase.ConcurrencyToken);

        ReviewCaseResponse firstDecision = Require(sameDecision[0]);
        ReviewCaseResponse secondDecision = Require(sameDecision[1]);
        Assert.Equal(firstDecision.ConcurrencyToken, secondDecision.ConcurrencyToken);
        Assert.Equal(
            firstDecision.ConcurrencyToken,
            await RowVersionTokenAsync(new ReviewCaseId(identical.ReviewCase.ReviewCaseId)));
        Assert.NotEqual(identical.ReviewCase.ConcurrencyToken, firstDecision.ConcurrencyToken);
        ReviewCaseResponse winningDecision = Require(
            Assert.Single(differentDecision, result => result.IsSuccess));
        Result<ReviewCaseResponse> losingDecision = Assert.Single(
            differentDecision,
            result => result.IsFailure);
        Assert.Equal(
            "reconciliation.review_case.terminal_conflict",
            losingDecision.Error!.Code);
        Assert.Equal(
            winningDecision.ConcurrencyToken,
            await RowVersionTokenAsync(new ReviewCaseId(different.ReviewCase.ReviewCaseId)));
    }

    [Fact]
    public async Task Allocation_recorded_validates_the_link_and_does_not_allocate_money()
    {
        OrganizationId organizationId = OrganizationId.New();
        OrganizationId otherOrganizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 100m);
        Invoice otherInvoice = PersistenceTestData.CreateInvoice(organizationId, 100m, "INV-002");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 100m);
        BankTransaction otherBank = PersistenceTestData.CreateBankTransaction(
            organizationId,
            100m,
            reference: "OTHER");
        Invoice foreignInvoice = PersistenceTestData.CreateInvoice(otherOrganizationId, 10m, "INV-F");
        BankTransaction foreignBank = PersistenceTestData.CreateBankTransaction(
            otherOrganizationId,
            10m,
            reference: "FOREIGN");
        await SeedAsync(invoice, otherInvoice, bankTransaction, otherBank, foreignInvoice, foreignBank);
        Allocation matching = CreateAllocation(organizationId, invoice.Id, bankTransaction.Id, 10m);
        Allocation wrongInvoice = CreateAllocation(organizationId, otherInvoice.Id, bankTransaction.Id, 10m);
        Allocation wrongBank = CreateAllocation(organizationId, invoice.Id, otherBank.Id, 10m);
        Allocation reversed = CreateAllocation(organizationId, invoice.Id, bankTransaction.Id, 5m);
        Allocation foreign = CreateAllocation(otherOrganizationId, foreignInvoice.Id, foreignBank.Id, 5m);
        await SeedAsync(matching, wrongInvoice, wrongBank, reversed, foreign);
        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            Allocation stored = await context.Allocations.SingleAsync(candidate => candidate.Id == reversed.Id);
            Assert.True(stored.Reverse("duplicate", OpenedAt.AddMinutes(1)).IsSuccess);
            await context.SaveChangesAsync();
        }

        OpenReviewCaseResponse opened = await OpenAsync(
            organizationId,
            new OpenReviewCaseCommand(
                ReviewCaseReason.CompetingMatch,
                invoice.Id,
                bankTransaction.Id,
                null,
                null));
        FinancialSnapshot before = await ReadFinancialAsync(organizationId, invoice.Id, bankTransaction.Id);
        ReviewCaseId reviewCaseId = new(opened.ReviewCase.ReviewCaseId);
        string token = opened.ReviewCase.ConcurrencyToken;

        Assert.Equal(
            "reconciliation.review_case.allocation_mismatch",
            (await ResolveResultAsync(
                organizationId,
                ResolveCommand(reviewCaseId, wrongInvoice.Id, token))).Error!.Code);
        Assert.Equal(
            "reconciliation.review_case.allocation_mismatch",
            (await ResolveResultAsync(
                organizationId,
                ResolveCommand(reviewCaseId, wrongBank.Id, token))).Error!.Code);
        Assert.Equal(
            "reconciliation.review_case.allocation_mismatch",
            (await ResolveResultAsync(
                organizationId,
                ResolveCommand(reviewCaseId, reversed.Id, token))).Error!.Code);
        Assert.Equal(
            "reconciliation.review_case.allocation_mismatch",
            (await ResolveResultAsync(
                organizationId,
                ResolveCommand(reviewCaseId, foreign.Id, token))).Error!.Code);
        Assert.Equal(
            "reconciliation.review_case.allocation_mismatch",
            (await ResolveResultAsync(
                organizationId,
                ResolveCommand(reviewCaseId, AllocationId.New(), token))).Error!.Code);

        ReviewCaseResponse resolved = await ResolveAsync(
            organizationId,
            ResolveCommand(reviewCaseId, matching.Id, token));
        FinancialSnapshot after = await ReadFinancialAsync(organizationId, invoice.Id, bankTransaction.Id);

        Assert.Equal("AllocationRecorded", resolved.Outcome);
        Assert.Equal(matching.Id.Value, resolved.AllocationId);
        Assert.Equal(before.InvoiceAllocated, after.InvoiceAllocated);
        Assert.Equal(before.BankAllocated, after.BankAllocated);
        Assert.Equal(before.InvoiceVersion, after.InvoiceVersion);
        Assert.Equal(before.BankVersion, after.BankVersion);
        Assert.Equal(before.AllocationCount, after.AllocationCount);
        Assert.Equal(before.ImportCount, after.ImportCount);
        Assert.Equal(0m, after.InvoiceAllocated);
        Assert.Equal(0m, after.BankAllocated);
    }

    [Fact]
    public async Task List_and_get_are_tenant_scoped_ordered_and_read_current_balances()
    {
        OrganizationId organizationId = OrganizationId.New();
        OrganizationId otherOrganizationId = OrganizationId.New();
        Invoice firstInvoice = PersistenceTestData.CreateInvoice(organizationId, 80m, "INV-A");
        Invoice secondInvoice = PersistenceTestData.CreateInvoice(organizationId, 90m, "INV-B");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 70m);
        Invoice otherInvoice = PersistenceTestData.CreateInvoice(otherOrganizationId, 10m, "INV-X");
        await SeedAsync(firstInvoice, secondInvoice, bankTransaction, otherInvoice);
        FixedTimeProvider sameTime = new(OpenedAt);
        OpenReviewCaseResponse first = await OpenAsync(
            organizationId,
            new OpenReviewCaseCommand(
                ReviewCaseReason.NeedsInvestigation,
                firstInvoice.Id,
                null,
                null,
                null),
            sameTime);
        OpenReviewCaseResponse second = await OpenAsync(
            organizationId,
            new OpenReviewCaseCommand(
                ReviewCaseReason.NeedsInvestigation,
                secondInvoice.Id,
                null,
                null,
                null),
            sameTime);
        OpenReviewCaseResponse bankCase = await OpenAsync(
            organizationId,
            new OpenReviewCaseCommand(
                ReviewCaseReason.NeedsInvestigation,
                null,
                bankTransaction.Id,
                null,
                null),
            new FixedTimeProvider(OpenedAt.AddMinutes(-5)));
        await OpenAsync(
            otherOrganizationId,
            new OpenReviewCaseCommand(
                ReviewCaseReason.NeedsInvestigation,
                otherInvoice.Id,
                null,
                null,
                null));
        await DismissAsync(
            organizationId,
            new DismissReviewCaseCommand(
                new ReviewCaseId(second.ReviewCase.ReviewCaseId),
                null,
                second.ReviewCase.ConcurrencyToken));

        Result<ReviewCaseListResponse> openCases = await ListAsync(
            organizationId,
            new ListReviewCasesQuery(ReviewCaseStatus.Open, null, null, 1, 100));
        Result<ReviewCaseListResponse> allCases = await ListAsync(
            organizationId,
            new ListReviewCasesQuery(null, null, null, 1, 50));
        Result<ReviewCaseListResponse> byInvoice = await ListAsync(
            organizationId,
            new ListReviewCasesQuery(null, firstInvoice.Id, null, 1, 50));
        Result<ReviewCaseListResponse> byBank = await ListAsync(
            organizationId,
            new ListReviewCasesQuery(null, null, bankTransaction.Id, 1, 50));
        Result<ReviewCaseListResponse> tooLarge = await ListAsync(
            organizationId,
            new ListReviewCasesQuery(null, null, null, 1, 101));
        Guid[] persistedOrder = await PersistedOrderAsync(organizationId);
        Result<ReviewCaseDetailResponse> detail = await GetAsync(
            organizationId,
            new ReviewCaseId(first.ReviewCase.ReviewCaseId));

        Assert.True(openCases.IsSuccess);
        Assert.True(allCases.IsSuccess);
        Assert.True(byInvoice.IsSuccess);
        Assert.True(byBank.IsSuccess);
        Assert.True(detail.IsSuccess);
        Assert.Equal(2, openCases.Value.TotalCount);
        Assert.Equal(100, openCases.Value.PageSize);
        Assert.DoesNotContain(
            openCases.Value.Items,
            item => item.ReviewCaseId == second.ReviewCase.ReviewCaseId);
        Assert.Equal(first.ReviewCase.ReviewCaseId, Assert.Single(byInvoice.Value.Items).ReviewCaseId);
        Assert.Equal(bankCase.ReviewCase.ReviewCaseId, Assert.Single(byBank.Value.Items).ReviewCaseId);
        Assert.Equal("reconciliation.review_cases.invalid_pagination", tooLarge.Error!.Code);
        Assert.Equal(
            persistedOrder,
            allCases.Value.Items.Select(item => item.ReviewCaseId).ToArray());
        Assert.Equal(3, allCases.Value.TotalCount);
        Assert.Equal(first.ReviewCase.ConcurrencyToken, detail.Value.ReviewCase.ConcurrencyToken);
        Assert.True(detail.Value.LinkedFinancials.InvoiceFound);
        Assert.Equal(80m, detail.Value.LinkedFinancials.InvoiceOutstanding!.Amount);
        Assert.Equal("PLN", detail.Value.LinkedFinancials.InvoiceOutstanding.Currency);
        Assert.False(detail.Value.LinkedFinancials.BankTransactionFound);

        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            Invoice stored = await context.Invoices.SingleAsync(candidate => candidate.Id == firstInvoice.Id);
            Assert.True(stored.Allocate(new Money(30m, PersistenceTestData.Pln)).IsSuccess);
            await context.SaveChangesAsync();
        }

        Result<ReviewCaseDetailResponse> afterAllocation = await GetAsync(
            organizationId,
            new ReviewCaseId(first.ReviewCase.ReviewCaseId));

        Assert.True(afterAllocation.IsSuccess);
        Assert.Equal("Open", afterAllocation.Value.ReviewCase.Status);
        Assert.Equal(detail.Value.ReviewCase.ConcurrencyToken, afterAllocation.Value.ReviewCase.ConcurrencyToken);
        Assert.Equal(50m, afterAllocation.Value.LinkedFinancials.InvoiceOutstanding!.Amount);
        Assert.Null(afterAllocation.Value.ReviewCase.AllocationId);
    }

    private static T Require<T>(Result<T> result)
        where T : notnull
    {
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private async Task<OpenReviewCaseResponse> OpenAsync(
        OrganizationId organizationId,
        OpenReviewCaseCommand command,
        TimeProvider? time = null)
    {
        Result<OpenReviewCaseResponse> result = await OpenResultAsync(organizationId, command, time);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private async Task<Result<OpenReviewCaseResponse>> OpenResultAsync(
        OrganizationId organizationId,
        OpenReviewCaseCommand command,
        TimeProvider? time = null)
    {
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        OpenReviewCaseHandler handler = CreateOpenHandler(scope.ServiceProvider, organizationId, time);
        return await handler.Handle(command, CancellationToken.None);
    }

    private async Task<ReviewCaseResponse> ResolveAsync(
        OrganizationId organizationId,
        ResolveReviewCaseCommand command)
    {
        Result<ReviewCaseResponse> result = await ResolveResultAsync(organizationId, command);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private async Task<Result<ReviewCaseResponse>> ResolveResultAsync(
        OrganizationId organizationId,
        ResolveReviewCaseCommand command)
    {
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        ResolveReviewCaseHandler handler = CreateResolveHandler(scope.ServiceProvider, organizationId);
        return await handler.Handle(command, CancellationToken.None);
    }

    private async Task<ReviewCaseResponse> DismissAsync(
        OrganizationId organizationId,
        DismissReviewCaseCommand command)
    {
        Result<ReviewCaseResponse> result = await DismissResultAsync(organizationId, command);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private async Task<Result<ReviewCaseResponse>> DismissResultAsync(
        OrganizationId organizationId,
        DismissReviewCaseCommand command)
    {
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        DismissReviewCaseHandler handler = CreateDismissHandler(scope.ServiceProvider, organizationId);
        return await handler.Handle(command, CancellationToken.None);
    }

    private async Task<Result<ReviewCaseListResponse>> ListAsync(
        OrganizationId organizationId,
        ListReviewCasesQuery query)
    {
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        ListReviewCasesHandler handler = new(
            new TestCurrentOrganizationContext(organizationId),
            scope.ServiceProvider.GetRequiredService<IReviewCaseQueries>());
        return await handler.Handle(query, CancellationToken.None);
    }

    private async Task<Result<ReviewCaseDetailResponse>> GetAsync(
        OrganizationId organizationId,
        ReviewCaseId reviewCaseId)
    {
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        GetReviewCaseHandler handler = new(
            new TestCurrentOrganizationContext(organizationId),
            scope.ServiceProvider.GetRequiredService<IReviewCaseQueries>());
        return await handler.Handle(new GetReviewCaseQuery(reviewCaseId), CancellationToken.None);
    }

    private async Task<Result<OpenReviewCaseResponse>[]> OpenTogetherAsync(
        OrganizationId organizationId,
        InvoiceId invoiceId,
        string firstNote,
        string secondNote)
    {
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        using Barrier gate = new(2);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        string[] notes = [firstNote, secondNote];

        Task<Result<OpenReviewCaseResponse>> Start(string note)
        {
            return Task.Run(
                async () =>
                {
                    await using AsyncServiceScope scope = provider.CreateAsyncScope();
                    OpenReviewCaseHandler handler = CreateOpenHandler(
                        scope.ServiceProvider,
                        organizationId,
                        unitOfWork: new GatedUnitOfWork(
                            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
                            gate));
                    return await handler.Handle(
                        new OpenReviewCaseCommand(
                            ReviewCaseReason.NeedsInvestigation,
                            invoiceId,
                            null,
                            null,
                            note),
                        timeout.Token);
                },
                timeout.Token);
        }

        return await Task.WhenAll(notes.Select(Start));
    }

    private async Task<Result<ReviewCaseResponse>[]> ResolveTogetherAsync(
        OrganizationId organizationId,
        ReviewCaseId reviewCaseId,
        string token,
        string firstNote,
        string secondNote)
    {
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        using Barrier gate = new(2);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        string[] notes = [firstNote, secondNote];

        Task<Result<ReviewCaseResponse>> Start(string note)
        {
            return Task.Run(
                async () =>
                {
                    await using AsyncServiceScope scope = provider.CreateAsyncScope();
                    ResolveReviewCaseHandler handler = CreateResolveHandler(
                        scope.ServiceProvider,
                        organizationId,
                        new GatedUnitOfWork(
                            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
                            gate));
                    return await handler.Handle(
                        new ResolveReviewCaseCommand(
                            reviewCaseId,
                            ReviewCaseOutcome.NoActionRequired,
                            null,
                            note,
                            token),
                        timeout.Token);
                },
                timeout.Token);
        }

        return await Task.WhenAll(notes.Select(Start));
    }

    private async Task<Result<ReviewCaseResponse>[]> DecideTogetherAsync(
        OrganizationId organizationId,
        ReviewCaseId reviewCaseId,
        string token)
    {
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        using Barrier gate = new(2);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));

        Task<Result<ReviewCaseResponse>> Resolve()
        {
            return Task.Run(
                async () =>
                {
                    await using AsyncServiceScope scope = provider.CreateAsyncScope();
                    ResolveReviewCaseHandler handler = CreateResolveHandler(
                        scope.ServiceProvider,
                        organizationId,
                        new GatedUnitOfWork(
                            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
                            gate));
                    return await handler.Handle(
                        new ResolveReviewCaseCommand(
                            reviewCaseId,
                            ReviewCaseOutcome.NoActionRequired,
                            null,
                            "resolved",
                            token),
                        timeout.Token);
                },
                timeout.Token);
        }

        Task<Result<ReviewCaseResponse>> Dismiss()
        {
            return Task.Run(
                async () =>
                {
                    await using AsyncServiceScope scope = provider.CreateAsyncScope();
                    DismissReviewCaseHandler handler = CreateDismissHandler(
                        scope.ServiceProvider,
                        organizationId,
                        new GatedUnitOfWork(
                            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
                            gate));
                    return await handler.Handle(
                        new DismissReviewCaseCommand(reviewCaseId, "dismissed", token),
                        timeout.Token);
                },
                timeout.Token);
        }

        return await Task.WhenAll(Resolve(), Dismiss());
    }

    private static OpenReviewCaseHandler CreateOpenHandler(
        IServiceProvider services,
        OrganizationId organizationId,
        TimeProvider? time = null,
        IUnitOfWork? unitOfWork = null)
    {
        return new OpenReviewCaseHandler(
            new TestCurrentOrganizationContext(organizationId),
            new TestCurrentActorContext(),
            services.GetRequiredService<IInvoiceRepository>(),
            services.GetRequiredService<IBankTransactionRepository>(),
            services.GetRequiredService<IReviewCaseRepository>(),
            services.GetRequiredService<IReviewCaseSnapshotLookup>(),
            services.GetRequiredService<IReviewCaseOpenConflict>(),
            services.GetRequiredService<IReviewCaseAuditWriter>(),
            unitOfWork ?? services.GetRequiredService<IUnitOfWork>(),
            time ?? new FixedTimeProvider(OpenedAt));
    }

    private static ResolveReviewCaseHandler CreateResolveHandler(
        IServiceProvider services,
        OrganizationId organizationId,
        IUnitOfWork? unitOfWork = null)
    {
        return new ResolveReviewCaseHandler(
            new TestCurrentOrganizationContext(organizationId),
            new TestCurrentActorContext(),
            services.GetRequiredService<IReviewCaseRepository>(),
            services.GetRequiredService<IReviewCaseSnapshotLookup>(),
            services.GetRequiredService<IAllocationLinkLookup>(),
            services.GetRequiredService<IReviewCaseAuditWriter>(),
            services.GetRequiredService<IReviewCaseAuditConflict>(),
            unitOfWork ?? services.GetRequiredService<IUnitOfWork>(),
            new FixedTimeProvider(OpenedAt));
    }

    private static DismissReviewCaseHandler CreateDismissHandler(
        IServiceProvider services,
        OrganizationId organizationId,
        IUnitOfWork? unitOfWork = null)
    {
        return new DismissReviewCaseHandler(
            new TestCurrentOrganizationContext(organizationId),
            new TestCurrentActorContext(),
            services.GetRequiredService<IReviewCaseRepository>(),
            services.GetRequiredService<IReviewCaseSnapshotLookup>(),
            services.GetRequiredService<IReviewCaseAuditWriter>(),
            services.GetRequiredService<IReviewCaseAuditConflict>(),
            unitOfWork ?? services.GetRequiredService<IUnitOfWork>(),
            new FixedTimeProvider(OpenedAt));
    }

    private static Allocation CreateAllocation(
        OrganizationId organizationId,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        decimal amount)
    {
        return new Allocation(
            AllocationId.New(),
            organizationId,
            invoiceId,
            bankTransactionId,
            new Money(amount, PersistenceTestData.Pln),
            OpenedAt,
            Guid.NewGuid(),
            AllocationIdempotencyOperation.ExplicitAmount);
    }

    private static ResolveReviewCaseCommand ResolveCommand(
        ReviewCaseId reviewCaseId,
        AllocationId allocationId,
        string token)
    {
        return new ResolveReviewCaseCommand(
            reviewCaseId,
            ReviewCaseOutcome.AllocationRecorded,
            allocationId,
            "recorded",
            token);
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

    private async Task<string> RowVersionTokenAsync(ReviewCaseId reviewCaseId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        ReviewCase reviewCase = await context.ReviewCases.SingleAsync(candidate => candidate.Id == reviewCaseId);
        byte[] rowVersion = context.Entry(reviewCase).Property<byte[]>("RowVersion").CurrentValue!;
        return ReviewCaseConcurrencyToken.Encode(rowVersion);
    }

    private async Task<Guid[]> PersistedOrderAsync(OrganizationId organizationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.ReviewCases
            .AsNoTracking()
            .Where(reviewCase => reviewCase.OrganizationId == organizationId)
            .OrderByDescending(reviewCase => reviewCase.OpenedAt)
            .ThenByDescending(reviewCase => reviewCase.Id)
            .Select(reviewCase => reviewCase.Id.Value)
            .ToArrayAsync();
    }

    private async Task<FinancialSnapshot> ReadFinancialAsync(
        OrganizationId organizationId,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Invoice invoice = await context.Invoices.SingleAsync(candidate => candidate.Id == invoiceId);
        BankTransaction bankTransaction = await context.BankTransactions.SingleAsync(
            candidate => candidate.Id == bankTransactionId);
        return new FinancialSnapshot(
            invoice.AllocatedAmount.Amount,
            bankTransaction.AllocatedAmount.Amount,
            context.Entry(invoice).Property<byte[]>("RowVersion").CurrentValue!.ToArray(),
            context.Entry(bankTransaction).Property<byte[]>("RowVersion").CurrentValue!.ToArray(),
            await context.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId),
            await context.ImportBatches.CountAsync());
    }

    private sealed record FinancialSnapshot(
        decimal InvoiceAllocated,
        decimal BankAllocated,
        byte[] InvoiceVersion,
        byte[] BankVersion,
        int AllocationCount,
        int ImportCount);

    private sealed class GatedUnitOfWork : IUnitOfWork
    {
        private readonly IUnitOfWork _inner;
        private readonly Barrier _barrier;

        public GatedUnitOfWork(IUnitOfWork inner, Barrier barrier)
        {
            _inner = inner;
            _barrier = barrier;
        }

        public async Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            _barrier.SignalAndWait(cancellationToken);
            await _inner.SaveChangesAsync(cancellationToken);
        }
    }

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