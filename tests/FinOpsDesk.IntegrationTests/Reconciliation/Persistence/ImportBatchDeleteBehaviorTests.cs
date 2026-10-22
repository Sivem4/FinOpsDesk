using System.Reflection;

using FinOpsDesk.Modules.Reconciliation.Domain.Imports;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class ImportBatchDeleteBehaviorTests
{
    private readonly SqlServerFixture _fixture;

    public ImportBatchDeleteBehaviorTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Ownership_model_is_cascade_while_the_foreign_key_is_no_action()
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        IForeignKey foreignKey = context.Model
            .FindEntityType(typeof(ImportBatch))!
            .FindNavigation(nameof(ImportBatch.RowErrors))!
            .ForeignKey;

        Assert.True(foreignKey.IsOwnership);
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);

        string? deleteAction = await SqlServerCatalog.ScalarStringAsync(
            context,
            """
            SELECT fk.delete_referential_action_desc
            FROM sys.foreign_keys AS fk
            INNER JOIN sys.tables AS t
                ON fk.parent_object_id = t.object_id
            INNER JOIN sys.schemas AS s
                ON t.schema_id = s.schema_id
            WHERE s.name = N'reconciliation'
                AND t.name = N'ImportRowErrors'
                AND fk.name = N'FK_ImportRowErrors_ImportBatches_OrganizationId_ImportBatchId';
            """);

        Assert.Equal("NO_ACTION", deleteAction);
    }

    [Fact]
    public async Task Deleting_a_tracked_batch_removes_its_loaded_row_errors()
    {
        OrganizationId organizationId = OrganizationId.New();
        ImportBatch batch = await SaveRejectedBatchAsync(organizationId);

        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            ImportBatch tracked = await context.ImportBatches.SingleAsync(
                stored => stored.Id == batch.Id);

            Assert.Equal(2, tracked.RowErrors.Count);
            Assert.Equal(2, context.ChangeTracker.Entries<ImportRowError>().Count());

            context.Remove(tracked);
            await context.SaveChangesAsync();
        }

        await AssertBatchAndErrorsAreGoneAsync(batch.Id);
    }

    [Fact]
    public async Task Deleting_a_batch_without_loaded_row_errors_is_rejected()
    {
        OrganizationId organizationId = OrganizationId.New();
        ImportBatch batch = await SaveRejectedBatchAsync(organizationId);

        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            ImportBatch stub = CreateUnloadedBatch();
            var entry = context.Entry(stub);
            entry.Property(stored => stored.Id).CurrentValue = batch.Id;
            entry.Property(stored => stored.OrganizationId).CurrentValue = organizationId;
            entry.State = EntityState.Deleted;

            Assert.Empty(context.ChangeTracker.Entries<ImportRowError>());

            DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
                () => context.SaveChangesAsync());
            SqlException sqlException = SqlServerCatalog.RequireSqlException(exception);

            Assert.Equal(547, sqlException.Number);
            Assert.Contains(
                "FK_ImportRowErrors_ImportBatches_OrganizationId_ImportBatchId",
                sqlException.Message,
                StringComparison.Ordinal);
        }

        await AssertBatchAndErrorsRemainAsync(batch.Id, 2);
    }

    [Fact]
    public async Task Direct_sql_delete_of_a_batch_with_row_errors_is_rejected()
    {
        OrganizationId organizationId = OrganizationId.New();
        ImportBatch batch = await SaveRejectedBatchAsync(organizationId);

        await using ReconciliationDbContext context = _fixture.CreateContext();
        Guid batchId = batch.Id.Value;
        SqlException sqlException = await Assert.ThrowsAsync<SqlException>(
            () => context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                DELETE FROM [reconciliation].[ImportBatches]
                WHERE [Id] = {batchId};
                """));

        Assert.Equal(547, sqlException.Number);
        Assert.Contains(
            "FK_ImportRowErrors_ImportBatches_OrganizationId_ImportBatchId",
            sqlException.Message,
            StringComparison.Ordinal);

        await AssertBatchAndErrorsRemainAsync(batch.Id, 2);
    }

    private async Task<ImportBatch> SaveRejectedBatchAsync(OrganizationId organizationId)
    {
        ImportBatch batch = ImportPersistenceData.RejectedBatch(
            organizationId,
            ImportPersistenceData.Hash('7'));

        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.ImportBatches.Add(batch);
        await context.SaveChangesAsync();
        return batch;
    }

    private async Task AssertBatchAndErrorsAreGoneAsync(ImportBatchId batchId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Assert.False(await context.ImportBatches.AnyAsync(stored => stored.Id == batchId));
        Assert.Equal("0", await CountErrorsAsync(context, batchId));
    }

    private async Task AssertBatchAndErrorsRemainAsync(ImportBatchId batchId, int errorCount)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        ImportBatch stored = await context.ImportBatches.SingleAsync(batch => batch.Id == batchId);
        Assert.Equal(errorCount, stored.RowErrors.Count);
        Assert.Equal(errorCount.ToString(), await CountErrorsAsync(context, batchId));
    }

    private static async Task<string?> CountErrorsAsync(
        ReconciliationDbContext context,
        ImportBatchId batchId)
    {
        return await SqlServerCatalog.ScalarStringAsync(
            context,
            """
            SELECT CONVERT(nvarchar(20), COUNT(*))
            FROM [reconciliation].[ImportRowErrors]
            WHERE [ImportBatchId] = @importBatchId;
            """,
            ("@importBatchId", batchId.Value));
    }

    private static ImportBatch CreateUnloadedBatch()
    {
        object batch = Activator.CreateInstance(
            typeof(ImportBatch),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: null,
            culture: null)!;

        return (ImportBatch)batch;
    }
}