using FinOpsDesk.Modules.Reconciliation.Application.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class TenantScopedRepositoryTests
{
    private readonly SqlServerFixture _fixture;

    public TenantScopedRepositoryTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Invoice_repository_returns_entity_only_for_matching_organization()
    {
        OrganizationId ownerOrganizationId = OrganizationId.New();
        OrganizationId otherOrganizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(ownerOrganizationId, 1000m);

        await using (ReconciliationDbContext seedContext = _fixture.CreateContext())
        {
            seedContext.Invoices.Add(invoice);
            await seedContext.SaveChangesAsync();
        }

        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IInvoiceRepository repository =
            scope.ServiceProvider.GetRequiredService<IInvoiceRepository>();

        Invoice? matching = await repository.GetByIdAsync(
            ownerOrganizationId,
            invoice.Id,
            CancellationToken.None);
        Invoice? crossTenant = await repository.GetByIdAsync(
            otherOrganizationId,
            invoice.Id,
            CancellationToken.None);

        Assert.NotNull(matching);
        Assert.Equal(invoice.Id, matching.Id);
        Assert.Null(crossTenant);
    }

    [Fact]
    public async Task Bank_transaction_repository_returns_entity_only_for_matching_organization()
    {
        OrganizationId ownerOrganizationId = OrganizationId.New();
        OrganizationId otherOrganizationId = OrganizationId.New();
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            ownerOrganizationId,
            1000m);

        await using (ReconciliationDbContext seedContext = _fixture.CreateContext())
        {
            seedContext.BankTransactions.Add(bankTransaction);
            await seedContext.SaveChangesAsync();
        }

        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IBankTransactionRepository repository =
            scope.ServiceProvider.GetRequiredService<IBankTransactionRepository>();

        BankTransaction? matching = await repository.GetByIdAsync(
            ownerOrganizationId,
            bankTransaction.Id,
            CancellationToken.None);
        BankTransaction? crossTenant = await repository.GetByIdAsync(
            otherOrganizationId,
            bankTransaction.Id,
            CancellationToken.None);

        Assert.NotNull(matching);
        Assert.Equal(bankTransaction.Id, matching.Id);
        Assert.Null(crossTenant);
    }
}