using System.Data;

using FinOpsDesk.Modules.Reconciliation.Application.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Queries;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Repositories;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class AllocationReversalPersistenceTests
{
    private static readonly DateTimeOffset ConfirmedAt = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset ReversedAt = ConfirmedAt.AddMinutes(10);

    private readonly SqlServerFixture _fixture;

    public AllocationReversalPersistenceTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Migrated_schema_keeps_existing_confirmation_rules_and_adds_reversal_storage()
    {
        Assert.Equal(189, await ScalarAsync<int>(
            """
            SELECT CAST(c.system_type_id AS int)
            FROM sys.columns AS c
            INNER JOIN sys.tables AS t ON t.object_id = c.object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE s.name = N'reconciliation' AND t.name = N'Allocations' AND c.name = N'RowVersion'
            """));
        Assert.Equal(1, await ScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM sys.indexes
            WHERE name = N'UX_Allocations_OrganizationId_ReversalIdempotencyKey'
              AND is_unique = 1
              AND has_filter = 1
              AND filter_definition LIKE N'%ReversalIdempotencyKey%IS NOT NULL%'
            """));
        Assert.Equal(1, await ScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM sys.indexes
            WHERE name = N'UX_Allocations_OrganizationId_IdempotencyKey'
              AND is_unique = 1
              AND has_filter = 1
              AND filter_definition LIKE N'%IdempotencyKey%IS NOT NULL%'
              AND filter_definition NOT LIKE N'%ReversalIdempotencyKey%'
            """));

        string allocationOperationCheck = await ScalarAsync<string>(
            "SELECT definition FROM sys.check_constraints WHERE name = N'CK_Allocations_IdempotencyOperation'");
        Assert.Contains("[IdempotencyOperation]=(1)", allocationOperationCheck, StringComparison.Ordinal);
        Assert.Contains("[IdempotencyOperation]=(2)", allocationOperationCheck, StringComparison.Ordinal);
        Assert.Contains("[IdempotencyOperation]=(3)", allocationOperationCheck, StringComparison.Ordinal);
        Assert.DoesNotContain("4", allocationOperationCheck, StringComparison.Ordinal);

        string actionCheck = await ScalarAsync<string>(
            "SELECT definition FROM sys.check_constraints WHERE name = N'CK_FinancialAuditEntries_Action'");
        Assert.Contains("1", actionCheck, StringComparison.Ordinal);
        Assert.Contains("2", actionCheck, StringComparison.Ordinal);
        Assert.Equal(1000, await ScalarAsync<int>(
            """
            SELECT CAST(c.max_length AS int)
            FROM sys.columns AS c
            INNER JOIN sys.tables AS t ON t.object_id = c.object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE s.name = N'reconciliation'
              AND t.name = N'FinancialAuditEntries'
              AND c.name = N'Reason'
              AND c.is_nullable = 1
            """));
        Assert.Equal(-1, await ScalarAsync<int>(
            """
            SELECT CAST(c.max_length AS int)
            FROM sys.columns AS c
            INNER JOIN sys.tables AS t ON t.object_id = c.object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE s.name = N'reconciliation' AND t.name = N'Allocations' AND c.name = N'ReversalReason'
            """));
    }

    [Fact]
    public async Task Insert_generates_rowversion_and_a_stale_allocation_update_is_rejected()
    {
        OrganizationId organizationId = OrganizationId.New();
        AllocationId allocationId = await SeedAllocationAsync(
            organizationId,
            Guid.NewGuid(),
            AllocationIdempotencyOperation.ExactMatch);

        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            Allocation stored = await context.Allocations.SingleAsync(candidate => candidate.Id == allocationId);
            byte[] rowVersion = context.Entry(stored)
                .Property<byte[]>(AllocationConfiguration.RowVersionPropertyName)
                .CurrentValue!;
            Assert.Equal(8, rowVersion.Length);
            Assert.NotEqual(new byte[8], rowVersion);
        }

        await using ReconciliationDbContext first = _fixture.CreateContext();
        await using ReconciliationDbContext second = _fixture.CreateContext();
        Allocation winner = await first.Allocations.SingleAsync(candidate => candidate.Id == allocationId);
        Allocation loser = await second.Allocations.SingleAsync(candidate => candidate.Id == allocationId);
        winner.Reverse("first correction", ReversedAt);
        await first.SaveChangesAsync();
        loser.Reverse("second correction", ReversedAt.AddMinutes(1));

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());

        await using ReconciliationDbContext verify = _fixture.CreateContext();
        Allocation persisted = await verify.Allocations.SingleAsync(candidate => candidate.Id == allocationId);
        Assert.Equal(AllocationStatus.Reversed, persisted.Status);
        Assert.Equal("first correction", persisted.ReversalReason);
        Assert.Equal(ReversedAt, persisted.ReversedAt);
    }

    [Fact]
    public async Task Confirmation_and_reversal_audits_share_an_allocation_and_a_second_reversal_is_rejected()
    {
        OrganizationId organizationId = OrganizationId.New();
        Guid sharedKey = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        AllocationId allocationId = await SeedAllocationAsync(
            organizationId,
            sharedKey,
            AllocationIdempotencyOperation.ExactMatch,
            42.5m);

        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            Allocation allocation = await context.Allocations.SingleAsync(candidate => candidate.Id == allocationId);
            new FinancialAuditWriter(context).AppendConfirmation(allocation, "confirm-operator", batchExecutionId: null);
            Assert.Equal(0, await CountAuditsAsync(organizationId));
            await context.SaveChangesAsync();
        }

        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            Allocation allocation = await context.Allocations.SingleAsync(candidate => candidate.Id == allocationId);
            allocation.Reverse("  customer refund  ", ReversedAt);
            AllocationReversalIdempotency.Assign(context, allocation, sharedKey);
            new FinancialAuditWriter(context).AppendReversal(allocation, "reversal-operator", sharedKey);
            Assert.Equal(1, await CountAuditsAsync(organizationId));
            await context.SaveChangesAsync();

            FinancialAuditEntry duplicate = FinancialAuditEntry.ForReversal(
                FinancialAuditEntryId.New(),
                allocation,
                "reversal-operator",
                Guid.NewGuid());
            context.FinancialAuditEntries.Add(duplicate);
            DbUpdateException duplicateFailure = await Assert.ThrowsAsync<DbUpdateException>(
                () => context.SaveChangesAsync());
            AssertUniqueIndex(
                duplicateFailure,
                FinancialAuditEntryConfiguration.UniqueIndexName);
        }

        await using ReconciliationDbContext read = _fixture.CreateContext();
        List<FinancialAuditEntry> rows = await read.FinancialAuditEntries
            .Where(entry => entry.OrganizationId == organizationId)
            .OrderBy(entry => entry.RecordedAt)
            .ThenBy(entry => entry.Id)
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(FinancialAuditAction.Confirmation, rows[0].Action);
        Assert.Equal(AllocationIdempotencyOperation.ExactMatch, rows[0].IdempotencyOperation);
        Assert.Equal(sharedKey, rows[0].IdempotencyKey);
        Assert.Null(rows[0].Reason);
        Assert.Null(rows[0].BatchExecutionId);
        Assert.Equal(FinancialAuditAction.Reversal, rows[1].Action);
        Assert.Equal(AllocationIdempotencyOperation.Reversal, rows[1].IdempotencyOperation);
        Assert.Equal(sharedKey, rows[1].IdempotencyKey);
        Assert.Equal("customer refund", rows[1].Reason);
        Assert.Null(rows[1].BatchExecutionId);
        Assert.Equal(42.5m, rows[1].Amount.Amount);
        Assert.Equal("PLN", rows[1].Amount.Currency.Value);
        Assert.Equal("reversal-operator", rows[1].ActorId);
        Assert.Equal(ReversedAt, rows[1].RecordedAt);
        Assert.Equal(allocationId, rows[1].AllocationId);

        Allocation stored = await read.Allocations.SingleAsync(candidate => candidate.Id == allocationId);
        Assert.Equal(sharedKey, stored.IdempotencyKey);
        Assert.Equal(AllocationIdempotencyOperation.ExactMatch, stored.IdempotencyOperation);
        Assert.Equal(sharedKey, read.Entry(stored)
            .Property<Guid?>(AllocationConfiguration.ReversalIdempotencyKeyPropertyName)
            .CurrentValue);
    }

    [Fact]
    public async Task Reversal_idempotency_key_is_unique_inside_one_organization()
    {
        OrganizationId organizationId = OrganizationId.New();
        OrganizationId otherOrganizationId = OrganizationId.New();
        Guid reversalKey = Guid.NewGuid();
        AllocationId firstId = await SeedAllocationAsync(
            organizationId,
            Guid.NewGuid(),
            AllocationIdempotencyOperation.ExplicitAmount);
        AllocationId secondId = await SeedAllocationAsync(
            organizationId,
            Guid.NewGuid(),
            AllocationIdempotencyOperation.ExplicitAmount);
        AllocationId otherId = await SeedAllocationAsync(
            otherOrganizationId,
            Guid.NewGuid(),
            AllocationIdempotencyOperation.ExplicitAmount);

        await AssignReversalKeyAsync(firstId, reversalKey);
        DbUpdateException duplicate = await Assert.ThrowsAsync<DbUpdateException>(
            () => AssignReversalKeyAsync(secondId, reversalKey));
        AssertUniqueIndex(duplicate, AllocationConfiguration.ReversalIdempotencyIndexName);
        await AssignReversalKeyAsync(otherId, reversalKey);

        await using ReconciliationDbContext context = _fixture.CreateContext();
        Allocation first = await context.Allocations.SingleAsync(candidate => candidate.Id == firstId);
        Allocation other = await context.Allocations.SingleAsync(candidate => candidate.Id == otherId);
        Assert.Equal(reversalKey, context.Entry(first)
            .Property<Guid?>(AllocationConfiguration.ReversalIdempotencyKeyPropertyName)
            .CurrentValue);
        Assert.Equal(reversalKey, context.Entry(other)
            .Property<Guid?>(AllocationConfiguration.ReversalIdempotencyKeyPropertyName)
            .CurrentValue);
    }

    [Fact]
    public async Task Historical_allocation_without_confirmation_audit_accepts_a_reversal_audit()
    {
        OrganizationId organizationId = OrganizationId.New();
        Guid reversalKey = Guid.NewGuid();
        AllocationId allocationId = await SeedAllocationAsync(
            organizationId,
            idempotencyKey: null,
            operation: null,
            amount: 18m);

        await using ReconciliationDbContext context = _fixture.CreateContext();
        Allocation allocation = await context.Allocations.SingleAsync(candidate => candidate.Id == allocationId);
        allocation.Reverse("opening balance correction", ReversedAt);
        AllocationReversalIdempotency.Assign(context, allocation, reversalKey);
        new FinancialAuditWriter(context).AppendReversal(allocation, "history-operator", reversalKey);
        await context.SaveChangesAsync();

        Result<FinancialAuditListResponse> result = await new FinancialAuditQueries(context)
            .GetForAllocationAsync(organizationId, allocationId, page: 1, pageSize: 50, CancellationToken.None);

        Assert.True(result.IsSuccess);
        FinancialAuditEntryResponse item = Assert.Single(result.Value.Items);
        Assert.Equal("reversal", item.Action);
        Assert.Equal("reversal", item.Operation);
        Assert.Equal("opening balance correction", item.Reason);
        Assert.Null(item.BatchExecutionId);
        Assert.Equal(18m, item.Amount.Amount);
        Assert.Equal("PLN", item.Amount.Currency);
        Assert.Equal("history-operator", item.ActorId);
        Assert.Equal(ReversedAt, item.RecordedAt);
        Assert.Equal(allocationId.Value, item.AllocationId);
        Assert.Null(allocation.IdempotencyKey);
        Assert.Null(allocation.IdempotencyOperation);
    }

    [Fact]
    public async Task Confirmation_and_reversal_rows_are_mapped_together()
    {
        OrganizationId organizationId = OrganizationId.New();
        AllocationId allocationId = await SeedAllocationAsync(
            organizationId,
            Guid.NewGuid(),
            AllocationIdempotencyOperation.ExactMatch,
            25m);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Allocation allocation = await context.Allocations.SingleAsync(candidate => candidate.Id == allocationId);
        new FinancialAuditWriter(context).AppendConfirmation(allocation, "confirm-operator", batchExecutionId: null);
        await context.SaveChangesAsync();
        allocation.Reverse("mapped reason", ReversedAt);
        Guid reversalKey = Guid.NewGuid();
        AllocationReversalIdempotency.Assign(context, allocation, reversalKey);
        new FinancialAuditWriter(context).AppendReversal(allocation, "reversal-operator", reversalKey);
        await context.SaveChangesAsync();

        Result<FinancialAuditListResponse> result = await new FinancialAuditQueries(context)
            .GetForAllocationAsync(organizationId, allocationId, page: 1, pageSize: 50, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.TotalCount);
        Assert.Equal("confirmation", result.Value.Items[0].Action);
        Assert.Equal("exactMatch", result.Value.Items[0].Operation);
        Assert.Null(result.Value.Items[0].Reason);
        Assert.Equal("reversal", result.Value.Items[1].Action);
        Assert.Equal("reversal", result.Value.Items[1].Operation);
        Assert.Equal("mapped reason", result.Value.Items[1].Reason);
        Assert.Null(result.Value.Items[1].BatchExecutionId);
        Assert.Equal(25m, result.Value.Items[1].Amount.Amount);
    }

    [Fact]
    public async Task Invalid_action_operation_and_reason_combinations_are_rejected()
    {
        OrganizationId organizationId = OrganizationId.New();
        AllocationId allocationId = await SeedAllocationAsync(
            organizationId,
            Guid.NewGuid(),
            AllocationIdempotencyOperation.ExactMatch);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Allocation allocation = await context.Allocations.SingleAsync(candidate => candidate.Id == allocationId);
        BatchExecutionId batchId = await SeedBatchAsync(organizationId);

        SqlException reversalWithConfirmationOperation = await InsertAuditExpectingFailureAsync(
            organizationId,
            allocation,
            action: 2,
            operation: 1,
            reason: "customer refund",
            batchExecutionId: null);
        SqlException confirmationWithReversalOperation = await InsertAuditExpectingFailureAsync(
            organizationId,
            allocation,
            action: 1,
            operation: 4,
            reason: null,
            batchExecutionId: null);
        SqlException reversalWithoutReason = await InsertAuditExpectingFailureAsync(
            organizationId,
            allocation,
            action: 2,
            operation: 4,
            reason: null,
            batchExecutionId: null);
        SqlException confirmationWithReason = await InsertAuditExpectingFailureAsync(
            organizationId,
            allocation,
            action: 1,
            operation: 1,
            reason: "should be empty",
            batchExecutionId: null);
        SqlException trailingReason = await InsertAuditExpectingFailureAsync(
            organizationId,
            allocation,
            action: 2,
            operation: 4,
            reason: "customer refund ",
            batchExecutionId: null);
        SqlException reversalWithBatch = await InsertAuditExpectingFailureAsync(
            organizationId,
            allocation,
            action: 2,
            operation: 4,
            reason: "customer refund",
            batchExecutionId: batchId);

        AssertCheck(reversalWithConfirmationOperation, FinancialAuditEntryConfiguration.ActionOperationCheckName);
        AssertCheck(confirmationWithReversalOperation, FinancialAuditEntryConfiguration.ActionOperationCheckName);
        AssertCheck(reversalWithoutReason, FinancialAuditEntryConfiguration.ReasonCheckName);
        AssertCheck(confirmationWithReason, FinancialAuditEntryConfiguration.ReasonCheckName);
        AssertCheck(trailingReason, FinancialAuditEntryConfiguration.ReasonCheckName);
        AssertCheck(reversalWithBatch, FinancialAuditEntryConfiguration.BatchReferenceCheckName);
        Assert.Equal(0, await CountAuditsAsync(organizationId));
    }

    [Fact]
    public async Task Append_only_trigger_rejects_reversal_audit_changes()
    {
        OrganizationId organizationId = OrganizationId.New();
        AllocationId allocationId = await SeedAllocationAsync(
            organizationId,
            Guid.NewGuid(),
            AllocationIdempotencyOperation.ExactMatch);
        Guid reversalKey = Guid.NewGuid();
        Guid auditId;
        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            Allocation allocation = await context.Allocations.SingleAsync(candidate => candidate.Id == allocationId);
            allocation.Reverse("protected reason", ReversedAt);
            AllocationReversalIdempotency.Assign(context, allocation, reversalKey);
            new FinancialAuditWriter(context).AppendReversal(allocation, "reversal-operator", reversalKey);
            auditId = context.FinancialAuditEntries.Local.Single().Id.Value;
            await context.SaveChangesAsync();
        }

        SqlException updated = await ExecuteExpectingSqlExceptionAsync(
            """
            UPDATE [reconciliation].[FinancialAuditEntries]
            SET [Reason] = N'changed reason'
            WHERE [Id] = @id
            """,
            ("@id", auditId));
        SqlException deleted = await ExecuteExpectingSqlExceptionAsync(
            """
            DELETE FROM [reconciliation].[FinancialAuditEntries]
            WHERE [Id] = @id
            """,
            ("@id", auditId));

        Assert.True(ContainsNumber(updated, 50001));
        Assert.True(ContainsNumber(deleted, 50001));
        await using ReconciliationDbContext verify = _fixture.CreateContext();
        FinancialAuditEntry stored = await verify.FinancialAuditEntries.SingleAsync(
            entry => entry.OrganizationId == organizationId);
        Assert.Equal("protected reason", stored.Reason);
        Assert.Equal(FinancialAuditAction.Reversal, stored.Action);
    }

    private async Task<AllocationId> SeedAllocationAsync(
        OrganizationId organizationId,
        Guid? idempotencyKey,
        AllocationIdempotencyOperation? operation,
        decimal amount = 25m)
    {
        Invoice invoice = PersistenceTestData.CreateInvoice(
            organizationId,
            100m,
            "INV-" + Guid.NewGuid().ToString("N")[..8]);
        BankTransaction bank = PersistenceTestData.CreateBankTransaction(
            organizationId,
            100m,
            reference: invoice.InvoiceNumber);
        var allocation = new Allocation(
            AllocationId.New(),
            organizationId,
            invoice.Id,
            bank.Id,
            new Money(amount, PersistenceTestData.Pln),
            ConfirmedAt,
            idempotencyKey,
            operation);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.Invoices.Add(invoice);
        context.BankTransactions.Add(bank);
        context.Allocations.Add(allocation);
        await context.SaveChangesAsync();
        return allocation.Id;
    }

    private async Task AssignReversalKeyAsync(AllocationId allocationId, Guid reversalKey)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Allocation allocation = await context.Allocations.SingleAsync(candidate => candidate.Id == allocationId);
        AllocationReversalIdempotency.Assign(context, allocation, reversalKey);
        await context.SaveChangesAsync();
    }

    private async Task<BatchExecutionId> SeedBatchAsync(OrganizationId organizationId)
    {
        var execution = BatchExecution.Start(
            BatchExecutionId.New(),
            organizationId,
            Guid.NewGuid(),
            new BatchExecutionFingerprint(new byte[BatchExecutionFingerprint.Length]),
            ConfirmedAt);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.BatchExecutions.Add(execution);
        await context.SaveChangesAsync();
        return execution.Id;
    }

    private async Task<int> CountAuditsAsync(OrganizationId organizationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.FinancialAuditEntries.CountAsync(
            entry => entry.OrganizationId == organizationId);
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        object? value = await command.ExecuteScalarAsync();
        return (T)value!;
    }

    private async Task<SqlException> InsertAuditExpectingFailureAsync(
        OrganizationId organizationId,
        Allocation allocation,
        int action,
        int operation,
        string? reason,
        BatchExecutionId? batchExecutionId)
    {
        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO [reconciliation].[FinancialAuditEntries]
                ([Id], [OrganizationId], [AllocationId], [InvoiceId], [BankTransactionId],
                 [BatchExecutionId], [Action], [ActorId], [RecordedAt], [IdempotencyOperation],
                 [IdempotencyKey], [Amount], [Currency], [Reason])
            VALUES
                (@id, @organizationId, @allocationId, @invoiceId, @bankTransactionId,
                 @batchExecutionId, @action, @actorId, @recordedAt, @idempotencyOperation,
                 @idempotencyKey, @amount, @currency, @reason)
            """;
        command.Parameters.Add(new SqlParameter("@id", Guid.NewGuid()));
        command.Parameters.Add(new SqlParameter("@organizationId", organizationId.Value));
        command.Parameters.Add(new SqlParameter("@allocationId", allocation.Id.Value));
        command.Parameters.Add(new SqlParameter("@invoiceId", allocation.InvoiceId.Value));
        command.Parameters.Add(new SqlParameter("@bankTransactionId", allocation.BankTransactionId.Value));
        command.Parameters.Add(new SqlParameter("@batchExecutionId", batchExecutionId?.Value ?? (object)DBNull.Value));
        command.Parameters.Add(new SqlParameter("@action", action));
        command.Parameters.Add(new SqlParameter("@actorId", "raw-operator"));
        command.Parameters.Add(new SqlParameter("@recordedAt", ReversedAt));
        command.Parameters.Add(new SqlParameter("@idempotencyOperation", operation));
        command.Parameters.Add(new SqlParameter("@idempotencyKey", Guid.NewGuid()));
        SqlParameter amount = command.Parameters.Add("@amount", SqlDbType.Decimal);
        amount.Precision = 19;
        amount.Scale = 4;
        amount.Value = allocation.Amount.Amount;
        command.Parameters.Add(new SqlParameter("@currency", allocation.Amount.Currency.Value));
        command.Parameters.Add(new SqlParameter("@reason", reason ?? (object)DBNull.Value));
        try
        {
            await command.ExecuteNonQueryAsync();
        }
        catch (SqlException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("The audit insert was expected to fail.");
    }

    private async Task<SqlException> ExecuteExpectingSqlExceptionAsync(
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.Add(new SqlParameter(name, value));
        }

        try
        {
            await command.ExecuteNonQueryAsync();
        }
        catch (SqlException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("The SQL statement was expected to fail.");
    }

    private static void AssertCheck(SqlException exception, string constraintName)
    {
        Assert.Equal(547, exception.Number);
        Assert.Contains(constraintName, exception.Message, StringComparison.Ordinal);
    }

    private static void AssertUniqueIndex(DbUpdateException exception, string indexName)
    {
        SqlException sqlException = GetSqlException(exception);
        Assert.True(sqlException.Number is 2601 or 2627);
        Assert.Contains(indexName, sqlException.Message, StringComparison.Ordinal);
    }

    private static SqlException GetSqlException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException)
            {
                return sqlException;
            }
        }

        throw new InvalidOperationException("Expected a SqlException.", exception);
    }

    private static bool ContainsNumber(SqlException exception, int number)
    {
        if (exception.Number == number)
        {
            return true;
        }

        foreach (SqlError error in exception.Errors)
        {
            if (error.Number == number)
            {
                return true;
            }
        }

        return false;
    }
}