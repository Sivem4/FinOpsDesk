using System.Net;
using System.Text.Json;

using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class AllocationDisputeQueueEndpointTests
{
    private static readonly DateTimeOffset ConfirmedAt = new(2026, 11, 8, 7, 0, 0, TimeSpan.Zero);

    private readonly SqlServerFixture _fixture;

    public AllocationDisputeQueueEndpointTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Summary_and_queue_use_the_same_organization_predicates_and_the_registration_list_rejects_queue()
    {
        OrganizationId organizationId = OrganizationId.New();
        OrganizationId foreignId = OrganizationId.New();
        Guid earlier = await SeedAsync(organizationId, "earlier", ConfirmedAt.AddHours(1), decision: null, reversed: false, corrected: false);
        Guid later = await SeedAsync(organizationId, "later", ConfirmedAt.AddHours(2), decision: null, reversed: false, corrected: false);
        await SeedAsync(organizationId, "upheld-open", ConfirmedAt.AddHours(3), AllocationDisputeResolutionOutcome.Upheld, reversed: false, corrected: false);
        await SeedAsync(organizationId, "upheld-reversed", ConfirmedAt.AddHours(4), AllocationDisputeResolutionOutcome.Upheld, reversed: true, corrected: false);
        await SeedAsync(organizationId, "upheld-linked", ConfirmedAt.AddHours(5), AllocationDisputeResolutionOutcome.Upheld, reversed: false, corrected: true);
        await SeedAsync(organizationId, "not-upheld", ConfirmedAt.AddHours(6), AllocationDisputeResolutionOutcome.NotUpheld, reversed: false, corrected: false);
        await SeedAsync(foreignId, "foreign", ConfirmedAt.AddHours(7), decision: null, reversed: false, corrected: false);

        using FinOpsDeskApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient client = factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));
        using HttpClient foreign = factory.CreateApiClient(TestAccessTokens.Create(foreignId.Value));

        using JsonDocument summary = await ReadAsync(client, "/api/allocation-disputes/summary");
        Assert.Equal(2, summary.RootElement.GetProperty("needsDecision").GetInt64());
        Assert.Equal(1, summary.RootElement.GetProperty("needsCorrection").GetInt64());

        using JsonDocument decisionPage = await ReadAsync(
            client,
            "/api/allocation-disputes/queue?queue=needs-decision&page=1&pageSize=1");
        Assert.Equal(2, decisionPage.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Equal(1, decisionPage.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(earlier, decisionPage.RootElement.GetProperty("items")[0].GetProperty("disputeId").GetGuid());

        using JsonDocument correctionPage = await ReadAsync(
            client,
            "/api/allocation-disputes/queue?queue=needs-correction&page=1&pageSize=1");
        Assert.Equal(1, correctionPage.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Equal("upheld-open", correctionPage.RootElement.GetProperty("items")[0].GetProperty("statement").GetString());

        using JsonDocument foreignSummary = await ReadAsync(foreign, "/api/allocation-disputes/summary");
        Assert.Equal(1, foreignSummary.RootElement.GetProperty("needsDecision").GetInt64());
        Assert.Equal(0, foreignSummary.RootElement.GetProperty("needsCorrection").GetInt64());

        using JsonDocument registration = await ReadAsync(client, "/api/allocation-disputes?page=1&pageSize=1");
        Assert.Equal(6, registration.RootElement.GetProperty("totalCount").GetInt64());
        HttpResponseMessage rejected = await client.GetAsync("/api/allocation-disputes?queue=needs-decision");
        string rejectedBody = await rejected.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(rejectedBody);
        Assert.Equal("reconciliation.allocation_dispute.invalid_request", problem.RootElement.GetProperty("code").GetString());
        Assert.NotEqual(later, earlier);
    }

    private async Task<Guid> SeedAsync(
        OrganizationId organizationId,
        string statement,
        DateTimeOffset recordedAt,
        AllocationDisputeResolutionOutcome? decision,
        bool reversed,
        bool corrected)
    {
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 40m, "INV-" + statement);
        BankTransaction bank = PersistenceTestData.CreateBankTransaction(organizationId, 40m, reference: statement);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.AddRange(invoice, bank);
        await context.SaveChangesAsync();
        Assert.True(invoice.Allocate(new Money(10m, PersistenceTestData.Pln)).IsSuccess);
        Assert.True(bank.Allocate(new Money(10m, PersistenceTestData.Pln)).IsSuccess);
        Allocation allocation = new(
            AllocationId.New(),
            organizationId,
            invoice.Id,
            bank.Id,
            new Money(10m, PersistenceTestData.Pln),
            ConfirmedAt,
            Guid.NewGuid(),
            AllocationIdempotencyOperation.ExplicitAmount);
        if (reversed)
        {
            Assert.True(allocation.Reverse("independent reversal", recordedAt).IsSuccess);
        }

        AllocationDispute dispute = AllocationDispute.Create(
            AllocationDisputeId.New(),
            organizationId,
            allocation.Id,
            statement,
            ReviewCaseActorSource.DevelopmentJwt,
            "operator-a",
            recordedAt,
            Guid.NewGuid());
        context.AddRange(allocation, dispute);
        if (decision is AllocationDisputeResolutionOutcome outcome)
        {
            context.AllocationDisputeResolutions.Add(AllocationDisputeResolution.Create(
                organizationId,
                dispute.Id,
                outcome,
                "recorded reason",
                ReviewCaseActorSource.DevelopmentJwt,
                "operator-a",
                recordedAt.AddMinutes(1),
                Guid.NewGuid()));
        }

        if (corrected)
        {
            context.AllocationDisputeCorrections.Add(AllocationDisputeCorrection.Create(
                organizationId,
                dispute.Id,
                allocation.Id,
                "stored correction",
                ReviewCaseActorSource.DevelopmentJwt,
                "operator-a",
                recordedAt.AddMinutes(2),
                Guid.NewGuid()));
        }

        await context.SaveChangesAsync();
        return dispute.Id.Value;
    }

    private static async Task<JsonDocument> ReadAsync(HttpClient client, string path)
    {
        HttpResponseMessage response = await client.GetAsync(path);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, path + " " + body);
        return JsonDocument.Parse(body);
    }
}