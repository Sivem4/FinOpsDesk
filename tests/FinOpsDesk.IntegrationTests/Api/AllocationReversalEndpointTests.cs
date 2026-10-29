using System.Net;
using System.Text;
using System.Text.Json;

using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class AllocationReversalEndpointTests : IDisposable
{
    private static readonly DateTimeOffset ConfirmedAt = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    private readonly SqlServerFixture _fixture;
    private readonly FinOpsDeskApiFactory _factory;

    public AllocationReversalEndpointTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _factory = new FinOpsDeskApiFactory(fixture.ConnectionString);
    }

    [Fact]
    public async Task Successful_reversal_stores_the_jwt_subject_and_ignores_spoofed_actor_input()
    {
        OrganizationId organizationId = OrganizationId.New();
        AllocationId allocationId = await SeedAllocationAsync(organizationId, 40m);
        using HttpClient client = Client(organizationId, "real-operator");
        Guid reversalKey = Guid.NewGuid();

        HttpResponseMessage response = await client.SendAsync(
            ReverseRequest(allocationId.Value, "  Duplicate payment  ", reversalKey, spoof: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement body = document.RootElement;
        Assert.Equal(allocationId.Value, body.GetProperty("allocationId").GetGuid());
        Assert.Equal("reversed", body.GetProperty("status").GetString());
        Assert.Equal(40m, body.GetProperty("amount").GetProperty("amount").GetDecimal());
        Assert.Equal("PLN", body.GetProperty("amount").GetProperty("currency").GetString());
        Assert.Equal("Duplicate payment", body.GetProperty("reason").GetString());
        Assert.False(body.GetProperty("replayed").GetBoolean());
        Assert.False(body.TryGetProperty("organizationId", out _));
        Assert.False(body.TryGetProperty("idempotencyKey", out _));
        await using ReconciliationDbContext context = _fixture.CreateContext();
        FinancialAuditEntry audit = await context.FinancialAuditEntries.SingleAsync(
            entry => entry.AllocationId == allocationId && entry.Action == FinancialAuditAction.Reversal);
        Assert.Equal("real-operator", audit.ActorId);
        Assert.NotEqual("spoofed-actor", audit.ActorId);
        Assert.Equal(0m, await AllocatedAsync(allocationId));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("blank")]
    [InlineData("spaces")]
    [InlineData("overlong")]
    public async Task Invalid_actor_returns_403_and_writes_nothing(string kind)
    {
        OrganizationId organizationId = OrganizationId.New();
        AllocationId allocationId = await SeedAllocationAsync(organizationId, 25m);
        string? subject = kind switch
        {
            "missing" => null,
            "blank" => string.Empty,
            "spaces" => "   ",
            "overlong" => new string('a', FinancialAuditEntry.MaxActorIdLength + 1),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        using HttpClient client = Client(organizationId, subject);

        HttpResponseMessage response = await client.SendAsync(
            ReverseRequest(allocationId.Value, "customer refund", Guid.NewGuid(), spoof: false));

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, ReconciliationApplicationErrors.ActorRequired.Code);
        Assert.Equal(25m, await AllocatedAsync(allocationId));
        Assert.Equal(0, await ReversalAuditCountAsync(allocationId));
    }

    [Fact]
    public async Task Replay_without_an_actor_returns_403_and_keeps_the_original_actor()
    {
        OrganizationId organizationId = OrganizationId.New();
        AllocationId allocationId = await SeedAllocationAsync(organizationId, 25m);
        Guid reversalKey = Guid.NewGuid();
        using HttpClient client = Client(organizationId, "original-operator");
        HttpResponseMessage created = await client.SendAsync(
            ReverseRequest(allocationId.Value, "customer refund", reversalKey, spoof: false));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using JsonDocument createdDocument = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        DateTimeOffset reversedAt = createdDocument.RootElement.GetProperty("reversedAt").GetDateTimeOffset();

        using HttpClient anonymous = Client(organizationId, subject: null);
        HttpResponseMessage replay = await anonymous.SendAsync(
            ReverseRequest(allocationId.Value, "customer refund", reversalKey, spoof: false));

        await AssertProblemAsync(replay, HttpStatusCode.Forbidden, ReconciliationApplicationErrors.ActorRequired.Code);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        FinancialAuditEntry audit = await context.FinancialAuditEntries.SingleAsync(
            entry => entry.AllocationId == allocationId);
        Assert.Equal("original-operator", audit.ActorId);
        Assert.Equal(reversedAt, audit.RecordedAt);
    }

    [Fact]
    public async Task Missing_and_foreign_allocations_return_the_same_not_found_problem()
    {
        OrganizationId owner = OrganizationId.New();
        OrganizationId other = OrganizationId.New();
        AllocationId allocationId = await SeedAllocationAsync(owner, 77m, "FOREIGN-ONLY-991");
        using HttpClient ownerClient = Client(owner, "owner-operator");
        using HttpClient otherClient = Client(other, "other-operator");

        HttpResponseMessage missing = await ownerClient.SendAsync(
            ReverseRequest(Guid.NewGuid(), "customer refund", Guid.NewGuid(), spoof: false));
        HttpResponseMessage foreign = await otherClient.SendAsync(
            ReverseRequest(allocationId.Value, "customer refund", Guid.NewGuid(), spoof: false));

        string missingBody = await AssertProblemAsync(
            missing,
            HttpStatusCode.NotFound,
            ReconciliationApplicationErrors.AllocationNotFound.Code);
        string foreignBody = await AssertProblemAsync(
            foreign,
            HttpStatusCode.NotFound,
            ReconciliationApplicationErrors.AllocationNotFound.Code);
        using JsonDocument missingDocument = JsonDocument.Parse(missingBody);
        using JsonDocument foreignDocument = JsonDocument.Parse(foreignBody);
        Assert.Equal(
            missingDocument.RootElement.GetProperty("title").GetString(),
            foreignDocument.RootElement.GetProperty("title").GetString());
        Assert.Equal(
            missingDocument.RootElement.GetProperty("detail").GetString(),
            foreignDocument.RootElement.GetProperty("detail").GetString());
        Assert.DoesNotContain("FOREIGN-ONLY-991", foreignBody, StringComparison.Ordinal);
        Assert.Equal(77m, await AllocatedAsync(allocationId));
    }

    [Theory]
    [InlineData(null, "reconciliation.confirmation.idempotency_key_required")]
    [InlineData("", "reconciliation.confirmation.idempotency_key_required")]
    [InlineData("   ", "reconciliation.confirmation.idempotency_key_required")]
    [InlineData("not-a-uuid", "reconciliation.confirmation.idempotency_key_invalid")]
    [InlineData("00000000-0000-0000-0000-000000000000", "reconciliation.confirmation.idempotency_key_invalid")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "reconciliation.confirmation.idempotency_key_invalid")]
    public async Task Malformed_idempotency_key_returns_400_and_writes_nothing(string? key, string code)
    {
        OrganizationId organizationId = OrganizationId.New();
        AllocationId allocationId = await SeedAllocationAsync(organizationId, 10m);
        using HttpClient client = Client(organizationId, "operator-a");

        HttpResponseMessage response = await client.SendAsync(
            ReverseRaw(allocationId.Value, "{\"reason\":\"customer refund\"}", key, "application/json"));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, code);
        Assert.Equal(10m, await AllocatedAsync(allocationId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"reason\":null}")]
    [InlineData("{\"reason\":\"\"}")]
    [InlineData("{\"reason\":\"   \"}")]
    [InlineData("{\"reason\":\"\\t\\n\"}")]
    public async Task Missing_or_blank_reason_returns_400_and_writes_nothing(string json)
    {
        OrganizationId organizationId = OrganizationId.New();
        AllocationId allocationId = await SeedAllocationAsync(organizationId, 10m);
        using HttpClient client = Client(organizationId, "operator-a");

        HttpResponseMessage response = await client.SendAsync(
            ReverseRaw(allocationId.Value, json, Guid.NewGuid().ToString("D"), "application/json"));

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.AllocationReversalInvalidRequest.Code);
        Assert.Equal(10m, await AllocatedAsync(allocationId));
    }

    [Fact]
    public async Task Overlong_reason_returns_400_without_truncation()
    {
        OrganizationId organizationId = OrganizationId.New();
        AllocationId allocationId = await SeedAllocationAsync(organizationId, 10m);
        using HttpClient client = Client(organizationId, "operator-a");
        string reason = new string('a', Allocation.MaxReversalReasonLength + 1);

        HttpResponseMessage response = await client.SendAsync(
            ReverseRequest(allocationId.Value, reason, Guid.NewGuid(), spoof: false));

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            ReconciliationErrors.AllocationReversalReasonTooLong.Code);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Allocation allocation = await context.Allocations.SingleAsync(candidate => candidate.Id == allocationId);
        Assert.Equal(AllocationStatus.Confirmed, allocation.Status);
        Assert.Null(allocation.ReversalReason);
    }

    [Theory]
    [InlineData("{\"reason\":\"ok\",\"amount\":1}")]
    [InlineData("{\"reason\":\"first\",\"reason\":\"second\"}")]
    [InlineData("[]")]
    [InlineData("\"customer refund\"")]
    public async Task Unknown_or_duplicate_json_is_rejected(string json)
    {
        OrganizationId organizationId = OrganizationId.New();
        AllocationId allocationId = await SeedAllocationAsync(organizationId, 10m);
        using HttpClient client = Client(organizationId, "operator-a");

        HttpResponseMessage response = await client.SendAsync(
            ReverseRaw(allocationId.Value, json, Guid.NewGuid().ToString("D"), "application/json"));

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.AllocationReversalInvalidRequest.Code);
        Assert.Equal(10m, await AllocatedAsync(allocationId));
    }

    [Fact]
    public async Task Unknown_query_malformed_id_and_unsupported_media_type_write_nothing()
    {
        OrganizationId organizationId = OrganizationId.New();
        AllocationId allocationId = await SeedAllocationAsync(organizationId, 10m);
        using HttpClient client = Client(organizationId, "operator-a");

        HttpResponseMessage query = await client.SendAsync(
            ReverseRaw(
                allocationId.Value,
                "{\"reason\":\"customer refund\"}",
                Guid.NewGuid().ToString("D"),
                "application/json",
                "/api/allocations/" + allocationId.Value.ToString("D") + "/reverse?actorId=spoofed-actor"));
        HttpResponseMessage malformed = await client.SendAsync(
            ReverseRaw(
                Guid.Empty,
                "{\"reason\":\"customer refund\"}",
                Guid.NewGuid().ToString("D"),
                "application/json",
                "/api/allocations/not-a-guid/reverse"));
        HttpResponseMessage media = await client.SendAsync(
            ReverseRaw(allocationId.Value, "{\"reason\":\"customer refund\"}", Guid.NewGuid().ToString("D"), "text/plain"));

        await AssertProblemAsync(query, HttpStatusCode.BadRequest, ReconciliationApplicationErrors.AllocationReversalInvalidRequest.Code);
        await AssertProblemAsync(malformed, HttpStatusCode.BadRequest, ReconciliationApplicationErrors.AllocationReversalInvalidRequest.Code);
        await AssertProblemAsync(media, HttpStatusCode.UnsupportedMediaType, ReconciliationApplicationErrors.AllocationUnsupportedMediaType.Code);
        Assert.Equal(10m, await AllocatedAsync(allocationId));
    }

    [Fact]
    public async Task Replay_and_conflicts_use_the_documented_status_codes()
    {
        OrganizationId organizationId = OrganizationId.New();
        AllocationId allocationId = await SeedAllocationAsync(organizationId, 30m);
        AllocationId other = await SeedAllocationAsync(organizationId, 20m);
        using HttpClient client = Client(organizationId, "operator-a");
        Guid reversalKey = Guid.NewGuid();
        HttpResponseMessage created = await client.SendAsync(
            ReverseRequest(allocationId.Value, "customer refund", reversalKey, spoof: false));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        string reversedAt = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("reversedAt").GetString()!;

        HttpResponseMessage replay = await client.SendAsync(
            ReverseRequest(allocationId.Value, " customer refund ", reversalKey, spoof: false));
        HttpResponseMessage differentReason = await client.SendAsync(
            ReverseRequest(allocationId.Value, "other reason", reversalKey, spoof: false));
        HttpResponseMessage differentAllocation = await client.SendAsync(
            ReverseRequest(other.Value, "customer refund", reversalKey, spoof: false));
        HttpResponseMessage differentKey = await client.SendAsync(
            ReverseRequest(allocationId.Value, "customer refund", Guid.NewGuid(), spoof: false));

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using JsonDocument replayDocument = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.True(replayDocument.RootElement.GetProperty("replayed").GetBoolean());
        Assert.Equal(reversedAt, replayDocument.RootElement.GetProperty("reversedAt").GetString());
        Assert.Equal("customer refund", replayDocument.RootElement.GetProperty("reason").GetString());
        await AssertProblemAsync(
            differentReason,
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.AllocationReversalIdempotencyPayloadConflict.Code);
        await AssertProblemAsync(
            differentAllocation,
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.AllocationReversalIdempotencyPayloadConflict.Code);
        await AssertProblemAsync(
            differentKey,
            HttpStatusCode.Conflict,
            ReconciliationErrors.AllocationAlreadyReversed.Code);
        Assert.Equal(1, await ReversalAuditCountAsync(allocationId));
        Assert.Equal(20m, await AllocatedAsync(other));
    }

    [Fact]
    public async Task OpenApi_documents_the_reversal_contract_like_the_existing_allocation_post()
    {
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(Guid.NewGuid()));

        HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement paths = document.RootElement.GetProperty("paths");
        JsonElement operation = paths.GetProperty("/api/allocations/{allocationId}/reverse").GetProperty("post");
        JsonElement allocationPost = paths.GetProperty("/api/allocations").GetProperty("post");
        Assert.Equal(
            allocationPost.TryGetProperty("security", out _),
            operation.TryGetProperty("security", out _));
        JsonElement schema = operation.GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");
        Assert.Equal(JsonValueKind.False, schema.GetProperty("additionalProperties").ValueKind);
        Assert.Contains("reason", schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(500, schema.GetProperty("properties").GetProperty("reason").GetProperty("maxLength").GetInt32());
        Assert.True(operation.GetProperty("responses").TryGetProperty("200", out _));
        Assert.True(operation.GetProperty("responses").TryGetProperty("400", out _));
        Assert.True(operation.GetProperty("responses").TryGetProperty("403", out _));
        Assert.True(operation.GetProperty("responses").TryGetProperty("404", out _));
        Assert.True(operation.GetProperty("responses").TryGetProperty("409", out _));
        bool headerFound = operation.GetProperty("parameters").EnumerateArray().Any(parameter =>
            parameter.GetProperty("name").GetString() == "Idempotency-Key"
            && parameter.GetProperty("in").GetString() == "header"
            && parameter.GetProperty("required").GetBoolean());
        Assert.True(headerFound);
        Assert.Contains("TenantMember", operation.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("bearer", operation.GetProperty("description").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private HttpClient Client(OrganizationId organizationId, string? subject)
    {
        return _factory.CreateApiClient(TestAccessTokens.CreateForActor(organizationId.Value, subject));
    }

    private async Task<AllocationId> SeedAllocationAsync(
        OrganizationId organizationId,
        decimal amount,
        string? invoiceNumber = null)
    {
        string number = invoiceNumber ?? "INV-" + Guid.NewGuid().ToString("N")[..8];
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, amount, number);
        BankTransaction bank = PersistenceTestData.CreateBankTransaction(organizationId, amount, reference: number);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.AddRange(invoice, bank);
        await context.SaveChangesAsync();
        Invoice storedInvoice = await context.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        BankTransaction storedBank = await context.BankTransactions.SingleAsync(candidate => candidate.Id == bank.Id);
        var money = new Money(amount, PersistenceTestData.Pln);
        Assert.True(AllocationConfirmation.Apply(storedInvoice, storedBank, money).IsSuccess);
        var allocation = new Allocation(
            AllocationId.New(),
            organizationId,
            storedInvoice.Id,
            storedBank.Id,
            money,
            ConfirmedAt,
            Guid.NewGuid(),
            AllocationIdempotencyOperation.ExplicitAmount);
        context.Allocations.Add(allocation);
        await context.SaveChangesAsync();
        return allocation.Id;
    }

    private async Task<decimal> AllocatedAsync(AllocationId allocationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Allocation allocation = await context.Allocations.SingleAsync(candidate => candidate.Id == allocationId);
        return (await context.Invoices.SingleAsync(invoice => invoice.Id == allocation.InvoiceId)).AllocatedAmount.Amount;
    }

    private async Task<int> ReversalAuditCountAsync(AllocationId allocationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.FinancialAuditEntries.CountAsync(
            entry => entry.AllocationId == allocationId && entry.Action == FinancialAuditAction.Reversal);
    }

    private static HttpRequestMessage ReverseRequest(Guid allocationId, string reason, Guid idempotencyKey, bool spoof)
    {
        string json = "{\"reason\":" + JsonSerializer.Serialize(reason) + "}";
        HttpRequestMessage request = ReverseRaw(
            allocationId,
            json,
            idempotencyKey.ToString("D"),
            "application/json");
        if (spoof)
        {
            request.Headers.TryAddWithoutValidation("X-Actor-Id", "spoofed-actor");
        }

        return request;
    }

    private static HttpRequestMessage ReverseRaw(
        Guid allocationId,
        string json,
        string? idempotencyKey,
        string? mediaType,
        string? path = null)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            path ?? "/api/allocations/" + allocationId.ToString("D") + "/reverse");
        if (mediaType is null)
        {
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        }
        else
        {
            request.Content = new StringContent(json, Encoding.UTF8, mediaType);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        return request;
    }

    private static async Task<string> AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode statusCode,
        string code)
    {
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == statusCode,
            $"Expected {(int)statusCode} but got {(int)response.StatusCode}. Body: {body}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.Equal((int)statusCode, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        return body;
    }
}