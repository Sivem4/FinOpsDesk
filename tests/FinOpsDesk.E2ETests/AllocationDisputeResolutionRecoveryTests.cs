using System.Text.Json;
using System.Text.RegularExpressions;

using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace FinOpsDesk.E2ETests;

[Collection(E2ECollection.Name)]
public sealed class AllocationDisputeResolutionRecoveryTests
{
    private const string AttemptKey = "finopsdesk.allocation-dispute-resolution-attempt";
    private const string Statement = "counterparty challenge";
    private const string Reason = "decision reason";

    private static readonly Regex ResolutionUrl = new(
        @"/api/allocation-disputes/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}/resolution$",
        RegexOptions.None);

    [Fact]
    public async Task Committed_allocation_dispute_resolution_survives_a_lost_response_and_replays_the_same_command()
    {
        await E2ETestRun.RunAsync(TimeSpan.FromSeconds(240), async host =>
        {
            Guid disputeId = await SeedConfirmedDisputeAsync(host.ConnectionString, host.OrganizationId);
            FinancialSnapshot before = await ReadFinancialSnapshotAsync(host.ConnectionString, host.OrganizationId);
            DecisionProof proof = new();

            await host.SignInAsync();
            await host.Page.GetByRole(AriaRole.Link, new() { Name = "Allocation disputes", Exact = true }).ClickAsync();
            await host.Page.GetByText(Statement).First.WaitForAsync();
            await host.Page.GetByRole(AriaRole.Button, new()
            {
                Name = "Show allocation dispute " + disputeId.ToString("D"),
                Exact = true,
            }).ClickAsync();
            await host.Page.GetByText("No decision is stored.").WaitForAsync();
            await host.Page.GetByRole(AriaRole.Radio, new() { Name = "Upheld", Exact = true }).ClickAsync();
            await host.Page.GetByRole(AriaRole.Textbox, new() { Name = "Reason", Exact = true }).FillAsync(Reason);

            E2ECapturedRequest captured;
            await using (E2ELossSession loss = await E2ELossSession.ArmAsync(
                host.Page,
                ResolutionUrl,
                () => HasSingleResolutionAsync(host.ConnectionString, host.OrganizationId),
                (request, response) => ProveDecisionCommitAsync(host, request, response, disputeId, before, proof)))
            {
                await host.Page.GetByRole(AriaRole.Button, new() { Name = "Record decision", Exact = true }).ClickAsync();
                captured = await loss.WaitAsync();
                Assert.Equal(1, loss.PostCount);
                Assert.Equal(0, loss.UnexpectedPosts);
                await Assertions.Expect(host.Page.GetByText(
                    "This decision has no verified result. Sending it again uses the same request.").First).ToBeVisibleAsync();
                await Assertions.Expect(host.Page.GetByText("Decyzja zapisana.")).ToHaveCountAsync(0);
                await E2EBrowser.RequireStoredRequestAsync(host.Page, AttemptKey, captured);
            }

            await using (E2EPostWatch watch = await E2ERecovery.WatchReloadAndSignInAsync(host, ResolutionUrl))
            {
                await host.Page.GetByRole(AriaRole.Link, new() { Name = "Allocation disputes", Exact = true }).ClickAsync();
                await Assertions.Expect(host.Page.GetByRole(AriaRole.Button, new() { Name = "Send the same decision again" })).ToBeVisibleAsync();
                await E2EBrowser.RequireStoredRequestAsync(host.Page, AttemptKey, captured);
                Assert.Equal(0, watch.Count);
            }

            await E2ELostResponse.ReplayAsync(
                host.Page,
                ResolutionUrl,
                captured,
                () => host.Page.GetByRole(AriaRole.Button, new() { Name = "Send the same decision again" }).ClickAsync(),
                response => RequireReplayAsync(response, proof));
            await Assertions.Expect(host.Page.GetByText("Ta decyzja była już zapisana.")).ToBeVisibleAsync();
            await RequireSingleResolutionAsync(
                host.ConnectionString,
                host.OrganizationId,
                disputeId,
                captured.IdempotencyKey,
                proof);
            FinancialSnapshot after = await ReadFinancialSnapshotAsync(host.ConnectionString, host.OrganizationId);
            AssertSameFinancialState(before, after);
            Assert.Null(await E2EBrowser.ReadSessionAsync(host.Page, AttemptKey));
            host.AssertBrowserIsQuiet(allowAbortedRequests: true);
        });
    }

    private static async Task ProveDecisionCommitAsync(
        E2EHost host,
        E2ECapturedRequest request,
        IAPIResponse response,
        Guid disputeId,
        FinancialSnapshot before,
        DecisionProof proof)
    {
        E2EBrowser.RequireNoOrganizationId(request, host.OrganizationId);
        using JsonDocument document = JsonDocument.Parse(await response.TextAsync());
        JsonElement root = document.RootElement;
        string? id = root.GetProperty("disputeId").GetString();
        string? outcome = root.GetProperty("outcome").GetString();
        string? reason = root.GetProperty("reason").GetString();
        string? actorId = root.GetProperty("actorId").GetString();
        string? decidedAt = root.GetProperty("decidedAt").GetString();
        bool identity = response.Status == 201
            && Guid.TryParse(id, out Guid parsed)
            && parsed == disputeId
            && string.Equals(outcome, "upheld", StringComparison.Ordinal)
            && string.Equals(reason, Reason, StringComparison.Ordinal)
            && string.Equals(actorId, E2EAccessToken.ActorId, StringComparison.Ordinal)
            && decidedAt is not null;
        proof.ActorId = actorId ?? string.Empty;
        proof.DecidedAt = decidedAt ?? string.Empty;
        bool committed = await HasSingleResolutionAsync(host.ConnectionString, host.OrganizationId);
        FinancialSnapshot during = await ReadFinancialSnapshotAsync(host.ConnectionString, host.OrganizationId);
        if (!identity || !committed || !SameFinancialState(before, during))
        {
            throw new E2ECommitUnprovenException(
                "The allocation dispute decision was not a committed HTTP 201 before the response was dropped.");
        }
    }

    private static async Task RequireReplayAsync(IAPIResponse response, DecisionProof proof)
    {
        Assert.Equal(200, response.Status);
        using JsonDocument document = JsonDocument.Parse(await response.TextAsync());
        JsonElement root = document.RootElement;
        Assert.Equal(proof.ActorId, root.GetProperty("actorId").GetString());
        Assert.Equal(proof.DecidedAt, root.GetProperty("decidedAt").GetString());
        Assert.Equal("upheld", root.GetProperty("outcome").GetString());
        Assert.Equal(Reason, root.GetProperty("reason").GetString());
    }

    private static async Task<Guid> SeedConfirmedDisputeAsync(string connectionString, Guid organizationId)
    {
        OrganizationId tenant = new(organizationId);
        CurrencyCode pln = new("PLN");
        const decimal principal = 40.2500m;
        const decimal allocated = 10.0000m;
        Invoice invoice = new(InvoiceId.New(), tenant, "E2E-DISPUTE-DECISION", new Money(principal, pln));
        BankTransaction bank = new(
            BankTransactionId.New(),
            tenant,
            new Money(principal, pln),
            new DateOnly(2026, 11, 7),
            "E2E-DISPUTE-DECISION");
        Assert.True(invoice.Allocate(new Money(allocated, pln)).IsSuccess);
        Assert.True(bank.Allocate(new Money(allocated, pln)).IsSuccess);
        Allocation allocation = new(
            AllocationId.New(),
            tenant,
            invoice.Id,
            bank.Id,
            new Money(allocated, pln),
            new DateTimeOffset(2026, 11, 7, 8, 0, 0, TimeSpan.Zero),
            Guid.NewGuid(),
            AllocationIdempotencyOperation.ExplicitAmount);
        AllocationDispute dispute = AllocationDispute.Create(
            AllocationDisputeId.New(),
            tenant,
            allocation.Id,
            Statement,
            ReviewCaseActorSource.DevelopmentJwt,
            E2EAccessToken.ActorId,
            new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero),
            Guid.NewGuid());
        await using ReconciliationDbContext context = E2EDatabase.CreateContext(connectionString);
        context.AddRange(invoice, bank, allocation, dispute);
        await context.SaveChangesAsync();
        return dispute.Id.Value;
    }

    private static async Task<bool> HasSingleResolutionAsync(string connectionString, Guid organizationId)
    {
        OrganizationId tenant = new(organizationId);
        await using ReconciliationDbContext context = E2EDatabase.CreateContext(connectionString);
        return await context.AllocationDisputeResolutions.CountAsync(item => item.OrganizationId == tenant) == 1;
    }

    private static async Task RequireSingleResolutionAsync(
        string connectionString,
        Guid organizationId,
        Guid disputeId,
        string idempotencyKey,
        DecisionProof proof)
    {
        OrganizationId tenant = new(organizationId);
        await using ReconciliationDbContext context = E2EDatabase.CreateContext(connectionString);
        AllocationDisputeResolution resolution = await context.AllocationDisputeResolutions.SingleAsync(item =>
            item.OrganizationId == tenant);
        Assert.Equal(disputeId, resolution.AllocationDisputeId.Value);
        Assert.Equal(AllocationDisputeResolutionOutcome.Upheld, resolution.Outcome);
        Assert.Equal(Reason, resolution.Reason);
        Assert.Equal(proof.ActorId, resolution.ActorId);
        Assert.Equal(Guid.Parse(idempotencyKey), resolution.IdempotencyKey);
        Assert.Equal(1, await context.AllocationDisputes.CountAsync(item => item.OrganizationId == tenant));
        Allocation allocation = await context.Allocations.SingleAsync(item => item.OrganizationId == tenant);
        Assert.Equal(AllocationStatus.Confirmed, allocation.Status);
    }

    private static async Task<FinancialSnapshot> ReadFinancialSnapshotAsync(string connectionString, Guid organizationId)
    {
        OrganizationId tenant = new(organizationId);
        await using ReconciliationDbContext context = E2EDatabase.CreateContext(connectionString);
        Invoice invoice = await context.Invoices.SingleAsync(item => item.OrganizationId == tenant);
        BankTransaction bank = await context.BankTransactions.SingleAsync(item => item.OrganizationId == tenant);
        Allocation allocation = await context.Allocations.SingleAsync(item => item.OrganizationId == tenant);
        int audits = await context.FinancialAuditEntries.CountAsync(item => item.OrganizationId == tenant);
        byte[] rowVersion = context.Entry(allocation).Property<byte[]>("RowVersion").CurrentValue ?? [];
        return new FinancialSnapshot(
            invoice.AllocatedAmount,
            bank.AllocatedAmount,
            allocation.Status,
            rowVersion.ToArray(),
            audits);
    }

    private static void AssertSameFinancialState(FinancialSnapshot before, FinancialSnapshot after)
    {
        Assert.True(SameFinancialState(before, after));
    }

    private static bool SameFinancialState(FinancialSnapshot before, FinancialSnapshot after)
    {
        return before.InvoiceAllocated == after.InvoiceAllocated
            && before.BankAllocated == after.BankAllocated
            && before.Status == after.Status
            && before.AuditCount == after.AuditCount
            && before.RowVersion.AsSpan().SequenceEqual(after.RowVersion);
    }

    private sealed record FinancialSnapshot(
        Money InvoiceAllocated,
        Money BankAllocated,
        AllocationStatus Status,
        byte[] RowVersion,
        int AuditCount);

    private sealed class DecisionProof
    {
        public string ActorId { get; set; } = string.Empty;

        public string DecidedAt { get; set; } = string.Empty;
    }
}