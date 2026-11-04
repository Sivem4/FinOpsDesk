using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Matching;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.Matching;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class SmartMatchEndpointTests : IDisposable
{
    private static readonly CurrencyCode Eur = new("EUR");

    private readonly SqlServerFixture _fixture;
    private readonly FinOpsDeskApiFactory _factory;

    public SmartMatchEndpointTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _factory = new FinOpsDeskApiFactory(fixture.ConnectionString);
    }

    [Fact]
    public async Task Get_smart_matches_without_a_bearer_token_returns_401()
    {
        using HttpClient client = _factory.CreateApiClient();

        HttpResponseMessage response = await client.GetAsync("/api/matches/smart");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_smart_matches_with_an_invalid_token_returns_401()
    {
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.CreateSignedWithOtherKey(Guid.NewGuid()));

        HttpResponseMessage response = await client.GetAsync("/api/matches/smart");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_smart_matches_without_an_organization_claim_returns_403()
    {
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.CreateWithoutOrganizationClaim());

        HttpResponseMessage response = await client.GetAsync("/api/matches/smart");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Get_smart_matches_with_an_unusable_organization_claim_returns_403(
        string organizationClaim)
    {
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.CreateWithOrganizationClaim(organizationClaim));

        HttpResponseMessage response = await client.GetAsync("/api/matches/smart");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_smart_matches_for_an_empty_book_returns_an_empty_advisory_result()
    {
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.Create(OrganizationId.New().Value));

        HttpResponseMessage response = await client.GetAsync("/api/matches/smart");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using JsonDocument document = await ReadJsonAsync(response);
        JsonElement root = document.RootElement;
        Assert.Equal("smart-1", root.GetProperty("algorithmVersion").GetString());
        Assert.True(root.GetProperty("advisory").GetBoolean());
        Assert.False(root.GetProperty("truncated").GetBoolean());
        Assert.False(root.GetProperty("coverageLimited").GetBoolean());
        Assert.Equal(0, root.GetProperty("suggestions").GetArrayLength());
    }

    [Fact]
    public async Task Get_smart_matches_returns_scored_suggestions_and_hides_the_other_tenant()
    {
        OrganizationId current = OrganizationId.New();
        OrganizationId other = OrganizationId.New();
        Invoice exact = Invoice(current, "EXACT-PAIR", 100m);
        BankTransaction exactBank = Transaction(current, "EXACT-PAIR", 100m);
        Invoice reordered = Invoice(current, "FV/2026/1042", 25m);
        BankTransaction reorderedBank = Transaction(current, "1042/2026/FV", 25m);
        Invoice numeric = Invoice(current, "INV-001234", 10m);
        BankTransaction numericBank = Transaction(current, "PAY-001234", 10m);
        Invoice partial = Invoice(current, "PART-40", 100m);
        BankTransaction partialBank = Transaction(current, "PART-40", 40m);
        Invoice amountOnly = Invoice(current, "RENT ONLY", 50m);
        BankTransaction amountOnlyBank = Transaction(current, "OFFICE ONLY", 50m);
        Invoice currencyInvoice = Invoice(current, "CUR-30", 30m);
        BankTransaction currencyBank = Transaction(current, "CUR-30", 30m, Eur);
        Invoice foreign = Invoice(other, "EXACT-PAIR", 99999.25m);
        BankTransaction foreignBank = Transaction(other, "FOREIGN-001234", 99999.25m);
        await SaveAsync(
            [exact, reordered, numeric, partial, amountOnly, currencyInvoice, foreign],
            [
                exactBank,
                reorderedBank,
                numericBank,
                partialBank,
                amountOnlyBank,
                currencyBank,
                foreignBank,
            ]);
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/matches/smart");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            TestAccessTokens.Create(current.Value));
        request.Headers.TryAddWithoutValidation("X-Organization-Id", other.Value.ToString());
        using HttpClient client = _factory.CreateApiClient();

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement suggestions = document.RootElement.GetProperty("suggestions");
        Assert.Equal(4, suggestions.GetArrayLength());
        Assert.DoesNotContain(other.Value.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(foreign.Id.Value.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(foreignBank.Id.Value.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FOREIGN-001234", body, StringComparison.Ordinal);
        Assert.DoesNotContain("99999.25", body, StringComparison.Ordinal);
        Assert.DoesNotContain("RENT ONLY", body, StringComparison.Ordinal);
        Assert.DoesNotContain("CUR-30", body, StringComparison.Ordinal);
        Assert.False(body.Contains("%", StringComparison.Ordinal));

        JsonElement exactSuggestion = Find(suggestions, exact.Id.Value);
        Assert.Equal(95, exactSuggestion.GetProperty("score").GetInt32());
        Assert.Equal("points", exactSuggestion.GetProperty("scoreScale").GetString());
        Assert.False(exactSuggestion.GetProperty("contested").GetBoolean());
        Assert.False(exactSuggestion.GetProperty("exactMatchAmbiguous").GetBoolean());
        Assert.Equal(exactBank.Id.Value, exactSuggestion.GetProperty("bankTransactionId").GetGuid());
        AssertMoney(exactSuggestion.GetProperty("suggestedAmount"), 100m, "PLN");
        AssertSignal(exactSuggestion, "ReferenceExact", SmartMatchRules.ReferenceExactPoints);
        AssertSignal(exactSuggestion, "AmountExact", SmartMatchRules.AmountExactPoints);
        AssertScoreEqualsSignals(exactSuggestion);

        JsonElement tokenSuggestion = Find(suggestions, reordered.Id.Value);
        Assert.Equal(85, tokenSuggestion.GetProperty("score").GetInt32());
        AssertSignal(tokenSuggestion, "ReferenceTokenSetEqual", SmartMatchRules.QualifiedReferencePoints);
        AssertScoreEqualsSignals(tokenSuggestion);

        JsonElement numericSuggestion = Find(suggestions, numeric.Id.Value);
        Assert.Equal(85, numericSuggestion.GetProperty("score").GetInt32());
        AssertSignal(numericSuggestion, "NumericIdentifierEqual", SmartMatchRules.QualifiedReferencePoints);
        AssertScoreEqualsSignals(numericSuggestion);

        JsonElement partialSuggestion = Find(suggestions, partial.Id.Value);
        Assert.Equal(70, partialSuggestion.GetProperty("score").GetInt32());
        AssertMoney(partialSuggestion.GetProperty("invoiceOutstanding"), 100m, "PLN");
        AssertMoney(partialSuggestion.GetProperty("bankTransactionUnapplied"), 40m, "PLN");
        AssertMoney(partialSuggestion.GetProperty("suggestedAmount"), 40m, "PLN");
        AssertSignal(partialSuggestion, "ReferenceExact", SmartMatchRules.ReferenceExactPoints);
        AssertSignal(partialSuggestion, "AmountDiffers", 0);
        AssertScoreEqualsSignals(partialSuggestion);
        Assert.Equal(JsonValueKind.Number, partialSuggestion.GetProperty("suggestedAmount").GetProperty("amount").ValueKind);
        Assert.Equal(JsonValueKind.String, partialSuggestion.GetProperty("signals")[0].GetProperty("code").ValueKind);
    }

    [Fact]
    public async Task Get_smart_matches_preserves_contested_and_ambiguous_flags()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice exact = Invoice(organizationId, "INV-001234", 100m);
        Invoice numeric = Invoice(organizationId, "PAY-001234", 100m);
        BankTransaction shared = Transaction(organizationId, "INV-001234", 100m);
        Invoice ambiguousA = Invoice(organizationId, "AMB-9", 40m);
        Invoice ambiguousB = Invoice(organizationId, "AMB-9", 40m);
        BankTransaction ambiguousBank = Transaction(organizationId, "AMB-9", 40m);
        await SaveAsync(
            [exact, numeric, ambiguousA, ambiguousB],
            [shared, ambiguousBank]);
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.Create(organizationId.Value));

        HttpResponseMessage response = await client.GetAsync("/api/matches/smart");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = await ReadJsonAsync(response);
        JsonElement suggestions = document.RootElement.GetProperty("suggestions");
        JsonElement exactSuggestion = Find(suggestions, exact.Id.Value);
        Assert.Equal(95, exactSuggestion.GetProperty("score").GetInt32());
        Assert.True(exactSuggestion.GetProperty("contested").GetBoolean());
        Assert.False(exactSuggestion.GetProperty("exactMatchAmbiguous").GetBoolean());
        AssertSignal(exactSuggestion, "ContestedCandidates", 0);
        JsonElement numericSuggestion = Find(suggestions, numeric.Id.Value);
        Assert.Equal(85, numericSuggestion.GetProperty("score").GetInt32());
        Assert.True(numericSuggestion.GetProperty("contested").GetBoolean());
        Assert.False(numericSuggestion.GetProperty("exactMatchAmbiguous").GetBoolean());
        foreach (Invoice ambiguous in new[] { ambiguousA, ambiguousB })
        {
            JsonElement suggestion = Find(suggestions, ambiguous.Id.Value);
            Assert.True(suggestion.GetProperty("exactMatchAmbiguous").GetBoolean());
            Assert.True(suggestion.GetProperty("contested").GetBoolean());
            Assert.Equal(ambiguousBank.Id.Value, suggestion.GetProperty("bankTransactionId").GetGuid());
            AssertSignal(suggestion, "ExactMatchAmbiguous", 0);
            AssertScoreEqualsSignals(suggestion);
        }
    }

    [Fact]
    public async Task Get_smart_matches_reports_coverage_limited_when_a_blocking_key_saturates()
    {
        OrganizationId organizationId = OrganizationId.New();
        List<Invoice> invoices = new();
        for (int index = 0; index < SmartMatchRules.MaxRowsPerBlockingKey + 1; index++)
        {
            invoices.Add(Invoice(organizationId, "SAT-001234", 10m));
        }

        await SaveAsync(invoices, [Transaction(organizationId, "SAT-001234", 10m)]);
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.Create(organizationId.Value));

        HttpResponseMessage response = await client.GetAsync("/api/matches/smart");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = await ReadJsonAsync(response);
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("coverageLimited").GetBoolean());
        Assert.False(root.GetProperty("truncated").GetBoolean());
        Assert.Equal(0, root.GetProperty("suggestions").GetArrayLength());
        Assert.True(root.GetProperty("advisory").GetBoolean());
    }

    [Fact]
    public async Task Get_smart_matches_truncates_after_200_suggestions()
    {
        OrganizationId organizationId = OrganizationId.New();
        const int pairCount = SmartMatchRules.MaxSuggestions + 1;
        List<Invoice> invoices = new(pairCount);
        List<BankTransaction> bankTransactions = new(pairCount);
        for (int index = 1; index <= pairCount; index++)
        {
            string reference = $"HTTP-{index:000000}";
            invoices.Add(Invoice(organizationId, reference, 12m));
            bankTransactions.Add(Transaction(organizationId, reference, 12m));
        }

        await SaveAsync(invoices, bankTransactions);
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.Create(organizationId.Value));

        HttpResponseMessage response = await client.GetAsync("/api/matches/smart");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = await ReadJsonAsync(response);
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.False(root.GetProperty("coverageLimited").GetBoolean());
        Assert.Equal(SmartMatchRules.MaxSuggestions, root.GetProperty("suggestions").GetArrayLength());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Candidate_limit_failure_returns_409_without_a_partial_result(bool invoicesExceeded)
    {
        using LimitExceededSmartApiFactory factory = new(_fixture.ConnectionString, invoicesExceeded);
        using HttpClient client = factory.CreateApiClient(TestAccessTokens.Create(Guid.NewGuid()));

        HttpResponseMessage response = await client.GetAsync("/api/matches/smart");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        Assert.Equal(409, root.GetProperty("status").GetInt32());
        Assert.Equal(
            ReconciliationApplicationErrors.MatchCandidateLimitExceeded.Code,
            root.GetProperty("code").GetString());
        Assert.Equal(
            "Smart matching was not run because the open book exceeds 5000 invoices or 5000 bank transactions.",
            root.GetProperty("detail").GetString());
        Assert.DoesNotContain("Exact matching", body, StringComparison.Ordinal);
        Assert.False(root.TryGetProperty("suggestions", out _));
        Assert.DoesNotContain("INV-LIMIT", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("42.5", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SqlException", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inconsistent_money_returns_sanitized_problem_details()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = Invoice(organizationId, "SAFE-PAIR", 10m);
        BankTransaction bankTransaction = Transaction(organizationId, "SAFE-PAIR", 10m);
        await SaveAsync([invoice], [bankTransaction]);
        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO [reconciliation].[Invoices]
                    ([Id], [OrganizationId], [InvoiceNumber], [AllocatedAmount], [AllocatedCurrency],
                     [TotalAmount], [TotalCurrency])
                VALUES
                    ({Guid.NewGuid()}, {organizationId.Value}, {"CORRUPT-ROW"}, {0m}, {"EUR"},
                     {12345.6789m}, {"PLN"});
                """);
        }

        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.Create(organizationId.Value));

        HttpResponseMessage response = await client.GetAsync("/api/matches/smart");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        Assert.Equal(500, root.GetProperty("status").GetInt32());
        Assert.Equal(
            ReconciliationApplicationErrors.InconsistentMatchMoney.Code,
            root.GetProperty("code").GetString());
        Assert.Equal(
            "Smart matching was not run because stored invoice or bank transaction amounts are inconsistent.",
            root.GetProperty("detail").GetString());
        Assert.DoesNotContain("Exact matching", body, StringComparison.Ordinal);
        Assert.DoesNotContain("CORRUPT-ROW", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SAFE-PAIR", body, StringComparison.OrdinalIgnoreCase);
        AssertDoesNotContainOutsideTrace(body, "12345");
        Assert.DoesNotContain("SqlException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(organizationId.Value.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unsupported_query_parameters_are_rejected_and_write_nothing()
    {
        OrganizationId organizationId = OrganizationId.New();
        OrganizationId other = OrganizationId.New();
        Invoice invoice = Invoice(organizationId, "EXACT-PAIR", 40m);
        Assert.True(invoice.Allocate(new Money(10m, PersistenceTestData.Pln)).IsSuccess);
        BankTransaction bankTransaction = Transaction(organizationId, "EXACT-PAIR", 30m);
        await SaveAsync([invoice], [bankTransaction]);
        byte[] invoiceVersion = await InvoiceRowVersionAsync(invoice.Id);
        byte[] bankVersion = await BankTransactionRowVersionAsync(bankTransaction.Id);
        int allocations = await AllocationCountAsync(organizationId);
        int importBatches = await ImportBatchCountAsync();
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.Create(organizationId.Value));

        HttpResponseMessage response = await client.GetAsync(
            $"/api/matches/smart?organizationId={other.Value}&page=1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.Equal(
            ReconciliationApplicationErrors.SmartMatchUnsupportedQuery.Code,
            document.RootElement.GetProperty("code").GetString());
        Assert.Equal(400, document.RootElement.GetProperty("status").GetInt32());
        Assert.DoesNotContain("suggestions", body, StringComparison.Ordinal);
        byte[] invoiceVersionAfter = await InvoiceRowVersionAsync(invoice.Id);
        byte[] bankVersionAfter = await BankTransactionRowVersionAsync(bankTransaction.Id);
        Assert.True(invoiceVersion.SequenceEqual(invoiceVersionAfter));
        Assert.True(bankVersion.SequenceEqual(bankVersionAfter));
        Assert.Equal(10m, await InvoiceAllocatedAmountAsync(invoice.Id));
        Assert.Equal(0m, await BankAllocatedAmountAsync(bankTransaction.Id));
        Assert.Equal(allocations, await AllocationCountAsync(organizationId));
        Assert.Equal(importBatches, await ImportBatchCountAsync());
    }

    [Fact]
    public async Task Get_smart_matches_does_not_change_financial_rows()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = Invoice(organizationId, "EXACT-PAIR", 40m);
        Assert.True(invoice.Allocate(new Money(10m, PersistenceTestData.Pln)).IsSuccess);
        BankTransaction bankTransaction = Transaction(organizationId, "EXACT-PAIR", 30m);
        await SaveAsync([invoice], [bankTransaction]);
        byte[] invoiceVersion = await InvoiceRowVersionAsync(invoice.Id);
        byte[] bankVersion = await BankTransactionRowVersionAsync(bankTransaction.Id);
        int allocations = await AllocationCountAsync(organizationId);
        int importBatches = await ImportBatchCountAsync();
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.Create(organizationId.Value));

        HttpResponseMessage response = await client.GetAsync("/api/matches/smart");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        byte[] invoiceVersionAfter = await InvoiceRowVersionAsync(invoice.Id);
        byte[] bankVersionAfter = await BankTransactionRowVersionAsync(bankTransaction.Id);
        Assert.True(invoiceVersion.SequenceEqual(invoiceVersionAfter));
        Assert.True(bankVersion.SequenceEqual(bankVersionAfter));
        Assert.Equal(10m, await InvoiceAllocatedAmountAsync(invoice.Id));
        Assert.Equal(0m, await BankAllocatedAmountAsync(bankTransaction.Id));
        Assert.Equal(allocations, await AllocationCountAsync(organizationId));
        Assert.Equal(importBatches, await ImportBatchCountAsync());
        Assert.Equal(0, allocations);
    }

    [Fact]
    public async Task Get_smart_matches_passes_a_cancelable_token_to_the_candidate_query()
    {
        using CancellationObservingApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient client = factory.CreateApiClient(TestAccessTokens.Create(Guid.NewGuid()));

        HttpResponseMessage response = await client.GetAsync("/api/matches/smart");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CancellationObservingQueries queries = factory.Services.GetRequiredService<CancellationObservingQueries>();
        Assert.True(queries.Observed.CanBeCanceled);
    }

    [Fact]
    public async Task Get_matches_remains_the_exact_1_route()
    {
        OrganizationId organizationId = OrganizationId.New();
        await SaveAsync(
            [Invoice(organizationId, "EXACT-PAIR", 15m)],
            [Transaction(organizationId, "EXACT-PAIR", 15m)]);
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.Create(organizationId.Value));

        HttpResponseMessage response = await client.GetAsync(
            $"/api/matches?organizationId={Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = await ReadJsonAsync(response);
        Assert.Equal("exact-1", document.RootElement.GetProperty("algorithmVersion").GetString());
        Assert.False(document.RootElement.TryGetProperty("suggestions", out _));
        Assert.Single(document.RootElement.GetProperty("exactMatches").EnumerateArray());
    }

    [Fact]
    public async Task OpenApi_document_describes_smart_matching()
    {
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(Guid.NewGuid()));

        HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = await ReadJsonAsync(response);
        JsonElement root = document.RootElement;
        JsonElement operation = root.GetProperty("paths").GetProperty("/api/matches/smart").GetProperty("get");
        JsonElement exactOperation = root.GetProperty("paths").GetProperty("/api/matches").GetProperty("get");
        Assert.True(operation.GetProperty("security")[0].TryGetProperty("DevelopmentJwt", out _));
        Assert.True(exactOperation.GetProperty("security")[0].TryGetProperty("DevelopmentJwt", out _));
        Assert.False(HasHeader(operation, "X-CSRF-Token"));
        Assert.False(HasHeader(exactOperation, "X-CSRF-Token"));
        Assert.False(operation.TryGetProperty("parameters", out JsonElement parameters) && parameters.GetArrayLength() > 0);
        Assert.False(exactOperation.TryGetProperty("responses", out JsonElement exactResponses)
            && exactResponses.TryGetProperty("400", out _));
        JsonElement ok = operation.GetProperty("responses").GetProperty("200");
        JsonElement schema = Resolve(
            root,
            ok.GetProperty("content").GetProperty("application/json").GetProperty("schema"));
        Assert.Equal("string", SchemaType(schema.GetProperty("properties").GetProperty("algorithmVersion")));
        Assert.Equal("boolean", SchemaType(schema.GetProperty("properties").GetProperty("advisory")));
        Assert.Equal("boolean", SchemaType(schema.GetProperty("properties").GetProperty("truncated")));
        Assert.Equal("boolean", SchemaType(schema.GetProperty("properties").GetProperty("coverageLimited")));
        JsonElement suggestions = Resolve(root, schema.GetProperty("properties").GetProperty("suggestions"));
        JsonElement item = Resolve(root, suggestions.GetProperty("items"));
        Assert.Equal("integer", SchemaType(item.GetProperty("properties").GetProperty("score")));
        Assert.Equal("string", SchemaType(item.GetProperty("properties").GetProperty("scoreScale")));
        Assert.Equal("boolean", SchemaType(item.GetProperty("properties").GetProperty("contested")));
        Assert.Equal("boolean", SchemaType(item.GetProperty("properties").GetProperty("exactMatchAmbiguous")));
        JsonElement signal = Resolve(root, item.GetProperty("properties").GetProperty("signals"));
        JsonElement signalItem = Resolve(root, signal.GetProperty("items"));
        Assert.Equal("string", SchemaType(signalItem.GetProperty("properties").GetProperty("code")));
        Assert.Equal("integer", SchemaType(signalItem.GetProperty("properties").GetProperty("points")));
        JsonElement amount = Resolve(root, item.GetProperty("properties").GetProperty("suggestedAmount"));
        Assert.Equal("number", SchemaType(amount.GetProperty("properties").GetProperty("amount")));
        Assert.Equal("string", SchemaType(amount.GetProperty("properties").GetProperty("currency")));
        JsonElement responses = operation.GetProperty("responses");
        Assert.True(responses.TryGetProperty("400", out _));
        Assert.True(responses.TryGetProperty("401", out _));
        Assert.True(responses.TryGetProperty("403", out _));
        Assert.True(responses.TryGetProperty("409", out _));
        Assert.True(responses.TryGetProperty("500", out _));
        Assert.Contains("points", operation.GetProperty("description").GetString(), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private async Task SaveAsync(
        IReadOnlyCollection<Invoice> invoices,
        IReadOnlyCollection<BankTransaction> bankTransactions)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.Invoices.AddRange(invoices);
        context.BankTransactions.AddRange(bankTransactions);
        await context.SaveChangesAsync();
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

    private async Task<int> AllocationCountAsync(OrganizationId organizationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.Allocations.CountAsync(
            allocation => allocation.OrganizationId == organizationId);
    }

    private async Task<int> ImportBatchCountAsync()
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.ImportBatches.CountAsync();
    }

    private static Invoice Invoice(OrganizationId organizationId, string invoiceNumber, decimal amount)
    {
        return new Invoice(
            InvoiceId.New(),
            organizationId,
            invoiceNumber,
            new Money(amount, PersistenceTestData.Pln));
    }

    private static BankTransaction Transaction(
        OrganizationId organizationId,
        string reference,
        decimal amount,
        CurrencyCode? currency = null)
    {
        return new BankTransaction(
            BankTransactionId.New(),
            organizationId,
            new Money(amount, currency ?? PersistenceTestData.Pln),
            new DateOnly(2026, 8, 24),
            reference);
    }

    private static JsonElement Find(JsonElement suggestions, Guid invoiceId)
    {
        return Assert.Single(
            suggestions.EnumerateArray(),
            suggestion => suggestion.GetProperty("invoiceId").GetGuid() == invoiceId);
    }

    private static void AssertSignal(JsonElement suggestion, string code, int points)
    {
        Assert.Contains(
            suggestion.GetProperty("signals").EnumerateArray(),
            signal => signal.GetProperty("code").GetString() == code
                && signal.GetProperty("points").GetInt32() == points);
    }

    private static void AssertScoreEqualsSignals(JsonElement suggestion)
    {
        int sum = suggestion.GetProperty("signals").EnumerateArray().Sum(
            signal => signal.GetProperty("points").GetInt32());
        Assert.Equal(suggestion.GetProperty("score").GetInt32(), sum);
    }

    private static void AssertDoesNotContainOutsideTrace(string body, string value)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (property.NameEquals("traceId"))
            {
                continue;
            }

            Assert.DoesNotContain(value, property.Value.GetRawText(), StringComparison.Ordinal);
        }
    }

    private static bool HasHeader(JsonElement operation, string name)
    {
        return operation.TryGetProperty("parameters", out JsonElement parameters)
            && parameters.EnumerateArray().Any(parameter =>
                parameter.GetProperty("in").GetString() == "header"
                && parameter.GetProperty("name").GetString() == name);
    }

    private static void AssertMoney(JsonElement money, decimal amount, string currency)
    {
        Assert.Equal(JsonValueKind.Number, money.GetProperty("amount").ValueKind);
        Assert.Equal(amount, money.GetProperty("amount").GetDecimal());
        Assert.Equal(currency, money.GetProperty("currency").GetString());
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        string json = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json);
    }

    private static JsonElement Resolve(JsonElement root, JsonElement schema)
    {
        if (schema.TryGetProperty("$ref", out JsonElement reference))
        {
            string name = reference.GetString()!.Split('/').Last();
            return root.GetProperty("components").GetProperty("schemas").GetProperty(name);
        }

        return schema;
    }

    private static string SchemaType(JsonElement schema)
    {
        JsonElement type = schema.GetProperty("type");
        if (type.ValueKind == JsonValueKind.Array)
        {
            return type[0].GetString() ?? string.Empty;
        }

        return type.GetString() ?? string.Empty;
    }

    private sealed class LimitExceededSmartApiFactory : FinOpsDeskApiFactory
    {
        private readonly bool _invoicesExceeded;

        public LimitExceededSmartApiFactory(string connectionString, bool invoicesExceeded)
            : base(connectionString)
        {
            _invoicesExceeded = invoicesExceeded;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IExactMatchCandidateQueries>();
                services.AddScoped<IExactMatchCandidateQueries>(_ => new LimitExceededCandidateQueries(_invoicesExceeded));
            });
        }

        private sealed class LimitExceededCandidateQueries : IExactMatchCandidateQueries
        {
            private readonly bool _invoicesExceeded;

            public LimitExceededCandidateQueries(bool invoicesExceeded)
            {
                _invoicesExceeded = invoicesExceeded;
            }

            public Task<ExactMatchCandidateSet> GetOpenCandidatesAsync(
                OrganizationId organizationId,
                int limit,
                CancellationToken cancellationToken)
            {
                if (limit != ExactMatchCandidateLimits.MaxPerSide)
                {
                    throw new InvalidOperationException(
                        "The smart match handler must apply the fixed open-book limit.");
                }

                CurrencyCode pln = new("PLN");
                MatchInvoiceSnapshot invoice = new(
                    organizationId,
                    new InvoiceId(new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
                    "INV-LIMIT",
                    new Money(42.5m, pln));
                MatchBankTransactionSnapshot bankTransaction = new(
                    organizationId,
                    new BankTransactionId(new Guid("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")),
                    "INV-LIMIT",
                    new Money(42.5m, pln));

                return Task.FromResult(new ExactMatchCandidateSet(
                    [invoice],
                    [bankTransaction],
                    invoiceLimitExceeded: _invoicesExceeded,
                    bankTransactionLimitExceeded: !_invoicesExceeded,
                    inconsistentMoney: false));
            }
        }
    }

    private sealed class CancellationObservingApiFactory : FinOpsDeskApiFactory
    {
        public CancellationObservingApiFactory(string connectionString)
            : base(connectionString)
        {
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IExactMatchCandidateQueries>();
                services.AddSingleton<CancellationObservingQueries>();
                services.AddSingleton<IExactMatchCandidateQueries>(
                    provider => provider.GetRequiredService<CancellationObservingQueries>());
            });
        }
    }

    private sealed class CancellationObservingQueries : IExactMatchCandidateQueries
    {
        public CancellationToken Observed { get; private set; }

        public Task<ExactMatchCandidateSet> GetOpenCandidatesAsync(
            OrganizationId organizationId,
            int limit,
            CancellationToken cancellationToken)
        {
            Observed = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ExactMatchCandidateSet(
                [],
                [],
                invoiceLimitExceeded: false,
                bankTransactionLimitExceeded: false,
                inconsistentMoney: false));
        }
    }
}