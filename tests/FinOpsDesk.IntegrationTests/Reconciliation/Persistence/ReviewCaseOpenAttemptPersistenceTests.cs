using System.Data;

using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Repositories;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class ReviewCaseOpenAttemptPersistenceTests
{
    private readonly SqlServerFixture _fixture;

    public ReviewCaseOpenAttemptPersistenceTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Primary_key_is_the_only_uniqueness_constraint_for_the_key()
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        (string Name, string Type, IReadOnlyList<string> Columns) key = await ReadPrimaryKeyAsync(context);
        IReadOnlyList<(string Name, bool Primary)> uniqueIndexes = await ReadUniqueIndexesAsync(context);

        Assert.Equal(ReviewCaseOpenAttemptConfiguration.PrimaryKeyName, key.Name);
        Assert.Equal("PRIMARY_KEY_CONSTRAINT", key.Type);
        Assert.Equal(["OrganizationId", "IdempotencyKey"], key.Columns);
        Assert.Equal([(ReviewCaseOpenAttemptConfiguration.PrimaryKeyName, true)], uniqueIndexes);
    }

    [Fact]
    public async Task Duplicate_primary_key_is_2627_and_a_failed_batch_commits_nothing()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 20m);
        ReviewCase reviewCase = ReviewCase.Open(
            ReviewCaseId.New(),
            organizationId,
            ReviewCaseReason.NeedsInvestigation,
            invoice.Id,
            null,
            ReviewCaseSource.Manual,
            "kept",
            new DateTimeOffset(2026, 11, 5, 8, 0, 0, TimeSpan.Zero));
        Guid key = Guid.NewGuid();
        await using (ReconciliationDbContext setup = _fixture.CreateContext())
        {
            setup.Add(invoice);
            setup.Add(reviewCase);
            setup.Add(ReviewCaseAuditEntry.ForOpened(ReviewCaseAuditEntryId.New(), reviewCase, "operator-a"));
            setup.Add(Attempt(organizationId, key, reviewCase));
            await setup.SaveChangesAsync();
        }

        Invoice otherInvoice = PersistenceTestData.CreateInvoice(organizationId, 30m, "INV-OTHER");
        ReviewCase second = ReviewCase.Open(
            ReviewCaseId.New(),
            organizationId,
            ReviewCaseReason.NeedsInvestigation,
            otherInvoice.Id,
            null,
            ReviewCaseSource.Manual,
            "second",
            new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero));
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.Add(otherInvoice);
        context.Add(second);
        context.Add(ReviewCaseAuditEntry.ForOpened(ReviewCaseAuditEntryId.New(), second, "operator-b"));
        context.Add(Attempt(organizationId, key, second));

        DbUpdateException failure = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        SqlException sqlException = SqlServerCatalog.RequireSqlException(failure);
        Assert.Equal(2627, sqlException.Number);
        Assert.True(new ReviewCaseOpenAttemptConflict().IsPrimaryKeyConflict(
            new UniqueConstraintConflictException("duplicate", failure)));
        Assert.False(new ReviewCaseOpenConflict().IsOpenSubjectConflict(failure));

        await using ReconciliationDbContext check = _fixture.CreateContext();
        Assert.Equal(1, await check.ReviewCases.CountAsync(candidate => candidate.OrganizationId == organizationId));
        Assert.Equal(0, await check.ReviewCases.CountAsync(candidate => candidate.Id == second.Id));
        Assert.Equal(1, await check.ReviewCaseAuditEntries.CountAsync(entry => entry.OrganizationId == organizationId));
        Assert.Equal(1, await check.ReviewCaseOpenAttempts.CountAsync(attempt => attempt.OrganizationId == organizationId));
    }

    private static ReviewCaseOpenAttempt Attempt(
        OrganizationId organizationId,
        Guid key,
        ReviewCase reviewCase)
    {
        return ReviewCaseOpenAttempt.Create(
            organizationId,
            key,
            reviewCase.Id,
            reviewCase.Reason,
            reviewCase.InvoiceId,
            reviewCase.BankTransactionId,
            reviewCase.SourceLabel,
            reviewCase.Note);
    }

    private static async Task<(string Name, string Type, IReadOnlyList<string> Columns)> ReadPrimaryKeyAsync(
        ReconciliationDbContext context)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT kc.name, kc.type_desc, c.name
            FROM sys.key_constraints AS kc
            INNER JOIN sys.index_columns AS ic
                ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
            INNER JOIN sys.columns AS c
                ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE kc.parent_object_id = OBJECT_ID(N'[reconciliation].[ReviewCaseOpenAttempts]')
            ORDER BY ic.key_ordinal
            """;
        if (command.Connection!.State != ConnectionState.Open)
        {
            await command.Connection.OpenAsync();
        }

        string name = string.Empty;
        string type = string.Empty;
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            name = reader.GetString(0);
            type = reader.GetString(1);
            columns.Add(reader.GetString(2));
        }

        return (name, type, columns);
    }

    private static async Task<IReadOnlyList<(string Name, bool Primary)>> ReadUniqueIndexesAsync(
        ReconciliationDbContext context)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT name, is_primary_key
            FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'[reconciliation].[ReviewCaseOpenAttempts]')
              AND is_unique = 1
            ORDER BY name
            """;
        if (command.Connection!.State != ConnectionState.Open)
        {
            await command.Connection.OpenAsync();
        }

        var indexes = new List<(string Name, bool Primary)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            indexes.Add((reader.GetString(0), reader.GetBoolean(1)));
        }

        return indexes;
    }
}