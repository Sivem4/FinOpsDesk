using System.Net;
using System.Text;
using System.Text.Json;

using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class ConfirmationActorEndpointTests : IDisposable
{
    private readonly SqlServerFixture _fixture;
    private readonly FinOpsDeskApiFactory _factory;

    public ConfirmationActorEndpointTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _factory = new FinOpsDeskApiFactory(fixture.ConnectionString);
    }

    [Theory]
    [InlineData("/api/matches/confirm", "missing")]
    [InlineData("/api/matches/confirm", "blank")]
    [InlineData("/api/matches/confirm", "spaces")]
    [InlineData("/api/matches/confirm", "overlong")]
    [InlineData("/api/allocations", "missing")]
    [InlineData("/api/allocations", "blank")]
    [InlineData("/api/allocations", "spaces")]
    [InlineData("/api/allocations", "overlong")]
    public async Task Invalid_actor_returns_403_before_financial_work(string path, string kind)
    {
        OrganizationId organizationId = OrganizationId.New();
        string? subject = kind switch
        {
            "missing" => null,
            "blank" => string.Empty,
            "spaces" => "   ",
            "overlong" => new string('a', FinancialAuditEntry.MaxActorIdLength + 1),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.CreateForActor(organizationId.Value, subject));

        HttpResponseMessage response = await client.SendAsync(
            CreateRequest(path, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString("D"), spoof: false));

        await AssertProblemAsync(
            response,
            HttpStatusCode.Forbidden,
            ReconciliationApplicationErrors.ActorRequired.Code);
        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Assert.Equal(
            0,
            await reload.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId));
        Assert.Equal(
            0,
            await reload.FinancialAuditEntries.CountAsync(entry => entry.OrganizationId == organizationId));
    }

    [Theory]
    [InlineData("/api/matches/confirm")]
    [InlineData("/api/allocations")]
    public async Task Valid_subject_is_stored_and_unrelated_actor_input_is_ignored(string path)
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 25m, "INV-ACTOR");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            25m,
            reference: "INV-ACTOR");
        await SeedAsync(invoice, bankTransaction);
        const string subject = "real-operator";
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.CreateForActor(organizationId.Value, subject));

        HttpResponseMessage response = await client.SendAsync(
            CreateRequest(
                path,
                invoice.Id.Value,
                bankTransaction.Id.Value,
                Guid.NewGuid().ToString("D"),
                spoof: true));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Guid allocationId = document.RootElement.GetProperty("allocationId").GetGuid();
        await using ReconciliationDbContext reload = _fixture.CreateContext();
        FinancialAuditEntry audit = await reload.FinancialAuditEntries.SingleAsync(
            entry => entry.OrganizationId == organizationId);
        Assert.Equal(allocationId, audit.AllocationId.Value);
        Assert.Equal(subject, audit.ActorId);
        Assert.NotEqual("spoofed-actor", audit.ActorId);
    }

    [Theory]
    [InlineData("/api/matches/confirm")]
    [InlineData("/api/allocations")]
    public async Task Replay_without_a_valid_actor_is_rejected_and_keeps_the_original_actor(string path)
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 25m, "INV-REPLAY");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            25m,
            reference: "INV-REPLAY");
        await SeedAsync(invoice, bankTransaction);
        const string subject = "original-operator";
        string idempotencyKey = Guid.NewGuid().ToString("D");
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.CreateForActor(organizationId.Value, subject));
        HttpResponseMessage created = await client.SendAsync(
            CreateRequest(path, invoice.Id.Value, bankTransaction.Id.Value, idempotencyKey, spoof: false));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using HttpClient anonymous = _factory.CreateApiClient(
            TestAccessTokens.CreateForActor(organizationId.Value, subject: null));
        HttpResponseMessage replay = await anonymous.SendAsync(
            CreateRequest(path, invoice.Id.Value, bankTransaction.Id.Value, idempotencyKey, spoof: false));

        await AssertProblemAsync(
            replay,
            HttpStatusCode.Forbidden,
            ReconciliationApplicationErrors.ActorRequired.Code);
        await using ReconciliationDbContext reload = _fixture.CreateContext();
        FinancialAuditEntry audit = await reload.FinancialAuditEntries.SingleAsync(
            entry => entry.OrganizationId == organizationId);
        Assert.Equal(subject, audit.ActorId);
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

    private static HttpRequestMessage CreateRequest(
        string path,
        Guid invoiceId,
        Guid bankTransactionId,
        string idempotencyKey,
        bool spoof)
    {
        string json = path == "/api/allocations"
            ? "{\"invoiceId\":\"" + invoiceId + "\",\"bankTransactionId\":\"" + bankTransactionId
                + "\",\"amount\":{\"amount\":25.00,\"currency\":\"PLN\"}}"
            : "{\"invoiceId\":\"" + invoiceId + "\",\"bankTransactionId\":\"" + bankTransactionId + "\"}";
        string uri = spoof ? $"{path}?actorId=spoofed-actor" : path;
        var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        if (spoof)
        {
            request.Headers.TryAddWithoutValidation("X-Actor-Id", "spoofed-actor");
        }

        return request;
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode statusCode,
        string code)
    {
        Assert.Equal(statusCode, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal((int)statusCode, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        Assert.Equal("Actor is required.", document.RootElement.GetProperty("title").GetString());
    }
}