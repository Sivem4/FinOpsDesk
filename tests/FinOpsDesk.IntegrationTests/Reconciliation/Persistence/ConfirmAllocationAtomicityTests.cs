using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class ConfirmAllocationAtomicityTests
{
    private readonly SqlServerFixture _fixture;

    public ConfirmAllocationAtomicityTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SaveChanges_rolls_back_invoice_and_bank_transaction_when_allocation_insert_fails()
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

        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER [reconciliation].[TR_Allocations_FailInsert]
                ON [reconciliation].[Allocations]
                AFTER INSERT
                AS
                BEGIN
                    THROW 50001, 'FinOpsDesk integration test forced failure', 1;
                END
                """);

            try
            {
                Invoice trackedInvoice = await context.Invoices.SingleAsync(
                    stored => stored.Id == invoice.Id);
                BankTransaction trackedBankTransaction =
                    await context.BankTransactions.SingleAsync(
                        stored => stored.Id == bankTransaction.Id);

                trackedInvoice.Allocate(amount);
                trackedBankTransaction.Allocate(amount);
                context.Allocations.Add(
                    new Allocation(
                        AllocationId.New(),
                        organizationId,
                        trackedInvoice.Id,
                        trackedBankTransaction.Id,
                        amount,
                        new DateTimeOffset(2026, 9, 4, 20, 0, 0, TimeSpan.Zero)));

                await Assert.ThrowsAsync<DbUpdateException>(
                    () => context.SaveChangesAsync());
            }
            finally
            {
                await context.Database.ExecuteSqlRawAsync(
                    """
                    IF OBJECT_ID(N'[reconciliation].[TR_Allocations_FailInsert]', N'TR') IS NOT NULL
                        DROP TRIGGER [reconciliation].[TR_Allocations_FailInsert];
                    """);
            }
        }

        await using ReconciliationDbContext reloadContext = _fixture.CreateContext();
        Invoice reloadedInvoice = await reloadContext.Invoices.SingleAsync(
            stored => stored.Id == invoice.Id);
        BankTransaction reloadedBankTransaction =
            await reloadContext.BankTransactions.SingleAsync(
                stored => stored.Id == bankTransaction.Id);
        int allocationCount = await reloadContext.Allocations.CountAsync(
            allocation => allocation.InvoiceId == invoice.Id);

        Assert.Equal(Money.Zero(PersistenceTestData.Pln), reloadedInvoice.AllocatedAmount);
        Assert.Equal(
            Money.Zero(PersistenceTestData.Pln),
            reloadedBankTransaction.AllocatedAmount);
        Assert.Equal(0, allocationCount);
    }
}