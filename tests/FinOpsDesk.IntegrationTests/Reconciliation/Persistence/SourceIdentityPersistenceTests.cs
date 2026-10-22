using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Imports;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class SourceIdentityPersistenceTests
{
    private readonly SqlServerFixture _fixture;

    public SourceIdentityPersistenceTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Imported_invoice_reloads_equivalent_source_identity()
    {
        OrganizationId organizationId = OrganizationId.New();
        SourceIdentity sourceIdentity = ImportPersistenceData.Identity(
            "mBank-Acct-1",
            " Inv-42 ");
        Invoice invoice = ImportPersistenceData.ImportedInvoice(
            organizationId,
            sourceIdentity);

        await SaveInvoiceAsync(invoice);

        await using ReconciliationDbContext reloadContext = _fixture.CreateContext();
        Invoice loaded = await reloadContext.Invoices.SingleAsync(
            stored => stored.Id == invoice.Id);

        Assert.Equal(sourceIdentity, loaded.SourceIdentity);
        Assert.Equal("MBANK-ACCT-1", loaded.SourceIdentity!.SourceSystem.Value);
        Assert.Equal("Inv-42", loaded.SourceIdentity.ExternalId.Value);
    }

    [Fact]
    public async Task Imported_bank_transaction_reloads_equivalent_source_identity()
    {
        OrganizationId organizationId = OrganizationId.New();
        SourceIdentity sourceIdentity = ImportPersistenceData.Identity(
            "mBank-Acct-1",
            "Txn-7");
        BankTransaction bankTransaction = ImportPersistenceData.ImportedBankTransaction(
            organizationId,
            sourceIdentity);

        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.BankTransactions.Add(bankTransaction);
            await context.SaveChangesAsync();
        }

        await using ReconciliationDbContext reloadContext = _fixture.CreateContext();
        BankTransaction loaded = await reloadContext.BankTransactions.SingleAsync(
            stored => stored.Id == bankTransaction.Id);

        Assert.Equal(sourceIdentity, loaded.SourceIdentity);
    }

    [Fact]
    public async Task Manual_invoice_reloads_with_null_source_identity()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 100m);

        await SaveInvoiceAsync(invoice);

        await using ReconciliationDbContext reloadContext = _fixture.CreateContext();
        Invoice loaded = await reloadContext.Invoices.SingleAsync(
            stored => stored.Id == invoice.Id);

        Assert.Null(loaded.SourceIdentity);
    }

    [Fact]
    public async Task Manual_bank_transaction_reloads_with_null_source_identity()
    {
        OrganizationId organizationId = OrganizationId.New();
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            100m);

        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.BankTransactions.Add(bankTransaction);
            await context.SaveChangesAsync();
        }

        await using ReconciliationDbContext reloadContext = _fixture.CreateContext();
        BankTransaction loaded = await reloadContext.BankTransactions.SingleAsync(
            stored => stored.Id == bankTransaction.Id);

        Assert.Null(loaded.SourceIdentity);
    }

    [Fact]
    public async Task Source_system_is_stored_in_canonical_form()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = ImportPersistenceData.ImportedInvoice(
            organizationId,
            ImportPersistenceData.Identity(" mBank ", "Inv-1"));

        await SaveInvoiceAsync(invoice);

        await using ReconciliationDbContext context = _fixture.CreateContext();
        string? storedSourceSystem = await SqlServerCatalog.ScalarStringAsync(
            context,
            """
            SELECT [SourceSystem]
            FROM [reconciliation].[Invoices]
            WHERE [Id] = @id;
            """,
            ("@id", invoice.Id.Value));

        Assert.Equal("MBANK", storedSourceSystem);

        Invoice duplicate = ImportPersistenceData.ImportedInvoice(
            organizationId,
            ImportPersistenceData.Identity("mbank", "Inv-1"),
            invoiceNumber: "INV-002");
        context.Invoices.Add(duplicate);

        SqlException sqlException = await RequireSaveFailureAsync(context);

        Assert.True(sqlException.Number is 2601 or 2627);
        Assert.Contains(
            "UX_Invoices_OrganizationId_SourceSystem_ExternalId",
            sqlException.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task External_id_comparison_is_case_sensitive()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice lower = ImportPersistenceData.ImportedInvoice(
            organizationId,
            ImportPersistenceData.Identity("MBANK", "abc"),
            invoiceNumber: "INV-LOWER");
        Invoice upper = ImportPersistenceData.ImportedInvoice(
            organizationId,
            ImportPersistenceData.Identity("MBANK", "ABC"),
            invoiceNumber: "INV-UPPER");

        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.Invoices.AddRange(lower, upper);
            await context.SaveChangesAsync();
        }

        await using ReconciliationDbContext reloadContext = _fixture.CreateContext();
        Invoice loadedLower = await reloadContext.Invoices.SingleAsync(
            stored => stored.Id == lower.Id);
        Invoice loadedUpper = await reloadContext.Invoices.SingleAsync(
            stored => stored.Id == upper.Id);

        Assert.Equal("abc", loadedLower.SourceIdentity!.ExternalId.Value);
        Assert.Equal("ABC", loadedUpper.SourceIdentity!.ExternalId.Value);
    }

    [Fact]
    public async Task Duplicate_source_identity_in_one_tenant_is_rejected()
    {
        OrganizationId organizationId = OrganizationId.New();
        SourceIdentity sourceIdentity = ImportPersistenceData.Identity("MBANK", "Inv-9");

        await SaveInvoiceAsync(
            ImportPersistenceData.ImportedInvoice(organizationId, sourceIdentity));

        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.Invoices.Add(
            ImportPersistenceData.ImportedInvoice(
                organizationId,
                sourceIdentity,
                invoiceNumber: "INV-OTHER"));

        SqlException sqlException = await RequireSaveFailureAsync(context);

        Assert.True(sqlException.Number is 2601 or 2627);
        Assert.Contains(
            "UX_Invoices_OrganizationId_SourceSystem_ExternalId",
            sqlException.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Same_source_identity_is_allowed_in_different_tenants()
    {
        SourceIdentity sourceIdentity = ImportPersistenceData.Identity("MBANK", "Inv-9");
        Invoice first = ImportPersistenceData.ImportedInvoice(
            OrganizationId.New(),
            sourceIdentity);
        Invoice second = ImportPersistenceData.ImportedInvoice(
            OrganizationId.New(),
            sourceIdentity);

        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.Invoices.AddRange(first, second);
        await context.SaveChangesAsync();

        int storedCount = await context.Invoices.CountAsync(
            stored => stored.Id == first.Id || stored.Id == second.Id);

        Assert.Equal(2, storedCount);
    }

    [Fact]
    public async Task Multiple_manual_records_are_allowed_in_one_tenant()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice first = PersistenceTestData.CreateInvoice(
            organizationId,
            100m,
            "INV-MANUAL-1");
        Invoice second = PersistenceTestData.CreateInvoice(
            organizationId,
            200m,
            "INV-MANUAL-2");
        BankTransaction firstTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            100m,
            reference: "MANUAL-1");
        BankTransaction secondTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            200m,
            reference: "MANUAL-2");

        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.Invoices.AddRange(first, second);
        context.BankTransactions.AddRange(firstTransaction, secondTransaction);
        await context.SaveChangesAsync();

        await using ReconciliationDbContext reloadContext = _fixture.CreateContext();
        Invoice[] invoices = await reloadContext.Invoices
            .Where(stored => stored.OrganizationId == organizationId)
            .ToArrayAsync();
        BankTransaction[] transactions = await reloadContext.BankTransactions
            .Where(stored => stored.OrganizationId == organizationId)
            .ToArrayAsync();

        Assert.Equal(2, invoices.Length);
        Assert.Equal(2, transactions.Length);
        Assert.All(invoices, invoice => Assert.Null(invoice.SourceIdentity));
        Assert.All(transactions, transaction => Assert.Null(transaction.SourceIdentity));
    }

    [Fact]
    public async Task Invoice_source_identity_null_pair_check_rejects_one_populated_column()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 100m);
        await SaveInvoiceAsync(invoice);

        await using ReconciliationDbContext context = _fixture.CreateContext();
        Guid invoiceId = invoice.Id.Value;
        SqlException sqlException = await Assert.ThrowsAsync<SqlException>(
            () => context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE [reconciliation].[Invoices]
                SET [SourceSystem] = N'MBANK'
                WHERE [Id] = {invoiceId};
                """));

        Assert.Equal(547, sqlException.Number);
        Assert.Contains(
            "CK_Invoices_SourceIdentity_NullPair",
            sqlException.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bank_transaction_source_identity_null_pair_check_rejects_one_populated_column()
    {
        OrganizationId organizationId = OrganizationId.New();
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            100m);

        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.BankTransactions.Add(bankTransaction);
            await context.SaveChangesAsync();
        }

        await using ReconciliationDbContext updateContext = _fixture.CreateContext();
        Guid bankTransactionId = bankTransaction.Id.Value;
        SqlException sqlException = await Assert.ThrowsAsync<SqlException>(
            () => updateContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE [reconciliation].[BankTransactions]
                SET [ExternalId] = N'TXN-1'
                WHERE [Id] = {bankTransactionId};
                """));

        Assert.Equal(547, sqlException.Number);
        Assert.Contains(
            "CK_BankTransactions_SourceIdentity_NullPair",
            sqlException.Message,
            StringComparison.Ordinal);
    }

    private async Task SaveInvoiceAsync(Invoice invoice)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.Invoices.Add(invoice);
        await context.SaveChangesAsync();
    }

    private static async Task<SqlException> RequireSaveFailureAsync(
        ReconciliationDbContext context)
    {
        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => context.SaveChangesAsync());

        return SqlServerCatalog.RequireSqlException(exception);
    }
}