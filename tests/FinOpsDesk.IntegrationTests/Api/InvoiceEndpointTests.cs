using System.Net;
using System.Text.Json;

using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class InvoiceEndpointTests : IDisposable
{
    private readonly SqlServerFixture _fixture;
    private readonly FinOpsDeskApiFactory _factory;

    public InvoiceEndpointTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _factory = new FinOpsDeskApiFactory(fixture.ConnectionString);
    }

    [Fact]
    public async Task Get_invoices_returns_only_the_token_organization()
    {
        SeededInvoices seeded = await SeedInvoicesAsync();
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.Create(seeded.OrganizationA.Value));

        HttpResponseMessage response = await client.GetAsync("/api/invoices");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = await ReadJsonAsync(response);
        JsonElement root = document.RootElement;
        Assert.Equal(1, root.GetProperty("page").GetInt32());
        Assert.Equal(50, root.GetProperty("pageSize").GetInt32());
        Assert.Equal(2, root.GetProperty("totalCount").GetInt32());
        Assert.False(root.TryGetProperty("organizationId", out _));
        Assert.False(root.TryGetProperty("rowVersion", out _));

        JsonElement items = root.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        JsonElement partial = AssertInvoice(items, seeded.PartialInvoice.Id.Value, "INV-A-1");
        AssertMoney(partial.GetProperty("totalAmount"), 1000.25m, "PLN");
        AssertMoney(partial.GetProperty("allocatedAmount"), 250.10m, "PLN");
        AssertMoney(partial.GetProperty("outstandingAmount"), 750.15m, "PLN");
        AssertInvoice(items, seeded.OtherInvoice.Id.Value, "INV-A-2");
        Assert.DoesNotContain(
            items.EnumerateArray(),
            item => item.GetProperty("invoiceNumber").GetString() == "INV-B-1");
    }

    [Fact]
    public async Task Get_invoices_pages_in_invoice_id_order()
    {
        SeededInvoices seeded = await SeedInvoicesAsync();
        List<Guid> expectedOrder = await LoadOrganizationAOrderAsync(seeded.OrganizationA);
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.Create(seeded.OrganizationA.Value));

        HttpResponseMessage response = await client.GetAsync("/api/invoices?page=1&pageSize=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = await ReadJsonAsync(response);
        JsonElement root = document.RootElement;
        Assert.Equal(1, root.GetProperty("page").GetInt32());
        Assert.Equal(1, root.GetProperty("pageSize").GetInt32());
        Assert.Equal(2, root.GetProperty("totalCount").GetInt32());
        JsonElement item = Assert.Single(root.GetProperty("items").EnumerateArray());
        Assert.Equal(expectedOrder[0], item.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Get_invoices_without_a_bearer_token_returns_401()
    {
        using HttpClient client = _factory.CreateApiClient();

        HttpResponseMessage response = await client.GetAsync("/api/invoices");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_invoices_with_an_invalid_token_returns_401()
    {
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.CreateSignedWithOtherKey(Guid.NewGuid()));

        HttpResponseMessage response = await client.GetAsync("/api/invoices");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_invoices_without_an_organization_claim_returns_403()
    {
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.CreateWithoutOrganizationClaim());

        HttpResponseMessage response = await client.GetAsync("/api/invoices");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Get_invoices_with_an_unusable_organization_claim_returns_403(
        string organizationClaim)
    {
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.CreateWithOrganizationClaim(organizationClaim));

        HttpResponseMessage response = await client.GetAsync("/api/invoices");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Organization_query_string_cannot_switch_tenant()
    {
        SeededInvoices seeded = await SeedInvoicesAsync();
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.Create(seeded.OrganizationA.Value));

        HttpResponseMessage response = await client.GetAsync(
            $"/api/invoices?organizationId={seeded.OrganizationB.Value}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = await ReadJsonAsync(response);
        JsonElement items = document.RootElement.GetProperty("items");
        Assert.Equal(2, document.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Contains(
            items.EnumerateArray(),
            item => item.GetProperty("invoiceNumber").GetString() == "INV-A-1");
        Assert.DoesNotContain(
            items.EnumerateArray(),
            item => item.GetProperty("invoiceNumber").GetString() == "INV-B-1");
    }

    [Fact]
    public async Task Organization_header_cannot_switch_tenant()
    {
        SeededInvoices seeded = await SeedInvoicesAsync();
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.Create(seeded.OrganizationA.Value));
        client.DefaultRequestHeaders.Add(
            "X-Organization-Id",
            seeded.OrganizationB.Value.ToString());

        HttpResponseMessage response = await client.GetAsync("/api/invoices");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = await ReadJsonAsync(response);
        JsonElement items = document.RootElement.GetProperty("items");
        Assert.Equal(2, document.RootElement.GetProperty("totalCount").GetInt32());
        Assert.DoesNotContain(
            items.EnumerateArray(),
            item => item.GetProperty("invoiceNumber").GetString() == "INV-B-1");
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    [InlineData("page=2147483647&pageSize=100")]
    public async Task Invalid_pagination_returns_400_problem_details(string query)
    {
        SeededInvoices seeded = await SeedInvoicesAsync();
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.Create(seeded.OrganizationA.Value));

        HttpResponseMessage response = await client.GetAsync($"/api/invoices?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using JsonDocument document = await ReadJsonAsync(response);
        JsonElement root = document.RootElement;
        Assert.Equal(400, root.GetProperty("status").GetInt32());
        Assert.Equal(
            ReconciliationApplicationErrors.InvalidInvoicePagination.Code,
            root.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("detail").GetString()));
    }

    [Fact]
    public async Task Health_remains_anonymous()
    {
        using HttpClient client = _factory.CreateApiClient();

        HttpResponseMessage response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private async Task<SeededInvoices> SeedInvoicesAsync()
    {
        OrganizationId organizationA = OrganizationId.New();
        OrganizationId organizationB = OrganizationId.New();
        Invoice partialInvoice = new(
            InvoiceId.New(),
            organizationA,
            "INV-A-1",
            new Money(1000.25m, PersistenceTestData.Pln));
        partialInvoice.Allocate(new Money(250.10m, PersistenceTestData.Pln));
        Invoice otherInvoice = new(
            InvoiceId.New(),
            organizationA,
            "INV-A-2",
            new Money(80m, PersistenceTestData.Pln));
        Invoice organizationBInvoice = new(
            InvoiceId.New(),
            organizationB,
            "INV-B-1",
            new Money(999m, PersistenceTestData.Pln));

        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.Invoices.AddRange(partialInvoice, otherInvoice, organizationBInvoice);
        await context.SaveChangesAsync();

        return new SeededInvoices(
            organizationA,
            organizationB,
            partialInvoice,
            otherInvoice);
    }

    private async Task<List<Guid>> LoadOrganizationAOrderAsync(OrganizationId organizationA)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.Invoices
            .AsNoTracking()
            .Where(invoice => invoice.OrganizationId == organizationA)
            .OrderBy(invoice => invoice.Id)
            .Select(invoice => invoice.Id.Value)
            .ToListAsync();
    }

    private static JsonElement AssertInvoice(JsonElement items, Guid id, string invoiceNumber)
    {
        JsonElement item = Assert.Single(
            items.EnumerateArray(),
            candidate => candidate.GetProperty("id").GetGuid() == id);
        Assert.Equal(invoiceNumber, item.GetProperty("invoiceNumber").GetString());
        Assert.False(item.TryGetProperty("organizationId", out _));
        Assert.False(item.TryGetProperty("rowVersion", out _));
        return item;
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

    private sealed record SeededInvoices(
        OrganizationId OrganizationA,
        OrganizationId OrganizationB,
        Invoice PartialInvoice,
        Invoice OtherInvoice);
}