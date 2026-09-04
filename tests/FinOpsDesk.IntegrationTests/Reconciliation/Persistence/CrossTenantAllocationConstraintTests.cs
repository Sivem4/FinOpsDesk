using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class CrossTenantAllocationConstraintTests
{
    private readonly SqlServerFixture _fixture;

    public CrossTenantAllocationConstraintTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Allocation_cannot_reference_an_invoice_owned_by_another_organization()
    {
        OrganizationId organizationA = OrganizationId.New();
        OrganizationId organizationB = OrganizationId.New();
        Invoice invoiceB = PersistenceTestData.CreateInvoice(organizationB, 1000m);
        BankTransaction bankTransactionA = PersistenceTestData.CreateBankTransaction(
            organizationA,
            1000m);
        var allocation = new Allocation(
            AllocationId.New(),
            organizationA,
            invoiceB.Id,
            bankTransactionA.Id,
            new Money(100m, PersistenceTestData.Pln),
            new DateTimeOffset(2026, 9, 4, 20, 0, 0, TimeSpan.Zero));

        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.Invoices.Add(invoiceB);
        context.BankTransactions.Add(bankTransactionA);
        context.Allocations.Add(allocation);

        await AssertSqlForeignKeyViolation(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Allocation_cannot_reference_a_bank_transaction_owned_by_another_organization()
    {
        OrganizationId organizationA = OrganizationId.New();
        OrganizationId organizationB = OrganizationId.New();
        Invoice invoiceA = PersistenceTestData.CreateInvoice(organizationA, 1000m);
        BankTransaction bankTransactionB = PersistenceTestData.CreateBankTransaction(
            organizationB,
            1000m);
        var allocation = new Allocation(
            AllocationId.New(),
            organizationA,
            invoiceA.Id,
            bankTransactionB.Id,
            new Money(100m, PersistenceTestData.Pln),
            new DateTimeOffset(2026, 9, 4, 20, 0, 0, TimeSpan.Zero));

        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.Invoices.Add(invoiceA);
        context.BankTransactions.Add(bankTransactionB);
        context.Allocations.Add(allocation);

        await AssertSqlForeignKeyViolation(() => context.SaveChangesAsync());
    }

    private static async Task AssertSqlForeignKeyViolation(Func<Task> saveChanges)
    {
        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(saveChanges);
        SqlException sqlException = Assert.IsType<SqlException>(exception.InnerException);
        Assert.Equal(547, sqlException.Number);
    }
}