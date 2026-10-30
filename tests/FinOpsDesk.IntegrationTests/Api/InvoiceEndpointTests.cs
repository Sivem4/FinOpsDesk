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

    [Fact]
    public async Task Exact_invoice_number_returns_only_matching_rows()
    {
        SeededInvoices seeded = await SeedInvoicesAsync();
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(seeded.OrganizationA.Value));

        using JsonDocument document = await ReadOkAsync(
            await client.GetAsync(InvoiceNumberQuery("  INV-A-1  ")));

        JsonElement root = document.RootElement;
        Assert.Equal(1, root.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, root.GetProperty("page").GetInt32());
        Assert.Equal(50, root.GetProperty("pageSize").GetInt32());
        JsonElement item = Assert.Single(root.GetProperty("items").EnumerateArray());
        Assert.Equal(seeded.PartialInvoice.Id.Value, item.GetProperty("id").GetGuid());
        Assert.Equal("INV-A-1", item.GetProperty("invoiceNumber").GetString());
    }

    [Fact]
    public async Task Unknown_invoice_number_returns_an_empty_page()
    {
        SeededInvoices seeded = await SeedInvoicesAsync();
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(seeded.OrganizationA.Value));

        using JsonDocument document = await ReadOkAsync(await client.GetAsync(InvoiceNumberQuery("MISSING")));

        Assert.Equal(0, document.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Empty(document.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Duplicate_invoice_numbers_are_paged_by_id()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice first = new(InvoiceId.New(), organizationId, "DUP", new Money(10m, PersistenceTestData.Pln));
        Invoice second = new(InvoiceId.New(), organizationId, "DUP", new Money(20m, PersistenceTestData.Pln));
        Invoice third = new(InvoiceId.New(), organizationId, "DUP", new Money(30m, PersistenceTestData.Pln));
        Invoice other = new(InvoiceId.New(), organizationId, "OTHER", new Money(40m, PersistenceTestData.Pln));
        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.Invoices.AddRange(first, second, third, other);
            await context.SaveChangesAsync();
        }

        List<Guid> expected = await LoadMatchingOrderAsync(organizationId, "DUP");
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));

        using JsonDocument firstPage = await ReadOkAsync(
            await client.GetAsync(InvoiceNumberQuery("DUP") + "&page=1&pageSize=1"));
        using JsonDocument secondPage = await ReadOkAsync(
            await client.GetAsync(InvoiceNumberQuery("DUP") + "&page=2&pageSize=1"));

        Assert.Equal(3, firstPage.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(expected[0], Assert.Single(firstPage.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(3, secondPage.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(expected[1], Assert.Single(secondPage.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task Empty_invoice_number_returns_400_without_the_term(string term)
    {
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(Guid.NewGuid()));

        HttpResponseMessage response = await client.GetAsync(InvoiceNumberQuery(term));
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.Equal(
            ReconciliationApplicationErrors.InvalidInvoiceNumberLookup.Code,
            document.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            ReconciliationApplicationErrors.InvalidInvoiceNumberLookup.Message,
            document.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Invoice_number_longer_than_256_characters_returns_400_without_the_term()
    {
        string term = "REJECTED-LOOKUP-" + new string('Z', 241);
        Assert.Equal(257, term.Length);
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(Guid.NewGuid()));

        HttpResponseMessage response = await client.GetAsync(InvoiceNumberQuery(term));
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.Equal(
            ReconciliationApplicationErrors.InvalidInvoiceNumberLookup.Code,
            document.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("REJECTED-LOOKUP", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Same_invoice_number_in_another_organization_stays_hidden()
    {
        OrganizationId organizationA = OrganizationId.New();
        OrganizationId organizationB = OrganizationId.New();
        Invoice invoiceA = new(InvoiceId.New(), organizationA, "SAME", new Money(10m, PersistenceTestData.Pln));
        Invoice invoiceB = new(InvoiceId.New(), organizationB, "SAME", new Money(20m, PersistenceTestData.Pln));
        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.Invoices.AddRange(invoiceA, invoiceB);
            await context.SaveChangesAsync();
        }

        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(organizationA.Value));
        using JsonDocument document = await ReadOkAsync(
            await client.GetAsync(
                InvoiceNumberQuery("SAME") + "&organizationId=" + organizationB.Value));

        Assert.Equal(1, document.RootElement.GetProperty("totalCount").GetInt32());
        JsonElement item = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(invoiceA.Id.Value, item.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Invoice_number_equality_follows_sql_server_comparison()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice cased = new(InvoiceId.New(), organizationId, "Inv-Case", new Money(10m, PersistenceTestData.Pln));
        Invoice padded = new(InvoiceId.New(), organizationId, "INV-PAD", new Money(11m, PersistenceTestData.Pln));
        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.Invoices.AddRange(cased, padded);
            await context.SaveChangesAsync();
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [reconciliation].[Invoices] SET [InvoiceNumber] = {"INV-PAD "} WHERE [Id] = {padded.Id.Value}");
        }

        bool caseEquals = await SqlEqualsAsync("Inv-Case", "inv-case");
        bool trailingSpaceEquals = await SqlEqualsAsync("INV-PAD", "INV-PAD ");
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));
        using JsonDocument caseDocument = await ReadOkAsync(await client.GetAsync(InvoiceNumberQuery("inv-case")));
        using JsonDocument spaceDocument = await ReadOkAsync(await client.GetAsync(InvoiceNumberQuery("INV-PAD")));

        Assert.Equal(caseEquals ? 1 : 0, caseDocument.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(trailingSpaceEquals ? 1 : 0, spaceDocument.RootElement.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Invalid_pagination_still_wins_when_an_invoice_number_is_supplied()
    {
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(Guid.NewGuid()));

        HttpResponseMessage response = await client.GetAsync("/api/invoices?page=0&invoiceNumber=INV-A-1");
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            ReconciliationApplicationErrors.InvalidInvoicePagination.Code,
            document.RootElement.GetProperty("code").GetString());
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

    private async Task<List<Guid>> LoadMatchingOrderAsync(OrganizationId organizationId, string invoiceNumber)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.Invoices
            .AsNoTracking()
            .Where(invoice => invoice.OrganizationId == organizationId && invoice.InvoiceNumber == invoiceNumber)
            .OrderBy(invoice => invoice.Id)
            .Select(invoice => invoice.Id.Value)
            .ToListAsync();
    }

    private async Task<bool> SqlEqualsAsync(string left, string right)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        List<FlagRow> rows = await context.Database.SqlQuery<FlagRow>(
            $"""
            SELECT CAST(CASE WHEN {left} = {right} THEN 1 ELSE 0 END AS int) AS [Flag]
            """)
            .ToListAsync();
        return Assert.Single(rows).Flag == 1;
    }

    private static string InvoiceNumberQuery(string term)
    {
        return "/api/invoices?invoiceNumber=" + Uri.EscapeDataString(term);
    }

    private static async Task<JsonDocument> ReadOkAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private sealed record FlagRow(int Flag);

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