using FinOpsDesk.Modules.Reconciliation.Domain.Imports;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class ImportPersistenceConstraintTests
{
    private readonly SqlServerFixture _fixture;

    public ImportPersistenceConstraintTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Terminal_status_check_rejects_processing_batch()
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync(
            """
            ALTER TABLE [reconciliation].[ImportBatches]
            NOCHECK CONSTRAINT [CK_ImportBatches_StatusMatchesCounters];
            """);

        try
        {
            SqlException sqlException = await Assert.ThrowsAsync<SqlException>(
                () => InsertBatchAsync(
                    context,
                    status: (int)ImportBatchStatus.Processing,
                    totalRows: 1,
                    importedRows: 1,
                    alreadyExistsRows: 0,
                    rejectedRows: 0));

            Assert.Equal(547, sqlException.Number);
            Assert.Contains(
                "CK_ImportBatches_TerminalStatus",
                sqlException.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                ALTER TABLE [reconciliation].[ImportBatches]
                WITH CHECK CHECK CONSTRAINT [CK_ImportBatches_StatusMatchesCounters];
                """);
        }
    }

    [Fact]
    public async Task Counter_check_rejects_inconsistent_totals()
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        SqlException sqlException = await Assert.ThrowsAsync<SqlException>(
            () => InsertBatchAsync(
                context,
                status: (int)ImportBatchStatus.Completed,
                totalRows: 2,
                importedRows: 1,
                alreadyExistsRows: 0,
                rejectedRows: 0));

        Assert.Equal(547, sqlException.Number);
        Assert.Contains(
            "CK_ImportBatches_Counters",
            sqlException.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_check_rejects_completed_batch_with_rejected_rows()
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        SqlException sqlException = await Assert.ThrowsAsync<SqlException>(
            () => InsertBatchAsync(
                context,
                status: (int)ImportBatchStatus.Completed,
                totalRows: 1,
                importedRows: 0,
                alreadyExistsRows: 0,
                rejectedRows: 1));

        Assert.Equal(547, sqlException.Number);
        Assert.Contains(
            "CK_ImportBatches_StatusMatchesCounters",
            sqlException.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Completed_at_check_rejects_completion_before_creation()
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        SqlException sqlException = await Assert.ThrowsAsync<SqlException>(
            () => InsertBatchAsync(
                context,
                status: (int)ImportBatchStatus.Completed,
                totalRows: 1,
                importedRows: 1,
                alreadyExistsRows: 0,
                rejectedRows: 0,
                createdAt: ImportPersistenceData.CompletedAt,
                completedAt: ImportPersistenceData.CreatedAt));

        Assert.Equal(547, sqlException.Number);
        Assert.Contains(
            "CK_ImportBatches_CompletedAt",
            sqlException.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Row_error_primary_key_rejects_a_second_error_for_the_same_data_row()
    {
        OrganizationId organizationId = OrganizationId.New();
        ImportBatch batch = ImportPersistenceData.RejectedBatch(
            organizationId,
            ImportPersistenceData.Hash('1'));

        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.ImportBatches.Add(batch);
            await context.SaveChangesAsync();
        }

        await using ReconciliationDbContext duplicateContext = _fixture.CreateContext();
        Guid organization = organizationId.Value;
        Guid importBatchId = batch.Id.Value;
        SqlException sqlException = await Assert.ThrowsAsync<SqlException>(
            () => duplicateContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO [reconciliation].[ImportRowErrors]
                    ([OrganizationId], [ImportBatchId], [DataRowIndex], [ErrorCode], [Message])
                VALUES
                    ({organization}, {importBatchId}, {1}, N'reconciliation.import.row_invalid', N'Duplicate logical row.');
                """));

        Assert.True(sqlException.Number is 2601 or 2627);
        Assert.Contains(
            "PK_ImportRowErrors",
            sqlException.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Row_error_foreign_key_rejects_a_different_tenant()
    {
        OrganizationId organizationId = OrganizationId.New();
        ImportBatch batch = ImportPersistenceData.CompletedBatch(
            organizationId,
            ImportType.Invoice,
            "MBANK-ACCT-1",
            ImportPersistenceData.Hash('2'));

        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.ImportBatches.Add(batch);
            await context.SaveChangesAsync();
        }

        await using ReconciliationDbContext foreignContext = _fixture.CreateContext();
        Guid otherOrganizationId = OrganizationId.New().Value;
        Guid importBatchId = batch.Id.Value;
        SqlException sqlException = await Assert.ThrowsAsync<SqlException>(
            () => foreignContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO [reconciliation].[ImportRowErrors]
                    ([OrganizationId], [ImportBatchId], [DataRowIndex], [ErrorCode], [Message])
                VALUES
                    ({otherOrganizationId}, {importBatchId}, {1}, N'reconciliation.import.row_invalid', N'Wrong tenant.');
                """));

        Assert.Equal(547, sqlException.Number);
        Assert.Contains(
            "FK_ImportRowErrors_ImportBatches_OrganizationId_ImportBatchId",
            sqlException.Message,
            StringComparison.Ordinal);
    }

    private static Task InsertBatchAsync(
        ReconciliationDbContext context,
        int status,
        int totalRows,
        int importedRows,
        int alreadyExistsRows,
        int rejectedRows,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? completedAt = null)
    {
        Guid id = Guid.NewGuid();
        Guid organizationId = Guid.NewGuid();
        DateTimeOffset created = createdAt ?? ImportPersistenceData.CreatedAt;
        DateTimeOffset completed = completedAt ?? ImportPersistenceData.CompletedAt;
        string fileHash = new('a', FileHash.HexLength);

        return context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO [reconciliation].[ImportBatches]
                ([Id], [OrganizationId], [ImportType], [SourceSystem], [FileHash], [FileName],
                 [Status], [TotalRows], [ImportedRows], [AlreadyExistsRows], [RejectedRows],
                 [CreatedAt], [CompletedAt])
            VALUES
                ({id}, {organizationId}, {(int)ImportType.Invoice}, N'MBANK-ACCT-1', {fileHash}, N'constraint.csv',
                 {status}, {totalRows}, {importedRows}, {alreadyExistsRows}, {rejectedRows},
                 {created}, {completed});
            """);
    }
}