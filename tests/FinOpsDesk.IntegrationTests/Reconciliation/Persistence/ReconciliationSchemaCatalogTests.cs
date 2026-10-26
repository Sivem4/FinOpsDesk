using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class ReconciliationSchemaCatalogTests
{
    private const string BinaryCollation = "Latin1_General_100_BIN2";

    private readonly SqlServerFixture _fixture;

    public ReconciliationSchemaCatalogTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Source_identity_and_import_indexes_exist()
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();

        await AssertUniqueIndexAsync(
            context,
            "Invoices",
            "UX_Invoices_OrganizationId_SourceSystem_ExternalId",
            filtered: true,
            "OrganizationId",
            "SourceSystem",
            "ExternalId");
        await AssertUniqueIndexAsync(
            context,
            "BankTransactions",
            "UX_BankTransactions_OrganizationId_SourceSystem_ExternalId",
            filtered: true,
            "OrganizationId",
            "SourceSystem",
            "ExternalId");
        await AssertUniqueIndexAsync(
            context,
            "ImportBatches",
            "UX_ImportBatches_OrganizationId_ImportType_SourceSystem_FileHash",
            filtered: false,
            "OrganizationId",
            "ImportType",
            "SourceSystem",
            "FileHash");
    }

    [Fact]
    public async Task Source_identity_and_import_check_constraints_exist()
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();

        await AssertCheckAsync(
            context,
            "Invoices",
            "CK_Invoices_SourceIdentity_NullPair",
            "SourceSystem",
            "ExternalId");
        await AssertCheckAsync(
            context,
            "BankTransactions",
            "CK_BankTransactions_SourceIdentity_NullPair",
            "SourceSystem",
            "ExternalId");
        await AssertCheckAsync(
            context,
            "ImportBatches",
            "CK_ImportBatches_TerminalStatus",
            "[Status]=(2)",
            "[Status]=(3)",
            "[Status]=(4)");
        await AssertCheckAsync(
            context,
            "ImportBatches",
            "CK_ImportBatches_CompletedAt",
            "CompletedAt");
        await AssertCheckAsync(context, "ImportBatches", "CK_ImportBatches_Counters", "TotalRows");
        await AssertCheckAsync(
            context,
            "ImportBatches",
            "CK_ImportBatches_StatusMatchesCounters",
            "RejectedRows");
        await AssertCheckAsync(
            context,
            "ImportRowErrors",
            "CK_ImportRowErrors_DataRowIndex",
            "DataRowIndex");
        await AssertCheckAsync(
            context,
            "ImportRowErrors",
            "CK_ImportRowErrors_SourceLineNumber",
            "SourceLineNumber");

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
    public async Task Source_identity_and_file_hash_columns_use_binary_collation()
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();

        Assert.Equal(
            BinaryCollation,
            await SqlServerCatalog.FindColumnCollationAsync(context, "Invoices", "SourceSystem"));
        Assert.Equal(
            BinaryCollation,
            await SqlServerCatalog.FindColumnCollationAsync(context, "Invoices", "ExternalId"));
        Assert.Equal(
            BinaryCollation,
            await SqlServerCatalog.FindColumnCollationAsync(
                context,
                "BankTransactions",
                "SourceSystem"));
        Assert.Equal(
            BinaryCollation,
            await SqlServerCatalog.FindColumnCollationAsync(
                context,
                "BankTransactions",
                "ExternalId"));
        Assert.Equal(
            BinaryCollation,
            await SqlServerCatalog.FindColumnCollationAsync(
                context,
                "ImportBatches",
                "SourceSystem"));
        Assert.Equal(
            BinaryCollation,
            await SqlServerCatalog.FindColumnCollationAsync(context, "ImportBatches", "FileHash"));
    }

    [Fact]
    public async Task Allocation_idempotency_index_and_operation_check_exist()
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        SqlIndex? index = await SqlServerCatalog.FindIndexAsync(
            context,
            "Allocations",
            "UX_Allocations_OrganizationId_IdempotencyKey");

        Assert.NotNull(index);
        Assert.True(index.IsUnique);
        Assert.True(index.HasFilter);
        Assert.NotNull(index.FilterDefinition);
        Assert.Contains("IdempotencyKey", index.FilterDefinition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IS NOT NULL", index.FilterDefinition, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "OrganizationId", "IdempotencyKey" }, index.Columns);

        string? definition = await SqlServerCatalog.FindCheckDefinitionAsync(
            context,
            "Allocations",
            AllocationConfiguration.IdempotencyOperationCheckName);

        Assert.False(string.IsNullOrWhiteSpace(definition));
        Assert.Contains("IdempotencyKey", definition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IdempotencyOperation", definition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("(1)", definition, StringComparison.Ordinal);
        Assert.Contains("(2)", definition, StringComparison.Ordinal);
    }

    private static async Task AssertUniqueIndexAsync(
        ReconciliationDbContext context,
        string tableName,
        string indexName,
        bool filtered,
        params string[] columns)
    {
        SqlIndex? index = await SqlServerCatalog.FindIndexAsync(context, tableName, indexName);

        Assert.NotNull(index);
        Assert.True(index.IsUnique);
        Assert.Equal(filtered, index.HasFilter);
        Assert.Equal(columns, index.Columns);

        if (filtered)
        {
            Assert.NotNull(index.FilterDefinition);
            Assert.Contains("SourceSystem", index.FilterDefinition, StringComparison.Ordinal);
            Assert.Contains("ExternalId", index.FilterDefinition, StringComparison.Ordinal);
            Assert.Contains("IS NOT NULL", index.FilterDefinition, StringComparison.Ordinal);
        }
    }

    private static async Task AssertCheckAsync(
        ReconciliationDbContext context,
        string tableName,
        string constraintName,
        params string[] expectedFragments)
    {
        string? definition = await SqlServerCatalog.FindCheckDefinitionAsync(
            context,
            tableName,
            constraintName);

        Assert.False(string.IsNullOrWhiteSpace(definition));
        foreach (string fragment in expectedFragments)
        {
            Assert.Contains(fragment, definition, StringComparison.OrdinalIgnoreCase);
        }
    }
}