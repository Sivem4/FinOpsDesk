using FinOpsDesk.Modules.Reconciliation.Domain.Imports;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class ImportPersistenceAtomicityTests
{
    private readonly SqlServerFixture _fixture;

    public ImportPersistenceAtomicityTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SaveChanges_rolls_back_imported_invoice_batch_and_row_errors_together()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = ImportPersistenceData.ImportedInvoice(
            organizationId,
            ImportPersistenceData.Identity("MBANK-ACCT-1", "INV-ATOMIC"));
        ImportBatch batch = ImportPersistenceData.RejectedBatch(
            organizationId,
            ImportPersistenceData.Hash('3'));

        await using ReconciliationDbContext context = _fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER [reconciliation].[TR_ImportRowErrors_FailInsert]
            ON [reconciliation].[ImportRowErrors]
            AFTER INSERT
            AS
            BEGIN
                THROW 50001, 'FinOpsDesk integration test forced failure', 1;
            END
            """);

        try
        {
            context.Invoices.Add(invoice);
            context.ImportBatches.Add(batch);

            DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
                () => context.SaveChangesAsync());
            SqlException sqlException = SqlServerCatalog.RequireSqlException(exception);

            Assert.Equal(50001, sqlException.Number);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                IF OBJECT_ID(N'[reconciliation].[TR_ImportRowErrors_FailInsert]', N'TR') IS NOT NULL
                    DROP TRIGGER [reconciliation].[TR_ImportRowErrors_FailInsert];
                """);
        }

        await using ReconciliationDbContext reloadContext = _fixture.CreateContext();
        bool invoiceExists = await reloadContext.Invoices.AnyAsync(
            stored => stored.Id == invoice.Id);
        bool batchExists = await reloadContext.ImportBatches.AnyAsync(
            stored => stored.Id == batch.Id);
        string? errorCount = await SqlServerCatalog.ScalarStringAsync(
            reloadContext,
            """
            SELECT CONVERT(nvarchar(20), COUNT(*))
            FROM [reconciliation].[ImportRowErrors]
            WHERE [ImportBatchId] = @importBatchId;
            """,
            ("@importBatchId", batch.Id.Value));

        Assert.False(invoiceExists);
        Assert.False(batchExists);
        Assert.Equal("0", errorCount);
    }
}