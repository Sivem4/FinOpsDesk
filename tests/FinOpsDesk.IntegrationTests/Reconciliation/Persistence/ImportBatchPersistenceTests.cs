using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Imports;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class ImportBatchPersistenceTests
{
    private readonly SqlServerFixture _fixture;

    public ImportBatchPersistenceTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Completed_batch_reloads_terminal_state()
    {
        OrganizationId organizationId = OrganizationId.New();
        ImportBatch batch = ImportPersistenceData.CompletedBatch(
            organizationId,
            ImportType.Invoice,
            " mBank-Acct-1 ",
            ImportPersistenceData.Hash('a'),
            " invoices.csv ");

        await SaveAsync(batch);

        ImportBatch loaded = await ReloadAsync(batch.Id);

        Assert.Equal(batch.Id, loaded.Id);
        Assert.Equal(organizationId, loaded.OrganizationId);
        Assert.Equal(ImportType.Invoice, loaded.ImportType);
        Assert.Equal(new SourceSystem("MBANK-ACCT-1"), loaded.SourceSystem);
        Assert.Equal(ImportPersistenceData.Hash('a'), loaded.FileHash);
        Assert.Equal("invoices.csv", loaded.FileName);
        Assert.Equal(ImportBatchStatus.Completed, loaded.Status);
        Assert.Equal(1, loaded.TotalRows);
        Assert.Equal(1, loaded.ImportedRows);
        Assert.Equal(0, loaded.AlreadyExistsRows);
        Assert.Equal(0, loaded.RejectedRows);
        Assert.Equal(ImportPersistenceData.CreatedAt, loaded.CreatedAt);
        Assert.Equal(ImportPersistenceData.CompletedAt, loaded.CompletedAt);
        Assert.Empty(loaded.RowErrors);
    }

    [Fact]
    public async Task Completed_with_errors_batch_reloads_accepted_and_rejected_rows()
    {
        OrganizationId organizationId = OrganizationId.New();
        ImportBatch batch = ImportPersistenceData.CompletedWithErrorsBatch(
            organizationId,
            ImportPersistenceData.Hash('b'));

        await SaveAsync(batch);

        ImportBatch loaded = await ReloadAsync(batch.Id);

        Assert.Equal(ImportBatchStatus.CompletedWithErrors, loaded.Status);
        Assert.Equal(2, loaded.TotalRows);
        Assert.Equal(1, loaded.ImportedRows);
        Assert.Equal(0, loaded.AlreadyExistsRows);
        Assert.Equal(1, loaded.RejectedRows);
        ImportRowError error = Assert.Single(loaded.RowErrors);
        Assert.Equal(ImportPersistenceData.RowError(2, 4, " Amount "), error);
        Assert.Equal("Amount", error.ColumnName);
    }

    [Fact]
    public async Task Rejected_batch_reloads_read_only_row_errors_and_owner_tenant()
    {
        OrganizationId organizationId = OrganizationId.New();
        ImportBatch batch = ImportPersistenceData.RejectedBatch(
            organizationId,
            ImportPersistenceData.Hash('c'));

        await SaveAsync(batch);

        ImportBatch loaded = await ReloadAsync(batch.Id);

        Assert.Equal(ImportBatchStatus.Rejected, loaded.Status);
        Assert.Equal(0, loaded.ImportedRows);
        Assert.Equal(0, loaded.AlreadyExistsRows);
        Assert.Equal(2, loaded.RejectedRows);
        Assert.IsAssignableFrom<IReadOnlyList<ImportRowError>>(loaded.RowErrors);
        Assert.Equal(
            [
                ImportPersistenceData.RowError(1, 8, "Reference"),
                ImportPersistenceData.RowError(2)
            ],
            loaded.RowErrors.OrderBy(error => error.DataRowIndex).ToArray());
        Assert.Equal(
            ReconciliationErrors.ImportBatchNotProcessing,
            loaded.RecordImported(1).Error);

        await using ReconciliationDbContext context = _fixture.CreateContext();
        string? storedOrganizationId = await SqlServerCatalog.ScalarStringAsync(
            context,
            """
            SELECT CONVERT(nvarchar(36), [OrganizationId])
            FROM [reconciliation].[ImportRowErrors]
            WHERE [ImportBatchId] = @importBatchId
                AND [DataRowIndex] = 1;
            """,
            ("@importBatchId", batch.Id.Value));

        Assert.Equal(
            organizationId.Value.ToString("D"),
            storedOrganizationId,
            ignoreCase: true);
    }

    [Fact]
    public async Task All_already_exists_batch_reloads_as_completed_without_errors()
    {
        OrganizationId organizationId = OrganizationId.New();
        ImportBatch batch = ImportPersistenceData.AlreadyExistsBatch(
            organizationId,
            ImportPersistenceData.Hash('d'));

        await SaveAsync(batch);

        ImportBatch loaded = await ReloadAsync(batch.Id);

        Assert.Equal(ImportBatchStatus.Completed, loaded.Status);
        Assert.Equal(2, loaded.TotalRows);
        Assert.Equal(0, loaded.ImportedRows);
        Assert.Equal(2, loaded.AlreadyExistsRows);
        Assert.Equal(0, loaded.RejectedRows);
        Assert.Empty(loaded.RowErrors);
    }

    [Fact]
    public async Task Same_file_hash_is_rejected_in_the_same_scope()
    {
        OrganizationId organizationId = OrganizationId.New();
        FileHash fileHash = ImportPersistenceData.Hash('e');
        await SaveAsync(
            ImportPersistenceData.CompletedBatch(
                organizationId,
                ImportType.Invoice,
                "MBANK-ACCT-1",
                fileHash,
                "first.csv"));

        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.ImportBatches.Add(
            ImportPersistenceData.CompletedBatch(
                organizationId,
                ImportType.Invoice,
                "MBANK-ACCT-1",
                fileHash,
                "second.csv"));

        SqlException sqlException = await RequireSaveFailureAsync(context);

        Assert.True(sqlException.Number is 2601 or 2627);
        Assert.Contains(
            "UX_ImportBatches_OrganizationId_ImportType_SourceSystem_FileHash",
            sqlException.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Same_file_hash_is_allowed_across_tenants()
    {
        FileHash fileHash = ImportPersistenceData.Hash('e');
        ImportBatch first = ImportPersistenceData.CompletedBatch(
            OrganizationId.New(),
            ImportType.Invoice,
            "MBANK-ACCT-1",
            fileHash);
        ImportBatch second = ImportPersistenceData.CompletedBatch(
            OrganizationId.New(),
            ImportType.Invoice,
            "MBANK-ACCT-1",
            fileHash);

        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.ImportBatches.AddRange(first, second);
        await context.SaveChangesAsync();

        Assert.Equal(2, await context.ImportBatches.CountAsync(
            stored => stored.Id == first.Id || stored.Id == second.Id));
    }

    [Fact]
    public async Task Same_file_hash_is_allowed_for_different_import_type_or_source()
    {
        OrganizationId organizationId = OrganizationId.New();
        FileHash fileHash = ImportPersistenceData.Hash('f');
        ImportBatch invoiceBatch = ImportPersistenceData.CompletedBatch(
            organizationId,
            ImportType.Invoice,
            "MBANK-ACCT-1",
            fileHash,
            "invoices.csv");
        ImportBatch transactionBatch = ImportPersistenceData.CompletedBatch(
            organizationId,
            ImportType.BankTransaction,
            "MBANK-ACCT-1",
            fileHash,
            "transactions.csv");
        ImportBatch otherSourceBatch = ImportPersistenceData.CompletedBatch(
            organizationId,
            ImportType.Invoice,
            "MBANK-ACCT-2",
            fileHash,
            "other-source.csv");

        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.ImportBatches.AddRange(invoiceBatch, transactionBatch, otherSourceBatch);
        await context.SaveChangesAsync();

        Assert.Equal(3, await context.ImportBatches.CountAsync(
            stored => stored.OrganizationId == organizationId
                && stored.FileHash == fileHash));
    }

    private async Task SaveAsync(ImportBatch batch)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.ImportBatches.Add(batch);
        await context.SaveChangesAsync();
    }

    private async Task<ImportBatch> ReloadAsync(ImportBatchId id)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.ImportBatches.SingleAsync(stored => stored.Id == id);
    }

    private static async Task<SqlException> RequireSaveFailureAsync(
        ReconciliationDbContext context)
    {
        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => context.SaveChangesAsync());

        return SqlServerCatalog.RequireSqlException(exception);
    }
}