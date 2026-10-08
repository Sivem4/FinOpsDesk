using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class InvoiceQueriesTests
{
    private readonly SqlServerFixture _fixture;

    public InvoiceQueriesTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task List_projects_only_the_requested_organization_money_and_outstanding()
    {
        SeededInvoices seeded = await SeedInvoicesAsync();

        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IInvoiceQueries queries = scope.ServiceProvider.GetRequiredService<IInvoiceQueries>();
        ReconciliationDbContext context =
            scope.ServiceProvider.GetRequiredService<ReconciliationDbContext>();

        InvoiceListResponse response = await queries.ListAsync(
            seeded.OrganizationA,
            page: 1,
            pageSize: 50,
            CancellationToken.None);

        Assert.Empty(context.Invoices.Local);
        Assert.Equal(3, response.TotalCount);
        Assert.Equal(3, response.Items.Count);
        Assert.DoesNotContain(response.Items, item => item.Id == seeded.InvoiceB.Id.Value);
        Assert.DoesNotContain(response.Items, item => item.InvoiceNumber == "INV-B-1");

        InvoiceListItemDto partial = Assert.Single(
            response.Items,
            item => item.Id == seeded.PartialInvoice.Id.Value);
        Assert.Equal("INV-A-1", partial.InvoiceNumber);
        Assert.Equal(new MoneyDto(1000.25m, "PLN"), partial.TotalAmount);
        Assert.Equal(new MoneyDto(250.10m, "PLN"), partial.AllocatedAmount);
        Assert.Equal(new MoneyDto(750.15m, "PLN"), partial.OutstandingAmount);

        InvoiceListResponse organizationB = await queries.ListAsync(
            seeded.OrganizationB,
            page: 1,
            pageSize: 50,
            CancellationToken.None);

        InvoiceListItemDto organizationBInvoice = Assert.Single(organizationB.Items);
        Assert.Equal(seeded.InvoiceB.Id.Value, organizationBInvoice.Id);
        Assert.Equal("INV-B-1", organizationBInvoice.InvoiceNumber);
        Assert.Equal(new MoneyDto(999m, "PLN"), organizationBInvoice.TotalAmount);
        Assert.Equal(new MoneyDto(0m, "PLN"), organizationBInvoice.AllocatedAmount);
        Assert.Equal(new MoneyDto(999m, "PLN"), organizationBInvoice.OutstandingAmount);
        Assert.Equal(1, organizationB.TotalCount);
    }

    [Fact]
    public async Task List_pages_in_invoice_id_order_with_a_tenant_scoped_total_count()
    {
        SeededInvoices seeded = await SeedInvoicesAsync();
        List<Guid> expectedOrder = await LoadOrganizationAOrderAsync(seeded.OrganizationA);

        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IInvoiceQueries queries = scope.ServiceProvider.GetRequiredService<IInvoiceQueries>();

        InvoiceListResponse firstPage = await queries.ListAsync(
            seeded.OrganizationA,
            page: 1,
            pageSize: 2,
            CancellationToken.None);
        InvoiceListResponse secondPage = await queries.ListAsync(
            seeded.OrganizationA,
            page: 2,
            pageSize: 2,
            CancellationToken.None);
        InvoiceListResponse thirdPage = await queries.ListAsync(
            seeded.OrganizationA,
            page: 3,
            pageSize: 2,
            CancellationToken.None);

        Assert.Equal(3, expectedOrder.Count);
        Assert.Equal(expectedOrder.Take(2), firstPage.Items.Select(item => item.Id));
        Assert.Equal(1, firstPage.Page);
        Assert.Equal(2, firstPage.PageSize);
        Assert.Equal(3, firstPage.TotalCount);

        Assert.Equal(expectedOrder.Skip(2), secondPage.Items.Select(item => item.Id));
        Assert.Equal(2, secondPage.Page);
        Assert.Equal(2, secondPage.PageSize);
        Assert.Equal(3, secondPage.TotalCount);

        Assert.Empty(thirdPage.Items);
        Assert.Equal(3, thirdPage.Page);
        Assert.Equal(2, thirdPage.PageSize);
        Assert.Equal(3, thirdPage.TotalCount);
        Assert.DoesNotContain(seeded.InvoiceB.Id.Value, expectedOrder);
    }

    private async Task<SeededInvoices> SeedInvoicesAsync()
    {
        OrganizationId organizationA = OrganizationId.New();
        OrganizationId organizationB = OrganizationId.New();
        Invoice laterInvoice = CreateInvoice(organizationA, "INV-A-3", 40m);
        Invoice partialInvoice = CreateInvoice(organizationA, "INV-A-1", 1000.25m);
        partialInvoice.Allocate(new Money(250.10m, PersistenceTestData.Pln));
        Invoice middleInvoice = CreateInvoice(organizationA, "INV-A-2", 500m);
        Invoice organizationBInvoice = CreateInvoice(organizationB, "INV-B-1", 999m);

        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.Invoices.AddRange(
            laterInvoice,
            partialInvoice,
            middleInvoice,
            organizationBInvoice);
        await context.SaveChangesAsync();

        return new SeededInvoices(
            organizationA,
            organizationB,
            partialInvoice,
            organizationBInvoice);
    }

    private async Task<List<Guid>> LoadOrganizationAOrderAsync(OrganizationId organizationA)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.Invoices
            .AsNoTracking()
            .Where(invoice => invoice.OrganizationId == organizationA)
            .OrderBy(invoice => invoice.Id)
            .Select(invoice => invoice.Id.Value)
            .ToListAsync();
    }

    private static Invoice CreateInvoice(
        OrganizationId organizationId,
        string invoiceNumber,
        decimal totalAmount)
    {
        return new Invoice(
            InvoiceId.New(),
            organizationId,
            invoiceNumber,
            new Money(totalAmount, PersistenceTestData.Pln));
    }

    private sealed record SeededInvoices(
        OrganizationId OrganizationA,
        OrganizationId OrganizationB,
        Invoice PartialInvoice,
        Invoice InvoiceB);
}