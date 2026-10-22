using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Imports;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class SourceIdentityLookupTests
{
    private readonly SqlServerFixture _fixture;

    public SourceIdentityLookupTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Invoice_lookup_by_source_identity_runs_on_the_server_and_is_tenant_scoped()
    {
        OrganizationId organizationId = OrganizationId.New();
        OrganizationId otherOrganizationId = OrganizationId.New();
        SourceIdentity sourceIdentity = ImportPersistenceData.Identity("mBank-Acct-1", "Inv-42");
        Invoice invoice = ImportPersistenceData.ImportedInvoice(
            organizationId,
            sourceIdentity,
            "INV-MATCH");
        Invoice otherTenant = ImportPersistenceData.ImportedInvoice(
            otherOrganizationId,
            sourceIdentity,
            "INV-OTHER-TENANT");
        Invoice manual = PersistenceTestData.CreateInvoice(organizationId, 80m, "INV-MANUAL");

        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.Invoices.AddRange(invoice, otherTenant, manual);
            await context.SaveChangesAsync();
        }

        await using ReconciliationDbContext queryContext = _fixture.CreateContext();
        IQueryable<Invoice> query = FindInvoices(queryContext, organizationId, sourceIdentity);
        string sql = query.ToQueryString();

        AssertServerSideSourceIdentityQuery(sql);

        Invoice found = await query.SingleAsync();

        Assert.Equal(invoice.Id, found.Id);
        Assert.Equal(sourceIdentity, found.SourceIdentity);
    }

    [Fact]
    public async Task Bank_transaction_lookup_by_source_identity_runs_on_the_server_and_is_tenant_scoped()
    {
        OrganizationId organizationId = OrganizationId.New();
        OrganizationId otherOrganizationId = OrganizationId.New();
        SourceIdentity sourceIdentity = ImportPersistenceData.Identity("mBank-Acct-1", "Txn-7");
        BankTransaction transaction = ImportPersistenceData.ImportedBankTransaction(
            organizationId,
            sourceIdentity,
            reference: "MATCH");
        BankTransaction otherTenant = ImportPersistenceData.ImportedBankTransaction(
            otherOrganizationId,
            sourceIdentity,
            reference: "OTHER-TENANT");
        BankTransaction manual = PersistenceTestData.CreateBankTransaction(
            organizationId,
            80m,
            reference: "MANUAL");

        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.BankTransactions.AddRange(transaction, otherTenant, manual);
            await context.SaveChangesAsync();
        }

        await using ReconciliationDbContext queryContext = _fixture.CreateContext();
        IQueryable<BankTransaction> query = FindBankTransactions(
            queryContext,
            organizationId,
            sourceIdentity);
        string sql = query.ToQueryString();

        AssertServerSideSourceIdentityQuery(sql);

        BankTransaction found = await query.SingleAsync();

        Assert.Equal(transaction.Id, found.Id);
        Assert.Equal(sourceIdentity, found.SourceIdentity);
    }

    [Fact]
    public async Task External_id_lookup_is_case_sensitive()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice lower = ImportPersistenceData.ImportedInvoice(
            organizationId,
            ImportPersistenceData.Identity("MBANK", "abc"),
            "INV-LOWER");
        Invoice upper = ImportPersistenceData.ImportedInvoice(
            organizationId,
            ImportPersistenceData.Identity("MBANK", "ABC"),
            "INV-UPPER");
        BankTransaction lowerTransaction = ImportPersistenceData.ImportedBankTransaction(
            organizationId,
            ImportPersistenceData.Identity("MBANK", "txn"),
            reference: "LOWER");
        BankTransaction upperTransaction = ImportPersistenceData.ImportedBankTransaction(
            organizationId,
            ImportPersistenceData.Identity("MBANK", "TXN"),
            reference: "UPPER");

        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.Invoices.AddRange(lower, upper);
            context.BankTransactions.AddRange(lowerTransaction, upperTransaction);
            await context.SaveChangesAsync();
        }

        await using ReconciliationDbContext queryContext = _fixture.CreateContext();
        Invoice foundInvoice = await FindInvoices(
                queryContext,
                organizationId,
                ImportPersistenceData.Identity("mbank", "abc"))
            .SingleAsync();
        Invoice? upperInvoiceMiss = await FindInvoices(
                queryContext,
                organizationId,
                ImportPersistenceData.Identity("MBANK", "Abc"))
            .SingleOrDefaultAsync();
        BankTransaction foundTransaction = await FindBankTransactions(
                queryContext,
                organizationId,
                ImportPersistenceData.Identity("MBANK", "txn"))
            .SingleAsync();

        Assert.Equal(lower.Id, foundInvoice.Id);
        Assert.Null(upperInvoiceMiss);
        Assert.Equal(lowerTransaction.Id, foundTransaction.Id);
        Assert.NotEqual(upper.Id, foundInvoice.Id);
        Assert.NotEqual(upperTransaction.Id, foundTransaction.Id);
    }

    [Fact]
    public async Task Null_source_identity_lookup_returns_only_manual_records()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice manual = PersistenceTestData.CreateInvoice(organizationId, 80m, "INV-MANUAL");
        Invoice imported = ImportPersistenceData.ImportedInvoice(
            organizationId,
            ImportPersistenceData.Identity("MBANK", "Inv-1"),
            "INV-IMPORTED");
        BankTransaction manualTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            80m,
            reference: "MANUAL");
        BankTransaction importedTransaction = ImportPersistenceData.ImportedBankTransaction(
            organizationId,
            ImportPersistenceData.Identity("MBANK", "Txn-1"),
            reference: "IMPORTED");

        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.Invoices.AddRange(manual, imported);
            context.BankTransactions.AddRange(manualTransaction, importedTransaction);
            await context.SaveChangesAsync();
        }

        await using ReconciliationDbContext queryContext = _fixture.CreateContext();
        IQueryable<Invoice> manualInvoices = queryContext.Invoices.Where(invoice =>
            invoice.OrganizationId == organizationId
            && invoice.SourceIdentity == null);
        IQueryable<BankTransaction> manualTransactions = queryContext.BankTransactions.Where(
            transaction =>
                transaction.OrganizationId == organizationId
                && transaction.SourceIdentity == null);

        Assert.Contains("IS NULL", manualInvoices.ToQueryString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "IS NULL",
            manualTransactions.ToQueryString(),
            StringComparison.OrdinalIgnoreCase);

        Invoice foundInvoice = await manualInvoices.SingleAsync();
        BankTransaction foundTransaction = await manualTransactions.SingleAsync();

        Assert.Equal(manual.Id, foundInvoice.Id);
        Assert.Null(foundInvoice.SourceIdentity);
        Assert.Equal(manualTransaction.Id, foundTransaction.Id);
        Assert.Null(foundTransaction.SourceIdentity);
    }

    private static IQueryable<Invoice> FindInvoices(
        ReconciliationDbContext context,
        OrganizationId organizationId,
        SourceIdentity sourceIdentity)
    {
        return context.Invoices.Where(invoice =>
            invoice.OrganizationId == organizationId
            && invoice.SourceIdentity != null
            && invoice.SourceIdentity.SourceSystem == sourceIdentity.SourceSystem
            && invoice.SourceIdentity.ExternalId == sourceIdentity.ExternalId);
    }

    private static IQueryable<BankTransaction> FindBankTransactions(
        ReconciliationDbContext context,
        OrganizationId organizationId,
        SourceIdentity sourceIdentity)
    {
        return context.BankTransactions.Where(transaction =>
            transaction.OrganizationId == organizationId
            && transaction.SourceIdentity != null
            && transaction.SourceIdentity.SourceSystem == sourceIdentity.SourceSystem
            && transaction.SourceIdentity.ExternalId == sourceIdentity.ExternalId);
    }

    private static void AssertServerSideSourceIdentityQuery(string sql)
    {
        Assert.Contains("[OrganizationId]", sql, StringComparison.Ordinal);
        Assert.Contains("[SourceSystem]", sql, StringComparison.Ordinal);
        Assert.Contains("[ExternalId]", sql, StringComparison.Ordinal);
    }
}