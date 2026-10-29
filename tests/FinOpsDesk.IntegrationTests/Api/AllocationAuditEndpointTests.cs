using System.Net;
using System.Text;
using System.Text.Json;

using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class AllocationAuditEndpointTests : IDisposable
{
    private readonly SqlServerFixture _fixture;
    private readonly FinOpsDeskApiFactory _factory;

    public AllocationAuditEndpointTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _factory = new FinOpsDeskApiFactory(fixture.ConnectionString);
    }

    [Fact]
    public async Task Exact_confirmation_audit_is_readable_by_the_owning_tenant()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "AUD-EXACT", 40m);
        using HttpClient writer = Client(organizationId, "exact-operator");
        HttpResponseMessage created = await writer.SendAsync(
            ConfirmRequest(invoice.Id.Value, bank.Id.Value, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Guid allocationId = await ReadAllocationIdAsync(created);
        (int allocations, int audits, decimal outstanding) = await SnapshotAsync(organizationId, invoice.Id);
        using HttpClient reader = Client(organizationId, subject: null);

        HttpResponseMessage response = await reader.GetAsync(AuditPath(allocationId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = document.RootElement;
        Assert.Equal(1, root.GetProperty("page").GetInt32());
        Assert.Equal(50, root.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, root.GetProperty("totalCount").GetInt32());
        JsonElement item = Assert.Single(root.GetProperty("items").EnumerateArray());
        Assert.Equal(allocationId, item.GetProperty("allocationId").GetGuid());
        Assert.Equal(invoice.Id.Value, item.GetProperty("invoiceId").GetGuid());
        Assert.Equal(bank.Id.Value, item.GetProperty("bankTransactionId").GetGuid());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("batchExecutionId").ValueKind);
        Assert.Equal("confirmation", item.GetProperty("action").GetString());
        Assert.Equal("exactMatch", item.GetProperty("operation").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("reason").ValueKind);
        Assert.Equal("exact-operator", item.GetProperty("actorId").GetString());
        Assert.Equal(40m, item.GetProperty("amount").GetProperty("amount").GetDecimal());
        Assert.Equal("PLN", item.GetProperty("amount").GetProperty("currency").GetString());
        Assert.False(item.TryGetProperty("idempotencyKey", out _));
        Assert.False(item.TryGetProperty("organizationId", out _));
        Assert.Equal((allocations, audits, outstanding), await SnapshotAsync(organizationId, invoice.Id));
    }

    [Fact]
    public async Task Explicit_amount_audit_records_the_confirmed_amount()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "AUD-PART", 100m, bankAmount: 80m);
        using HttpClient client = Client(organizationId, "explicit-operator");
        HttpResponseMessage created = await client.SendAsync(
            AllocateRequest(invoice.Id.Value, bank.Id.Value, 40m, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Guid allocationId = await ReadAllocationIdAsync(created);

        HttpResponseMessage response = await client.GetAsync(AuditPath(allocationId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement item = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("explicitAmount", item.GetProperty("operation").GetString());
        Assert.Equal("confirmation", item.GetProperty("action").GetString());
        Assert.Equal(40m, item.GetProperty("amount").GetProperty("amount").GetDecimal());
        Assert.Equal("explicit-operator", item.GetProperty("actorId").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("batchExecutionId").ValueKind);
    }

    [Fact]
    public async Task Batch_confirmation_audit_includes_the_persisted_batch_and_actor()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "AUD-BATCH", 25m);
        using HttpClient client = Client(organizationId, "batch-operator");
        HttpResponseMessage created = await client.SendAsync(
            BatchRequest(invoice.Id.Value, bank.Id.Value, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using JsonDocument createdDocument = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Guid allocationId = createdDocument.RootElement
            .GetProperty("items")[0]
            .GetProperty("allocationId")
            .GetGuid();

        HttpResponseMessage response = await client.GetAsync(AuditPath(allocationId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement item = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("batchExactMatch", item.GetProperty("operation").GetString());
        Assert.Equal("batch-operator", item.GetProperty("actorId").GetString());
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Guid batchExecutionId = await context.BatchExecutions
            .Where(execution => execution.OrganizationId == organizationId)
            .Select(execution => execution.Id.Value)
            .SingleAsync();
        Assert.Equal(batchExecutionId, item.GetProperty("batchExecutionId").GetGuid());
    }

    [Fact]
    public async Task Historical_allocation_without_an_audit_returns_an_empty_page()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "AUD-OLD", 12m);
        AllocationId allocationId = await SaveHistoricalAllocationAsync(organizationId, invoice, bank, 12m);
        using HttpClient client = Client(organizationId, subject: null);

        HttpResponseMessage response = await client.GetAsync(AuditPath(allocationId.Value));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, document.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Empty(document.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(0, await AuditCountAsync(organizationId));
    }

    [Fact]
    public async Task Unknown_and_cross_tenant_allocations_return_404_without_foreign_details()
    {
        OrganizationId owner = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(owner, "SECRET-AUD", 18m);
        using HttpClient ownerClient = Client(owner, "secret-operator");
        HttpResponseMessage created = await ownerClient.SendAsync(
            ConfirmRequest(invoice.Id.Value, bank.Id.Value, Guid.NewGuid()));
        Guid allocationId = await ReadAllocationIdAsync(created);
        using HttpClient intruder = Client(OrganizationId.New(), "intruder");

        HttpResponseMessage unknown = await ownerClient.GetAsync(AuditPath(Guid.NewGuid()));
        HttpResponseMessage foreign = await intruder.GetAsync(AuditPath(allocationId));

        string unknownBody = await AssertProblemAsync(
            unknown,
            HttpStatusCode.NotFound,
            ReconciliationApplicationErrors.AllocationNotFound.Code);
        string foreignBody = await AssertProblemAsync(
            foreign,
            HttpStatusCode.NotFound,
            ReconciliationApplicationErrors.AllocationNotFound.Code);
        Assert.DoesNotContain("SECRET-AUD", foreignBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-operator", foreignBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(owner.Value.ToString(), foreignBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-operator", unknownBody, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Malformed_allocation_id_returns_400(string allocationId)
    {
        using HttpClient client = Client(OrganizationId.New(), "reader");

        HttpResponseMessage response = await client.GetAsync("/api/allocations/" + allocationId + "/audit");

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.AllocationAuditInvalidRequest.Code);
    }

    [Theory]
    [InlineData("?page=0")]
    [InlineData("?page=-1")]
    [InlineData("?pageSize=0")]
    [InlineData("?pageSize=101")]
    [InlineData("?page=abc")]
    [InlineData("?page=1&page=2")]
    [InlineData("?pageSize=1&pageSize=2")]
    [InlineData("?pageSize=1&PageSize=2")]
    [InlineData("?Page=0")]
    [InlineData("?PageSize=101")]
    [InlineData("?page=2147483647&pageSize=100")]
    public async Task Invalid_pagination_returns_400(string query)
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "AUD-PAGE", 10m);
        AllocationId allocationId = await SaveHistoricalAllocationAsync(organizationId, invoice, bank, 10m);
        using HttpClient client = Client(organizationId, subject: null);

        HttpResponseMessage response = await client.GetAsync(AuditPath(allocationId.Value) + query);

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.AllocationAuditInvalidPagination.Code);
        Assert.Equal(0, await AuditCountAsync(organizationId));
    }

    [Fact]
    public async Task Page_beyond_the_end_returns_no_items_and_the_real_total()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "AUD-RANGE", 11m);
        using HttpClient client = Client(organizationId, "range-operator");
        HttpResponseMessage created = await client.SendAsync(
            ConfirmRequest(invoice.Id.Value, bank.Id.Value, Guid.NewGuid()));
        Guid allocationId = await ReadAllocationIdAsync(created);

        HttpResponseMessage first = await client.GetAsync(AuditPath(allocationId) + "?page=1&pageSize=1");
        HttpResponseMessage beyond = await client.GetAsync(AuditPath(allocationId) + "?page=2&pageSize=1");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, beyond.StatusCode);
        using JsonDocument firstDocument = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        using JsonDocument beyondDocument = JsonDocument.Parse(await beyond.Content.ReadAsStringAsync());
        Assert.Single(firstDocument.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(1, firstDocument.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Empty(beyondDocument.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(1, beyondDocument.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, beyondDocument.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(1, beyondDocument.RootElement.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task Case_variant_page_keys_bind_the_requested_page()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "AUD-CASE", 8m);
        AllocationId allocationId = await SaveHistoricalAllocationAsync(organizationId, invoice, bank, 8m);
        using HttpClient client = Client(organizationId, subject: null);

        HttpResponseMessage response = await client.GetAsync(
            AuditPath(allocationId.Value) + "?Page=2&PageSize=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, document.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("pageSize").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Empty(document.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(0, await AuditCountAsync(organizationId));
    }

    [Fact]
    public async Task Unknown_query_parameter_returns_400()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "AUD-QUERY", 9m);
        AllocationId allocationId = await SaveHistoricalAllocationAsync(organizationId, invoice, bank, 9m);
        using HttpClient client = Client(organizationId, subject: null);

        HttpResponseMessage response = await client.GetAsync(
            AuditPath(allocationId.Value) + "?actorId=spoofed-actor");

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.AllocationAuditInvalidRequest.Code);
    }

    [Fact]
    public async Task Missing_bearer_token_returns_401_and_missing_organization_returns_403()
    {
        using HttpClient anonymous = _factory.CreateApiClient();
        using HttpClient withoutOrganization = _factory.CreateApiClient(
            TestAccessTokens.CreateWithoutOrganizationClaim());

        HttpResponseMessage unauthenticated = await anonymous.GetAsync(AuditPath(Guid.NewGuid()));
        HttpResponseMessage forbidden = await withoutOrganization.GetAsync(AuditPath(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task OpenApi_document_describes_allocation_audit_pagination()
    {
        using HttpClient client = Client(OrganizationId.New(), "reader");

        HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement operation = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/allocations/{allocationId}/audit")
            .GetProperty("get");
        Assert.Equal("GetAllocationAudit", operation.GetProperty("operationId").GetString());
        Assert.Contains("TenantMember", operation.GetProperty("description").GetString(), StringComparison.Ordinal);
        bool page = false;
        bool pageSize = false;
        foreach (JsonElement parameter in operation.GetProperty("parameters").EnumerateArray())
        {
            string? name = parameter.GetProperty("name").GetString();
            if (name == "page")
            {
                page = true;
                Assert.Equal(1, parameter.GetProperty("schema").GetProperty("default").GetInt32());
            }

            if (name == "pageSize")
            {
                pageSize = true;
                Assert.Equal(50, parameter.GetProperty("schema").GetProperty("default").GetInt32());
                Assert.Equal(100, parameter.GetProperty("schema").GetProperty("maximum").GetInt32());
            }
        }

        Assert.True(page);
        Assert.True(pageSize);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private HttpClient Client(OrganizationId organizationId, string? subject)
    {
        return _factory.CreateApiClient(TestAccessTokens.CreateForActor(organizationId.Value, subject));
    }

    private async Task<(Invoice Invoice, BankTransaction Bank)> SeedPairAsync(
        OrganizationId organizationId,
        string reference,
        decimal amount,
        decimal? bankAmount = null)
    {
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, amount, reference);
        BankTransaction bank = PersistenceTestData.CreateBankTransaction(
            organizationId,
            bankAmount ?? amount,
            reference: reference);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.AddRange(invoice, bank);
        await context.SaveChangesAsync();
        return (invoice, bank);
    }

    private async Task<AllocationId> SaveHistoricalAllocationAsync(
        OrganizationId organizationId,
        Invoice invoice,
        BankTransaction bank,
        decimal amount)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Invoice storedInvoice = await context.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        BankTransaction storedBank = await context.BankTransactions.SingleAsync(
            candidate => candidate.Id == bank.Id);
        Money confirmed = new(amount, PersistenceTestData.Pln);
        Assert.True(AllocationConfirmation.Apply(storedInvoice, storedBank, confirmed).IsSuccess);
        Allocation allocation = new(
            AllocationId.New(),
            organizationId,
            storedInvoice.Id,
            storedBank.Id,
            confirmed,
            new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero),
            Guid.NewGuid(),
            AllocationIdempotencyOperation.ExplicitAmount);
        context.Allocations.Add(allocation);
        await context.SaveChangesAsync();
        return allocation.Id;
    }

    private async Task<(int Allocations, int Audits, decimal Outstanding)> SnapshotAsync(
        OrganizationId organizationId,
        InvoiceId invoiceId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        int allocations = await context.Allocations.CountAsync(
            allocation => allocation.OrganizationId == organizationId);
        int audits = await context.FinancialAuditEntries.CountAsync(
            entry => entry.OrganizationId == organizationId);
        decimal outstanding = await context.Invoices
            .Where(invoice => invoice.Id == invoiceId)
            .Select(invoice => invoice.OutstandingAmount.Amount)
            .SingleAsync();
        return (allocations, audits, outstanding);
    }

    private async Task<int> AuditCountAsync(OrganizationId organizationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.FinancialAuditEntries.CountAsync(
            entry => entry.OrganizationId == organizationId);
    }

    private static string AuditPath(Guid allocationId)
    {
        return "/api/allocations/" + allocationId.ToString("D") + "/audit";
    }

    private static HttpRequestMessage ConfirmRequest(Guid invoiceId, Guid bankTransactionId, Guid idempotencyKey)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/api/matches/confirm")
        {
            Content = new StringContent(
                "{\"invoiceId\":\"" + invoiceId + "\",\"bankTransactionId\":\"" + bankTransactionId + "\"}",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey.ToString("D"));
        return request;
    }

    private static HttpRequestMessage AllocateRequest(
        Guid invoiceId,
        Guid bankTransactionId,
        decimal amount,
        Guid idempotencyKey)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/api/allocations")
        {
            Content = new StringContent(
                "{\"invoiceId\":\"" + invoiceId
                    + "\",\"bankTransactionId\":\"" + bankTransactionId
                    + "\",\"amount\":{\"amount\":" + amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"currency\":\"PLN\"}}",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey.ToString("D"));
        return request;
    }

    private static HttpRequestMessage BatchRequest(Guid invoiceId, Guid bankTransactionId, Guid idempotencyKey)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/api/matches/batch/execute")
        {
            Content = new StringContent(
                "{\"pairs\":[{\"invoiceId\":\"" + invoiceId + "\",\"bankTransactionId\":\"" + bankTransactionId + "\"}]}",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey.ToString("D"));
        return request;
    }

    private static async Task<Guid> ReadAllocationIdAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("allocationId").GetGuid();
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
        return body;
    }
}