using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class BankTransactionPersistenceTests
{
    private readonly SqlServerFixture _fixture;

    public BankTransactionPersistenceTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Bank_transaction_round_trips_money_date_only_reference_and_nonzero_allocated_amount()
    {
        OrganizationId organizationId = OrganizationId.New();
        var bookingDate = new DateOnly(2026, 8, 24);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            2500.5000m,
            bookingDate,
            "REF-42");
        bankTransaction.Allocate(new Money(400.2500m, PersistenceTestData.Pln));

        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.BankTransactions.Add(bankTransaction);
            await context.SaveChangesAsync();
        }

        await using ReconciliationDbContext reloadContext = _fixture.CreateContext();
        BankTransaction loaded = await reloadContext.BankTransactions.SingleAsync(
            stored => stored.Id == bankTransaction.Id);

        Assert.Equal(bankTransaction.Id, loaded.Id);
        Assert.Equal(organizationId, loaded.OrganizationId);
        Assert.Equal(new Money(2500.5000m, PersistenceTestData.Pln), loaded.Amount);
        Assert.Equal(new Money(400.2500m, PersistenceTestData.Pln), loaded.AllocatedAmount);
        Assert.Equal(new Money(2100.2500m, PersistenceTestData.Pln), loaded.UnappliedAmount);
        Assert.Equal(bookingDate, loaded.BookingDate);
        Assert.Equal("REF-42", loaded.Reference);
    }
}