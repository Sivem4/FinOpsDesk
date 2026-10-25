using FinOpsDesk.Modules.Reconciliation.Application.Matching;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.Matching;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class ExactMatchCandidateQueriesTests
{
    private readonly SqlServerFixture _fixture;

    public ExactMatchCandidateQueriesTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Open_invoices_are_limited_to_the_requested_organization()
    {
        OrganizationId organizationA = OrganizationId.New();
        OrganizationId organizationB = OrganizationId.New();
        Invoice invoiceA = CreateInvoice(organizationA, "INV-A", 80m);
        Invoice invoiceB = CreateInvoice(organizationB, "INV-A", 80m);
        await SaveAsync([invoiceA, invoiceB]);

        ExactMatchCandidateSet set = await LoadAsync(organizationA);

        MatchInvoiceSnapshot loaded = Assert.Single(set.Invoices);
        Assert.Equal(invoiceA.Id, loaded.InvoiceId);
        Assert.Equal(organizationA, loaded.OrganizationId);
        Assert.Equal(80m, loaded.OutstandingAmount.Amount);
        Assert.Equal("PLN", loaded.OutstandingAmount.Currency.Value);
        Assert.Empty(set.BankTransactions);
    }

    [Fact]
    public async Task Open_bank_transactions_are_limited_to_the_requested_organization()
    {
        OrganizationId organizationA = OrganizationId.New();
        OrganizationId organizationB = OrganizationId.New();
        BankTransaction transactionA = CreateBankTransaction(organizationA, 80m, "INV-A");
        BankTransaction transactionB = CreateBankTransaction(organizationB, 80m, "INV-A");
        await SaveAsync([], [transactionA, transactionB]);

        ExactMatchCandidateSet set = await LoadAsync(organizationA);

        MatchBankTransactionSnapshot loaded = Assert.Single(set.BankTransactions);
        Assert.Equal(transactionA.Id, loaded.BankTransactionId);
        Assert.Equal(organizationA, loaded.OrganizationId);
        Assert.Equal(80m, loaded.UnappliedAmount.Amount);
        Assert.Equal("INV-A", loaded.Reference);
        Assert.Empty(set.Invoices);
    }

    [Fact]
    public async Task Identical_references_and_amounts_in_another_organization_do_not_interact()
    {
        OrganizationId organizationA = OrganizationId.New();
        OrganizationId organizationB = OrganizationId.New();
        Invoice invoiceA = CreateInvoice(organizationA, "INV-100", 80m);
        Invoice invoiceB = CreateInvoice(organizationB, "INV-100", 80m);
        BankTransaction transactionA = CreateBankTransaction(organizationA, 80m, "inv-100");
        BankTransaction transactionB = CreateBankTransaction(organizationB, 80m, "inv-100");
        await SaveAsync([invoiceA, invoiceB], [transactionA, transactionB]);

        ExactMatchCandidateSet setA = await LoadAsync(organizationA);
        Assert.Equal(invoiceA.Id, Assert.Single(setA.Invoices).InvoiceId);
        Assert.Equal(transactionA.Id, Assert.Single(setA.BankTransactions).BankTransactionId);

        ExactMatchBatchResult result = ExactMatchRules.Evaluate(
            organizationA,
            setA.Invoices,
            setA.BankTransactions);
        ExactMatch match = Assert.Single(result.ExactMatches);
        Assert.Equal(invoiceA.Id, match.InvoiceId);
        Assert.Equal(transactionA.Id, match.BankTransactionId);
        Assert.Empty(result.AmbiguousGroups);

        ExactMatchCandidateSet setB = await LoadAsync(organizationB);
        Assert.Equal(invoiceB.Id, Assert.Single(setB.Invoices).InvoiceId);
        Assert.Equal(transactionB.Id, Assert.Single(setB.BankTransactions).BankTransactionId);
    }

    [Fact]
    public async Task Fully_allocated_invoices_are_excluded()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice open = CreateInvoice(organizationId, "INV-1", 50m);
        Invoice closed = CreateInvoice(organizationId, "INV-1", 50m, allocatedAmount: 50m);
        BankTransaction bankTransaction = CreateBankTransaction(organizationId, 50m, "INV-1");
        await SaveAsync([open, closed], [bankTransaction]);

        ExactMatchCandidateSet set = await LoadAsync(organizationId);

        Assert.Equal(open.Id, Assert.Single(set.Invoices).InvoiceId);
        Assert.DoesNotContain(set.Invoices, invoice => invoice.InvoiceId == closed.Id);
        ExactMatch match = Assert.Single(
            ExactMatchRules.Evaluate(organizationId, set.Invoices, set.BankTransactions).ExactMatches);
        Assert.Equal(open.Id, match.InvoiceId);
        Assert.Equal(bankTransaction.Id, match.BankTransactionId);
    }

    [Fact]
    public async Task Fully_allocated_bank_transactions_are_excluded()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = CreateInvoice(organizationId, "INV-1", 50m);
        BankTransaction open = CreateBankTransaction(organizationId, 50m, "INV-1");
        BankTransaction closed = CreateBankTransaction(
            organizationId,
            50m,
            "INV-1",
            allocatedAmount: 50m);
        await SaveAsync([invoice], [open, closed]);

        ExactMatchCandidateSet set = await LoadAsync(organizationId);

        Assert.Equal(open.Id, Assert.Single(set.BankTransactions).BankTransactionId);
        Assert.DoesNotContain(
            set.BankTransactions,
            bankTransaction => bankTransaction.BankTransactionId == closed.Id);
        ExactMatch match = Assert.Single(
            ExactMatchRules.Evaluate(organizationId, set.Invoices, set.BankTransactions).ExactMatches);
        Assert.Equal(invoice.Id, match.InvoiceId);
        Assert.Equal(open.Id, match.BankTransactionId);
    }

    [Fact]
    public async Task Partial_allocations_project_the_remaining_decimal_balance()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = CreateInvoice(
            organizationId,
            "INV-PART",
            1000.2500m,
            allocatedAmount: 250.1000m);
        BankTransaction bankTransaction = CreateBankTransaction(
            organizationId,
            1000.2500m,
            "inv-part",
            allocatedAmount: 250.1000m);
        await SaveAsync([invoice], [bankTransaction]);

        ExactMatchCandidateSet set = await LoadAsync(organizationId);

        MatchInvoiceSnapshot loadedInvoice = Assert.Single(set.Invoices);
        MatchBankTransactionSnapshot loadedTransaction = Assert.Single(set.BankTransactions);
        Assert.Equal(750.1500m, loadedInvoice.OutstandingAmount.Amount);
        Assert.Equal(750.1500m, loadedTransaction.UnappliedAmount.Amount);
        Assert.Equal(4, DecimalScale(loadedInvoice.OutstandingAmount.Amount));
        Assert.Equal(4, DecimalScale(loadedTransaction.UnappliedAmount.Amount));
        Assert.Equal("PLN", loadedInvoice.OutstandingAmount.Currency.Value);
        Assert.Equal("PLN", loadedTransaction.UnappliedAmount.Currency.Value);

        ExactMatch match = Assert.Single(
            ExactMatchRules.Evaluate(organizationId, set.Invoices, set.BankTransactions).ExactMatches);
        Assert.Equal(invoice.Id, match.InvoiceId);
        Assert.Equal(bankTransaction.Id, match.BankTransactionId);
        Assert.Equal(750.1500m, match.Amount.Amount);
    }

    [Fact]
    public async Task Null_and_blank_references_stay_in_the_open_candidate_set()
    {
        OrganizationId organizationId = OrganizationId.New();
        BankTransaction nullReference = CreateBankTransaction(organizationId, 15m, null);
        await SaveAsync([], [nullReference]);
        await InsertInvoiceAsync(organizationId, Guid.NewGuid(), "   ", 12m, "PLN", 0m, "PLN");
        Guid whitespaceTransactionId = Guid.NewGuid();
        Guid emptyTransactionId = Guid.NewGuid();
        await InsertBankTransactionAsync(
            organizationId,
            whitespaceTransactionId,
            "   ",
            11m,
            "PLN",
            0m,
            "PLN");
        await InsertBankTransactionAsync(
            organizationId,
            emptyTransactionId,
            string.Empty,
            9m,
            "PLN",
            0m,
            "PLN");

        ExactMatchCandidateSet set = await LoadAsync(organizationId);

        Assert.Equal("   ", Assert.Single(set.Invoices).InvoiceNumber);
        Assert.Equal(3, set.BankTransactions.Count);
        Assert.Contains(
            set.BankTransactions,
            bankTransaction => bankTransaction.BankTransactionId == nullReference.Id
                && bankTransaction.Reference is null);
        Assert.Contains(
            set.BankTransactions,
            bankTransaction => bankTransaction.BankTransactionId.Value == whitespaceTransactionId
                && bankTransaction.Reference == "   ");
        Assert.Contains(
            set.BankTransactions,
            bankTransaction => bankTransaction.BankTransactionId.Value == emptyTransactionId
                && bankTransaction.Reference == string.Empty);

        ExactMatchBatchResult result = ExactMatchRules.Evaluate(
            organizationId,
            set.Invoices,
            set.BankTransactions);
        Assert.Empty(result.ExactMatches);
        Assert.Empty(result.AmbiguousGroups);
        Assert.Equal(4, result.Ineligible.Count);
        Assert.All(
            result.Ineligible,
            record => Assert.Equal(MatchReasonCode.MissingReference, record.Reason));
    }

    [Fact]
    public async Task Competing_open_records_remain_ambiguous()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice first = CreateInvoice(organizationId, "INV-9", 40m);
        Invoice second = CreateInvoice(organizationId, "inv-9", 40m);
        BankTransaction bankTransaction = CreateBankTransaction(organizationId, 40m, " INV-9 ");
        await SaveAsync([first, second], [bankTransaction]);

        ExactMatchCandidateSet set = await LoadAsync(organizationId);

        Assert.Equal(2, set.Invoices.Count);
        Assert.Single(set.BankTransactions);
        ExactMatchBatchResult result = ExactMatchRules.Evaluate(
            organizationId,
            set.Invoices,
            set.BankTransactions);
        Assert.Empty(result.ExactMatches);
        AmbiguousMatchGroup group = Assert.Single(result.AmbiguousGroups);
        Assert.Equal("INV-9", group.NormalizedReference);
        Assert.Equal(MatchReasonCode.CompetingInvoices, group.Reason);
        Assert.Equal(40m, group.OpenAmount.Amount);
        Assert.Equal(2, group.InvoiceIds.Count);
        Assert.Equal(bankTransaction.Id, Assert.Single(group.BankTransactionIds));
    }

    [Fact]
    public async Task Open_candidate_queries_do_not_track_entities()
    {
        OrganizationId organizationId = OrganizationId.New();
        await SaveAsync(
            [CreateInvoice(organizationId, "INV-1", 10m)],
            [CreateBankTransaction(organizationId, 10m, "INV-1")]);

        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IExactMatchCandidateQueries queries =
            scope.ServiceProvider.GetRequiredService<IExactMatchCandidateQueries>();
        ReconciliationDbContext context =
            scope.ServiceProvider.GetRequiredService<ReconciliationDbContext>();

        ExactMatchCandidateSet set = await queries.GetOpenCandidatesAsync(
            organizationId,
            ExactMatchCandidateLimits.MaxPerSide,
            CancellationToken.None);

        Assert.Single(set.Invoices);
        Assert.Single(set.BankTransactions);
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Empty(context.Invoices.Local);
        Assert.Empty(context.BankTransactions.Local);
    }

    [Fact]
    public async Task Candidate_limit_boundary_includes_every_open_row()
    {
        OrganizationId organizationId = OrganizationId.New();
        await SaveAsync(
            [
                CreateInvoice(organizationId, "INV-1", 10m),
                CreateInvoice(organizationId, "INV-2", 11m),
                CreateInvoice(organizationId, "INV-CLOSED-1", 12m, allocatedAmount: 12m),
                CreateInvoice(organizationId, "INV-CLOSED-2", 13m, allocatedAmount: 13m),
                CreateInvoice(organizationId, "INV-CLOSED-3", 14m, allocatedAmount: 14m),
            ],
            [
                CreateBankTransaction(organizationId, 10m, "REF-1"),
                CreateBankTransaction(organizationId, 11m, "REF-2"),
                CreateBankTransaction(organizationId, 12m, "REF-CLOSED", allocatedAmount: 12m),
            ]);

        ExactMatchCandidateSet set = await LoadAsync(organizationId, limit: 2);

        Assert.False(set.InvoiceLimitExceeded);
        Assert.False(set.BankTransactionLimitExceeded);
        Assert.False(set.InconsistentMoney);
        Assert.Equal(2, set.Invoices.Count);
        Assert.Equal(2, set.BankTransactions.Count);
        Assert.DoesNotContain(
            set.Invoices,
            invoice => invoice.InvoiceNumber is not null
                && invoice.InvoiceNumber.StartsWith("INV-CLOSED", StringComparison.Ordinal));
        Assert.DoesNotContain(
            set.BankTransactions,
            bankTransaction => bankTransaction.Reference == "REF-CLOSED");
    }

    [Fact]
    public async Task Invoice_candidate_overflow_returns_no_snapshots()
    {
        OrganizationId organizationId = OrganizationId.New();
        await SaveAsync(
            [
                CreateInvoice(organizationId, "INV-1", 10m),
                CreateInvoice(organizationId, "INV-2", 11m),
                CreateInvoice(organizationId, "INV-3", 12m),
            ],
            [CreateBankTransaction(organizationId, 10m, "INV-1")]);

        ExactMatchCandidateSet set = await LoadAsync(organizationId, limit: 2);

        Assert.True(set.InvoiceLimitExceeded);
        Assert.False(set.BankTransactionLimitExceeded);
        Assert.False(set.InconsistentMoney);
        Assert.Empty(set.Invoices);
        Assert.Empty(set.BankTransactions);
    }

    [Fact]
    public async Task Bank_transaction_candidate_overflow_returns_no_snapshots()
    {
        OrganizationId organizationId = OrganizationId.New();
        await SaveAsync(
            [CreateInvoice(organizationId, "INV-1", 10m)],
            [
                CreateBankTransaction(organizationId, 10m, "INV-1"),
                CreateBankTransaction(organizationId, 10m, "INV-1"),
                CreateBankTransaction(organizationId, 10m, "INV-2"),
            ]);

        ExactMatchCandidateSet set = await LoadAsync(organizationId, limit: 2);

        Assert.False(set.InvoiceLimitExceeded);
        Assert.True(set.BankTransactionLimitExceeded);
        Assert.False(set.InconsistentMoney);
        Assert.Empty(set.Invoices);
        Assert.Empty(set.BankTransactions);
    }

    [Fact]
    public async Task Mismatched_invoice_currencies_fail_the_open_book()
    {
        OrganizationId organizationId = OrganizationId.New();
        await SeedValidPairAsync(organizationId);
        await InsertInvoiceAsync(
            organizationId,
            Guid.NewGuid(),
            "CORRUPT-ROW",
            totalAmount: 100m,
            totalCurrency: "PLN",
            allocatedAmount: 0m,
            allocatedCurrency: "EUR");

        await AssertInconsistentAsync(organizationId);
    }

    [Fact]
    public async Task Mismatched_bank_transaction_currencies_fail_the_open_book()
    {
        OrganizationId organizationId = OrganizationId.New();
        await SeedValidPairAsync(organizationId);
        await InsertBankTransactionAsync(
            organizationId,
            Guid.NewGuid(),
            "CORRUPT-ROW",
            amount: 100m,
            currency: "PLN",
            allocatedAmount: 0m,
            allocatedCurrency: "EUR");

        await AssertInconsistentAsync(organizationId);
    }

    [Fact]
    public async Task Allocated_invoice_amount_above_the_total_fails_the_open_book()
    {
        OrganizationId organizationId = OrganizationId.New();
        await SeedValidPairAsync(organizationId);
        await InsertInvoiceAsync(
            organizationId,
            Guid.NewGuid(),
            "CORRUPT-ROW",
            totalAmount: 100m,
            totalCurrency: "PLN",
            allocatedAmount: 150m,
            allocatedCurrency: "PLN");

        await AssertInconsistentAsync(organizationId);
    }

    [Fact]
    public async Task Negative_allocated_invoice_amount_fails_the_open_book()
    {
        OrganizationId organizationId = OrganizationId.New();
        await SeedValidPairAsync(organizationId);
        await InsertInvoiceAsync(
            organizationId,
            Guid.NewGuid(),
            "CORRUPT-ROW",
            totalAmount: 100m,
            totalCurrency: "PLN",
            allocatedAmount: -1m,
            allocatedCurrency: "PLN");

        await AssertInconsistentAsync(organizationId);
    }

    [Fact]
    public async Task Non_positive_invoice_total_fails_the_open_book()
    {
        OrganizationId organizationId = OrganizationId.New();
        await SeedValidPairAsync(organizationId);
        await InsertInvoiceAsync(
            organizationId,
            Guid.NewGuid(),
            "CORRUPT-ROW",
            totalAmount: 0m,
            totalCurrency: "PLN",
            allocatedAmount: 0m,
            allocatedCurrency: "PLN");

        await AssertInconsistentAsync(organizationId);
    }

    [Fact]
    public async Task Allocated_bank_transaction_amount_above_the_original_fails_the_open_book()
    {
        OrganizationId organizationId = OrganizationId.New();
        await SeedValidPairAsync(organizationId);
        await InsertBankTransactionAsync(
            organizationId,
            Guid.NewGuid(),
            "CORRUPT-ROW",
            amount: 100m,
            currency: "PLN",
            allocatedAmount: 150m,
            allocatedCurrency: "PLN");

        await AssertInconsistentAsync(organizationId);
    }

    [Fact]
    public async Task Reading_candidates_does_not_change_row_versions_or_allocations()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = CreateInvoice(organizationId, "INV-1", 40m, allocatedAmount: 10m);
        BankTransaction bankTransaction = CreateBankTransaction(organizationId, 30m, "INV-1");
        await SaveAsync([invoice], [bankTransaction]);
        byte[] invoiceVersion = await InvoiceRowVersionAsync(invoice.Id);
        byte[] bankTransactionVersion = await BankTransactionRowVersionAsync(bankTransaction.Id);
        int allocations = await AllocationCountAsync(organizationId);

        ExactMatchCandidateSet set = await LoadAsync(organizationId);

        Assert.Single(set.Invoices);
        Assert.Single(set.BankTransactions);
        byte[] invoiceVersionAfter = await InvoiceRowVersionAsync(invoice.Id);
        byte[] bankTransactionVersionAfter = await BankTransactionRowVersionAsync(bankTransaction.Id);
        Assert.True(invoiceVersion.SequenceEqual(invoiceVersionAfter));
        Assert.True(bankTransactionVersion.SequenceEqual(bankTransactionVersionAfter));
        Assert.Equal(allocations, await AllocationCountAsync(organizationId));
        Assert.Equal(0, allocations);
    }

    [Fact]
    public async Task Open_candidate_query_honors_a_cancelled_token()
    {
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IExactMatchCandidateQueries queries =
            scope.ServiceProvider.GetRequiredService<IExactMatchCandidateQueries>();
        using CancellationTokenSource cancellationTokenSource = new();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            queries.GetOpenCandidatesAsync(
                OrganizationId.New(),
                limit: 2,
                cancellationTokenSource.Token));
    }

    [Fact]
    public async Task Open_candidate_query_rejects_a_limit_outside_the_policy()
    {
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IExactMatchCandidateQueries queries =
            scope.ServiceProvider.GetRequiredService<IExactMatchCandidateQueries>();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            queries.GetOpenCandidatesAsync(null!, limit: 1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            queries.GetOpenCandidatesAsync(OrganizationId.New(), limit: 0, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            queries.GetOpenCandidatesAsync(
                OrganizationId.New(),
                ExactMatchCandidateLimits.MaxPerSide + 1,
                CancellationToken.None));
    }

    [Fact]
    public async Task Malformed_persisted_currency_fails_the_open_book()
    {
        OrganizationId organizationId = OrganizationId.New();
        await SeedValidPairAsync(organizationId);
        await InsertInvoiceAsync(
            organizationId,
            Guid.NewGuid(),
            "BAD-CURRENCY",
            totalAmount: 25m,
            totalCurrency: "12A",
            allocatedAmount: 0m,
            allocatedCurrency: "12A");

        await AssertInconsistentAsync(organizationId);
    }

    [Fact]
    public async Task Corruption_in_another_organization_does_not_fail_a_valid_exact_match()
    {
        OrganizationId organizationA = OrganizationId.New();
        OrganizationId organizationB = OrganizationId.New();
        Invoice invoiceA = CreateInvoice(organizationA, "INV-A-EXACT", 80m);
        BankTransaction transactionA = CreateBankTransaction(organizationA, 80m, "INV-A-EXACT");
        await SaveAsync([invoiceA], [transactionA]);
        Guid corruptInvoiceId = Guid.NewGuid();
        const decimal corruptAllocatedAmount = 99999.5m;
        await InsertInvoiceAsync(
            organizationB,
            corruptInvoiceId,
            "ORG-B-CORRUPT",
            totalAmount: 10m,
            totalCurrency: "PLN",
            allocatedAmount: corruptAllocatedAmount,
            allocatedCurrency: "PLN");

        ExactMatchCandidateSet set = await LoadAsync(organizationA);

        Assert.False(set.InconsistentMoney);
        Assert.False(set.InvoiceLimitExceeded);
        Assert.False(set.BankTransactionLimitExceeded);
        MatchInvoiceSnapshot loadedInvoice = Assert.Single(set.Invoices);
        MatchBankTransactionSnapshot loadedTransaction = Assert.Single(set.BankTransactions);
        Assert.Equal(invoiceA.Id, loadedInvoice.InvoiceId);
        Assert.Equal(organizationA, loadedInvoice.OrganizationId);
        Assert.Equal("INV-A-EXACT", loadedInvoice.InvoiceNumber);
        Assert.Equal(80m, loadedInvoice.OutstandingAmount.Amount);
        Assert.Equal(transactionA.Id, loadedTransaction.BankTransactionId);
        Assert.Equal(organizationA, loadedTransaction.OrganizationId);
        Assert.DoesNotContain(set.Invoices, invoice => invoice.InvoiceId.Value == corruptInvoiceId);
        Assert.DoesNotContain(set.Invoices, invoice => invoice.InvoiceNumber == "ORG-B-CORRUPT");
        Assert.DoesNotContain(
            set.Invoices,
            invoice => invoice.OutstandingAmount.Amount == corruptAllocatedAmount);
        Assert.DoesNotContain(
            set.BankTransactions,
            bankTransaction => bankTransaction.OrganizationId == organizationB);

        ExactMatch match = Assert.Single(
            ExactMatchRules.Evaluate(organizationA, set.Invoices, set.BankTransactions).ExactMatches);
        Assert.Equal(invoiceA.Id, match.InvoiceId);
        Assert.Equal(transactionA.Id, match.BankTransactionId);
        Assert.Equal(80m, match.Amount.Amount);
    }

    private async Task AssertInconsistentAsync(OrganizationId organizationId)
    {
        ExactMatchCandidateSet set = await LoadAsync(organizationId);

        Assert.True(set.InconsistentMoney);
        Assert.False(set.InvoiceLimitExceeded);
        Assert.False(set.BankTransactionLimitExceeded);
        Assert.Empty(set.Invoices);
        Assert.Empty(set.BankTransactions);
    }

    private async Task SeedValidPairAsync(OrganizationId organizationId)
    {
        await SaveAsync(
            [CreateInvoice(organizationId, "INV-SAFE", 10m)],
            [CreateBankTransaction(organizationId, 10m, "INV-SAFE")]);
    }

    private async Task<ExactMatchCandidateSet> LoadAsync(
        OrganizationId organizationId,
        int? limit = null)
    {
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IExactMatchCandidateQueries queries =
            scope.ServiceProvider.GetRequiredService<IExactMatchCandidateQueries>();

        return await queries.GetOpenCandidatesAsync(
            organizationId,
            limit ?? ExactMatchCandidateLimits.MaxPerSide,
            CancellationToken.None);
    }

    private async Task SaveAsync(
        IReadOnlyCollection<Invoice> invoices,
        IReadOnlyCollection<BankTransaction>? bankTransactions = null)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.Invoices.AddRange(invoices);

        if (bankTransactions is not null)
        {
            context.BankTransactions.AddRange(bankTransactions);
        }

        await context.SaveChangesAsync();
    }

    private async Task InsertInvoiceAsync(
        OrganizationId organizationId,
        Guid invoiceId,
        string invoiceNumber,
        decimal totalAmount,
        string totalCurrency,
        decimal allocatedAmount,
        string allocatedCurrency)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO [reconciliation].[Invoices]
                ([Id], [OrganizationId], [InvoiceNumber], [AllocatedAmount], [AllocatedCurrency],
                 [TotalAmount], [TotalCurrency])
            VALUES
                ({invoiceId}, {organizationId.Value}, {invoiceNumber}, {allocatedAmount},
                 {allocatedCurrency}, {totalAmount}, {totalCurrency});
            """);
    }

    private async Task InsertBankTransactionAsync(
        OrganizationId organizationId,
        Guid bankTransactionId,
        string? reference,
        decimal amount,
        string currency,
        decimal allocatedAmount,
        string allocatedCurrency)
    {
        DateOnly bookingDate = new(2026, 8, 24);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO [reconciliation].[BankTransactions]
                ([Id], [OrganizationId], [Reference], [BookingDate], [AllocatedAmount],
                 [AllocatedCurrency], [Amount], [Currency])
            VALUES
                ({bankTransactionId}, {organizationId.Value}, {reference}, {bookingDate},
                 {allocatedAmount}, {allocatedCurrency}, {amount}, {currency});
            """);
    }

    private async Task<byte[]> InvoiceRowVersionAsync(InvoiceId invoiceId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Invoice invoice = await context.Invoices.SingleAsync(candidate => candidate.Id == invoiceId);
        return context.Entry(invoice).Property<byte[]>("RowVersion").CurrentValue.ToArray();
    }

    private async Task<byte[]> BankTransactionRowVersionAsync(BankTransactionId bankTransactionId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        BankTransaction bankTransaction = await context.BankTransactions.SingleAsync(
            candidate => candidate.Id == bankTransactionId);
        return context.Entry(bankTransaction).Property<byte[]>("RowVersion").CurrentValue.ToArray();
    }

    private async Task<int> AllocationCountAsync(OrganizationId organizationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.Allocations.CountAsync(
            allocation => allocation.OrganizationId == organizationId);
    }

    private static Invoice CreateInvoice(
        OrganizationId organizationId,
        string invoiceNumber,
        decimal totalAmount,
        decimal? allocatedAmount = null)
    {
        Invoice invoice = new(
            InvoiceId.New(),
            organizationId,
            invoiceNumber,
            new Money(totalAmount, PersistenceTestData.Pln));

        if (allocatedAmount is not null)
        {
            Assert.True(
                invoice.Allocate(new Money(allocatedAmount.Value, PersistenceTestData.Pln)).IsSuccess);
        }

        return invoice;
    }

    private static BankTransaction CreateBankTransaction(
        OrganizationId organizationId,
        decimal amount,
        string? reference,
        decimal? allocatedAmount = null)
    {
        BankTransaction bankTransaction = new(
            BankTransactionId.New(),
            organizationId,
            new Money(amount, PersistenceTestData.Pln),
            new DateOnly(2026, 8, 24),
            reference);

        if (allocatedAmount is not null)
        {
            Assert.True(
                bankTransaction.Allocate(
                    new Money(allocatedAmount.Value, PersistenceTestData.Pln)).IsSuccess);
        }

        return bankTransaction;
    }

    private static int DecimalScale(decimal value)
    {
        return (decimal.GetBits(value)[3] >> 16) & 0x7F;
    }
}