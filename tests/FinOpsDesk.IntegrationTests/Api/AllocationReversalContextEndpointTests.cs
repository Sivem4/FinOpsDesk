using System.Net;
using System.Text.Json;

using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class AllocationReversalContextEndpointTests : IDisposable
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly SqlServerFixture _fixture;
    private readonly FinOpsDeskApiFactory _factory;

    public AllocationReversalContextEndpointTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _factory = new FinOpsDeskApiFactory(fixture.ConnectionString);
    }

    [Fact]
    public async Task Owned_allocations_return_current_labels_original_amounts_and_no_write()
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

        DateTimeOffset confirmedAt = new(2026, 10, 31, 12, 0, 0, 123, TimeSpan.Zero);
        DateTimeOffset reversedConfirmedAt = new(2026, 10, 31, 8, 0, 0, TimeSpan.FromHours(2));
        DateTimeOffset reversedAt = new(2026, 10, 31, 9, 30, 0, TimeSpan.FromHours(2));
        AllocationId confirmed = await SaveAllocationAsync(
            organizationId,
            sameNumberA,
            sameReferenceA,
            10m,
            confirmedAt,
            AllocationIdempotencyOperation.ExactMatch);
        AllocationId duplicate = await SaveAllocationAsync(
            organizationId,
            sameNumberB,
            sameReferenceB,
            20m,
            confirmedAt,
            operation: null);
        AllocationId fraction = await SaveAllocationAsync(
            organizationId,
            fractionInvoice,
            nullReference,
            0.0001m,
            confirmedAt,
            operation: null);
        AllocationId reversed = await SaveAllocationAsync(
            organizationId,
            reversedInvoice,
            reversedBank,
            5m,
            reversedConfirmedAt,
            operation: null,
            reversalReason: "  Duplicate payment  ",
            reversedAt: reversedAt);

        RowVersions before = await ReadVersionsAsync(confirmed, fractionInvoice.Id, nullReference.Id);
        int auditsBefore = await AuditCountAsync(organizationId);
        decimal allocatedBefore = await AllocatedAmountAsync(fractionInvoice.Id);
        using HttpClient client = Client(organizationId, subject: null);
        using JsonDocument confirmedDocument = await ReadOkAsync(client, Path(confirmed));
        using JsonDocument duplicateDocument = await ReadOkAsync(client, Path(duplicate));
        using JsonDocument fractionDocument = await ReadOkAsync(client, Path(fraction));
        using JsonDocument reversedDocument = await ReadOkAsync(client, Path(reversed));
        RowVersions after = await ReadVersionsAsync(confirmed, fractionInvoice.Id, nullReference.Id);

        Assert.Equal(0, auditsBefore);
        Assert.Equal(0, await AuditCountAsync(organizationId));
        Assert.Equal(allocatedBefore, await AllocatedAmountAsync(fractionInvoice.Id));
        Assert.Equal(before.Allocation, after.Allocation);
        Assert.Equal(before.Invoice, after.Invoice);
        Assert.Equal(before.BankTransaction, after.BankTransaction);
        Assert.Equal(AllocationStatus.Confirmed, await StatusAsync(confirmed));
        Assert.Equal(reversedConfirmedAt, await ConfirmedAtAsync(reversed));

        JsonElement confirmedBody = confirmedDocument.RootElement;
        AssertShape(confirmedBody);
        Assert.Equal(confirmed.Value, confirmedBody.GetProperty("allocationId").GetGuid());
        Assert.Equal(sameNumberA.Id.Value, confirmedBody.GetProperty("invoiceId").GetGuid());
        Assert.Equal(sameReferenceA.Id.Value, confirmedBody.GetProperty("bankTransactionId").GetGuid());
        Assert.Equal("SAME-NO", confirmedBody.GetProperty("invoiceNumber").GetString());
        Assert.Equal("SAME-REF", confirmedBody.GetProperty("reference").GetString());
        Assert.Equal(10m, confirmedBody.GetProperty("amount").GetProperty("amount").GetDecimal());
        Assert.Equal("PLN", confirmedBody.GetProperty("amount").GetProperty("currency").GetString());
        Assert.Equal("confirmed", confirmedBody.GetProperty("status").GetString());
        Assert.Equal(SerializeTimestamp(confirmedAt), confirmedBody.GetProperty("confirmedAt").GetString());
        Assert.Equal(JsonValueKind.Null, confirmedBody.GetProperty("reversedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, confirmedBody.GetProperty("reversalReason").ValueKind);

        JsonElement duplicateBody = duplicateDocument.RootElement;
        Assert.Equal("SAME-NO", duplicateBody.GetProperty("invoiceNumber").GetString());
        Assert.Equal("SAME-REF", duplicateBody.GetProperty("reference").GetString());
        Assert.NotEqual(confirmed.Value, duplicateBody.GetProperty("allocationId").GetGuid());
        Assert.NotEqual(
            confirmedBody.GetProperty("invoiceId").GetGuid(),
            duplicateBody.GetProperty("invoiceId").GetGuid());
        Assert.NotEqual(
            confirmedBody.GetProperty("bankTransactionId").GetGuid(),
            duplicateBody.GetProperty("bankTransactionId").GetGuid());

        JsonElement fractionBody = fractionDocument.RootElement;
        Assert.Equal(JsonValueKind.Null, fractionBody.GetProperty("reference").ValueKind);
        Assert.Equal(0.0001m, fractionBody.GetProperty("amount").GetProperty("amount").GetDecimal());
        Assert.Equal("0.0001", fractionBody.GetProperty("amount").GetProperty("amount").GetRawText());
        Assert.Equal("confirmed", fractionBody.GetProperty("status").GetString());
        Assert.DoesNotContain("idempotency", fractionDocument.RootElement.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rowVersion", fractionDocument.RootElement.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("organizationId", fractionDocument.RootElement.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("actor", fractionDocument.RootElement.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("batch", fractionDocument.RootElement.ToString(), StringComparison.OrdinalIgnoreCase);

        JsonElement reversedBody = reversedDocument.RootElement;
        Assert.Equal("reversed", reversedBody.GetProperty("status").GetString());
        Assert.Equal(5m, reversedBody.GetProperty("amount").GetProperty("amount").GetDecimal());
        Assert.False(reversedBody.GetProperty("amount").GetProperty("amount").GetRawText().Contains('-'));
        Assert.Equal("Duplicate payment", reversedBody.GetProperty("reversalReason").GetString());
        Assert.Equal(SerializeTimestamp(reversedConfirmedAt), reversedBody.GetProperty("confirmedAt").GetString());
        Assert.Equal(SerializeTimestamp(reversedAt), reversedBody.GetProperty("reversedAt").GetString());
    }

    [Fact]
    public async Task Missing_and_foreign_allocations_return_the_same_not_found_problem()
    {
        OrganizationId owner = OrganizationId.New();
        OrganizationId other = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(owner, "FOREIGN-ONLY-991", 77m);
        AllocationId allocationId = await SaveAllocationAsync(
            owner,
            invoice,
            bank,
            77m,
            DateTimeOffset.UnixEpoch,
            AllocationIdempotencyOperation.ExplicitAmount);
        using HttpClient ownerClient = Client(owner, subject: null);
        using HttpClient otherClient = Client(other, subject: null);

        string missing = await ReadProblemAsync(ownerClient, Path(AllocationId.New()), HttpStatusCode.NotFound);
        string foreign = await ReadProblemAsync(otherClient, Path(allocationId), HttpStatusCode.NotFound);

        using JsonDocument missingDocument = JsonDocument.Parse(missing);
        using JsonDocument foreignDocument = JsonDocument.Parse(foreign);
        Assert.Equal(
            missingDocument.RootElement.GetProperty("title").GetString(),
            foreignDocument.RootElement.GetProperty("title").GetString());
        Assert.Equal(
            missingDocument.RootElement.GetProperty("detail").GetString(),
            foreignDocument.RootElement.GetProperty("detail").GetString());
        Assert.Equal(
            ReconciliationApplicationErrors.AllocationNotFound.Code,
            missingDocument.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            missingDocument.RootElement.GetProperty("code").GetString(),
            foreignDocument.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("FOREIGN-ONLY-991", foreign, StringComparison.Ordinal);
        Assert.DoesNotContain("77", foreign, StringComparison.Ordinal);
        Assert.Equal(77m, await AllocatedAmountAsync(invoice.Id));
    }

    [Fact]
    public async Task Missing_bearer_token_returns_401_and_missing_organization_returns_403()
    {
        using HttpClient anonymous = _factory.CreateApiClient();
        using HttpClient withoutOrganization = _factory.CreateApiClient(
            TestAccessTokens.CreateWithoutOrganizationClaim());
        string path = Path(AllocationId.New());

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await withoutOrganization.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Forged_organization_header_does_not_change_the_tenant_scope()
    {
        OrganizationId owner = OrganizationId.New();
        OrganizationId other = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(owner, "OWNER-ONLY", 12m);
        AllocationId allocationId = await SaveAllocationAsync(
            owner,
            invoice,
            bank,
            12m,
            DateTimeOffset.UnixEpoch,
            operation: null);
        using HttpClient client = Client(owner, subject: null);
        client.DefaultRequestHeaders.Add("X-Organization-Id", other.Value.ToString());

        using JsonDocument document = await ReadOkAsync(client, Path(allocationId));
        string rejected = await ReadProblemAsync(
            client,
            Path(allocationId) + "?organizationId=" + other.Value.ToString("D"),
            HttpStatusCode.BadRequest);

        Assert.Equal(allocationId.Value, document.RootElement.GetProperty("allocationId").GetGuid());
        Assert.Equal("OWNER-ONLY", document.RootElement.GetProperty("invoiceNumber").GetString());
        using JsonDocument rejectedDocument = JsonDocument.Parse(rejected);
        Assert.Equal(
            ReconciliationApplicationErrors.AllocationReversalContextInvalidRequest.Code,
            rejectedDocument.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("OWNER-ONLY", rejected, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Malformed_allocation_id_returns_400_without_echoing_the_input(string allocationId)
    {
        using HttpClient client = Client(OrganizationId.New(), subject: null);

        string body = await ReadProblemAsync(
            client,
            "/api/allocations/" + allocationId + "/reversal-context",
            HttpStatusCode.BadRequest);

        using JsonDocument document = JsonDocument.Parse(body);
        Assert.Equal(
            ReconciliationApplicationErrors.AllocationReversalContextInvalidRequest.Code,
            document.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain(allocationId, body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("?organizationId=11111111-1111-1111-1111-111111111111")]
    [InlineData("?page=1")]
    [InlineData("?invoiceId=11111111-1111-1111-1111-111111111111")]
    public async Task Unsupported_query_returns_400_without_echoing_the_parameter(string query)
    {
        using HttpClient client = Client(OrganizationId.New(), subject: null);

        string body = await ReadProblemAsync(
            client,
            Path(AllocationId.New()) + query,
            HttpStatusCode.BadRequest);

        using JsonDocument document = JsonDocument.Parse(body);
        Assert.Equal(
            ReconciliationApplicationErrors.AllocationReversalContextInvalidRequest.Code,
            document.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("organizationId", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("11111111-1111-1111-1111-111111111111", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancelled_token_is_observed_by_the_query()
    {
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IAllocationReversalContextQueries queries =
            scope.ServiceProvider.GetRequiredService<IAllocationReversalContextQueries>();
        using CancellationTokenSource cancellationTokenSource = new();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            queries.FindAsync(OrganizationId.New(), AllocationId.New(), cancellationTokenSource.Token));
    }

    [Fact]
    public async Task OpenApi_document_describes_reversal_context()
    {
        using HttpClient client = Client(OrganizationId.New(), subject: null);

        HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement operation = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/allocations/{allocationId}/reversal-context")
            .GetProperty("get");
        Assert.Equal("GetAllocationReversalContext", operation.GetProperty("operationId").GetString());
        string description = operation.GetProperty("description").GetString() ?? string.Empty;
        Assert.Contains("TenantMember", description, StringComparison.Ordinal);
        Assert.Contains("rowversion", description, StringComparison.OrdinalIgnoreCase);
        foreach (JsonElement parameter in operation.GetProperty("parameters").EnumerateArray())
        {
            Assert.Equal("allocationId", parameter.GetProperty("name").GetString());
            Assert.Equal("path", parameter.GetProperty("in").GetString());
        }
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
        string invoiceNumber,
        decimal amount)
    {
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, amount, invoiceNumber);
        BankTransaction bank = PersistenceTestData.CreateBankTransaction(
            organizationId,
            amount,
            reference: invoiceNumber);
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

    private async Task<RowVersions> ReadVersionsAsync(
        AllocationId allocationId,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        byte[] allocation = await context.Allocations
            .Where(candidate => candidate.Id == allocationId)
            .Select(candidate => EF.Property<byte[]>(candidate, "RowVersion"))
            .SingleAsync();
        byte[] invoice = await context.Invoices
            .Where(candidate => candidate.Id == invoiceId)
            .Select(candidate => EF.Property<byte[]>(candidate, "RowVersion"))
            .SingleAsync();
        byte[] bankTransaction = await context.BankTransactions
            .Where(candidate => candidate.Id == bankTransactionId)
            .Select(candidate => EF.Property<byte[]>(candidate, "RowVersion"))
            .SingleAsync();
        return new RowVersions(allocation, invoice, bankTransaction);
    }

    private async Task<int> AuditCountAsync(OrganizationId organizationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.FinancialAuditEntries.CountAsync(
            entry => entry.OrganizationId == organizationId);
    }

    private async Task<decimal> AllocatedAmountAsync(InvoiceId invoiceId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.Invoices
            .Where(invoice => invoice.Id == invoiceId)
            .Select(invoice => invoice.AllocatedAmount.Amount)
            .SingleAsync();
    }

    private async Task<AllocationStatus> StatusAsync(AllocationId allocationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.Allocations
            .Where(allocation => allocation.Id == allocationId)
            .Select(allocation => allocation.Status)
            .SingleAsync();
    }

    private async Task<DateTimeOffset> ConfirmedAtAsync(AllocationId allocationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.Allocations
            .Where(allocation => allocation.Id == allocationId)
            .Select(allocation => allocation.ConfirmedAt)
            .SingleAsync();
    }

    private static string Path(AllocationId allocationId)
    {
        return "/api/allocations/" + allocationId.Value.ToString("D") + "/reversal-context";
    }

    private static async Task<JsonDocument> ReadOkAsync(HttpClient client, string path)
    {
        HttpResponseMessage response = await client.GetAsync(path);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body);
    }

    private static async Task<string> ReadProblemAsync(HttpClient client, string path, HttpStatusCode statusCode)
    {
        HttpResponseMessage response = await client.GetAsync(path);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(statusCode, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return body;
    }

    private static void AssertShape(JsonElement body)
    {
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
            ],
            body.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(
            ["amount", "currency"],
            body.GetProperty("amount").EnumerateObject().Select(property => property.Name).ToArray());
    }

    private static string SerializeTimestamp(DateTimeOffset timestamp)
    {
        return JsonSerializer.Serialize(timestamp, WebJson).Trim('"');
    }

    private sealed record RowVersions(byte[] Allocation, byte[] Invoice, byte[] BankTransaction);
}