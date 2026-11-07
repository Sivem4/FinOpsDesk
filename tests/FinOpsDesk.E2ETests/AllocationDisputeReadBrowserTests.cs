using System.Threading;

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
public sealed class AllocationDisputeReadBrowserTests
{
    private const string Statement = "counterparty challenge";

    private static readonly DateTimeOffset RecordedAt = new(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Reads_a_stored_allocation_dispute_after_reversal_without_posting()
    {
        await E2ETestRun.RunAsync(TimeSpan.FromSeconds(180), async host =>
        {
            StoredDispute seeded = await SeedReversedAllocationAsync(host.ConnectionString, host.OrganizationId);
            await host.SignInAsync();
            int[] posts = [0];
            host.Page.Request += (_, request) =>
            {
                if (string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref posts[0]);
                }
            };

            await host.Page.GetByRole(AriaRole.Link, new() { Name = "Allocation disputes", Exact = true }).ClickAsync();
            await host.Page.GetByText(Statement, new() { Exact = true }).First.WaitForAsync();
            await host.Page.GetByRole(AriaRole.Button, new()
            {
                Name = "Show allocation dispute " + seeded.DisputeId.ToString("D"),
                Exact = true,
            }).ClickAsync();

            ILocator detail = host.Page.GetByRole(AriaRole.Region, new() { Name = "Allocation dispute registration" });
            await Assertions.Expect(detail.GetByText(Statement, new() { Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(detail.GetByText("Actor source developmentJwt", new() { Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(detail.GetByText(E2EAccessToken.ActorId, new() { Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(detail.GetByText("2026-11-07T09:00:00", new() { Exact = false })).ToBeVisibleAsync();
            Assert.Equal(0, Volatile.Read(ref posts[0]));

            StoredDispute after = await ReadDisputeAsync(host.ConnectionString, host.OrganizationId);
            Assert.Equal(seeded, after);
            host.AssertBrowserIsQuiet();
        });
    }

    private static async Task<StoredDispute> SeedReversedAllocationAsync(string connectionString, Guid organizationId)
    {
        OrganizationId tenant = new(organizationId);
        CurrencyCode pln = new("PLN");
        const decimal principal = 40.2500m;
        const decimal allocated = 10.0000m;
        Invoice invoice = new(InvoiceId.New(), tenant, "E2E-DISPUTE-READ", new Money(principal, pln));
        BankTransaction bank = new(
            BankTransactionId.New(),
            tenant,
            new Money(principal, pln),
            new DateOnly(2026, 11, 7),
            "E2E-DISPUTE-READ");
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
            RecordedAt,
            Guid.NewGuid());
        await using (ReconciliationDbContext context = E2EDatabase.CreateContext(connectionString))
        {
            context.AddRange(invoice, bank, allocation, dispute);
            await context.SaveChangesAsync();
        }

        await using (ReconciliationDbContext context = E2EDatabase.CreateContext(connectionString))
        {
            Allocation storedAllocation = await context.Allocations.SingleAsync(item => item.OrganizationId == tenant);
            Invoice storedInvoice = await context.Invoices.SingleAsync(item => item.OrganizationId == tenant);
            BankTransaction storedBank = await context.BankTransactions.SingleAsync(item =>
                item.OrganizationId == tenant);
            Result reversed = AllocationReversal.Apply(
                storedAllocation,
                storedInvoice,
                storedBank,
                "recorded before reversal",
                new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero));
            Assert.True(reversed.IsSuccess);
            await context.SaveChangesAsync();
            Assert.Equal(AllocationStatus.Reversed, storedAllocation.Status);
        }

        return await ReadDisputeAsync(connectionString, organizationId);
    }

    private static async Task<StoredDispute> ReadDisputeAsync(string connectionString, Guid organizationId)
    {
        OrganizationId tenant = new(organizationId);
        await using ReconciliationDbContext context = E2EDatabase.CreateContext(connectionString);
        AllocationDispute dispute = await context.AllocationDisputes.SingleAsync(item => item.OrganizationId == tenant);
        return new StoredDispute(
            dispute.Id.Value,
            dispute.AllocationId.Value,
            dispute.Statement,
            dispute.ActorSource,
            dispute.ActorId,
            dispute.RecordedAt);
    }

    private sealed record StoredDispute(
        Guid DisputeId,
        Guid AllocationId,
        string Statement,
        ReviewCaseActorSource ActorSource,
        string ActorId,
        DateTimeOffset RecordedAt);
}