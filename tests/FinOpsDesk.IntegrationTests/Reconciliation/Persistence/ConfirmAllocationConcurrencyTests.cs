using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Application.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class ConfirmAllocationConcurrencyTests
{
    private static readonly DateTimeOffset ConfirmedAt =
        new(2026, 9, 5, 14, 0, 0, TimeSpan.Zero);

    private readonly SqlServerFixture _fixture;

    public ConfirmAllocationConcurrencyTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Stale_confirm_allocation_conflicts_and_does_not_partially_commit()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice seededInvoice = PersistenceTestData.CreateInvoice(organizationId, 1000m);
        BankTransaction seededBankTransaction =
            PersistenceTestData.CreateBankTransaction(organizationId, 1000m);
        var amountA = new Money(300m, PersistenceTestData.Pln);
        var amountB = new Money(400m, PersistenceTestData.Pln);
        var allocationA = new Allocation(
            AllocationId.New(),
            organizationId,
            seededInvoice.Id,
            seededBankTransaction.Id,
            amountA,
            ConfirmedAt);
        var allocationB = new Allocation(
            AllocationId.New(),
            organizationId,
            seededInvoice.Id,
            seededBankTransaction.Id,
            amountB,
            ConfirmedAt);

        await using (ReconciliationDbContext seedContext = _fixture.CreateContext())
        {
            seedContext.Invoices.Add(seededInvoice);
            seedContext.BankTransactions.Add(seededBankTransaction);
            await seedContext.SaveChangesAsync();
        }

        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scopeA = provider.CreateAsyncScope();
        await using AsyncServiceScope scopeB = provider.CreateAsyncScope();

        Invoice invoiceA = await LoadInvoiceAsync(scopeA.ServiceProvider, organizationId, seededInvoice.Id);
        BankTransaction bankTransactionA = await LoadBankTransactionAsync(
            scopeA.ServiceProvider,
            organizationId,
            seededBankTransaction.Id);
        Invoice invoiceB = await LoadInvoiceAsync(scopeB.ServiceProvider, organizationId, seededInvoice.Id);
        BankTransaction bankTransactionB = await LoadBankTransactionAsync(
            scopeB.ServiceProvider,
            organizationId,
            seededBankTransaction.Id);

        Assert.True(invoiceA.Allocate(amountA).IsSuccess);
        Assert.True(bankTransactionA.Allocate(amountA).IsSuccess);
        scopeA.ServiceProvider.GetRequiredService<IAllocationRepository>().Add(allocationA);

        Assert.True(invoiceB.Allocate(amountB).IsSuccess);
        Assert.True(bankTransactionB.Allocate(amountB).IsSuccess);
        scopeB.ServiceProvider.GetRequiredService<IAllocationRepository>().Add(allocationB);

        await scopeA.ServiceProvider.GetRequiredService<IUnitOfWork>()
            .SaveChangesAsync(CancellationToken.None);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => scopeB.ServiceProvider.GetRequiredService<IUnitOfWork>()
                .SaveChangesAsync(CancellationToken.None));

        await using ReconciliationDbContext reloadContext = _fixture.CreateContext();
        Invoice loadedInvoice = await reloadContext.Invoices.SingleAsync(
            stored => stored.Id == seededInvoice.Id);
        BankTransaction loadedBankTransaction =
            await reloadContext.BankTransactions.SingleAsync(
                stored => stored.Id == seededBankTransaction.Id);
        List<Allocation> allocations = await reloadContext.Allocations
            .Where(allocation => allocation.InvoiceId == seededInvoice.Id)
            .ToListAsync();

        Assert.Equal(amountA, loadedInvoice.AllocatedAmount);
        Assert.Equal(amountA, loadedBankTransaction.AllocatedAmount);
        Allocation persistedAllocation = Assert.Single(allocations);
        Assert.Equal(allocationA.Id, persistedAllocation.Id);
        Assert.DoesNotContain(allocations, allocation => allocation.Id == allocationB.Id);
    }

    private static async Task<Invoice> LoadInvoiceAsync(
        IServiceProvider services,
        OrganizationId organizationId,
        InvoiceId invoiceId)
    {
        Invoice? invoice = await services.GetRequiredService<IInvoiceRepository>()
            .GetByIdAsync(organizationId, invoiceId, CancellationToken.None);

        Assert.NotNull(invoice);
        return invoice;
    }

    private static async Task<BankTransaction> LoadBankTransactionAsync(
        IServiceProvider services,
        OrganizationId organizationId,
        BankTransactionId bankTransactionId)
    {
        BankTransaction? bankTransaction = await services
            .GetRequiredService<IBankTransactionRepository>()
            .GetByIdAsync(organizationId, bankTransactionId, CancellationToken.None);

        Assert.NotNull(bankTransaction);
        return bankTransaction;
    }
}