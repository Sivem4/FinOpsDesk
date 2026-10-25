using System.Net;
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
public sealed class ExactMatchEndpointTests : IDisposable
{
    private readonly SqlServerFixture _fixture;
    private readonly FinOpsDeskApiFactory _factory;

    public ExactMatchEndpointTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _factory = new FinOpsDeskApiFactory(fixture.ConnectionString);
    }

    [Fact]
    public async Task Get_matches_returns_the_advisory_exact_match_result()
    {
        SeededBook seeded = await SeedBookAsync();
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.Create(seeded.OrganizationA.Value));

        HttpResponseMessage response = await client.GetAsync(
            $"/api/matches?organizationId={seeded.OrganizationB.Value}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;

        Assert.Equal(ExactMatchRules.AlgorithmVersion, root.GetProperty("algorithmVersion").GetString());
        Assert.False(root.TryGetProperty("organizationId", out _));
        Assert.False(root.TryGetProperty("rowVersion", out _));
        Assert.DoesNotContain(seeded.OrganizationA.Value.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(seeded.OrganizationB.Value.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rowVersion", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(seeded.ClosedInvoice.Id.Value.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(seeded.OrganizationBInvoice.Id.Value.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            seeded.OrganizationBTransaction.Id.Value.ToString(),
            body,
            StringComparison.OrdinalIgnoreCase);

        JsonElement exactMatch = Assert.Single(root.GetProperty("exactMatches").EnumerateArray());
        Assert.Equal(JsonValueKind.String, exactMatch.GetProperty("reason").ValueKind);
        Assert.Equal("ExactReferenceCurrencyAndOpenAmount", exactMatch.GetProperty("reason").GetString());
        Assert.Equal(seeded.ExactInvoice.Id.Value, exactMatch.GetProperty("invoiceId").GetGuid());
        Assert.Equal(
            seeded.ExactTransaction.Id.Value,
            exactMatch.GetProperty("bankTransactionId").GetGuid());
        Assert.Equal("INV-100", exactMatch.GetProperty("normalizedReference").GetString());
        AssertMoney(exactMatch.GetProperty("amount"), 100.25m, "PLN");
        Assert.False(exactMatch.TryGetProperty("organizationId", out _));
        Assert.False(exactMatch.TryGetProperty("rowVersion", out _));

        JsonElement ambiguous = Assert.Single(root.GetProperty("ambiguousGroups").EnumerateArray());
        Assert.Equal(JsonValueKind.String, ambiguous.GetProperty("reason").ValueKind);
        Assert.Equal("CompetingInvoices", ambiguous.GetProperty("reason").GetString());
        Assert.Equal("INV-AMB", ambiguous.GetProperty("normalizedReference").GetString());
        AssertMoney(ambiguous.GetProperty("openAmount"), 20m, "PLN");
        Assert.Equal(2, ambiguous.GetProperty("invoiceIds").GetArrayLength());
        Assert.Contains(
            ambiguous.GetProperty("invoiceIds").EnumerateArray(),
            id => id.GetGuid() == seeded.AmbiguousInvoiceA.Id.Value);
        Assert.Contains(
            ambiguous.GetProperty("invoiceIds").EnumerateArray(),
            id => id.GetGuid() == seeded.AmbiguousInvoiceB.Id.Value);
        Assert.Equal(
            seeded.AmbiguousTransaction.Id.Value,
            Assert.Single(ambiguous.GetProperty("bankTransactionIds").EnumerateArray()).GetGuid());

        JsonElement unmatched = root.GetProperty("unmatched");
        Assert.Equal(2, unmatched.GetArrayLength());
        JsonElement unmatchedInvoice = Assert.Single(
            unmatched.EnumerateArray(),
            item => item.GetProperty("invoiceId").ValueKind == JsonValueKind.String);
        Assert.Equal(JsonValueKind.String, unmatchedInvoice.GetProperty("classification").ValueKind);
        Assert.Equal("NoMatch", unmatchedInvoice.GetProperty("classification").GetString());
        Assert.Equal("NoCounterpart", unmatchedInvoice.GetProperty("reason").GetString());
        Assert.Equal(seeded.UnmatchedInvoice.Id.Value, unmatchedInvoice.GetProperty("invoiceId").GetGuid());
        Assert.Equal(JsonValueKind.Null, unmatchedInvoice.GetProperty("bankTransactionId").ValueKind);
        Assert.Equal("INV-ONLY", unmatchedInvoice.GetProperty("normalizedReference").GetString());
        AssertMoney(unmatchedInvoice.GetProperty("openAmount"), 50m, "PLN");

        JsonElement unmatchedTransaction = Assert.Single(
            unmatched.EnumerateArray(),
            item => item.GetProperty("bankTransactionId").ValueKind == JsonValueKind.String
                && item.GetProperty("invoiceId").ValueKind == JsonValueKind.Null);
        Assert.Equal(
            seeded.ClosedTransaction.Id.Value,
            unmatchedTransaction.GetProperty("bankTransactionId").GetGuid());
        Assert.Equal("NoMatch", unmatchedTransaction.GetProperty("classification").GetString());

        JsonElement ineligible = Assert.Single(root.GetProperty("ineligible").EnumerateArray());
        Assert.Equal(JsonValueKind.String, ineligible.GetProperty("classification").ValueKind);
        Assert.Equal("Ineligible", ineligible.GetProperty("classification").GetString());
        Assert.Equal("MissingReference", ineligible.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, ineligible.GetProperty("invoiceId").ValueKind);
        Assert.Equal(
            seeded.MissingReferenceTransaction.Id.Value,
            ineligible.GetProperty("bankTransactionId").GetGuid());
        Assert.Equal(JsonValueKind.Null, ineligible.GetProperty("normalizedReference").ValueKind);
        AssertMoney(ineligible.GetProperty("openAmount"), 10m, "PLN");
    }

    [Fact]
    public async Task Get_matches_for_an_empty_book_returns_empty_collections()
    {
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.Create(OrganizationId.New().Value));

        HttpResponseMessage response = await client.GetAsync("/api/matches");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = await ReadJsonAsync(response);
        JsonElement root = document.RootElement;
        Assert.Equal("exact-1", root.GetProperty("algorithmVersion").GetString());
        Assert.Equal(0, root.GetProperty("exactMatches").GetArrayLength());
        Assert.Equal(0, root.GetProperty("ambiguousGroups").GetArrayLength());
        Assert.Equal(0, root.GetProperty("unmatched").GetArrayLength());
        Assert.Equal(0, root.GetProperty("ineligible").GetArrayLength());
    }

    [Fact]
    public async Task Get_matches_without_a_bearer_token_returns_401()
    {
        using HttpClient client = _factory.CreateApiClient();

        HttpResponseMessage response = await client.GetAsync("/api/matches");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_matches_with_an_invalid_token_returns_401()
    {
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.CreateSignedWithOtherKey(Guid.NewGuid()));

        HttpResponseMessage response = await client.GetAsync("/api/matches");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_matches_without_an_organization_claim_returns_403()
    {
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.CreateWithoutOrganizationClaim());

        HttpResponseMessage response = await client.GetAsync("/api/matches");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Get_matches_with_an_unusable_organization_claim_returns_403(
        string organizationClaim)
    {
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.CreateWithOrganizationClaim(organizationClaim));

        HttpResponseMessage response = await client.GetAsync("/api/matches");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Candidate_limit_failure_returns_409_problem_details()
    {
        using LimitExceededMatchApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient client = factory.CreateApiClient(TestAccessTokens.Create(Guid.NewGuid()));

        HttpResponseMessage response = await client.GetAsync("/api/matches");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        Assert.Equal(409, root.GetProperty("status").GetInt32());
        Assert.Equal(
            ReconciliationApplicationErrors.MatchCandidateLimitExceeded.Code,
            root.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("detail").GetString()));
        Assert.False(root.TryGetProperty("exactMatches", out _));
        Assert.DoesNotContain("INV-LIMIT", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("42.5", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SqlException", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inconsistent_money_returns_sanitized_problem_details()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = new(
            InvoiceId.New(),
            organizationId,
            "SAFE-PAIR",
            new Money(10m, PersistenceTestData.Pln));
        BankTransaction bankTransaction = new(
            BankTransactionId.New(),
            organizationId,
            new Money(10m, PersistenceTestData.Pln),
            new DateOnly(2026, 8, 24),
            "SAFE-PAIR");
        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.Invoices.Add(invoice);
            context.BankTransactions.Add(bankTransaction);
            await context.SaveChangesAsync();
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

        HttpResponseMessage response = await client.GetAsync("/api/matches");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        Assert.Equal(500, root.GetProperty("status").GetInt32());
        Assert.Equal(
            ReconciliationApplicationErrors.InconsistentMatchMoney.Code,
            root.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("detail").GetString()));
        Assert.False(root.TryGetProperty("exactMatches", out _));
        Assert.False(root.TryGetProperty("exception", out _));
        Assert.DoesNotContain("CORRUPT-ROW", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SAFE-PAIR", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("12345", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SqlException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(organizationId.Value.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Get_matches_does_not_change_financial_rows()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = new(
            InvoiceId.New(),
            organizationId,
            "INV-1",
            new Money(40m, PersistenceTestData.Pln));
        Assert.True(invoice.Allocate(new Money(10m, PersistenceTestData.Pln)).IsSuccess);
        BankTransaction bankTransaction = new(
            BankTransactionId.New(),
            organizationId,
            new Money(30m, PersistenceTestData.Pln),
            new DateOnly(2026, 8, 24),
            "INV-1");
        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.Invoices.Add(invoice);
            context.BankTransactions.Add(bankTransaction);
            await context.SaveChangesAsync();
        }

        byte[] invoiceVersion = await InvoiceRowVersionAsync(invoice.Id);
        byte[] bankTransactionVersion = await BankTransactionRowVersionAsync(bankTransaction.Id);
        int allocations = await AllocationCountAsync(organizationId);
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.Create(organizationId.Value));

        HttpResponseMessage response = await client.GetAsync("/api/matches");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        byte[] invoiceVersionAfter = await InvoiceRowVersionAsync(invoice.Id);
        byte[] bankTransactionVersionAfter = await BankTransactionRowVersionAsync(bankTransaction.Id);
        Assert.True(invoiceVersion.SequenceEqual(invoiceVersionAfter));
        Assert.True(bankTransactionVersion.SequenceEqual(bankTransactionVersionAfter));
        Assert.Equal(allocations, await AllocationCountAsync(organizationId));
        Assert.Equal(0, allocations);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private async Task<SeededBook> SeedBookAsync()
    {
        OrganizationId organizationA = OrganizationId.New();
        OrganizationId organizationB = OrganizationId.New();
        Invoice exactInvoice = new(
            InvoiceId.New(),
            organizationA,
            " inv-100 ",
            new Money(100.25m, PersistenceTestData.Pln));
        BankTransaction exactTransaction = new(
            BankTransactionId.New(),
            organizationA,
            new Money(100.25m, PersistenceTestData.Pln),
            new DateOnly(2026, 8, 24),
            "INV-100");
        Invoice unmatchedInvoice = new(
            InvoiceId.New(),
            organizationA,
            "INV-ONLY",
            new Money(50m, PersistenceTestData.Pln));
        BankTransaction missingReferenceTransaction = new(
            BankTransactionId.New(),
            organizationA,
            new Money(10m, PersistenceTestData.Pln),
            new DateOnly(2026, 8, 24),
            reference: null);
        Invoice closedInvoice = new(
            InvoiceId.New(),
            organizationA,
            "INV-CLOSED",
            new Money(80m, PersistenceTestData.Pln));
        Assert.True(closedInvoice.Allocate(new Money(80m, PersistenceTestData.Pln)).IsSuccess);
        BankTransaction closedTransaction = new(
            BankTransactionId.New(),
            organizationA,
            new Money(80m, PersistenceTestData.Pln),
            new DateOnly(2026, 8, 24),
            "INV-CLOSED");
        Invoice ambiguousInvoiceA = new(
            InvoiceId.New(),
            organizationA,
            "INV-AMB",
            new Money(20m, PersistenceTestData.Pln));
        Invoice ambiguousInvoiceB = new(
            InvoiceId.New(),
            organizationA,
            "INV-AMB",
            new Money(20m, PersistenceTestData.Pln));
        BankTransaction ambiguousTransaction = new(
            BankTransactionId.New(),
            organizationA,
            new Money(20m, PersistenceTestData.Pln),
            new DateOnly(2026, 8, 24),
            "INV-AMB");
        Invoice organizationBInvoice = new(
            InvoiceId.New(),
            organizationB,
            "inv-100",
            new Money(100.25m, PersistenceTestData.Pln));
        BankTransaction organizationBTransaction = new(
            BankTransactionId.New(),
            organizationB,
            new Money(100.25m, PersistenceTestData.Pln),
            new DateOnly(2026, 8, 24),
            "INV-100");

        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.Invoices.AddRange(
            exactInvoice,
            unmatchedInvoice,
            closedInvoice,
            ambiguousInvoiceA,
            ambiguousInvoiceB,
            organizationBInvoice);
        context.BankTransactions.AddRange(
            exactTransaction,
            missingReferenceTransaction,
            closedTransaction,
            ambiguousTransaction,
            organizationBTransaction);
        await context.SaveChangesAsync();

        return new SeededBook(
            organizationA,
            organizationB,
            exactInvoice,
            exactTransaction,
            unmatchedInvoice,
            missingReferenceTransaction,
            closedInvoice,
            closedTransaction,
            ambiguousInvoiceA,
            ambiguousInvoiceB,
            ambiguousTransaction,
            organizationBInvoice,
            organizationBTransaction);
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

    private sealed record SeededBook(
        OrganizationId OrganizationA,
        OrganizationId OrganizationB,
        Invoice ExactInvoice,
        BankTransaction ExactTransaction,
        Invoice UnmatchedInvoice,
        BankTransaction MissingReferenceTransaction,
        Invoice ClosedInvoice,
        BankTransaction ClosedTransaction,
        Invoice AmbiguousInvoiceA,
        Invoice AmbiguousInvoiceB,
        BankTransaction AmbiguousTransaction,
        Invoice OrganizationBInvoice,
        BankTransaction OrganizationBTransaction);

    private sealed class LimitExceededMatchApiFactory : FinOpsDeskApiFactory
    {
        public LimitExceededMatchApiFactory(string connectionString)
            : base(connectionString)
        {
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IExactMatchCandidateQueries>();
                services.AddScoped<IExactMatchCandidateQueries, LimitExceededCandidateQueries>();
            });
        }

        private sealed class LimitExceededCandidateQueries : IExactMatchCandidateQueries
        {
            public Task<ExactMatchCandidateSet> GetOpenCandidatesAsync(
                OrganizationId organizationId,
                int limit,
                CancellationToken cancellationToken)
            {
                if (limit != ExactMatchCandidateLimits.MaxPerSide)
                {
                    throw new InvalidOperationException(
                        "The match handler must apply the fixed open-book limit.");
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
                    invoiceLimitExceeded: true,
                    bankTransactionLimitExceeded: false,
                    inconsistentMoney: false));
            }
        }
    }
}