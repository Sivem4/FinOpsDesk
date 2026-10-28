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
public sealed class BatchExecutionActorEndpointTests : IDisposable
{
    private const string ExecutePath = "/api/matches/batch/execute";

    private readonly SqlServerFixture _fixture;
    private readonly FinOpsDeskApiFactory _factory;

    public BatchExecutionActorEndpointTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _factory = new FinOpsDeskApiFactory(fixture.ConnectionString);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("blank")]
    [InlineData("spaces")]
    [InlineData("overlong")]
    public async Task Invalid_actor_returns_403_before_batch_execution(string kind)
    {
        OrganizationId organizationId = OrganizationId.New();
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.CreateForActor(organizationId.Value, Subject(kind)));

        HttpResponseMessage response = await client.SendAsync(
            ExecuteRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), spoof: false));

        await AssertActorProblemAsync(response);
        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Assert.Equal(
            0,
            await reload.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId));
        Assert.Equal(
            0,
            await reload.FinancialAuditEntries.CountAsync(entry => entry.OrganizationId == organizationId));
        Assert.Equal(
            0,
            await reload.BatchExecutions.CountAsync(execution => execution.OrganizationId == organizationId));
    }

    [Fact]
    public async Task Valid_subject_is_stored_and_unrelated_actor_input_is_ignored()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "BATCH-ACTOR", 25m);
        const string subject = "batch-operator";
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.CreateForActor(organizationId.Value, subject));

        HttpResponseMessage response = await client.SendAsync(
            ExecuteRequest(Guid.NewGuid(), invoice.Id.Value, bank.Id.Value, spoof: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Guid allocationId = document.RootElement
            .GetProperty("items")[0]
            .GetProperty("allocationId")
            .GetGuid();
        await using ReconciliationDbContext reload = _fixture.CreateContext();
        FinancialAuditEntry audit = await reload.FinancialAuditEntries.SingleAsync(
            entry => entry.OrganizationId == organizationId);
        Assert.Equal(allocationId, audit.AllocationId.Value);
        Assert.Equal(subject, audit.ActorId);
    }

    [Fact]
    public async Task Completed_replay_without_a_valid_actor_is_rejected_and_keeps_the_original_actor()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "BATCH-REPLAY", 25m);
        Guid key = Guid.NewGuid();
        const string subject = "original-batch-operator";
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.CreateForActor(organizationId.Value, subject));
        HttpResponseMessage created = await client.SendAsync(
            ExecuteRequest(key, invoice.Id.Value, bank.Id.Value, spoof: false));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        using HttpClient anonymous = _factory.CreateApiClient(
            TestAccessTokens.CreateForActor(organizationId.Value, subject: null));
        HttpResponseMessage replay = await anonymous.SendAsync(
            ExecuteRequest(key, invoice.Id.Value, bank.Id.Value, spoof: false));

        await AssertActorProblemAsync(replay);
        await using ReconciliationDbContext reload = _fixture.CreateContext();
        FinancialAuditEntry audit = await reload.FinancialAuditEntries.SingleAsync(
            entry => entry.OrganizationId == organizationId);
        Assert.Equal(subject, audit.ActorId);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private static string? Subject(string kind)
    {
        return kind switch
        {
            "missing" => null,
            "blank" => string.Empty,
            "spaces" => "   ",
            "overlong" => new string('a', FinancialAuditEntry.MaxActorIdLength + 1),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
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

    private static HttpRequestMessage ExecuteRequest(
        Guid idempotencyKey,
        Guid invoiceId,
        Guid bankTransactionId,
        bool spoof)
    {
        string json = "{\"pairs\":[{\"invoiceId\":\""
            + invoiceId
            + "\",\"bankTransactionId\":\""
            + bankTransactionId
            + "\"}]}";
        string uri = spoof ? ExecutePath + "?actorId=spoofed-actor" : ExecutePath;
        HttpRequestMessage request = new(HttpMethod.Post, uri)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey.ToString("D"));
        if (spoof)
        {
            request.Headers.TryAddWithoutValidation("X-Actor-Id", "spoofed-actor");
        }

        return request;
    }

    private static async Task AssertActorProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal((int)HttpStatusCode.Forbidden, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(
            ReconciliationApplicationErrors.ActorRequired.Code,
            document.RootElement.GetProperty("code").GetString());
        Assert.Equal("Actor is required.", document.RootElement.GetProperty("title").GetString());
    }
}