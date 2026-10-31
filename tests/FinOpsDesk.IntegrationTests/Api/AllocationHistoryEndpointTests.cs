using System.Net;
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
public sealed class AllocationHistoryEndpointTests : IDisposable
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly SqlServerFixture _fixture;
    private readonly FinOpsDeskApiFactory _factory;

    public AllocationHistoryEndpointTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _factory = new FinOpsDeskApiFactory(fixture.ConnectionString);
    }

    [Fact]
    public async Task Empty_tenant_returns_an_empty_page()
    {
        using HttpClient client = Client(OrganizationId.New(), subject: null);

        using JsonDocument document = await ReadOkAsync(client, "/api/allocations");

        Assert.Equal(0, document.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(50, document.RootElement.GetProperty("pageSize").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task History_returns_current_labels_original_amounts_and_stable_pages()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice sameNumberA = PersistenceTestData.CreateInvoice(organizationId, 100m, "SAME-NO");
        Invoice sameNumberB = PersistenceTestData.CreateInvoice(organizationId, 100m, "SAME-NO");
        Invoice fractionInvoice = PersistenceTestData.CreateInvoice(organizationId, 1m, "FRAC-1");
        Invoice reversedInvoice = PersistenceTestData.CreateInvoice(organizationId, 5m, "REV-1");
        BankTransaction sameReferenceA = PersistenceTestData.CreateBankTransaction(
            organizationId,
            100m,
            reference: "SAME-REF");
        BankTransaction sameReferenceB = PersistenceTestData.CreateBankTransaction(
            organizationId,
            100m,
            reference: "SAME-REF");
        BankTransaction nullReference = PersistenceTestData.CreateBankTransaction(
            organizationId,
            1m,
            reference: null);
        BankTransaction reversedBank = PersistenceTestData.CreateBankTransaction(
            organizationId,
            5m,
            reference: "REV-REF");
        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.AddRange(
                sameNumberA,
                sameNumberB,
                fractionInvoice,
                reversedInvoice,
                sameReferenceA,
                sameReferenceB,
                nullReference,
                reversedBank);
            await context.SaveChangesAsync();
        }

        DateTimeOffset sharedTime = new(2026, 10, 31, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset laterTime = new(2026, 10, 31, 12, 0, 0, 123, TimeSpan.Zero);
        DateTimeOffset earlierTime = new(2026, 10, 31, 8, 0, 0, TimeSpan.FromHours(2));
        DateTimeOffset reversedAt = new(2026, 10, 31, 9, 30, 0, TimeSpan.FromHours(2));
        AllocationId firstShared = await SaveAllocationAsync(
            organizationId,
            sameNumberA,
            sameReferenceA,
            10m,
            sharedTime,
            AllocationIdempotencyOperation.ExactMatch);
        AllocationId secondShared = await SaveAllocationAsync(
            organizationId,
            sameNumberB,
            sameReferenceB,
            20m,
            sharedTime,
            AllocationIdempotencyOperation.ExplicitAmount);
        AllocationId fraction = await SaveAllocationAsync(
            organizationId,
            fractionInvoice,
            nullReference,
            0.0001m,
            laterTime,
            AllocationIdempotencyOperation.BatchExactMatch);
        AllocationId reversed = await SaveAllocationAsync(
            organizationId,
            reversedInvoice,
            reversedBank,
            5m,
            earlierTime,
            operation: null,
            reversalReason: "  Duplicate payment  ",
            reversedAt: reversedAt);

        List<Guid> expectedOrder = await ExpectedOrderAsync(organizationId);
        Assert.Equal(4, expectedOrder.Count);
        Assert.Equal(fraction.Value, expectedOrder[0]);
        Assert.Contains(firstShared.Value, expectedOrder);
        Assert.Contains(secondShared.Value, expectedOrder);
        Assert.Equal(reversed.Value, expectedOrder[3]);

        (int allocations, int audits, decimal outstanding) =
            await SnapshotAsync(organizationId, reversedInvoice.Id);
        using HttpClient client = Client(organizationId, subject: null);
        using JsonDocument firstPage = await ReadOkAsync(client, "/api/allocations?page=1&pageSize=2");
        using JsonDocument secondPage = await ReadOkAsync(client, "/api/allocations?page=2&pageSize=2");
        using JsonDocument beyond = await ReadOkAsync(client, "/api/allocations?page=3&pageSize=2");
        (int allocationsAfter, int auditsAfter, decimal outstandingAfter) =
            await SnapshotAsync(organizationId, reversedInvoice.Id);

        Assert.Equal(allocations, allocationsAfter);
        Assert.Equal(0, audits);
        Assert.Equal(audits, auditsAfter);
        Assert.Equal(outstanding, outstandingAfter);
        Assert.Equal(4, firstPage.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(4, secondPage.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(4, beyond.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, beyond.RootElement.GetProperty("items").GetArrayLength());
        Guid[] pageIds = ReadIds(firstPage).Concat(ReadIds(secondPage)).ToArray();
        Assert.Equal(expectedOrder, pageIds);

        JsonElement fractionItem = FindItem(firstPage, fraction.Value);
        Assert.Equal("FRAC-1", fractionItem.GetProperty("invoiceNumber").GetString());
        Assert.Equal(JsonValueKind.Null, fractionItem.GetProperty("reference").ValueKind);
        Assert.Equal(0.0001m, fractionItem.GetProperty("amount").GetProperty("amount").GetDecimal());
        Assert.Equal("0.0001", fractionItem.GetProperty("amount").GetProperty("amount").GetRawText());
        Assert.Equal("PLN", fractionItem.GetProperty("amount").GetProperty("currency").GetString());
        Assert.Equal("confirmed", fractionItem.GetProperty("status").GetString());
        Assert.Equal("batchExactMatch", fractionItem.GetProperty("idempotencyOperation").GetString());
        Assert.Equal(SerializeTimestamp(laterTime), fractionItem.GetProperty("confirmedAt").GetString());
        Assert.Equal(JsonValueKind.Null, fractionItem.GetProperty("reversedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, fractionItem.GetProperty("reversalReason").ValueKind);
        AssertItemShape(fractionItem);

        JsonElement sharedA = RequireItem(firstPage, secondPage, firstShared.Value);
        JsonElement sharedB = RequireItem(firstPage, secondPage, secondShared.Value);

        Assert.Equal("SAME-NO", sharedA.GetProperty("invoiceNumber").GetString());
        Assert.Equal("SAME-NO", sharedB.GetProperty("invoiceNumber").GetString());
        Assert.NotEqual(
            sharedA.GetProperty("invoiceId").GetGuid(),
            sharedB.GetProperty("invoiceId").GetGuid());
        Assert.Equal("SAME-REF", sharedA.GetProperty("reference").GetString());
        Assert.Equal("SAME-REF", sharedB.GetProperty("reference").GetString());
        Assert.NotEqual(
            sharedA.GetProperty("bankTransactionId").GetGuid(),
            sharedB.GetProperty("bankTransactionId").GetGuid());
        Assert.Equal("exactMatch", sharedA.GetProperty("idempotencyOperation").GetString());
        Assert.Equal("explicitAmount", sharedB.GetProperty("idempotencyOperation").GetString());
        Assert.Equal(10m, sharedA.GetProperty("amount").GetProperty("amount").GetDecimal());
        Assert.Equal(20m, sharedB.GetProperty("amount").GetProperty("amount").GetDecimal());

        JsonElement reversedItem = RequireItem(firstPage, secondPage, reversed.Value);
        Assert.Equal("reversed", reversedItem.GetProperty("status").GetString());
        Assert.Equal(5m, reversedItem.GetProperty("amount").GetProperty("amount").GetDecimal());
        Assert.Equal("Duplicate payment", reversedItem.GetProperty("reversalReason").GetString());
        Assert.Equal(SerializeTimestamp(earlierTime), reversedItem.GetProperty("confirmedAt").GetString());
        Assert.Equal(SerializeTimestamp(reversedAt), reversedItem.GetProperty("reversedAt").GetString());
        Assert.Equal(JsonValueKind.Null, reversedItem.GetProperty("idempotencyOperation").ValueKind);
        AssertItemShape(reversedItem);

        using JsonDocument invoiceFilter = await ReadOkAsync(
            client,
            "/api/allocations?invoiceId=" + sameNumberA.Id.Value.ToString("D"));
        using JsonDocument pairFilter = await ReadOkAsync(
            client,
            "/api/allocations?invoiceId=" + sameNumberA.Id.Value.ToString("D")
                + "&bankTransactionId=" + sameReferenceA.Id.Value.ToString("D"));
        using JsonDocument otherPair = await ReadOkAsync(
            client,
            "/api/allocations?invoiceId=" + sameNumberA.Id.Value.ToString("D")
                + "&bankTransactionId=" + sameReferenceB.Id.Value.ToString("D"));
        using JsonDocument missing = await ReadOkAsync(
            client,
            "/api/allocations?invoiceId=" + Guid.NewGuid().ToString("D"));

        Assert.Equal(1, invoiceFilter.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(firstShared.Value, invoiceFilter.RootElement.GetProperty("items")[0].GetProperty("allocationId").GetGuid());
        Assert.Equal(1, pairFilter.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, otherPair.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, otherPair.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(0, missing.RootElement.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Multiple_partial_allocations_for_one_invoice_remain_separate_rows()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 100m, "PARTIAL");
        BankTransaction firstBank = PersistenceTestData.CreateBankTransaction(
            organizationId,
            40m,
            reference: "PART-1");
        BankTransaction secondBank = PersistenceTestData.CreateBankTransaction(
            organizationId,
            60m,
            reference: "PART-2");
        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.AddRange(invoice, firstBank, secondBank);
            await context.SaveChangesAsync();
        }

        AllocationId older = await SaveAllocationAsync(
            organizationId,
            invoice,
            firstBank,
            30m,
            new DateTimeOffset(2026, 10, 31, 8, 0, 0, TimeSpan.Zero),
            AllocationIdempotencyOperation.ExplicitAmount);
        AllocationId newer = await SaveAllocationAsync(
            organizationId,
            invoice,
            secondBank,
            40m,
            new DateTimeOffset(2026, 10, 31, 9, 0, 0, TimeSpan.Zero),
            AllocationIdempotencyOperation.ExplicitAmount);
        using HttpClient client = Client(organizationId, subject: null);

        using JsonDocument document = await ReadOkAsync(
            client,
            "/api/allocations?invoiceId=" + invoice.Id.Value.ToString("D"));

        Assert.Equal(2, document.RootElement.GetProperty("totalCount").GetInt32());
        JsonElement first = document.RootElement.GetProperty("items")[0];
        JsonElement second = document.RootElement.GetProperty("items")[1];
        Assert.Equal(newer.Value, first.GetProperty("allocationId").GetGuid());
        Assert.Equal(40m, first.GetProperty("amount").GetProperty("amount").GetDecimal());
        Assert.Equal(older.Value, second.GetProperty("allocationId").GetGuid());
        Assert.Equal(30m, second.GetProperty("amount").GetProperty("amount").GetDecimal());
        Assert.Equal(invoice.Id.Value, first.GetProperty("invoiceId").GetGuid());
        Assert.Equal(invoice.Id.Value, second.GetProperty("invoiceId").GetGuid());
        Assert.NotEqual(
            first.GetProperty("bankTransactionId").GetGuid(),
            second.GetProperty("bankTransactionId").GetGuid());
    }

    [Fact]
    public async Task Foreign_tenant_filters_return_an_empty_page()
    {
        OrganizationId owner = OrganizationId.New();
        OrganizationId reader = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(owner, "FOREIGN-1", 8m);
        AllocationId allocationId = await SaveAllocationAsync(
            owner,
            invoice,
            bank,
            8m,
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            AllocationIdempotencyOperation.ExactMatch);
        (Invoice ownInvoice, BankTransaction ownBank) = await SeedPairAsync(reader, "OWN-1", 3m);
        AllocationId ownAllocationId = await SaveAllocationAsync(
            reader,
            ownInvoice,
            ownBank,
            3m,
            new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero),
            AllocationIdempotencyOperation.ExplicitAmount);
        using HttpClient client = Client(reader, "reader");

        using JsonDocument ownPage = await ReadOkAsync(client, "/api/allocations");
        using JsonDocument foreignInvoice = await ReadOkAsync(
            client,
            "/api/allocations?invoiceId=" + invoice.Id.Value.ToString("D"));
        using JsonDocument foreignBank = await ReadOkAsync(
            client,
            "/api/allocations?bankTransactionId=" + bank.Id.Value.ToString("D"));
        string foreignInvoiceBody = foreignInvoice.RootElement.GetRawText();
        string foreignBankBody = foreignBank.RootElement.GetRawText();

        Assert.Equal(1, ownPage.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(ownAllocationId.Value, ownPage.RootElement.GetProperty("items")[0].GetProperty("allocationId").GetGuid());
        Assert.Equal(0, foreignInvoice.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, foreignBank.RootElement.GetProperty("totalCount").GetInt32());
        Assert.DoesNotContain(allocationId.Value.ToString(), foreignInvoiceBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FOREIGN-1", foreignInvoiceBody, StringComparison.Ordinal);
        Assert.DoesNotContain(allocationId.Value.ToString(), foreignBankBody, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("?page=0", "reconciliation.allocation_history.invalid_pagination")]
    [InlineData("?page=-1", "reconciliation.allocation_history.invalid_pagination")]
    [InlineData("?page=", "reconciliation.allocation_history.invalid_pagination")]
    [InlineData("?page=1.5", "reconciliation.allocation_history.invalid_pagination")]
    [InlineData("?pageSize=0", "reconciliation.allocation_history.invalid_pagination")]
    [InlineData("?pageSize=101", "reconciliation.allocation_history.invalid_pagination")]
    [InlineData("?pageSize=", "reconciliation.allocation_history.invalid_pagination")]
    [InlineData("?page=1&page=2", "reconciliation.allocation_history.invalid_pagination")]
    [InlineData("?pageSize=1&pageSize=2", "reconciliation.allocation_history.invalid_pagination")]
    [InlineData("?page=21474838&pageSize=100", "reconciliation.allocation_history.invalid_pagination")]
    [InlineData("?invoiceId=", "reconciliation.allocation_history.invalid_request")]
    [InlineData("?invoiceId=%20", "reconciliation.allocation_history.invalid_request")]
    [InlineData("?invoiceId=00000000-0000-0000-0000-000000000000", "reconciliation.allocation_history.invalid_request")]
    [InlineData("?invoiceId=not-a-guid", "reconciliation.allocation_history.invalid_request")]
    [InlineData("?bankTransactionId=", "reconciliation.allocation_history.invalid_request")]
    [InlineData("?bankTransactionId=not-a-guid", "reconciliation.allocation_history.invalid_request")]
    [InlineData("?search=INV", "reconciliation.allocation_history.invalid_request")]
    [InlineData(
        "?invoiceId=11111111-1111-1111-1111-111111111111&invoiceId=22222222-2222-2222-2222-222222222222",
        "reconciliation.allocation_history.invalid_request")]
    [InlineData(
        "?organizationId=11111111-1111-1111-1111-111111111111",
        "reconciliation.allocation_history.invalid_request")]
    public async Task Invalid_history_query_returns_400(string query, string code)
    {
        using HttpClient client = Client(OrganizationId.New(), subject: null);

        string body = await AssertProblemAsync(client, "/api/allocations" + query, code);

        Assert.DoesNotContain("not-a-guid", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("11111111-1111-1111-1111-111111111111", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("organizationId", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_bearer_token_returns_401_and_missing_organization_returns_403()
    {
        using HttpClient anonymous = _factory.CreateApiClient();
        using HttpClient withoutOrganization = _factory.CreateApiClient(
            TestAccessTokens.CreateWithoutOrganizationClaim());

        HttpResponseMessage unauthenticated = await anonymous.GetAsync("/api/allocations");
        HttpResponseMessage forbidden = await withoutOrganization.GetAsync("/api/allocations");

        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task OpenApi_document_describes_allocation_history()
    {
        using HttpClient client = Client(OrganizationId.New(), "reader");

        HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement path = document.RootElement.GetProperty("paths").GetProperty("/api/allocations");
        Assert.True(path.TryGetProperty("post", out _));
        JsonElement operation = path.GetProperty("get");
        Assert.Equal("ListAllocationHistory", operation.GetProperty("operationId").GetString());
        Assert.Contains("TenantMember", operation.GetProperty("description").GetString(), StringComparison.Ordinal);
        bool page = false;
        bool pageSize = false;
        bool invoiceId = false;
        bool bankTransactionId = false;
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

            if (name == "invoiceId")
            {
                invoiceId = true;
                Assert.False(IsRequired(parameter));
            }

            if (name == "bankTransactionId")
            {
                bankTransactionId = true;
                Assert.False(IsRequired(parameter));
            }
        }

        Assert.True(page);
        Assert.True(pageSize);
        Assert.True(invoiceId);
        Assert.True(bankTransactionId);
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
        decimal amount)
    {
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, amount, reference);
        BankTransaction bank = PersistenceTestData.CreateBankTransaction(
            organizationId,
            amount,
            reference: reference);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.AddRange(invoice, bank);
        await context.SaveChangesAsync();
        return (invoice, bank);
    }

    private async Task<AllocationId> SaveAllocationAsync(
        OrganizationId organizationId,
        Invoice invoice,
        BankTransaction bank,
        decimal amount,
        DateTimeOffset confirmedAt,
        AllocationIdempotencyOperation? operation,
        string? reversalReason = null,
        DateTimeOffset? reversedAt = null)
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
            confirmedAt,
            operation is null ? null : Guid.NewGuid(),
            operation);
        if (reversalReason is not null)
        {
            Assert.True(allocation.Reverse(reversalReason, reversedAt!.Value).IsSuccess);
        }

        context.Allocations.Add(allocation);
        await context.SaveChangesAsync();
        return allocation.Id;
    }

    private async Task<List<Guid>> ExpectedOrderAsync(OrganizationId organizationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.Allocations
            .AsNoTracking()
            .Where(allocation => allocation.OrganizationId == organizationId)
            .OrderByDescending(allocation => allocation.ConfirmedAt)
            .ThenByDescending(allocation => allocation.Id)
            .Select(allocation => allocation.Id.Value)
            .ToListAsync();
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

    private static async Task<JsonDocument> ReadOkAsync(HttpClient client, string path)
    {
        HttpResponseMessage response = await client.GetAsync(path);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body);
    }

    private static async Task<string> AssertProblemAsync(HttpClient client, string path, string code)
    {
        HttpResponseMessage response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.Equal(400, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        return body;
    }

    private static Guid[] ReadIds(JsonDocument document)
    {
        return document.RootElement.GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("allocationId").GetGuid())
            .ToArray();
    }

    private static JsonElement RequireItem(JsonDocument firstPage, JsonDocument secondPage, Guid allocationId)
    {
        JsonElement item = FindItem(firstPage, allocationId);
        if (item.ValueKind == JsonValueKind.Undefined)
        {
            item = FindItem(secondPage, allocationId);
        }

        Assert.NotEqual(JsonValueKind.Undefined, item.ValueKind);
        return item;
    }

    private static JsonElement FindItem(JsonDocument document, Guid allocationId)
    {
        foreach (JsonElement item in document.RootElement.GetProperty("items").EnumerateArray())
        {
            if (item.GetProperty("allocationId").GetGuid() == allocationId)
            {
                return item;
            }
        }

        return default;
    }

    private static void AssertItemShape(JsonElement item)
    {
        string[] names = item.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(
            [
                "allocationId",
                "invoiceId",
                "bankTransactionId",
                "invoiceNumber",
                "reference",
                "amount",
                "status",
                "confirmedAt",
                "reversedAt",
                "reversalReason",
                "idempotencyOperation",
            ],
            names);
        Assert.Equal(
            ["amount", "currency"],
            item.GetProperty("amount").EnumerateObject().Select(property => property.Name).ToArray());
    }

    private static bool IsRequired(JsonElement parameter)
    {
        return parameter.TryGetProperty("required", out JsonElement required) && required.GetBoolean();
    }

    private static string SerializeTimestamp(DateTimeOffset timestamp)
    {
        return JsonSerializer.Serialize(timestamp, WebJson).Trim('"');
    }
}