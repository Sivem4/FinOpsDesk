using System.Net;
using System.Text;
using System.Text.Json;

using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
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
public sealed class ConfirmMatchEndpointTests : IDisposable
{
    private readonly SqlServerFixture _fixture;
    private readonly FinOpsDeskApiFactory _factory;

    public ConfirmMatchEndpointTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _factory = new FinOpsDeskApiFactory(fixture.ConnectionString);
    }

    [Fact]
    public async Task Confirm_match_returns_201_and_a_replay_returns_200()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000.25m, "INV-100");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            1000.25m,
            reference: "inv-100");
        await SeedAsync(invoice, bankTransaction);
        string idempotencyKey = Guid.NewGuid().ToString("D");
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.Create(organizationId.Value));

        HttpResponseMessage created = await client.SendAsync(
            ConfirmRequest(invoice.Id.Value, bankTransaction.Id.Value, idempotencyKey));
        HttpResponseMessage replayed = await client.SendAsync(
            ConfirmRequest(invoice.Id.Value, bankTransaction.Id.Value, idempotencyKey));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        using JsonDocument createdDocument = await ReadJsonAsync(created);
        using JsonDocument replayedDocument = await ReadJsonAsync(replayed);
        JsonElement createdBody = createdDocument.RootElement;
        JsonElement replayedBody = replayedDocument.RootElement;

        Assert.False(createdBody.GetProperty("replayed").GetBoolean());
        Assert.True(replayedBody.GetProperty("replayed").GetBoolean());
        Assert.Equal(
            createdBody.GetProperty("allocationId").GetGuid(),
            replayedBody.GetProperty("allocationId").GetGuid());
        Assert.Equal(invoice.Id.Value, createdBody.GetProperty("invoiceId").GetGuid());
        Assert.Equal(bankTransaction.Id.Value, createdBody.GetProperty("bankTransactionId").GetGuid());
        Assert.Equal(1000.25m, createdBody.GetProperty("amount").GetProperty("amount").GetDecimal());
        Assert.Equal("PLN", createdBody.GetProperty("amount").GetProperty("currency").GetString());
        Assert.False(createdBody.TryGetProperty("rowVersion", out _));
        Assert.False(createdBody.TryGetProperty("organizationId", out _));

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Invoice stored = await reload.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        Assert.Equal(new Money(1000.25m, PersistenceTestData.Pln), stored.AllocatedAmount);
        Assert.Equal(
            1,
            await reload.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId));
    }

    [Theory]
    [InlineData("amount", "1")]
    [InlineData("note", "\"manual review\"")]
    public async Task Unknown_json_properties_return_400_and_write_nothing(
        string propertyName,
        string propertyValue)
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 75m, "INV-JSON");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            75m,
            reference: "INV-JSON");
        await SeedAsync(invoice, bankTransaction);
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.Create(organizationId.Value));
        string json = $$"""
            {"invoiceId":"{{invoice.Id.Value}}","bankTransactionId":"{{bankTransaction.Id.Value}}","{{propertyName}}":{{propertyValue}}}
            """;

        HttpResponseMessage response = await client.SendAsync(
            ConfirmRaw(json, Guid.NewGuid().ToString("D")));

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ConfirmationInvalidRequest.Code);

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Invoice storedInvoice = await reload.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        BankTransaction storedBankTransaction = await reload.BankTransactions.SingleAsync(
            candidate => candidate.Id == bankTransaction.Id);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), storedInvoice.AllocatedAmount);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), storedBankTransaction.AllocatedAmount);
        Assert.Equal(
            0,
            await reload.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId));
    }

    [Fact]
    public async Task Explicit_confirmation_of_an_ambiguous_pair_allocates_only_the_selected_records()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoiceA = PersistenceTestData.CreateInvoice(organizationId, 100m, "INV-100");
        Invoice invoiceB = PersistenceTestData.CreateInvoice(organizationId, 100m, "INV-100");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            100m,
            reference: "INV-100");
        await SeedAsync(invoiceA, invoiceB, bankTransaction);
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.Create(organizationId.Value));

        HttpResponseMessage matches = await client.GetAsync("/api/matches");

        Assert.Equal(HttpStatusCode.OK, matches.StatusCode);
        using JsonDocument matchDocument = await ReadJsonAsync(matches);
        JsonElement root = matchDocument.RootElement;
        Assert.Equal(0, root.GetProperty("exactMatches").GetArrayLength());
        JsonElement ambiguous = Assert.Single(root.GetProperty("ambiguousGroups").EnumerateArray());
        Assert.Contains(
            ambiguous.GetProperty("invoiceIds").EnumerateArray(),
            id => id.GetGuid() == invoiceA.Id.Value);
        Assert.Contains(
            ambiguous.GetProperty("invoiceIds").EnumerateArray(),
            id => id.GetGuid() == invoiceB.Id.Value);
        Assert.Equal(
            bankTransaction.Id.Value,
            Assert.Single(ambiguous.GetProperty("bankTransactionIds").EnumerateArray()).GetGuid());

        HttpResponseMessage confirmed = await client.SendAsync(
            ConfirmRequest(invoiceA.Id.Value, bankTransaction.Id.Value, Guid.NewGuid().ToString("D")));

        Assert.Equal(HttpStatusCode.Created, confirmed.StatusCode);

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Allocation stored = await reload.Allocations.SingleAsync(
            allocation => allocation.OrganizationId == organizationId);
        Invoice storedA = await reload.Invoices.SingleAsync(candidate => candidate.Id == invoiceA.Id);
        Invoice storedB = await reload.Invoices.SingleAsync(candidate => candidate.Id == invoiceB.Id);
        BankTransaction storedBankTransaction = await reload.BankTransactions.SingleAsync(
            candidate => candidate.Id == bankTransaction.Id);

        Assert.Equal(invoiceA.Id, stored.InvoiceId);
        Assert.Equal(bankTransaction.Id, stored.BankTransactionId);
        Assert.Equal(new Money(100m, PersistenceTestData.Pln), storedA.AllocatedAmount);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), storedB.AllocatedAmount);
        Assert.Equal(new Money(100m, PersistenceTestData.Pln), storedBankTransaction.AllocatedAmount);
    }

    [Fact]
    public async Task Same_key_with_a_changed_pair_returns_409_and_writes_nothing_new()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice firstInvoice = PersistenceTestData.CreateInvoice(organizationId, 20m, "INV-001");
        BankTransaction firstBankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            20m,
            reference: "INV-001");
        Invoice secondInvoice = PersistenceTestData.CreateInvoice(organizationId, 20m, "INV-002");
        BankTransaction secondBankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            20m,
            reference: "INV-002");
        await SeedAsync(firstInvoice, firstBankTransaction, secondInvoice, secondBankTransaction);
        string idempotencyKey = Guid.NewGuid().ToString("D");
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.Create(organizationId.Value));

        HttpResponseMessage created = await client.SendAsync(
            ConfirmRequest(firstInvoice.Id.Value, firstBankTransaction.Id.Value, idempotencyKey));
        HttpResponseMessage conflict = await client.SendAsync(
            ConfirmRequest(secondInvoice.Id.Value, secondBankTransaction.Id.Value, idempotencyKey));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await AssertProblemAsync(
            conflict,
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.IdempotencyPayloadConflict.Code);

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Assert.Equal(
            1,
            await reload.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId));
    }

    [Fact]
    public async Task Stale_reference_and_currency_mismatches_write_nothing()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice staleInvoice = PersistenceTestData.CreateInvoice(organizationId, 100m, "INV-STALE");
        BankTransaction staleBankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            40m,
            reference: "INV-STALE");
        Invoice referenceInvoice = PersistenceTestData.CreateInvoice(organizationId, 10m, "INV-REF");
        BankTransaction referenceBankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            10m,
            reference: "OTHER");
        Invoice currencyInvoice = PersistenceTestData.CreateInvoice(organizationId, 10m, "INV-CUR");
        var currencyBankTransaction = new BankTransaction(
            BankTransactionId.New(),
            organizationId,
            new Money(10m, new CurrencyCode("EUR")),
            new DateOnly(2026, 8, 24),
            "INV-CUR");
        await SeedAsync(
            staleInvoice,
            staleBankTransaction,
            referenceInvoice,
            referenceBankTransaction,
            currencyInvoice,
            currencyBankTransaction);
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.Create(organizationId.Value));

        HttpResponseMessage stale = await client.SendAsync(
            ConfirmRequest(staleInvoice.Id.Value, staleBankTransaction.Id.Value, Guid.NewGuid().ToString("D")));
        HttpResponseMessage reference = await client.SendAsync(
            ConfirmRequest(
                referenceInvoice.Id.Value,
                referenceBankTransaction.Id.Value,
                Guid.NewGuid().ToString("D")));
        HttpResponseMessage currency = await client.SendAsync(
            ConfirmRequest(
                currencyInvoice.Id.Value,
                currencyBankTransaction.Id.Value,
                Guid.NewGuid().ToString("D")));

        await AssertProblemAsync(stale, HttpStatusCode.Conflict, "reconciliation.match.stale");
        await AssertProblemAsync(reference, HttpStatusCode.Conflict, "reconciliation.match.reference_mismatch");
        await AssertProblemAsync(currency, HttpStatusCode.Conflict, "reconciliation.match.currency_mismatch");

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Assert.Equal(
            0,
            await reload.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId));
    }

    [Fact]
    public async Task Cross_tenant_ids_return_404_without_foreign_details()
    {
        OrganizationId owner = OrganizationId.New();
        OrganizationId caller = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(owner, 88.5m, "SECRET-INV");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            owner,
            88.5m,
            reference: "SECRET-INV");
        await SeedAsync(invoice, bankTransaction);
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(caller.Value));

        HttpResponseMessage response = await client.SendAsync(
            ConfirmRequest(invoice.Id.Value, bankTransaction.Id.Value, Guid.NewGuid().ToString("D")));

        string body = await AssertProblemAsync(
            response,
            HttpStatusCode.NotFound,
            ReconciliationApplicationErrors.InvoiceNotFound.Code);
        Assert.DoesNotContain("SECRET-INV", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("88.5", body, StringComparison.Ordinal);
        Assert.DoesNotContain(owner.Value.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Organization_query_and_header_cannot_switch_tenant()
    {
        OrganizationId organizationA = OrganizationId.New();
        OrganizationId organizationB = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationA, 30m, "INV-A");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationA,
            30m,
            reference: "INV-A");
        await SeedAsync(invoice, bankTransaction);
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.Create(organizationA.Value));
        client.DefaultRequestHeaders.Add("X-Organization-Id", organizationB.Value.ToString());

        HttpResponseMessage response = await client.SendAsync(
            ConfirmRequest(
                invoice.Id.Value,
                bankTransaction.Id.Value,
                Guid.NewGuid().ToString("D"),
                $"/api/matches/confirm?organizationId={organizationB.Value}"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Allocation stored = await reload.Allocations.SingleAsync(
            allocation => allocation.InvoiceId == invoice.Id);
        Assert.Equal(organizationA, stored.OrganizationId);
    }

    [Fact]
    public async Task Missing_token_returns_401()
    {
        using HttpClient client = _factory.CreateApiClient();

        HttpResponseMessage response = await client.SendAsync(
            ConfirmRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString("D")));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_token_returns_401()
    {
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.CreateSignedWithOtherKey(Guid.NewGuid()));

        HttpResponseMessage response = await client.SendAsync(
            ConfirmRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString("D")));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Missing_organization_claim_returns_403()
    {
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.CreateWithoutOrganizationClaim());

        HttpResponseMessage response = await client.SendAsync(
            ConfirmRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString("D")));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Unusable_organization_claim_returns_403(string organizationClaim)
    {
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.CreateWithOrganizationClaim(organizationClaim));

        HttpResponseMessage response = await client.SendAsync(
            ConfirmRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString("D")));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Missing_or_invalid_idempotency_key_returns_400()
    {
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(Guid.NewGuid()));

        HttpResponseMessage missing = await client.SendAsync(
            ConfirmRequest(Guid.NewGuid(), Guid.NewGuid(), idempotencyKey: null));
        HttpResponseMessage invalid = await client.SendAsync(
            ConfirmRequest(Guid.NewGuid(), Guid.NewGuid(), "not-a-uuid"));
        HttpResponseMessage empty = await client.SendAsync(
            ConfirmRequest(Guid.Empty, Guid.NewGuid(), Guid.NewGuid().ToString("D")));

        await AssertProblemAsync(
            missing,
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.IdempotencyKeyRequired.Code);
        await AssertProblemAsync(
            invalid,
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.IdempotencyKeyInvalid.Code);
        await AssertProblemAsync(
            empty,
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ConfirmationInvalidRequest.Code);
    }

    [Fact]
    public async Task Concurrency_conflict_returns_409_and_persists_nothing()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 12m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 12m);
        await SeedAsync(invoice, bankTransaction);
        using ConflictApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient client = factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));

        HttpResponseMessage response = await client.SendAsync(
            ConfirmRequest(invoice.Id.Value, bankTransaction.Id.Value, Guid.NewGuid().ToString("D")));

        string body = await AssertProblemAsync(
            response,
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.ConcurrencyConflict.Code);
        Assert.DoesNotContain("rowVersion", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SqlException", body, StringComparison.Ordinal);

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Assert.Equal(
            0,
            await reload.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId));
        Invoice stored = await reload.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), stored.AllocatedAmount);
    }

    public void Dispose()
    {
        _factory.Dispose();
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

    private static HttpRequestMessage ConfirmRequest(
        Guid invoiceId,
        Guid bankTransactionId,
        string? idempotencyKey,
        string path = "/api/matches/confirm")
    {
        string json = $$"""{"invoiceId":"{{invoiceId}}","bankTransactionId":"{{bankTransactionId}}"}""";
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        return request;
    }

    private static HttpRequestMessage ConfirmRaw(string json, string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/matches/confirm")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        return request;
    }

    private static async Task<string> AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode statusCode,
        string code)
    {
        Assert.Equal(statusCode, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.Equal((int)statusCode, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("detail").GetString()));
        return body;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        string json = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json);
    }

    private sealed class ConflictApiFactory : FinOpsDeskApiFactory
    {
        public ConflictApiFactory(string connectionString)
            : base(connectionString)
        {
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUnitOfWork>();
                services.AddScoped<IUnitOfWork, ConflictingUnitOfWork>();
            });
        }

        private sealed class ConflictingUnitOfWork : IUnitOfWork
        {
            public Task SaveChangesAsync(CancellationToken cancellationToken)
            {
                throw new ConcurrencyConflictException(
                    "The persisted data was modified by another operation.");
            }
        }
    }
}