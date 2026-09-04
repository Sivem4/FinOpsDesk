using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class InvoicePersistenceTests
{
    private readonly SqlServerFixture _fixture;

    public InvoicePersistenceTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Invoice_round_trips_typed_ids_money_and_nonzero_allocated_amount()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000.2500m);
        invoice.Allocate(new Money(300.1250m, PersistenceTestData.Pln));

        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.Invoices.Add(invoice);
            await context.SaveChangesAsync();
        }

        await using ReconciliationDbContext reloadContext = _fixture.CreateContext();
        Invoice loaded = await reloadContext.Invoices.SingleAsync(
            stored => stored.Id == invoice.Id);

        Assert.Equal(invoice.Id, loaded.Id);
        Assert.Equal(organizationId, loaded.OrganizationId);
        Assert.Equal("INV-001", loaded.InvoiceNumber);
        Assert.Equal(new Money(1000.2500m, PersistenceTestData.Pln), loaded.TotalAmount);
        Assert.Equal(new Money(300.1250m, PersistenceTestData.Pln), loaded.AllocatedAmount);
        Assert.Equal(new Money(700.1250m, PersistenceTestData.Pln), loaded.OutstandingAmount);
    }
}