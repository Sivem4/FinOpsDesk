using System.Text.Json;
using System.Text.RegularExpressions;

using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace FinOpsDesk.E2ETests;

[Collection(E2ECollection.Name)]
public sealed class AllocationDisputeRegistrationRecoveryTests
{
    private const string AttemptKey = "finopsdesk.allocation-dispute-attempt";
    private const string Statement = "counterparty challenge";

    private static readonly Regex DisputeUrl = new(
        @"/api/allocations/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}/disputes$",
        RegexOptions.None);

    [Fact]
    public async Task Committed_allocation_dispute_survives_a_lost_response_and_replays_after_reversal()
    {
        await E2ETestRun.RunAsync(TimeSpan.FromSeconds(240), async host =>
        {
            Guid allocationId = await SeedConfirmedAllocationAsync(host.ConnectionString, host.OrganizationId);
            BalanceSnapshot before = await ReadBalancesAsync(host.ConnectionString, host.OrganizationId);
            DisputeProof proof = new();

            await host.SignInAsync();
            await host.Page.GetByRole(AriaRole.Link, new() { Name = "Allocation history", Exact = true }).ClickAsync();
            await host.Page.GetByText("E2E-DISPUTE-REG").First.WaitForAsync();
            await host.Page.GetByRole(AriaRole.Button, new()
            {
                Name = "Record dispute for allocation " + allocationId.ToString("D"),
                Exact = true,
            }).ClickAsync();
            await host.Page.GetByRole(AriaRole.Textbox, new() { Name = "Statement", Exact = true }).FillAsync(Statement);

            E2ECapturedRequest captured;
            await using (E2ELossSession loss = await E2ELossSession.ArmAsync(
                host.Page,
                DisputeUrl,
                () => HasSingleConfirmedDisputeAsync(host.ConnectionString, host.OrganizationId),
                (request, response) => ProveDisputeCommitAsync(host, request, response, allocationId, before, proof)))
            {
                await host.Page.GetByRole(AriaRole.Button, new() { Name = "Record dispute", Exact = true }).ClickAsync();
                captured = await loss.WaitAsync();
                Assert.Equal(1, loss.PostCount);
                Assert.Equal(0, loss.UnexpectedPosts);
                await Assertions.Expect(host.Page.GetByText(
                    "This dispute has no verified result. Sending it again uses the same request.").First).ToBeVisibleAsync();
                await Assertions.Expect(host.Page.GetByText("Dispute recorded.")).ToHaveCountAsync(0);
                await E2EBrowser.RequireStoredRequestAsync(host.Page, AttemptKey, captured);
            }

            await ReverseAllocationAsync(host.ConnectionString, host.OrganizationId);
            await using (E2EPostWatch watch = await E2ERecovery.WatchReloadAndSignInAsync(host, DisputeUrl))
            {
                await host.Page.GetByRole(AriaRole.Link, new() { Name = "Allocation history", Exact = true }).ClickAsync();
                await Assertions.Expect(host.Page.GetByRole(AriaRole.Button, new() { Name = "Send the same dispute again" })).ToBeVisibleAsync();
                await E2EBrowser.RequireStoredRequestAsync(host.Page, AttemptKey, captured);
                Assert.Equal(0, watch.Count);
            }

            await E2ELostResponse.ReplayAsync(
                host.Page,
                DisputeUrl,
                captured,
                () => host.Page.GetByRole(AriaRole.Button, new() { Name = "Send the same dispute again" }).ClickAsync(),
                response => RequireDisputeReplayAsync(response, proof));
            await Assertions.Expect(host.Page.GetByText("This dispute was already stored.")).ToBeVisibleAsync();
            await RequireSingleDisputeAsync(host.ConnectionString, host.OrganizationId, allocationId, captured.IdempotencyKey, proof);
            BalanceSnapshot after = await ReadBalancesAsync(host.ConnectionString, host.OrganizationId);
            Assert.NotEqual(before, after);
            Assert.Equal(AllocationStatus.Reversed, after.Status);
            Assert.Null(await E2EBrowser.ReadSessionAsync(host.Page, AttemptKey));
            host.AssertBrowserIsQuiet(allowAbortedRequests: true);
        });
    }

    private static async Task ProveDisputeCommitAsync(
        E2EHost host,
        E2ECapturedRequest request,
        IAPIResponse response,
        Guid allocationId,
        BalanceSnapshot before,
        DisputeProof proof)
    {
        E2EBrowser.RequireNoOrganizationId(request, host.OrganizationId);
        using JsonDocument document = JsonDocument.Parse(await response.TextAsync());
        JsonElement root = document.RootElement;
        string? id = root.GetProperty("disputeId").GetString();
        string? statement = root.GetProperty("statement").GetString();
        string? actorId = root.GetProperty("actorId").GetString();
        string? recordedAt = root.GetProperty("recordedAt").GetString();
        bool identity = response.Status == 201
            && Guid.TryParse(id, out Guid parsed)
            && parsed != Guid.Empty
            && string.Equals(root.GetProperty("allocationId").GetString(), allocationId.ToString("D"), StringComparison.OrdinalIgnoreCase)
            && string.Equals(statement, Statement, StringComparison.Ordinal)
            && string.Equals(actorId, E2EAccessToken.ActorId, StringComparison.Ordinal)
            && recordedAt is not null;
        proof.DisputeId = id ?? string.Empty;
        proof.ActorId = actorId ?? string.Empty;
        proof.RecordedAt = recordedAt ?? string.Empty;
        bool committed = await HasSingleConfirmedDisputeAsync(host.ConnectionString, host.OrganizationId);
        BalanceSnapshot during = await ReadBalancesAsync(host.ConnectionString, host.OrganizationId);
        if (!identity || !committed || during != before)
        {
            throw new E2ECommitUnprovenException(
                "The allocation dispute was not a committed HTTP 201 before the response was dropped.");
        }
    }

    private static async Task RequireDisputeReplayAsync(IAPIResponse response, DisputeProof proof)
    {
        using JsonDocument document = JsonDocument.Parse(await response.TextAsync());
        JsonElement root = document.RootElement;
        if (response.Status != 200
            || !string.Equals(root.GetProperty("disputeId").GetString(), proof.DisputeId, StringComparison.Ordinal)
            || !string.Equals(root.GetProperty("statement").GetString(), Statement, StringComparison.Ordinal)
            || !string.Equals(root.GetProperty("actorId").GetString(), proof.ActorId, StringComparison.Ordinal)
            || !string.Equals(root.GetProperty("recordedAt").GetString(), proof.RecordedAt, StringComparison.Ordinal))
        {
            throw new E2ECommitUnprovenException("The allocation dispute replay was not HTTP 200 for the original registration.");
        }
    }

    private static async Task<bool> HasSingleConfirmedDisputeAsync(string connectionString, Guid organizationId)
    {
        OrganizationId tenant = new(organizationId);
        await using ReconciliationDbContext context = E2EDatabase.CreateContext(connectionString);
        int disputes = await context.AllocationDisputes.CountAsync(item => item.OrganizationId == tenant);
        Allocation? allocation = await context.Allocations.SingleOrDefaultAsync(item => item.OrganizationId == tenant);
        return disputes == 1 && allocation is not null && allocation.Status == AllocationStatus.Confirmed;
    }

    private static async Task RequireSingleDisputeAsync(
        string connectionString,
        Guid organizationId,
        Guid allocationId,
        string idempotencyKey,
        DisputeProof proof)
    {
        OrganizationId tenant = new(organizationId);
        await using ReconciliationDbContext context = E2EDatabase.CreateContext(connectionString);
        AllocationDispute dispute = await context.AllocationDisputes.SingleAsync(item => item.OrganizationId == tenant);
        Assert.Equal(Guid.Parse(proof.DisputeId), dispute.Id.Value);
        Assert.Equal(allocationId, dispute.AllocationId.Value);
        Assert.Equal(Statement, dispute.Statement);
        Assert.Equal(ReviewCaseActorSource.DevelopmentJwt, dispute.ActorSource);
        Assert.Equal(proof.ActorId, dispute.ActorId);
        Assert.Equal(DateTimeOffset.Parse(proof.RecordedAt), dispute.RecordedAt);
        Assert.Equal(Guid.Parse(idempotencyKey), dispute.IdempotencyKey);
        Allocation allocation = await context.Allocations.SingleAsync(item => item.OrganizationId == tenant);
        Assert.Equal(AllocationStatus.Reversed, allocation.Status);
    }

    private static async Task<Guid> SeedConfirmedAllocationAsync(string connectionString, Guid organizationId)
    {
        OrganizationId tenant = new(organizationId);
        CurrencyCode pln = new("PLN");
        const decimal principal = 40.2500m;
        const decimal allocated = 10.0000m;
        Invoice invoice = new(InvoiceId.New(), tenant, "E2E-DISPUTE-REG", new Money(principal, pln));
        BankTransaction bank = new(
            BankTransactionId.New(),
            tenant,
            new Money(principal, pln),
            new DateOnly(2026, 11, 7),
            "E2E-DISPUTE-REG");
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
        await using ReconciliationDbContext context = E2EDatabase.CreateContext(connectionString);
        context.AddRange(invoice, bank, allocation);
        await context.SaveChangesAsync();
        return allocation.Id.Value;
    }

    private static async Task ReverseAllocationAsync(string connectionString, Guid organizationId)
    {
        OrganizationId tenant = new(organizationId);
        await using ReconciliationDbContext context = E2EDatabase.CreateContext(connectionString);
        Allocation allocation = await context.Allocations.SingleAsync(item => item.OrganizationId == tenant);
        Invoice invoice = await context.Invoices.SingleAsync(item => item.OrganizationId == tenant);
        BankTransaction bank = await context.BankTransactions.SingleAsync(item => item.OrganizationId == tenant);
        Result reversed = AllocationReversal.Apply(
            allocation,
            invoice,
            bank,
            "recorded before replay",
            new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero));
        Assert.True(reversed.IsSuccess);
        await context.SaveChangesAsync();
    }

    private static async Task<BalanceSnapshot> ReadBalancesAsync(string connectionString, Guid organizationId)
    {
        OrganizationId tenant = new(organizationId);
        await using ReconciliationDbContext context = E2EDatabase.CreateContext(connectionString);
        Invoice invoice = await context.Invoices.SingleAsync(item => item.OrganizationId == tenant);
        BankTransaction bank = await context.BankTransactions.SingleAsync(item => item.OrganizationId == tenant);
        Allocation allocation = await context.Allocations.SingleAsync(item => item.OrganizationId == tenant);
        return new BalanceSnapshot(invoice.AllocatedAmount, bank.AllocatedAmount, allocation.Status);
    }

    private sealed record BalanceSnapshot(Money InvoiceAllocated, Money BankAllocated, AllocationStatus Status);

    private sealed class DisputeProof
    {
        public string DisputeId { get; set; } = string.Empty;

        public string ActorId { get; set; } = string.Empty;

        public string RecordedAt { get; set; } = string.Empty;
    }
}