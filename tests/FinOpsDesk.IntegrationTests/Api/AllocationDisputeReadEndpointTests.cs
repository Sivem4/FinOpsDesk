using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class AllocationDisputeReadEndpointTests
{
    private static readonly DateTimeOffset ConfirmedAt = new(2026, 11, 7, 8, 0, 0, TimeSpan.Zero);

    private readonly SqlServerFixture _fixture;

    public AllocationDisputeReadEndpointTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task List_filter_and_detail_stay_inside_the_caller_organization()
    {
        OrganizationId north = OrganizationId.New();
        OrganizationId south = OrganizationId.New();
        Seeded northAllocation = await SeedAsync(north, 20m);
        Seeded southAllocation = await SeedAsync(south, 30m);
        Seeded northWithoutDisputes = await SeedAsync(north, 15m);
        using FinOpsDeskApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient northClient = Client(factory, north, "north-reader");
        using HttpClient southClient = Client(factory, south, "south-reader");
        Guid northDispute = await RegisterAsync(northClient, northAllocation.AllocationId.Value, "north challenge");
        Guid southDispute = await RegisterAsync(southClient, southAllocation.AllocationId.Value, "south challenge");

        using JsonDocument northList = await ReadOkAsync(northClient, "/api/allocation-disputes");
        Assert.Equal(1, northList.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Equal(northDispute, OnlyItem(northList).GetProperty("disputeId").GetGuid());
        Assert.DoesNotContain("south challenge", northList.RootElement.GetRawText(), StringComparison.Ordinal);

        using JsonDocument foreignFilter = await ReadOkAsync(
            northClient,
            "/api/allocation-disputes?allocationId=" + southAllocation.AllocationId.Value.ToString("D"));
        Assert.Equal(0, foreignFilter.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Empty(foreignFilter.RootElement.GetProperty("items").EnumerateArray());
        using JsonDocument unknownFilter = await ReadOkAsync(
            northClient,
            "/api/allocation-disputes?allocationId=" + Guid.NewGuid().ToString("D"));
        Assert.Equal(0, unknownFilter.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Empty(unknownFilter.RootElement.GetProperty("items").EnumerateArray());
        using JsonDocument emptyOwnFilter = await ReadOkAsync(
            northClient,
            "/api/allocation-disputes?allocationId=" + northWithoutDisputes.AllocationId.Value.ToString("D"));
        Assert.Equal(0, emptyOwnFilter.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Empty(emptyOwnFilter.RootElement.GetProperty("items").EnumerateArray());

        using HttpResponseMessage foreignDetail = await northClient.GetAsync(
            "/api/allocation-disputes/" + southDispute.ToString("D"));
        using HttpResponseMessage unknownDetail = await northClient.GetAsync(
            "/api/allocation-disputes/" + Guid.NewGuid().ToString("D"));
        string foreignBody = await foreignDetail.Content.ReadAsStringAsync();
        string unknownBody = await unknownDetail.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.NotFound, foreignDetail.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownDetail.StatusCode);
        using JsonDocument foreignDocument = JsonDocument.Parse(foreignBody);
        using JsonDocument unknownDocument = JsonDocument.Parse(unknownBody);
        Assert.Equal(
            "reconciliation.allocation_dispute.not_found",
            foreignDocument.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            foreignDocument.RootElement.GetProperty("code").GetString(),
            unknownDocument.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("south challenge", foreignBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_organization_and_repeated_statements_share_one_allocation()
    {
        OrganizationId empty = OrganizationId.New();
        OrganizationId organizationId = OrganizationId.New();
        Seeded seeded = await SeedAsync(organizationId, 18m);
        using FinOpsDeskApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient emptyClient = Client(factory, empty, "empty-reader");
        using HttpClient client = Client(factory, organizationId, "repeat-reader");

        using JsonDocument emptyList = await ReadOkAsync(emptyClient, "/api/allocation-disputes");
        Assert.Equal(1, emptyList.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(50, emptyList.RootElement.GetProperty("pageSize").GetInt32());
        Assert.Equal(0, emptyList.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Empty(emptyList.RootElement.GetProperty("items").EnumerateArray());

        Guid first = await RegisterAsync(client, seeded.AllocationId.Value, "same challenge");
        Guid second = await RegisterAsync(client, seeded.AllocationId.Value, "same challenge");
        using JsonDocument list = await ReadOkAsync(
            client,
            "/api/allocation-disputes?allocationId=" + seeded.AllocationId.Value.ToString("D"));
        Assert.Equal(2, list.RootElement.GetProperty("totalCount").GetInt64());
        JsonElement[] items = list.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Guid[] ids = items.Select(item => item.GetProperty("disputeId").GetGuid()).ToArray();
        Assert.Equal(2, ids.Distinct().Count());
        Assert.Contains(first, ids);
        Assert.Contains(second, ids);
        Assert.All(items, item => Assert.Equal("same challenge", item.GetProperty("statement").GetString()));
    }

    [Fact]
    public async Task Equal_recorded_times_follow_sql_server_identifier_order()
    {
        OrganizationId organizationId = OrganizationId.New();
        Seeded seeded = await SeedAsync(organizationId, 22m);
        DateTimeOffset recordedAt = new(2026, 11, 7, 11, 0, 0, TimeSpan.Zero);
        Guid sqlFirst = Guid.Parse("00000001-0000-0000-0000-000000000000");
        Guid sqlSecond = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Guid later = Guid.Parse("00000000-0000-0000-0000-000000000002");
        await InsertAsync(organizationId, seeded.AllocationId, sqlFirst, "same text", recordedAt);
        await InsertAsync(organizationId, seeded.AllocationId, sqlSecond, "same text", recordedAt);
        await InsertAsync(organizationId, seeded.AllocationId, later, "later text", recordedAt.AddMinutes(1));

        Guid[] sqlOrder = await ReadSqlOrderAsync(organizationId);
        Guid[] dotnetPair = [sqlFirst, sqlSecond];
        Array.Sort(dotnetPair);
        Assert.Equal([sqlFirst, sqlSecond, later], sqlOrder);
        Assert.False(dotnetPair.SequenceEqual(sqlOrder.Take(2)));

        using FinOpsDeskApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient client = Client(factory, organizationId, "order-reader");
        List<Guid> paged = [];
        for (int page = 1; page <= 3; page++)
        {
            using JsonDocument document = await ReadOkAsync(
                client,
                "/api/allocation-disputes?page=" + page.ToString(CultureInfo.InvariantCulture) + "&pageSize=1");
            Assert.Equal(3, document.RootElement.GetProperty("totalCount").GetInt64());
            JsonElement[] items = document.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Single(items);
            paged.Add(items[0].GetProperty("disputeId").GetGuid());
        }

        Assert.Equal(sqlOrder, paged);
        using JsonDocument pairs = await ReadOkAsync(client, "/api/allocation-disputes?pageSize=2");
        Guid[] firstPage = pairs.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("disputeId").GetGuid())
            .ToArray();
        using JsonDocument rest = await ReadOkAsync(client, "/api/allocation-disputes?page=2&pageSize=2");
        Guid[] secondPage = rest.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("disputeId").GetGuid())
            .ToArray();
        Assert.Equal(sqlOrder, firstPage.Concat(secondPage));
        Assert.Equal(3, pairs.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Equal(3, rest.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Equal(firstPage.Length, firstPage.Distinct().Count());
    }

    [Fact]
    public async Task Pagination_and_query_names_follow_the_read_contract()
    {
        OrganizationId organizationId = OrganizationId.New();
        Seeded seeded = await SeedAsync(organizationId, 11m);
        await InsertAsync(
            organizationId,
            seeded.AllocationId,
            Guid.NewGuid(),
            "one challenge",
            new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
        using FinOpsDeskApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient client = Client(factory, organizationId, "page-reader");
        Guid disputeId = (await ReadSqlOrderAsync(organizationId)).Single();

        using JsonDocument defaults = await ReadOkAsync(client, "/api/allocation-disputes");
        Assert.Equal(1, defaults.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(50, defaults.RootElement.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, defaults.RootElement.GetProperty("totalCount").GetInt64());
        using JsonDocument wide = await ReadOkAsync(client, "/api/allocation-disputes?pageSize=100");
        Assert.Equal(100, wide.RootElement.GetProperty("pageSize").GetInt32());
        Assert.Single(wide.RootElement.GetProperty("items").EnumerateArray());
        using JsonDocument beyond = await ReadOkAsync(client, "/api/allocation-disputes?page=21474837&pageSize=100");
        Assert.Equal(21474837, beyond.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(100, beyond.RootElement.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, beyond.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Empty(beyond.RootElement.GetProperty("items").EnumerateArray());

        await AssertProblemAsync(client, "/api/allocation-disputes?pageSize=101", "reconciliation.allocation_dispute.invalid_pagination");
        await AssertProblemAsync(client, "/api/allocation-disputes?page=0", "reconciliation.allocation_dispute.invalid_pagination");
        await AssertProblemAsync(client, "/api/allocation-disputes?page=-1", "reconciliation.allocation_dispute.invalid_pagination");
        await AssertProblemAsync(client, "/api/allocation-disputes?pageSize=0", "reconciliation.allocation_dispute.invalid_pagination");
        await AssertProblemAsync(client, "/api/allocation-disputes?pageSize=-5", "reconciliation.allocation_dispute.invalid_pagination");
        await AssertProblemAsync(client, "/api/allocation-disputes?page=21474838&pageSize=100", "reconciliation.allocation_dispute.invalid_pagination");
        await AssertProblemAsync(client, "/api/allocation-disputes?page=1&page=2", "reconciliation.allocation_dispute.invalid_request");
        await AssertProblemAsync(client, "/api/allocation-disputes?pageSize=50&pageSize=50", "reconciliation.allocation_dispute.invalid_request");
        await AssertProblemAsync(client, "/api/allocation-disputes?allocationId=", "reconciliation.allocation_dispute.invalid_request");
        await AssertProblemAsync(
            client,
            "/api/allocation-disputes?allocationId=00000000-0000-0000-0000-000000000000",
            "reconciliation.allocation_dispute.invalid_request");
        await AssertProblemAsync(client, "/api/allocation-disputes?allocationId=not-a-guid", "reconciliation.allocation_dispute.invalid_request");
        await AssertProblemAsync(client, "/api/allocation-disputes?sort=recordedAt", "reconciliation.allocation_dispute.invalid_request");
        await AssertProblemAsync(
            client,
            "/api/allocation-disputes/" + disputeId.ToString("D") + "?page=1",
            "reconciliation.allocation_dispute.invalid_request");
        await AssertProblemAsync(client, "/api/allocation-disputes/not-a-guid", "reconciliation.allocation_dispute.invalid_request");
        await AssertProblemAsync(
            client,
            "/api/allocation-disputes/00000000-0000-0000-0000-000000000000",
            "reconciliation.allocation_dispute.invalid_request");
    }

    [Fact]
    public async Task Reads_after_reversal_keep_the_registration_and_the_financial_rows()
    {
        OrganizationId organizationId = OrganizationId.New();
        Seeded seeded = await SeedAsync(organizationId, 40m);
        using FinOpsDeskApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient client = Client(factory, organizationId, "reversal-reader");
        HttpResponseMessage created = await client.SendAsync(
            DisputeRequest(seeded.AllocationId.Value, "customer challenge", Guid.NewGuid()));
        string createdBody = await created.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using JsonDocument confirmation = JsonDocument.Parse(createdBody);
        Guid disputeId = confirmation.RootElement.GetProperty("disputeId").GetGuid();

        HttpResponseMessage reversed = await client.SendAsync(ReverseRequest(seeded.AllocationId.Value, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, reversed.StatusCode);
        reversed.Dispose();
        FinancialState afterReversal = await ReadStateAsync(seeded.AllocationId);
        StoredDispute beforeReads = await ReadDisputeAsync(disputeId);
        Assert.Equal(AllocationStatus.Reversed, afterReversal.Status);
        Assert.NotEqual(seeded.RowVersion, afterReversal.RowVersion);
        Assert.True(afterReversal.InvoiceOutstanding > seeded.State.InvoiceOutstanding);
        Assert.True(afterReversal.BankUnapplied > seeded.State.BankUnapplied);
        Assert.True(afterReversal.AuditCount > seeded.State.AuditCount);

        using JsonDocument list = await ReadOkAsync(
            client,
            "/api/allocation-disputes?allocationId=" + seeded.AllocationId.Value.ToString("D"));
        using JsonDocument detail = await ReadOkAsync(client, "/api/allocation-disputes/" + disputeId.ToString("D"));
        AssertRegistration(confirmation.RootElement, OnlyItem(list));
        AssertRegistration(confirmation.RootElement, detail.RootElement);
        Assert.False(list.RootElement.GetRawText().Contains("idempotencyKey", StringComparison.OrdinalIgnoreCase));
        Assert.False(detail.RootElement.TryGetProperty("idempotencyKey", out _));
        Assert.False(detail.RootElement.TryGetProperty("organizationId", out _));
        Assert.False(OnlyItem(list).TryGetProperty("organizationId", out _));

        FinancialState afterReads = await ReadStateAsync(seeded.AllocationId);
        StoredDispute afterDispute = await ReadDisputeAsync(disputeId);
        Assert.Equal(afterReversal.Status, afterReads.Status);
        Assert.Equal(afterReversal.InvoiceAllocated, afterReads.InvoiceAllocated);
        Assert.Equal(afterReversal.InvoiceOutstanding, afterReads.InvoiceOutstanding);
        Assert.Equal(afterReversal.BankAllocated, afterReads.BankAllocated);
        Assert.Equal(afterReversal.BankUnapplied, afterReads.BankUnapplied);
        Assert.Equal(afterReversal.AuditCount, afterReads.AuditCount);
        Assert.Equal(afterReversal.RowVersion, afterReads.RowVersion);
        Assert.Equal(beforeReads, afterDispute);
        Assert.Equal(1, await CountDisputesAsync(organizationId));
    }

    [Fact]
    public async Task DevelopmentJwt_without_sub_can_read_and_cannot_register()
    {
        OrganizationId organizationId = OrganizationId.New();
        Seeded seeded = await SeedAsync(organizationId, 9m);
        Guid disputeId = Guid.NewGuid();
        await InsertAsync(
            organizationId,
            seeded.AllocationId,
            disputeId,
            "stored challenge",
            new DateTimeOffset(2026, 11, 7, 13, 0, 0, TimeSpan.Zero));
        using FinOpsDeskApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient reader = Client(factory, organizationId, null);
        using HttpClient writer = Client(factory, organizationId, "writer");

        HttpResponseMessage denied = await reader.SendAsync(
            DisputeRequest(seeded.AllocationId.Value, "another challenge", Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("reconciliation.actor.required", await ReadCodeAsync(denied));
        denied.Dispose();

        using JsonDocument list = await ReadOkAsync(reader, "/api/allocation-disputes");
        using JsonDocument detail = await ReadOkAsync(reader, "/api/allocation-disputes/" + disputeId.ToString("D"));
        Assert.Equal(disputeId, OnlyItem(list).GetProperty("disputeId").GetGuid());
        Assert.Equal("stored challenge", detail.RootElement.GetProperty("statement").GetString());
        Assert.Equal("developmentJwt", detail.RootElement.GetProperty("actorSource").GetString());
        using HttpResponseMessage created = await writer.SendAsync(
            DisputeRequest(seeded.AllocationId.Value, "writer challenge", Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    private static void AssertRegistration(JsonElement confirmation, JsonElement read)
    {
        Assert.Equal(confirmation.GetProperty("disputeId").GetGuid(), read.GetProperty("disputeId").GetGuid());
        Assert.Equal(confirmation.GetProperty("allocationId").GetGuid(), read.GetProperty("allocationId").GetGuid());
        Assert.Equal(confirmation.GetProperty("statement").GetString(), read.GetProperty("statement").GetString());
        Assert.Equal(confirmation.GetProperty("actorSource").GetString(), read.GetProperty("actorSource").GetString());
        Assert.Equal(confirmation.GetProperty("actorId").GetString(), read.GetProperty("actorId").GetString());
        Assert.Equal(confirmation.GetProperty("recordedAt").GetDateTimeOffset(), read.GetProperty("recordedAt").GetDateTimeOffset());
    }

    private static JsonElement OnlyItem(JsonDocument document)
    {
        return Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
    }

    private async Task AssertProblemAsync(HttpClient client, string path, string code)
    {
        using HttpResponseMessage response = await client.GetAsync(path);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
    }

    private static async Task<JsonDocument> ReadOkAsync(HttpClient client, string path)
    {
        HttpResponseMessage response = await client.GetAsync(path);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, path + " returned " + response.StatusCode + " " + body);
        response.Dispose();
        return JsonDocument.Parse(body);
    }

    private static async Task<Guid> RegisterAsync(HttpClient client, Guid allocationId, string statement)
    {
        using HttpResponseMessage response = await client.SendAsync(DisputeRequest(allocationId, statement, Guid.NewGuid()));
        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("disputeId").GetGuid();
    }

    private static HttpClient Client(FinOpsDeskApiFactory factory, OrganizationId organizationId, string? subject)
    {
        return factory.CreateApiClient(TestAccessTokens.CreateForActor(organizationId.Value, subject));
    }

    private async Task<Seeded> SeedAsync(OrganizationId organizationId, decimal amount)
    {
        string number = "INV-" + Guid.NewGuid().ToString("N")[..8];
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, amount, number);
        BankTransaction bank = PersistenceTestData.CreateBankTransaction(organizationId, amount, reference: number);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.AddRange(invoice, bank);
        await context.SaveChangesAsync();
        Invoice storedInvoice = await context.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        BankTransaction storedBank = await context.BankTransactions.SingleAsync(candidate => candidate.Id == bank.Id);
        Assert.True(AllocationConfirmation.Apply(
            storedInvoice,
            storedBank,
            new Money(amount, PersistenceTestData.Pln)).IsSuccess);
        Allocation allocation = new(
            AllocationId.New(),
            organizationId,
            storedInvoice.Id,
            storedBank.Id,
            new Money(amount, PersistenceTestData.Pln),
            ConfirmedAt,
            Guid.NewGuid(),
            AllocationIdempotencyOperation.ExplicitAmount);
        context.Allocations.Add(allocation);
        await context.SaveChangesAsync();
        await context.Entry(allocation).ReloadAsync();
        byte[] rowVersion = context.Entry(allocation)
            .Property<byte[]>(AllocationConfiguration.RowVersionPropertyName)
            .CurrentValue!
            .ToArray();
        return new Seeded(allocation.Id, rowVersion, await ReadStateAsync(allocation.Id));
    }

    private async Task InsertAsync(
        OrganizationId organizationId,
        AllocationId allocationId,
        Guid disputeId,
        string statement,
        DateTimeOffset recordedAt)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.AllocationDisputes.Add(AllocationDispute.Create(
            new AllocationDisputeId(disputeId),
            organizationId,
            allocationId,
            statement,
            ReviewCaseActorSource.DevelopmentJwt,
            "stored-actor",
            recordedAt,
            Guid.NewGuid()));
        await context.SaveChangesAsync();
    }

    private async Task<Guid[]> ReadSqlOrderAsync(OrganizationId organizationId)
    {
        await using SqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id
            FROM reconciliation.AllocationDisputes
            WHERE OrganizationId = @organizationId
            ORDER BY RecordedAt ASC, Id ASC
            """;
        command.Parameters.Add(new SqlParameter("@organizationId", organizationId.Value));
        List<Guid> ids = [];
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids.ToArray();
    }

    private async Task<FinancialState> ReadStateAsync(AllocationId allocationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Allocation allocation = await context.Allocations.SingleAsync(candidate => candidate.Id == allocationId);
        Invoice invoice = await context.Invoices.SingleAsync(candidate => candidate.Id == allocation.InvoiceId);
        BankTransaction bank = await context.BankTransactions.SingleAsync(
            candidate => candidate.Id == allocation.BankTransactionId);
        int audits = await context.FinancialAuditEntries.CountAsync(entry => entry.AllocationId == allocationId);
        byte[] rowVersion = context.Entry(allocation)
            .Property<byte[]>(AllocationConfiguration.RowVersionPropertyName)
            .CurrentValue!
            .ToArray();
        return new FinancialState(
            allocation.Status,
            invoice.AllocatedAmount.Amount,
            invoice.OutstandingAmount.Amount,
            bank.AllocatedAmount.Amount,
            bank.UnappliedAmount.Amount,
            audits,
            rowVersion);
    }

    private async Task<StoredDispute> ReadDisputeAsync(Guid disputeId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        AllocationDispute dispute = await context.AllocationDisputes.SingleAsync(
            candidate => candidate.Id == new AllocationDisputeId(disputeId));
        return new StoredDispute(
            dispute.Id.Value,
            dispute.AllocationId.Value,
            dispute.Statement,
            dispute.ActorSource,
            dispute.ActorId,
            dispute.RecordedAt,
            dispute.IdempotencyKey);
    }

    private async Task<int> CountDisputesAsync(OrganizationId organizationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.AllocationDisputes.CountAsync(dispute => dispute.OrganizationId == organizationId);
    }

    private static async Task<string> ReadCodeAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString() ?? string.Empty;
    }

    private static HttpRequestMessage DisputeRequest(Guid allocationId, string statement, Guid idempotencyKey)
    {
        HttpRequestMessage request = new(
            HttpMethod.Post,
            "/api/allocations/" + allocationId.ToString("D") + "/disputes")
        {
            Content = new StringContent(
                "{\"statement\":" + JsonSerializer.Serialize(statement) + "}",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey.ToString("D"));
        return request;
    }

    private static HttpRequestMessage ReverseRequest(Guid allocationId, Guid idempotencyKey)
    {
        HttpRequestMessage request = new(
            HttpMethod.Post,
            "/api/allocations/" + allocationId.ToString("D") + "/reverse")
        {
            Content = new StringContent("{\"reason\":\"customer refund\"}", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey.ToString("D"));
        return request;
    }

    private sealed record Seeded(AllocationId AllocationId, byte[] RowVersion, FinancialState State);

    private sealed record FinancialState(
        AllocationStatus Status,
        decimal InvoiceAllocated,
        decimal InvoiceOutstanding,
        decimal BankAllocated,
        decimal BankUnapplied,
        int AuditCount,
        byte[] RowVersion);

    private sealed record StoredDispute(
        Guid DisputeId,
        Guid AllocationId,
        string Statement,
        ReviewCaseActorSource ActorSource,
        string ActorId,
        DateTimeOffset RecordedAt,
        Guid IdempotencyKey);
}