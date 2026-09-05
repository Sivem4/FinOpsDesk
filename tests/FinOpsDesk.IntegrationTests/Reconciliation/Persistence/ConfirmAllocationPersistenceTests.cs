using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Application.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class ConfirmAllocationPersistenceTests
{
    private static readonly DateTimeOffset ConfirmedAt =
        new(2026, 9, 4, 20, 0, 0, TimeSpan.Zero);

    private readonly SqlServerFixture _fixture;

    public ConfirmAllocationPersistenceTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Confirm_allocation_persists_invoice_bank_transaction_and_allocation_together()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            1000m);
        var amount = new Money(300m, PersistenceTestData.Pln);

        await using (ReconciliationDbContext seedContext = _fixture.CreateContext())
        {
            seedContext.Invoices.Add(invoice);
            seedContext.BankTransactions.Add(bankTransaction);
            await seedContext.SaveChangesAsync();
        }

        Result<ConfirmAllocationResult> result;
        await using (ServiceProvider provider = _fixture.CreateServiceProvider())
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            ConfirmAllocationHandler handler = CreateHandler(
                scope.ServiceProvider,
                organizationId);

            result = await handler.Handle(
                new ConfirmAllocationCommand(invoice.Id, bankTransaction.Id, amount),
                CancellationToken.None);
        }

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);

        await using ReconciliationDbContext reloadContext = _fixture.CreateContext();
        Invoice loadedInvoice = await reloadContext.Invoices.SingleAsync(
            stored => stored.Id == invoice.Id);
        BankTransaction loadedBankTransaction =
            await reloadContext.BankTransactions.SingleAsync(
                stored => stored.Id == bankTransaction.Id);
        Allocation loadedAllocation = await reloadContext.Allocations.SingleAsync(
            stored => stored.Id == result.Value.AllocationId);

        Assert.Equal(amount, loadedInvoice.AllocatedAmount);
        Assert.Equal(amount, loadedBankTransaction.AllocatedAmount);
        Assert.Equal(organizationId, loadedAllocation.OrganizationId);
        Assert.Equal(invoice.Id, loadedAllocation.InvoiceId);
        Assert.Equal(bankTransaction.Id, loadedAllocation.BankTransactionId);
        Assert.Equal(amount, loadedAllocation.Amount);
        Assert.Equal(AllocationStatus.Confirmed, loadedAllocation.Status);
        Assert.Equal(ConfirmedAt, loadedAllocation.ConfirmedAt);
    }

    private static ConfirmAllocationHandler CreateHandler(
        IServiceProvider serviceProvider,
        OrganizationId organizationId)
    {
        return new ConfirmAllocationHandler(
            new TestCurrentOrganizationContext(organizationId),
            serviceProvider.GetRequiredService<IInvoiceRepository>(),
            serviceProvider.GetRequiredService<IBankTransactionRepository>(),
            serviceProvider.GetRequiredService<IAllocationRepository>(),
            serviceProvider.GetRequiredService<IUnitOfWork>(),
            new FixedTimeProvider(ConfirmedAt));
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }
    }
}