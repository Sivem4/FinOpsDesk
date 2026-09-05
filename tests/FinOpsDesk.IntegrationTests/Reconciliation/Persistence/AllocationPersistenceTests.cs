using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class AllocationPersistenceTests
{
    private readonly SqlServerFixture _fixture;

    public AllocationPersistenceTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Confirmed_allocation_round_trips_status_amount_and_timestamps()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            1000m);
        var confirmedAt = new DateTimeOffset(2026, 9, 4, 18, 0, 0, TimeSpan.Zero);
        var allocation = new Allocation(
            AllocationId.New(),
            organizationId,
            invoice.Id,
            bankTransaction.Id,
            new Money(250m, PersistenceTestData.Pln),
            confirmedAt);

        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.Invoices.Add(invoice);
            context.BankTransactions.Add(bankTransaction);
            context.Allocations.Add(allocation);
            await context.SaveChangesAsync();
        }

        await using ReconciliationDbContext reloadContext = _fixture.CreateContext();
        Allocation loaded = await reloadContext.Allocations.SingleAsync(
            stored => stored.Id == allocation.Id);

        Assert.Equal(allocation.Id, loaded.Id);
        Assert.Equal(organizationId, loaded.OrganizationId);
        Assert.Equal(invoice.Id, loaded.InvoiceId);
        Assert.Equal(bankTransaction.Id, loaded.BankTransactionId);
        Assert.Equal(new Money(250m, PersistenceTestData.Pln), loaded.Amount);
        Assert.Equal(AllocationStatus.Confirmed, loaded.Status);
        Assert.Equal(confirmedAt, loaded.ConfirmedAt);
        Assert.Null(loaded.ReversedAt);
        Assert.Null(loaded.ReversalReason);
    }

    [Fact]
    public async Task Reversed_allocation_round_trips_status_and_reversal_fields()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            1000m);
        var confirmedAt = new DateTimeOffset(2026, 9, 4, 18, 0, 0, TimeSpan.Zero);
        var reversedAt = new DateTimeOffset(2026, 9, 5, 9, 30, 0, TimeSpan.Zero);
        var allocation = new Allocation(
            AllocationId.New(),
            organizationId,
            invoice.Id,
            bankTransaction.Id,
            new Money(250m, PersistenceTestData.Pln),
            confirmedAt);
        allocation.Reverse("Customer requested reversal", reversedAt);

        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.Invoices.Add(invoice);
            context.BankTransactions.Add(bankTransaction);
            context.Allocations.Add(allocation);
            await context.SaveChangesAsync();
        }

        await using ReconciliationDbContext reloadContext = _fixture.CreateContext();
        Allocation loaded = await reloadContext.Allocations.SingleAsync(
            stored => stored.Id == allocation.Id);

        Assert.Equal(AllocationStatus.Reversed, loaded.Status);
        Assert.Equal(confirmedAt, loaded.ConfirmedAt);
        Assert.Equal(reversedAt, loaded.ReversedAt);
        Assert.Equal("Customer requested reversal", loaded.ReversalReason);
    }
}