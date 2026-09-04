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

using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class ReconciliationDbContextScopeTests
{
    private readonly SqlServerFixture _fixture;

    public ReconciliationDbContextScopeTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Repositories_and_unit_of_work_share_the_scoped_dbcontext()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            1000m);

        await using (ReconciliationDbContext seedContext = _fixture.CreateContext())
        {
            seedContext.Invoices.Add(invoice);
            seedContext.BankTransactions.Add(bankTransaction);
            await seedContext.SaveChangesAsync();
        }

        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IServiceProvider services = scope.ServiceProvider;

        ReconciliationDbContext context =
            services.GetRequiredService<ReconciliationDbContext>();
        IUnitOfWork unitOfWork = services.GetRequiredService<IUnitOfWork>();
        IInvoiceRepository invoiceRepository =
            services.GetRequiredService<IInvoiceRepository>();
        IBankTransactionRepository bankTransactionRepository =
            services.GetRequiredService<IBankTransactionRepository>();
        IAllocationRepository allocationRepository =
            services.GetRequiredService<IAllocationRepository>();

        Assert.Same(context, unitOfWork);

        Invoice? trackedInvoice = await invoiceRepository.GetByIdAsync(
            organizationId,
            invoice.Id,
            CancellationToken.None);
        BankTransaction? trackedBankTransaction =
            await bankTransactionRepository.GetByIdAsync(
                organizationId,
                bankTransaction.Id,
                CancellationToken.None);

        Assert.NotNull(trackedInvoice);
        Assert.NotNull(trackedBankTransaction);
        Assert.Same(trackedInvoice, context.Invoices.Local.Single());
        Assert.Same(trackedBankTransaction, context.BankTransactions.Local.Single());

        var allocation = new Allocation(
            AllocationId.New(),
            organizationId,
            invoice.Id,
            bankTransaction.Id,
            new Money(100m, PersistenceTestData.Pln),
            new DateTimeOffset(2026, 9, 4, 20, 0, 0, TimeSpan.Zero));

        allocationRepository.Add(allocation);

        Assert.Same(allocation, context.Allocations.Local.Single());
    }
}