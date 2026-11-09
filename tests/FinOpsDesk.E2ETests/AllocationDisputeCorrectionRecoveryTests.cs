using System.Text.Json;
using System.Text.RegularExpressions;

using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace FinOpsDesk.E2ETests;

[Collection(E2ECollection.Name)]
public sealed class AllocationDisputeCorrectionRecoveryTests
{
    private const string AttemptKey = "finopsdesk.allocation-dispute-correction-attempt";
    private const string Statement = "counterparty settlement";
    private const string Reason = "settlement correction";

    private static readonly Regex CorrectionUrl = new(
        @"/api/allocation-disputes/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}/correction$",
        RegexOptions.None);

    [Fact]
    public async Task Committed_allocation_dispute_correction_survives_a_lost_response_and_replays_the_same_command()
    {
        await E2ETestRun.RunAsync(TimeSpan.FromSeconds(240), async host =>
        {
            Guid disputeId = await SeedUpheldDisputeAsync(host.ConnectionString, host.OrganizationId);
            await host.SignInAsync();
            await host.Page.GetByRole(AriaRole.Link, new() { Name = "Allocation disputes", Exact = true }).ClickAsync();
            await host.Page.GetByText(Statement).First.WaitForAsync();
            await host.Page.GetByRole(AriaRole.Button, new()
            {
                Name = "Show allocation dispute " + disputeId.ToString("D"),
                Exact = true,
            }).ClickAsync();
            ILocator correction = host.Page.GetByRole(AriaRole.Region, new() { Name = "Allocation dispute correction", Exact = true });
            await correction.GetByRole(AriaRole.Button, new() { Name = "Reverse the full allocation", Exact = true }).WaitForAsync();
            await correction.GetByRole(AriaRole.Textbox, new() { Name = "Reason", Exact = true }).FillAsync(Reason);

            E2ECapturedRequest captured;
            await using (E2ELossSession loss = await E2ELossSession.ArmAsync(
                host.Page,
                CorrectionUrl,
                () => HasSingleCorrectionAsync(host.ConnectionString, host.OrganizationId),
                (request, response) => ProveCorrectionCommitAsync(host, request, response, disputeId)))
            {
                await correction.GetByRole(AriaRole.Button, new() { Name = "Reverse the full allocation", Exact = true }).ClickAsync();
                captured = await loss.WaitAsync();
                Assert.Equal(1, loss.PostCount);
                Assert.Equal(0, loss.UnexpectedPosts);
                await Assertions.Expect(host.Page.GetByText(
                    "This correction has no verified result. Sending it again uses the same request.").First).ToBeVisibleAsync();
                await E2EBrowser.RequireStoredRequestAsync(host.Page, AttemptKey, captured);
            }

            await using (E2EPostWatch watch = await E2ERecovery.WatchReloadAndSignInAsync(host, CorrectionUrl))
            {
                await host.Page.GetByRole(AriaRole.Link, new() { Name = "Allocation disputes", Exact = true }).ClickAsync();
                await Assertions.Expect(host.Page.GetByRole(AriaRole.Button, new() { Name = "Send the same correction again" })).ToBeVisibleAsync();
                await E2EBrowser.RequireStoredRequestAsync(host.Page, AttemptKey, captured);
                Assert.Equal(0, watch.Count);
            }

            await E2ELostResponse.ReplayAsync(
                host.Page,
                CorrectionUrl,
                captured,
                () => host.Page.GetByRole(AriaRole.Button, new() { Name = "Send the same correction again" }).ClickAsync(),
                RequireReplayAsync);
            await Assertions.Expect(host.Page.GetByText("This correction was already stored.")).ToBeVisibleAsync();
            await RequireSingleCorrectionAsync(host.ConnectionString, host.OrganizationId, disputeId, captured.IdempotencyKey);
            Assert.Null(await E2EBrowser.ReadSessionAsync(host.Page, AttemptKey));
            host.AssertBrowserIsQuiet(allowAbortedRequests: true);
        });
    }

    private static async Task ProveCorrectionCommitAsync(
        E2EHost host,
        E2ECapturedRequest request,
        IAPIResponse response,
        Guid disputeId)
    {
        E2EBrowser.RequireNoOrganizationId(request, host.OrganizationId);
        using JsonDocument document = JsonDocument.Parse(await response.TextAsync());
        JsonElement root = document.RootElement;
        bool identity = response.Status == 200
            && root.GetProperty("disputeId").GetGuid() == disputeId
            && string.Equals(root.GetProperty("status").GetString(), "reversed", StringComparison.Ordinal)
            && string.Equals(root.GetProperty("reason").GetString(), Reason, StringComparison.Ordinal)
            && root.GetProperty("replayed").GetBoolean() == false;
        bool committed = await HasSingleCorrectionAsync(host.ConnectionString, host.OrganizationId);
        if (!identity || !committed)
        {
            throw new E2ECommitUnprovenException(
                "The allocation dispute correction was not a committed HTTP 200 before the response was dropped.");
        }
    }

    private static async Task RequireReplayAsync(IAPIResponse response)
    {
        Assert.Equal(200, response.Status);
        using JsonDocument document = JsonDocument.Parse(await response.TextAsync());
        Assert.True(document.RootElement.GetProperty("replayed").GetBoolean());
        Assert.Equal(Reason, document.RootElement.GetProperty("reason").GetString());
    }

    private static async Task<Guid> SeedUpheldDisputeAsync(string connectionString, Guid organizationId)
    {
        OrganizationId tenant = new(organizationId);
        CurrencyCode pln = new("PLN");
        const decimal principal = 40.2500m;
        const decimal allocated = 10.0000m;
        DateTimeOffset confirmedAt = new(2026, 11, 8, 8, 0, 0, TimeSpan.Zero);
        Invoice invoice = new(InvoiceId.New(), tenant, "E2E-DISPUTE-CORRECTION", new Money(principal, pln));
        BankTransaction bank = new(
            BankTransactionId.New(),
            tenant,
            new Money(principal, pln),
            new DateOnly(2026, 11, 8),
            "E2E-DISPUTE-CORRECTION");
        Assert.True(invoice.Allocate(new Money(allocated, pln)).IsSuccess);
        Assert.True(bank.Allocate(new Money(allocated, pln)).IsSuccess);
        Allocation allocation = new(
            AllocationId.New(),
            tenant,
            invoice.Id,
            bank.Id,
            new Money(allocated, pln),
            confirmedAt,
            Guid.NewGuid(),
            AllocationIdempotencyOperation.ExplicitAmount);
        AllocationDispute dispute = AllocationDispute.Create(
            AllocationDisputeId.New(),
            tenant,
            allocation.Id,
            Statement,
            ReviewCaseActorSource.DevelopmentJwt,
            E2EAccessToken.ActorId,
            confirmedAt.AddHours(1),
            Guid.NewGuid());
        AllocationDisputeResolution resolution = AllocationDisputeResolution.Create(
            tenant,
            dispute.Id,
            AllocationDisputeResolutionOutcome.Upheld,
            "decision reason",
            ReviewCaseActorSource.DevelopmentJwt,
            E2EAccessToken.ActorId,
            confirmedAt.AddHours(2),
            Guid.NewGuid());
        await using ReconciliationDbContext context = E2EDatabase.CreateContext(connectionString);
        context.AddRange(invoice, bank, allocation, dispute, resolution);
        await context.SaveChangesAsync();
        return dispute.Id.Value;
    }

    private static async Task<bool> HasSingleCorrectionAsync(string connectionString, Guid organizationId)
    {
        OrganizationId tenant = new(organizationId);
        await using ReconciliationDbContext context = E2EDatabase.CreateContext(connectionString);
        int corrections = await context.AllocationDisputeCorrections.CountAsync(item => item.OrganizationId == tenant);
        int reversals = await context.Allocations.CountAsync(item =>
            item.OrganizationId == tenant && item.Status == AllocationStatus.Reversed);
        int audits = await context.FinancialAuditEntries.CountAsync(item =>
            item.OrganizationId == tenant && item.Action == FinancialAuditAction.Reversal);
        return corrections == 1 && reversals == 1 && audits == 1;
    }

    private static async Task RequireSingleCorrectionAsync(
        string connectionString,
        Guid organizationId,
        Guid disputeId,
        string idempotencyKey)
    {
        OrganizationId tenant = new(organizationId);
        await using ReconciliationDbContext context = E2EDatabase.CreateContext(connectionString);
        AllocationDisputeCorrection correction = await context.AllocationDisputeCorrections.SingleAsync(item =>
            item.OrganizationId == tenant);
        Assert.Equal(disputeId, correction.AllocationDisputeId.Value);
        Assert.Equal(Reason, correction.Reason);
        Assert.Equal(Guid.Parse(idempotencyKey), correction.IdempotencyKey);
        Allocation allocation = await context.Allocations.SingleAsync(item => item.OrganizationId == tenant);
        Assert.Equal(AllocationStatus.Reversed, allocation.Status);
        Assert.Equal(1, await context.FinancialAuditEntries.CountAsync(item =>
            item.OrganizationId == tenant && item.Action == FinancialAuditAction.Reversal));
    }
}