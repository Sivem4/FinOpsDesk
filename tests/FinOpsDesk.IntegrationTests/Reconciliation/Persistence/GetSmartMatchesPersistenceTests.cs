using FinOpsDesk.Modules.Reconciliation;
using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Application.Matching;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.Matching;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class GetSmartMatchesPersistenceTests
{
    private static readonly CurrencyCode Eur = new("EUR");

    private readonly SqlServerFixture _fixture;

    public GetSmartMatchesPersistenceTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Handle_returns_a_reordered_token_suggestion()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = CreateInvoice(organizationId, "FV/2026/1042", 25m);
        BankTransaction bankTransaction = CreateBankTransaction(
            organizationId,
            25m,
            "1042/2026/FV");
        await SaveAsync([invoice], [bankTransaction]);

        SmartMatchResponse response = await HandleSuccessAsync(organizationId);

        SmartMatchSuggestionResponse suggestion = Assert.Single(response.Suggestions);
        Assert.Equal(85, suggestion.Score);
        Assert.Equal(invoice.Id.Value, suggestion.InvoiceId);
        Assert.Equal(bankTransaction.Id.Value, suggestion.BankTransactionId);
        AssertSignal(suggestion, SmartMatchSignalCode.ReferenceTokenSetEqual, SmartMatchRules.QualifiedReferencePoints);
        AssertSignal(suggestion, SmartMatchSignalCode.AmountExact, SmartMatchRules.AmountExactPoints);
        Assert.Equal(suggestion.Score, suggestion.Signals.Sum(signal => signal.Points));
    }

    [Fact]
    public async Task Handle_returns_a_long_numeric_identifier_suggestion()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = CreateInvoice(organizationId, "INV-001234", 10m);
        BankTransaction bankTransaction = CreateBankTransaction(organizationId, 10m, "PAY-001234");
        await SaveAsync([invoice], [bankTransaction]);

        SmartMatchResponse response = await HandleSuccessAsync(organizationId);

        SmartMatchSuggestionResponse suggestion = Assert.Single(response.Suggestions);
        Assert.Equal(85, suggestion.Score);
        AssertSignal(suggestion, SmartMatchSignalCode.NumericIdentifierEqual, SmartMatchRules.QualifiedReferencePoints);
        AssertSignal(suggestion, SmartMatchSignalCode.AmountExact, SmartMatchRules.AmountExactPoints);
    }

    [Fact]
    public async Task Handle_returns_an_exact_reference_and_the_smaller_open_amount()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = CreateInvoice(organizationId, "EXACT-100", 100m);
        BankTransaction bankTransaction = CreateBankTransaction(organizationId, 40m, "EXACT-100");
        await SaveAsync([invoice], [bankTransaction]);

        SmartMatchResponse response = await HandleSuccessAsync(organizationId);

        Assert.Equal(SmartMatchRules.AlgorithmVersion, response.AlgorithmVersion);
        Assert.True(response.Advisory);
        Assert.False(response.Truncated);
        Assert.False(response.CoverageLimited);
        SmartMatchSuggestionResponse suggestion = Assert.Single(response.Suggestions);
        Assert.Equal(70, suggestion.Score);
        Assert.Equal("points", suggestion.ScoreScale);
        Assert.Equal(new MoneyDto(100m, "PLN"), suggestion.InvoiceOutstanding);
        Assert.Equal(new MoneyDto(40m, "PLN"), suggestion.BankTransactionUnapplied);
        Assert.Equal(new MoneyDto(40m, "PLN"), suggestion.SuggestedAmount);
        AssertSignal(suggestion, SmartMatchSignalCode.ReferenceExact, SmartMatchRules.ReferenceExactPoints);
        AssertSignal(suggestion, SmartMatchSignalCode.AmountDiffers, 0);
        Assert.Equal(suggestion.Score, suggestion.Signals.Sum(signal => signal.Points));
        Assert.All(suggestion.Signals, signal => Assert.False(int.TryParse(signal.Code, out _)));
    }

    [Fact]
    public async Task Handle_returns_no_suggestion_from_equal_amounts_or_mismatched_currencies()
    {
        OrganizationId organizationId = OrganizationId.New();
        await SaveAsync(
            [
                CreateInvoice(organizationId, "RENT ONLY", 50m),
                CreateInvoice(organizationId, "CUR-30", 30m),
            ],
            [
                CreateBankTransaction(organizationId, 50m, "OFFICE ONLY"),
                CreateBankTransaction(organizationId, 30m, "CUR-30", currency: Eur),
            ]);

        SmartMatchResponse response = await HandleSuccessAsync(organizationId);

        Assert.Empty(response.Suggestions);
        Assert.False(response.CoverageLimited);
        Assert.False(response.Truncated);
    }

    [Fact]
    public async Task Handle_returns_an_empty_advisory_result_for_an_empty_open_book()
    {
        SmartMatchResponse response = await HandleSuccessAsync(OrganizationId.New());

        Assert.Equal("smart-1", response.AlgorithmVersion);
        Assert.True(response.Advisory);
        Assert.False(response.Truncated);
        Assert.False(response.CoverageLimited);
        Assert.Empty(response.Suggestions);
    }

    [Fact]
    public async Task Handle_uses_the_organization_context_and_hides_the_other_tenant()
    {
        OrganizationId current = OrganizationId.New();
        OrganizationId other = OrganizationId.New();
        Invoice currentInvoice = CreateInvoice(current, "EXACT-100", 80m);
        BankTransaction currentTransaction = CreateBankTransaction(current, 80m, "EXACT-100");
        Invoice foreignInvoice = CreateInvoice(other, "EXACT-100", 99999.25m);
        BankTransaction foreignTransaction = CreateBankTransaction(other, 99999.25m, "EXACT-100");
        await SaveAsync(
            [currentInvoice, foreignInvoice],
            [currentTransaction, foreignTransaction]);

        SmartMatchResponse response = await HandleSuccessAsync(current);

        SmartMatchSuggestionResponse suggestion = Assert.Single(response.Suggestions);
        Assert.Equal(currentInvoice.Id.Value, suggestion.InvoiceId);
        Assert.Equal(currentTransaction.Id.Value, suggestion.BankTransactionId);
        Assert.Equal(new MoneyDto(80m, "PLN"), suggestion.SuggestedAmount);
        Assert.DoesNotContain(response.Suggestions, item => item.InvoiceId == foreignInvoice.Id.Value);
        Assert.DoesNotContain(
            response.Suggestions,
            item => item.BankTransactionId == foreignTransaction.Id.Value);
        Assert.DoesNotContain(
            response.Suggestions,
            item => item.InvoiceOutstanding.Amount == 99999.25m
                || item.BankTransactionUnapplied.Amount == 99999.25m
                || item.SuggestedAmount.Amount == 99999.25m);
    }

    [Fact]
    public async Task Handle_scores_outstanding_balances_and_skips_zero_balances()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice partial = CreateInvoice(
            organizationId,
            "OPEN-PART",
            100m,
            allocatedAmount: 60m);
        Invoice closedInvoice = CreateInvoice(
            organizationId,
            "CLOSED-REF",
            20m,
            allocatedAmount: 20m);
        BankTransaction partialBank = CreateBankTransaction(organizationId, 100m, "OPEN-PART");
        BankTransaction closedBank = CreateBankTransaction(
            organizationId,
            20m,
            "CLOSED-REF",
            allocatedAmount: 20m);
        await SaveAsync([partial, closedInvoice], [partialBank, closedBank]);

        SmartMatchResponse response = await HandleSuccessAsync(organizationId);

        SmartMatchSuggestionResponse suggestion = Assert.Single(response.Suggestions);
        Assert.Equal(partial.Id.Value, suggestion.InvoiceId);
        Assert.Equal(partialBank.Id.Value, suggestion.BankTransactionId);
        Assert.Equal(new MoneyDto(40m, "PLN"), suggestion.InvoiceOutstanding);
        Assert.Equal(new MoneyDto(100m, "PLN"), suggestion.BankTransactionUnapplied);
        Assert.Equal(new MoneyDto(40m, "PLN"), suggestion.SuggestedAmount);
        Assert.Equal(70, suggestion.Score);
        Assert.DoesNotContain(response.Suggestions, item => item.InvoiceId == closedInvoice.Id.Value);
        Assert.DoesNotContain(response.Suggestions, item => item.BankTransactionId == closedBank.Id.Value);
    }

    [Fact]
    public async Task Handle_keeps_decimal_open_balances_exact()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = CreateInvoice(
            organizationId,
            "DEC-7501",
            1000.2500m,
            allocatedAmount: 250.1000m);
        BankTransaction bankTransaction = CreateBankTransaction(
            organizationId,
            1000.2500m,
            "DEC-7501",
            allocatedAmount: 250.1000m);
        await SaveAsync([invoice], [bankTransaction]);

        SmartMatchResponse response = await HandleSuccessAsync(organizationId);

        SmartMatchSuggestionResponse suggestion = Assert.Single(response.Suggestions);
        Assert.Equal(95, suggestion.Score);
        Assert.Equal(750.1500m, suggestion.InvoiceOutstanding.Amount);
        Assert.Equal(750.1500m, suggestion.BankTransactionUnapplied.Amount);
        Assert.Equal(750.1500m, suggestion.SuggestedAmount.Amount);
        Assert.Equal(4, DecimalScale(suggestion.SuggestedAmount.Amount));
        Assert.Equal("PLN", suggestion.SuggestedAmount.Currency);
    }

    [Fact]
    public async Task Handle_rejects_inconsistent_money_without_returning_matches()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = CreateInvoice(organizationId, "INV-SAFE", 10m);
        BankTransaction bankTransaction = CreateBankTransaction(organizationId, 10m, "INV-SAFE");
        await SaveAsync([invoice], [bankTransaction]);
        byte[] invoiceVersion = await InvoiceRowVersionAsync(invoice.Id);
        await InsertInvoiceAsync(
            organizationId,
            Guid.NewGuid(),
            "CORRUPT-ROW",
            totalAmount: 100m,
            totalCurrency: "PLN",
            allocatedAmount: 0m,
            allocatedCurrency: "EUR");

        Result<SmartMatchResponse> result = await HandleAsync(organizationId);

        Assert.True(result.IsFailure);
        Assert.Null(result.Value);
        Assert.Equal(ReconciliationApplicationErrors.InconsistentMatchMoney, result.Error);
        Assert.Equal(0m, await InvoiceAllocatedAmountAsync(invoice.Id));
        Assert.Equal(0m, await BankAllocatedAmountAsync(bankTransaction.Id));
        byte[] invoiceVersionAfter = await InvoiceRowVersionAsync(invoice.Id);
        Assert.True(invoiceVersion.SequenceEqual(invoiceVersionAfter));
    }

    [Fact]
    public async Task Handle_does_not_change_persisted_financial_rows()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = CreateInvoice(organizationId, "EXACT-100", 40m, allocatedAmount: 10m);
        BankTransaction bankTransaction = CreateBankTransaction(organizationId, 30m, "EXACT-100");
        await SaveAsync([invoice], [bankTransaction]);
        byte[] invoiceVersion = await InvoiceRowVersionAsync(invoice.Id);
        byte[] bankVersion = await BankTransactionRowVersionAsync(bankTransaction.Id);
        int allocations = await CountAsync(context => context.Allocations, organizationId);
        int importBatches = await CountImportBatchesAsync();

        await using ServiceProvider provider = CreateProvider(organizationId);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        GetSmartMatchesHandler handler = scope.ServiceProvider.GetRequiredService<GetSmartMatchesHandler>();
        ReconciliationDbContext context = scope.ServiceProvider.GetRequiredService<ReconciliationDbContext>();

        Result<SmartMatchResponse> result = await handler.Handle(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.Suggestions);
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Equal(10m, await InvoiceAllocatedAmountAsync(invoice.Id));
        Assert.Equal(0m, await BankAllocatedAmountAsync(bankTransaction.Id));
        byte[] invoiceVersionAfter = await InvoiceRowVersionAsync(invoice.Id);
        byte[] bankVersionAfter = await BankTransactionRowVersionAsync(bankTransaction.Id);
        Assert.True(invoiceVersion.SequenceEqual(invoiceVersionAfter));
        Assert.True(bankVersion.SequenceEqual(bankVersionAfter));
        Assert.Equal(allocations, await CountAsync(context => context.Allocations, organizationId));
        Assert.Equal(importBatches, await CountImportBatchesAsync());
        Assert.Equal(0, await ReconciliationTableCountAsync("MatchSuggestions"));
    }

    [Fact]
    public async Task Handle_accepts_5000_open_invoices_and_rejects_the_5001st_without_writing()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = CreateInvoice(organizationId, "BOUNDARY-MATCH", 15m);
        BankTransaction bankTransaction = CreateBankTransaction(
            organizationId,
            15m,
            "BOUNDARY-MATCH");
        await SaveAsync([invoice], [bankTransaction]);
        await InsertOpenInvoicesAsync(organizationId, ExactMatchCandidateLimits.MaxPerSide - 1, "BND-");
        Assert.Equal(ExactMatchCandidateLimits.MaxPerSide, await OpenInvoiceCountAsync(organizationId));

        byte[] invoiceVersion = await InvoiceRowVersionAsync(invoice.Id);
        byte[] bankVersion = await BankTransactionRowVersionAsync(bankTransaction.Id);
        PersistedCounts beforeSuccess = await ReadCountsAsync(organizationId, invoice, bankTransaction);

        SmartMatchResponse accepted = await HandleSuccessAsync(organizationId);

        SmartMatchSuggestionResponse suggestion = Assert.Single(accepted.Suggestions);
        Assert.Equal(invoice.Id.Value, suggestion.InvoiceId);
        Assert.Equal(bankTransaction.Id.Value, suggestion.BankTransactionId);
        Assert.Equal(95, suggestion.Score);
        await AssertUnchangedAsync(organizationId, invoice, bankTransaction, invoiceVersion, bankVersion, beforeSuccess);

        await InsertOpenInvoicesAsync(organizationId, 1, "EXTRA-");
        Assert.Equal(ExactMatchCandidateLimits.MaxPerSide + 1, await OpenInvoiceCountAsync(organizationId));
        invoiceVersion = await InvoiceRowVersionAsync(invoice.Id);
        bankVersion = await BankTransactionRowVersionAsync(bankTransaction.Id);
        PersistedCounts beforeFailure = await ReadCountsAsync(organizationId, invoice, bankTransaction);

        Result<SmartMatchResponse> rejected = await HandleAsync(organizationId);

        Assert.True(rejected.IsFailure);
        Assert.Null(rejected.Value);
        Assert.Equal(ReconciliationApplicationErrors.MatchCandidateLimitExceeded, rejected.Error);
        await AssertUnchangedAsync(organizationId, invoice, bankTransaction, invoiceVersion, bankVersion, beforeFailure);
    }

    [Fact]
    public async Task Handle_accepts_5000_open_transactions_and_rejects_the_5001st_without_writing()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = CreateInvoice(organizationId, "TXN-BOUNDARY", 15m);
        BankTransaction bankTransaction = CreateBankTransaction(
            organizationId,
            15m,
            "TXN-BOUNDARY");
        await SaveAsync([invoice], [bankTransaction]);
        await InsertOpenBankTransactionsAsync(
            organizationId,
            ExactMatchCandidateLimits.MaxPerSide - 1,
            "TXN-");
        Assert.Equal(ExactMatchCandidateLimits.MaxPerSide, await OpenBankTransactionCountAsync(organizationId));

        byte[] invoiceVersion = await InvoiceRowVersionAsync(invoice.Id);
        byte[] bankVersion = await BankTransactionRowVersionAsync(bankTransaction.Id);
        PersistedCounts beforeSuccess = await ReadCountsAsync(organizationId, invoice, bankTransaction);

        SmartMatchResponse accepted = await HandleSuccessAsync(organizationId);

        SmartMatchSuggestionResponse suggestion = Assert.Single(accepted.Suggestions);
        Assert.Equal(invoice.Id.Value, suggestion.InvoiceId);
        Assert.Equal(bankTransaction.Id.Value, suggestion.BankTransactionId);
        await AssertUnchangedAsync(organizationId, invoice, bankTransaction, invoiceVersion, bankVersion, beforeSuccess);

        await InsertOpenBankTransactionsAsync(organizationId, 1, "EXTRA-TXN-");
        Assert.Equal(
            ExactMatchCandidateLimits.MaxPerSide + 1,
            await OpenBankTransactionCountAsync(organizationId));
        invoiceVersion = await InvoiceRowVersionAsync(invoice.Id);
        bankVersion = await BankTransactionRowVersionAsync(bankTransaction.Id);
        PersistedCounts beforeFailure = await ReadCountsAsync(organizationId, invoice, bankTransaction);

        Result<SmartMatchResponse> rejected = await HandleAsync(organizationId);

        Assert.True(rejected.IsFailure);
        Assert.Null(rejected.Value);
        Assert.Equal(ReconciliationApplicationErrors.MatchCandidateLimitExceeded, rejected.Error);
        await AssertUnchangedAsync(organizationId, invoice, bankTransaction, invoiceVersion, bankVersion, beforeFailure);
    }

    [Fact]
    public async Task Handle_preserves_contested_and_ambiguous_indicators_in_a_stable_order()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice exact = CreateInvoice(organizationId, "INV-001234", 100m);
        Invoice numeric = CreateInvoice(organizationId, "PAY-001234", 100m);
        BankTransaction shared = CreateBankTransaction(organizationId, 100m, "INV-001234");
        Invoice ambiguousLow = CreateInvoice(organizationId, "AMB-9", 40m);
        Invoice ambiguousHigh = CreateInvoice(organizationId, "AMB-9", 40m);
        BankTransaction ambiguousBank = CreateBankTransaction(organizationId, 40m, "AMB-9");
        await SaveAsync(
            [exact, numeric, ambiguousLow, ambiguousHigh],
            [shared, ambiguousBank]);

        SmartMatchResponse first = await HandleSuccessAsync(organizationId);
        SmartMatchResponse second = await HandleSuccessAsync(organizationId);

        Assert.Equal(4, first.Suggestions.Count);
        Assert.Equal(Identities(first), Identities(second));
        Assert.Equal([1, 2, 3, 4], first.Suggestions.Select(suggestion => suggestion.Rank));
        Assert.Equal(
            first.Suggestions
                .OrderByDescending(suggestion => suggestion.Score)
                .ThenBy(suggestion => suggestion.InvoiceId.ToString("D"), StringComparer.Ordinal)
                .ThenBy(suggestion => suggestion.BankTransactionId.ToString("D"), StringComparer.Ordinal)
                .Select(suggestion => suggestion.InvoiceId),
            first.Suggestions.Select(suggestion => suggestion.InvoiceId));

        SmartMatchSuggestionResponse exactSuggestion = Assert.Single(
            first.Suggestions,
            suggestion => suggestion.InvoiceId == exact.Id.Value);
        Assert.Equal(95, exactSuggestion.Score);
        Assert.True(exactSuggestion.Contested);
        Assert.False(exactSuggestion.ExactMatchAmbiguous);
        AssertSignal(exactSuggestion, SmartMatchSignalCode.ContestedCandidates, 0);

        SmartMatchSuggestionResponse numericSuggestion = Assert.Single(
            first.Suggestions,
            suggestion => suggestion.InvoiceId == numeric.Id.Value);
        Assert.Equal(85, numericSuggestion.Score);
        Assert.True(numericSuggestion.Contested);
        Assert.False(numericSuggestion.ExactMatchAmbiguous);

        Assert.All(
            first.Suggestions.Where(suggestion =>
                suggestion.InvoiceId == ambiguousLow.Id.Value
                || suggestion.InvoiceId == ambiguousHigh.Id.Value),
            suggestion =>
            {
                Assert.True(suggestion.ExactMatchAmbiguous);
                Assert.True(suggestion.Contested);
                Assert.Equal(95, suggestion.Score);
                Assert.Equal(ambiguousBank.Id.Value, suggestion.BankTransactionId);
                AssertSignal(suggestion, SmartMatchSignalCode.ExactMatchAmbiguous, 0);
            });
    }

    [Fact]
    public async Task Handle_preserves_coverage_limited_when_a_blocking_key_saturates()
    {
        OrganizationId organizationId = OrganizationId.New();
        List<Invoice> invoices = new(SmartMatchRules.MaxRowsPerBlockingKey + 1);

        for (int id = 1; id <= SmartMatchRules.MaxRowsPerBlockingKey + 1; id++)
        {
            invoices.Add(CreateInvoice(organizationId, "SAT-001234", 10m));
        }

        BankTransaction bankTransaction = CreateBankTransaction(organizationId, 10m, "SAT-001234");
        await SaveAsync(invoices, [bankTransaction]);

        SmartMatchResponse response = await HandleSuccessAsync(organizationId);

        Assert.True(response.CoverageLimited);
        Assert.False(response.Truncated);
        Assert.Empty(response.Suggestions);
        Assert.True(response.Advisory);
    }

    [Fact]
    public async Task Handle_preserves_truncation_after_200_suggestions()
    {
        OrganizationId organizationId = OrganizationId.New();
        const int pairCount = SmartMatchRules.MaxSuggestions + 1;
        List<Invoice> invoices = new(pairCount);
        List<BankTransaction> bankTransactions = new(pairCount);

        for (int id = 1; id <= pairCount; id++)
        {
            string reference = $"PAIR-{id:000000}";
            invoices.Add(CreateInvoice(organizationId, reference, 12m));
            bankTransactions.Add(CreateBankTransaction(organizationId, 12m, reference));
        }

        await SaveAsync(invoices, bankTransactions);

        SmartMatchResponse response = await HandleSuccessAsync(organizationId);
        string[] expectedInvoiceIds = invoices
            .Select(invoice => invoice.Id.Value.ToString("D"))
            .OrderBy(invoiceId => invoiceId, StringComparer.Ordinal)
            .Take(SmartMatchRules.MaxSuggestions)
            .ToArray();

        Assert.True(response.Truncated);
        Assert.False(response.CoverageLimited);
        Assert.Equal(SmartMatchRules.MaxSuggestions, response.Suggestions.Count);
        Assert.Equal(1, response.Suggestions[0].Rank);
        Assert.Equal(SmartMatchRules.MaxSuggestions, response.Suggestions[^1].Rank);
        Assert.Equal(expectedInvoiceIds, response.Suggestions.Select(suggestion => suggestion.InvoiceId.ToString("D")));
        Assert.All(response.Suggestions, suggestion => Assert.Equal(95, suggestion.Score));
    }

    [Fact]
    public async Task Handle_honors_a_cancelled_token()
    {
        await using ServiceProvider provider = CreateProvider(OrganizationId.New());
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        GetSmartMatchesHandler handler = scope.ServiceProvider.GetRequiredService<GetSmartMatchesHandler>();
        using CancellationTokenSource cancellationTokenSource = new();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            handler.Handle(cancellationTokenSource.Token));
    }

    private async Task AssertUnchangedAsync(
        OrganizationId organizationId,
        Invoice invoice,
        BankTransaction bankTransaction,
        byte[] invoiceVersion,
        byte[] bankVersion,
        PersistedCounts before)
    {
        Assert.Equal(before.InvoiceAllocated, await InvoiceAllocatedAmountAsync(invoice.Id));
        Assert.Equal(before.BankAllocated, await BankAllocatedAmountAsync(bankTransaction.Id));
        byte[] invoiceVersionAfter = await InvoiceRowVersionAsync(invoice.Id);
        byte[] bankVersionAfter = await BankTransactionRowVersionAsync(bankTransaction.Id);
        Assert.True(invoiceVersion.SequenceEqual(invoiceVersionAfter));
        Assert.True(bankVersion.SequenceEqual(bankVersionAfter));
        PersistedCounts after = await ReadCountsAsync(organizationId, invoice, bankTransaction);
        Assert.Equal(before.Allocations, after.Allocations);
        Assert.Equal(before.ImportBatches, after.ImportBatches);
        Assert.Equal(0, await ReconciliationTableCountAsync("MatchSuggestions"));
    }

    private async Task<PersistedCounts> ReadCountsAsync(
        OrganizationId organizationId,
        Invoice invoice,
        BankTransaction bankTransaction)
    {
        return new PersistedCounts(
            await InvoiceAllocatedAmountAsync(invoice.Id),
            await BankAllocatedAmountAsync(bankTransaction.Id),
            await CountAsync(context => context.Allocations, organizationId),
            await CountImportBatchesAsync());
    }

    private static void AssertSignal(
        SmartMatchSuggestionResponse suggestion,
        SmartMatchSignalCode code,
        int points)
    {
        Assert.Contains(
            suggestion.Signals,
            signal => signal.Code == code.ToString() && signal.Points == points);
    }

    private static IEnumerable<string> Identities(SmartMatchResponse response)
    {
        return response.Suggestions.Select(suggestion =>
            $"{suggestion.Rank}:{suggestion.Score}:{suggestion.InvoiceId:D}:{suggestion.BankTransactionId:D}:{suggestion.Contested}:{suggestion.ExactMatchAmbiguous}");
    }

    private async Task<SmartMatchResponse> HandleSuccessAsync(OrganizationId organizationId)
    {
        Result<SmartMatchResponse> result = await HandleAsync(organizationId);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private async Task<Result<SmartMatchResponse>> HandleAsync(OrganizationId organizationId)
    {
        await using ServiceProvider provider = CreateProvider(organizationId);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        GetSmartMatchesHandler handler = scope.ServiceProvider.GetRequiredService<GetSmartMatchesHandler>();
        return await handler.Handle(CancellationToken.None);
    }

    private ServiceProvider CreateProvider(OrganizationId organizationId)
    {
        ServiceCollection services = new();
        services.AddReconciliationModule(_fixture.ConnectionString);
        services.AddSingleton<ICurrentOrganizationContext>(new TestCurrentOrganizationContext(organizationId));
        return services.BuildServiceProvider();
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

    private async Task InsertOpenInvoicesAsync(OrganizationId organizationId, int count, string prefix)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            WITH numbers AS (
                SELECT TOP ({count}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                FROM sys.all_objects AS leftSide
                CROSS JOIN sys.all_objects AS rightSide
            )
            INSERT INTO [reconciliation].[Invoices]
                ([Id], [OrganizationId], [InvoiceNumber], [AllocatedAmount], [AllocatedCurrency],
                 [TotalAmount], [TotalCurrency])
            SELECT NEWID(), {organizationId.Value}, CONCAT({prefix}, numbers.n),
                   {0m}, {"PLN"}, {11m}, {"PLN"}
            FROM numbers;
            """);
    }

    private async Task InsertOpenBankTransactionsAsync(
        OrganizationId organizationId,
        int count,
        string prefix)
    {
        DateOnly bookingDate = new(2026, 8, 24);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            WITH numbers AS (
                SELECT TOP ({count}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                FROM sys.all_objects AS leftSide
                CROSS JOIN sys.all_objects AS rightSide
            )
            INSERT INTO [reconciliation].[BankTransactions]
                ([Id], [OrganizationId], [Reference], [BookingDate], [AllocatedAmount],
                 [AllocatedCurrency], [Amount], [Currency])
            SELECT NEWID(), {organizationId.Value}, CONCAT({prefix}, numbers.n), {bookingDate},
                   {0m}, {"PLN"}, {11m}, {"PLN"}
            FROM numbers;
            """);
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

    private async Task<int> OpenInvoiceCountAsync(OrganizationId organizationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.Invoices.CountAsync(invoice =>
            invoice.OrganizationId == organizationId
            && invoice.TotalAmount.Amount > invoice.AllocatedAmount.Amount);
    }

    private async Task<int> OpenBankTransactionCountAsync(OrganizationId organizationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.BankTransactions.CountAsync(bankTransaction =>
            bankTransaction.OrganizationId == organizationId
            && bankTransaction.Amount.Amount > bankTransaction.AllocatedAmount.Amount);
    }

    private async Task<decimal> InvoiceAllocatedAmountAsync(InvoiceId invoiceId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Invoice invoice = await context.Invoices.SingleAsync(candidate => candidate.Id == invoiceId);
        return invoice.AllocatedAmount.Amount;
    }

    private async Task<decimal> BankAllocatedAmountAsync(BankTransactionId bankTransactionId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        BankTransaction bankTransaction = await context.BankTransactions.SingleAsync(
            candidate => candidate.Id == bankTransactionId);
        return bankTransaction.AllocatedAmount.Amount;
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

    private async Task<int> CountAsync(
        Func<ReconciliationDbContext, IQueryable<Allocation>> rows,
        OrganizationId organizationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await rows(context).CountAsync(row => row.OrganizationId == organizationId);
    }

    private async Task<int> CountImportBatchesAsync()
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.ImportBatches.CountAsync();
    }

    private async Task<int> ReconciliationTableCountAsync(string tableName)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM sys.tables AS tables
            INNER JOIN sys.schemas AS schemas ON schemas.schema_id = tables.schema_id
            WHERE schemas.name = N'reconciliation' AND tables.name = @tableName
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@tableName";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);
        object? result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result);
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
            Assert.True(invoice.Allocate(new Money(allocatedAmount.Value, PersistenceTestData.Pln)).IsSuccess);
        }

        return invoice;
    }

    private static BankTransaction CreateBankTransaction(
        OrganizationId organizationId,
        decimal amount,
        string? reference,
        decimal? allocatedAmount = null,
        CurrencyCode? currency = null)
    {
        CurrencyCode resolved = currency ?? PersistenceTestData.Pln;
        BankTransaction bankTransaction = new(
            BankTransactionId.New(),
            organizationId,
            new Money(amount, resolved),
            new DateOnly(2026, 8, 24),
            reference);

        if (allocatedAmount is not null)
        {
            Assert.True(bankTransaction.Allocate(new Money(allocatedAmount.Value, resolved)).IsSuccess);
        }

        return bankTransaction;
    }

    private static int DecimalScale(decimal value)
    {
        return (decimal.GetBits(value)[3] >> 16) & 0x7F;
    }

    private readonly record struct PersistedCounts(
        decimal InvoiceAllocated,
        decimal BankAllocated,
        int Allocations,
        int ImportBatches);
}