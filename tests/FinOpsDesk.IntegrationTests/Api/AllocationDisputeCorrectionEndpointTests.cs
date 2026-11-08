using System.Net;
using System.Text;
using System.Text.Json;

using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Repositories;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class AllocationDisputeCorrectionEndpointTests
{
    private static readonly DateTimeOffset ConfirmedAt = DateTimeOffset.Parse("2026-11-08T09:00:00Z");

    private readonly SqlServerFixture _fixture;

    public AllocationDisputeCorrectionEndpointTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task New_correction_returns_a_json_number_and_replay_keeps_the_original_actor()
    {
        OrganizationId organizationId = OrganizationId.New();
        AllocationDispute dispute = await SeedDisputeAsync(organizationId, upheld: true);
        using FinOpsDeskApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient client = factory.CreateApiClient(TestAccessTokens.CreateForActor(organizationId.Value, "operator-a"));
        Guid key = Guid.NewGuid();

        HttpResponseMessage created = await client.SendAsync(Correct(dispute.Id.Value, " Customer evidence accepted ", key));
        string createdBody = await created.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using JsonDocument createdDocument = JsonDocument.Parse(createdBody);
        JsonElement amount = createdDocument.RootElement.GetProperty("amount").GetProperty("amount");
        Assert.Equal(JsonValueKind.Number, amount.ValueKind);
        Assert.Equal(125.50m, amount.GetDecimal());
        Assert.Equal("PLN", createdDocument.RootElement.GetProperty("amount").GetProperty("currency").GetString());
        Assert.False(createdDocument.RootElement.GetProperty("replayed").GetBoolean());
        Assert.Equal(dispute.Id.Value, createdDocument.RootElement.GetProperty("disputeId").GetGuid());
        Assert.Equal("operator-a", createdDocument.RootElement.GetProperty("actorId").GetString());
        Assert.False(createdDocument.RootElement.TryGetProperty("organizationId", out _));
        Assert.False(createdDocument.RootElement.TryGetProperty("idempotencyKey", out _));

        using HttpClient other = factory.CreateApiClient(TestAccessTokens.CreateForActor(organizationId.Value, "operator-b"));
        HttpResponseMessage replay = await other.SendAsync(Correct(dispute.Id.Value, "Customer evidence accepted", key));
        using JsonDocument replayDocument = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(replayDocument.RootElement.GetProperty("replayed").GetBoolean());
        Assert.Equal("operator-a", replayDocument.RootElement.GetProperty("actorId").GetString());

        HttpResponseMessage read = await client.GetAsync(Path(dispute.Id.Value));
        using JsonDocument readDocument = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(125.50m, readDocument.RootElement.GetProperty("correction").GetProperty("amount").GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task Missing_decision_not_upheld_unknown_and_foreign_do_not_move_money()
    {
        OrganizationId organizationId = OrganizationId.New();
        OrganizationId otherOrganization = OrganizationId.New();
        AllocationDispute open = await SeedDisputeAsync(organizationId, upheld: false);
        AllocationDispute rejected = await SeedDisputeAsync(organizationId, upheld: false, notUpheld: true);
        AllocationDispute foreign = await SeedDisputeAsync(otherOrganization, upheld: true);
        using FinOpsDeskApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient client = factory.CreateApiClient(TestAccessTokens.CreateForActor(organizationId.Value, "operator-a"));

        HttpResponseMessage noDecision = await client.SendAsync(Correct(open.Id.Value, "Customer evidence accepted", Guid.NewGuid()));
        HttpResponseMessage notUpheld = await client.SendAsync(Correct(rejected.Id.Value, "Customer evidence accepted", Guid.NewGuid()));
        HttpResponseMessage missing = await client.SendAsync(Correct(Guid.NewGuid(), "Customer evidence accepted", Guid.NewGuid()));
        HttpResponseMessage foreignWrite = await client.SendAsync(Correct(foreign.Id.Value, "Customer evidence accepted", Guid.NewGuid()));
        using HttpClient reader = factory.CreateApiClient(TestAccessTokens.CreateForActor(organizationId.Value, null));
        HttpResponseMessage noActor = await reader.SendAsync(Correct(open.Id.Value, "Customer evidence accepted", Guid.NewGuid()));

        Assert.Equal("reconciliation.allocation_dispute.correction.decision_required", await ReadCodeAsync(noDecision));
        Assert.Equal("reconciliation.allocation_dispute.correction.not_upheld", await ReadCodeAsync(notUpheld));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignWrite.StatusCode);
        Assert.Equal("reconciliation.allocation_dispute.not_found", await ReadCodeAsync(missing));
        Assert.Equal("reconciliation.allocation_dispute.not_found", await ReadCodeAsync(foreignWrite));
        Assert.Equal(HttpStatusCode.Forbidden, noActor.StatusCode);
        Assert.Equal("reconciliation.actor.required", await ReadCodeAsync(noActor));

        HttpResponseMessage emptyRead = await client.GetAsync(Path(open.Id.Value));
        using JsonDocument emptyDocument = JsonDocument.Parse(await emptyRead.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, emptyDocument.RootElement.GetProperty("correction").ValueKind);
        HttpResponseMessage foreignRead = await client.GetAsync(Path(foreign.Id.Value));
        Assert.Equal("reconciliation.allocation_dispute.not_found", await ReadCodeAsync(foreignRead));

        await using ReconciliationDbContext context = _fixture.CreateContext();
        Assert.Equal(0, await context.AllocationDisputeCorrections.CountAsync(
            row => row.OrganizationId == organizationId || row.OrganizationId == otherOrganization));
        Assert.Equal(0, await context.FinancialAuditEntries.CountAsync(
            entry => entry.Action == FinancialAuditAction.Reversal
                && (entry.OrganizationId == organizationId || entry.OrganizationId == otherOrganization)));
    }

    [Fact]
    public async Task Invalid_key_duplicate_field_and_unknown_field_are_rejected()
    {
        OrganizationId organizationId = OrganizationId.New();
        AllocationDispute dispute = await SeedDisputeAsync(organizationId, upheld: true);
        using FinOpsDeskApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient client = factory.CreateApiClient(TestAccessTokens.CreateForActor(organizationId.Value, "operator-a"));

        HttpResponseMessage missingKey = await client.SendAsync(CorrectRaw(dispute.Id.Value, "{\"reason\":\"Customer evidence accepted\"}", null));
        HttpResponseMessage plusAlias = await client.SendAsync(CorrectRaw(
            dispute.Id.Value,
            "{\"reason\":\"Customer evidence accepted\"}",
            "+00000000-0000-0000-0000-000000000001"));
        HttpResponseMessage unknown = await client.SendAsync(CorrectRaw(
            dispute.Id.Value,
            "{\"reason\":\"Customer evidence accepted\",\"allocationId\":\"" + Guid.NewGuid().ToString("D") + "\"}",
            Guid.NewGuid().ToString("D")));
        HttpResponseMessage duplicate = await client.SendAsync(CorrectRaw(
            dispute.Id.Value,
            "{\"reason\":\"one\",\"reason\":\"two\"}",
            Guid.NewGuid().ToString("D")));

        Assert.Equal("reconciliation.allocation_dispute.correction.idempotency_key_required", await ReadCodeAsync(missingKey));
        Assert.Equal("reconciliation.allocation_dispute.correction.idempotency_key_invalid", await ReadCodeAsync(plusAlias));
        Assert.Equal("reconciliation.allocation_dispute.correction.invalid_request", await ReadCodeAsync(unknown));
        Assert.Equal("reconciliation.allocation_dispute.correction.invalid_request", await ReadCodeAsync(duplicate));
    }

    private async Task<AllocationDispute> SeedDisputeAsync(
        OrganizationId organizationId,
        bool upheld,
        bool notUpheld = false)
    {
        Invoice invoice = new(InvoiceId.New(), organizationId, "INV-" + Guid.NewGuid().ToString("N")[..8], new Money(125.50m, PersistenceTestData.Pln));
        BankTransaction bank = new(
            BankTransactionId.New(),
            organizationId,
            new Money(125.50m, PersistenceTestData.Pln),
            new DateOnly(2026, 11, 8),
            invoice.InvoiceNumber);
        Assert.True(AllocationConfirmation.Apply(invoice, bank, new Money(125.50m, PersistenceTestData.Pln)).IsSuccess);
        Allocation allocation = new(
            AllocationId.New(),
            organizationId,
            invoice.Id,
            bank.Id,
            new Money(125.50m, PersistenceTestData.Pln),
            ConfirmedAt,
            Guid.NewGuid(),
            AllocationIdempotencyOperation.ExactMatch);
        AllocationDispute dispute = AllocationDispute.Create(
            AllocationDisputeId.New(),
            organizationId,
            allocation.Id,
            "The counterparty challenges this allocation.",
            ReviewCaseActorSource.DevelopmentJwt,
            "registrar",
            ConfirmedAt,
            Guid.NewGuid());
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.AddRange(invoice, bank, allocation, dispute);
        new FinancialAuditWriter(context).AppendConfirmation(allocation, "confirm-operator", null);
        if (upheld || notUpheld)
        {
            context.Add(AllocationDisputeResolution.Create(
                organizationId,
                dispute.Id,
                notUpheld
                    ? AllocationDisputeResolutionOutcome.NotUpheld
                    : AllocationDisputeResolutionOutcome.Upheld,
                "The recorded decision.",
                ReviewCaseActorSource.DevelopmentJwt,
                "judge",
                ConfirmedAt.AddHours(1),
                Guid.NewGuid()));
        }

        await context.SaveChangesAsync();
        return dispute;
    }

    private static string Path(Guid disputeId)
    {
        return "/api/allocation-disputes/" + disputeId.ToString("D") + "/correction";
    }

    private static HttpRequestMessage Correct(Guid disputeId, string reason, Guid key)
    {
        return CorrectRaw(
            disputeId,
            "{\"reason\":" + JsonSerializer.Serialize(reason) + "}",
            key.ToString("D"));
    }

    private static HttpRequestMessage CorrectRaw(Guid disputeId, string json, string? key)
    {
        HttpRequestMessage request = new(HttpMethod.Post, Path(disputeId))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        return request;
    }

    private static async Task<string> ReadCodeAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString() ?? string.Empty;
    }
}