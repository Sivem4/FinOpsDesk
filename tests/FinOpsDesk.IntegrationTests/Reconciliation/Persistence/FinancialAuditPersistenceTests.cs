using System.Data;
using System.Data.Common;

using FinOpsDesk.Modules.Reconciliation.Application.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Repositories;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class FinancialAuditPersistenceTests
{
    private static readonly DateTimeOffset ConfirmedAt = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    private readonly SqlServerFixture _fixture;

    public FinancialAuditPersistenceTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Migration_creates_the_audit_table_and_mutation_trigger()
    {
        Assert.Equal(1, await CountAsync(
            """
            SELECT COUNT(*)
            FROM sys.tables AS t
            INNER JOIN sys.schemas AS s ON t.schema_id = s.schema_id
            WHERE s.name = N'reconciliation' AND t.name = N'FinancialAuditEntries'
            """));
        Assert.Equal(1, await CountAsync(
            """
            SELECT COUNT(*)
            FROM sys.triggers AS tr
            INNER JOIN sys.tables AS t ON tr.parent_id = t.object_id
            INNER JOIN sys.schemas AS s ON t.schema_id = s.schema_id
            WHERE s.name = N'reconciliation'
              AND t.name = N'FinancialAuditEntries'
              AND tr.name = N'TR_FinancialAuditEntries_RejectMutation'
              AND tr.is_disabled = 0
            """));
    }

    [Fact]
    public async Task Writer_persists_one_confirmation_without_opening_its_own_transaction()
    {
        OrganizationId organizationId = OrganizationId.New();
        Guid idempotencyKey = Guid.NewGuid();
        SeededAllocation seeded = await SeedAllocationAsync(
            organizationId,
            AllocationIdempotencyOperation.ExactMatch,
            idempotencyKey);
        var commands = new CommandCapture();
        await using ReconciliationDbContext context = CreateCapturingContext(commands);
        Allocation allocation = await context.Allocations.SingleAsync(candidate => candidate.Id == seeded.AllocationId);
        var writer = new FinancialAuditWriter(context);

        writer.AppendConfirmation(allocation, "operator-a", batchExecutionId: null);

        FinancialAuditEntry tracked = Assert.Single(context.FinancialAuditEntries.Local);
        Assert.Equal(EntityState.Added, context.Entry(tracked).State);
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Equal(0, await CountAuditRowsAsync(organizationId));

        await context.SaveChangesAsync();

        string sql = string.Join(Environment.NewLine, commands.Commands);
        Assert.Contains("INSERT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OUTPUT", sql, StringComparison.OrdinalIgnoreCase);
        AuditRow row = await ReadSingleAsync(organizationId);
        Assert.Equal(seeded.AllocationId.Value, row.AllocationId);
        Assert.Equal(seeded.InvoiceId.Value, row.InvoiceId);
        Assert.Equal(seeded.BankTransactionId.Value, row.BankTransactionId);
        Assert.Null(row.BatchExecutionId);
        Assert.Equal((int)FinancialAuditAction.Confirmation, row.Action);
        Assert.Equal("operator-a", row.ActorId);
        Assert.Equal(25m, row.Amount);
        Assert.Equal("PLN", row.Currency);
        Assert.Equal(ConfirmedAt, row.RecordedAt);
        Assert.Equal((int)AllocationIdempotencyOperation.ExactMatch, row.IdempotencyOperation);
        Assert.Equal(idempotencyKey, row.IdempotencyKey);
    }

    [Fact]
    public async Task Explicit_amount_and_batch_confirmation_keep_their_operation_metadata()
    {
        OrganizationId organizationId = OrganizationId.New();
        Guid explicitKey = Guid.NewGuid();
        SeededAllocation explicitSeed = await SeedAllocationAsync(
            organizationId,
            AllocationIdempotencyOperation.ExplicitAmount,
            explicitKey,
            amount: 11m);
        await AppendAndSaveAsync(explicitSeed.AllocationId, "explicit-operator", batchExecutionId: null);

        Guid batchKey = Guid.NewGuid();
        BatchExecutionId batchExecutionId = await SeedBatchAsync(organizationId, batchKey);
        SeededAllocation batchSeed = await SeedAllocationAsync(
            organizationId,
            AllocationIdempotencyOperation.BatchExactMatch,
            batchKey,
            amount: 12m);
        await AppendAndSaveAsync(batchSeed.AllocationId, "batch-operator", batchExecutionId);

        AuditRow explicitRow = await ReadSingleAsync(organizationId, explicitSeed.AllocationId);
        AuditRow batchRow = await ReadSingleAsync(organizationId, batchSeed.AllocationId);
        Assert.Equal((int)AllocationIdempotencyOperation.ExplicitAmount, explicitRow.IdempotencyOperation);
        Assert.Equal(explicitKey, explicitRow.IdempotencyKey);
        Assert.Null(explicitRow.BatchExecutionId);
        Assert.Equal((int)AllocationIdempotencyOperation.BatchExactMatch, batchRow.IdempotencyOperation);
        Assert.Equal(batchKey, batchRow.IdempotencyKey);
        Assert.Equal(batchExecutionId.Value, batchRow.BatchExecutionId);
    }

    [Fact]
    public async Task Duplicate_confirmation_for_the_same_allocation_is_rejected()
    {
        OrganizationId organizationId = OrganizationId.New();
        SeededAllocation seeded = await SeedAllocationAsync(
            organizationId,
            AllocationIdempotencyOperation.ExactMatch,
            Guid.NewGuid());
        await AppendAndSaveAsync(seeded.AllocationId, "operator-a", batchExecutionId: null);

        await using ReconciliationDbContext context = _fixture.CreateContext();
        Allocation allocation = await context.Allocations.SingleAsync(candidate => candidate.Id == seeded.AllocationId);
        context.FinancialAuditEntries.Add(FinancialAuditEntry.ForConfirmation(
            FinancialAuditEntryId.New(),
            allocation,
            "operator-b",
            batchExecutionId: null));
        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => context.SaveChangesAsync());

        SqlException sqlException = GetSqlException(exception);
        Assert.True(sqlException.Number is 2601 or 2627);
        Assert.Contains(
            FinancialAuditEntryConfiguration.UniqueIndexName,
            sqlException.Message,
            StringComparison.Ordinal);
        Assert.Equal(1, await CountAuditRowsAsync(organizationId));
        Assert.Equal("operator-a", (await ReadSingleAsync(organizationId)).ActorId);
    }

    [Fact]
    public async Task Actor_id_check_accepts_a_trimmed_value_and_rejects_ascii_padding_and_blanks()
    {
        OrganizationId organizationId = OrganizationId.New();
        SeededAllocation seeded = await SeedAllocationAsync(
            organizationId,
            AllocationIdempotencyOperation.ExactMatch,
            Guid.NewGuid());

        SqlException leading = await InsertRawExpectingFailureAsync(
            organizationId,
            seeded.AllocationId,
            seeded.InvoiceId,
            seeded.BankTransactionId,
            batchExecutionId: null,
            AllocationIdempotencyOperation.ExactMatch,
            actorId: " operator-a");
        SqlException trailing = await InsertRawExpectingFailureAsync(
            organizationId,
            seeded.AllocationId,
            seeded.InvoiceId,
            seeded.BankTransactionId,
            batchExecutionId: null,
            AllocationIdempotencyOperation.ExactMatch,
            actorId: "operator-a ");
        SqlException blank = await InsertRawExpectingFailureAsync(
            organizationId,
            seeded.AllocationId,
            seeded.InvoiceId,
            seeded.BankTransactionId,
            batchExecutionId: null,
            AllocationIdempotencyOperation.ExactMatch,
            actorId: "   ");

        AssertActorCheckViolation(leading);
        AssertActorCheckViolation(trailing);
        AssertActorCheckViolation(blank);
        Assert.Equal(0, await CountAuditRowsAsync(organizationId));

        await InsertRawAsync(
            organizationId,
            seeded.AllocationId,
            seeded.InvoiceId,
            seeded.BankTransactionId,
            batchExecutionId: null,
            AllocationIdempotencyOperation.ExactMatch,
            Guid.NewGuid(),
            actorId: "operator-a");

        Assert.Equal("operator-a", (await ReadSingleAsync(organizationId)).ActorId);
    }

    [Fact]
    public async Task Update_and_delete_are_rejected_and_the_row_stays_unchanged()
    {
        OrganizationId organizationId = OrganizationId.New();
        SeededAllocation seeded = await SeedAllocationAsync(
            organizationId,
            AllocationIdempotencyOperation.ExactMatch,
            Guid.NewGuid());
        await AppendAndSaveAsync(seeded.AllocationId, "operator-a", batchExecutionId: null);
        AuditRow before = await ReadSingleAsync(organizationId);

        SqlException updated = await ExecuteExpectingSqlExceptionAsync(
            """
            UPDATE [reconciliation].[FinancialAuditEntries]
            SET [ActorId] = N'changed-actor'
            WHERE [Id] = @id
            """,
            ("@id", before.Id));
        AuditRow afterUpdate = await ReadSingleAsync(organizationId);
        SqlException deleted = await ExecuteExpectingSqlExceptionAsync(
            """
            DELETE FROM [reconciliation].[FinancialAuditEntries]
            WHERE [Id] = @id
            """,
            ("@id", before.Id));
        AuditRow afterDelete = await ReadSingleAsync(organizationId);

        Assert.True(ContainsNumber(updated, 50001));
        Assert.True(ContainsNumber(deleted, 50001));
        Assert.Equal(before, afterUpdate);
        Assert.Equal(before, afterDelete);
    }

    [Fact]
    public async Task Cross_tenant_allocation_invoice_bank_and_batch_references_are_rejected()
    {
        OrganizationId organizationA = OrganizationId.New();
        OrganizationId organizationB = OrganizationId.New();
        SeededAllocation allocationA = await SeedAllocationAsync(
            organizationA,
            AllocationIdempotencyOperation.ExactMatch,
            Guid.NewGuid());
        SeededAllocation allocationB = await SeedAllocationAsync(
            organizationB,
            AllocationIdempotencyOperation.ExactMatch,
            Guid.NewGuid());
        BatchExecutionId batchB = await SeedBatchAsync(organizationB, Guid.NewGuid());

        SqlException allocation = await InsertRawExpectingFailureAsync(
            organizationA,
            allocationB.AllocationId,
            allocationA.InvoiceId,
            allocationA.BankTransactionId,
            batchExecutionId: null,
            AllocationIdempotencyOperation.ExactMatch);
        SqlException invoice = await InsertRawExpectingFailureAsync(
            organizationA,
            allocationA.AllocationId,
            allocationB.InvoiceId,
            allocationA.BankTransactionId,
            batchExecutionId: null,
            AllocationIdempotencyOperation.ExactMatch);
        SqlException bank = await InsertRawExpectingFailureAsync(
            organizationA,
            allocationA.AllocationId,
            allocationA.InvoiceId,
            allocationB.BankTransactionId,
            batchExecutionId: null,
            AllocationIdempotencyOperation.ExactMatch);
        SqlException batch = await InsertRawExpectingFailureAsync(
            organizationA,
            allocationA.AllocationId,
            allocationA.InvoiceId,
            allocationA.BankTransactionId,
            batchB,
            AllocationIdempotencyOperation.BatchExactMatch);

        Assert.Equal(547, allocation.Number);
        Assert.Equal(547, invoice.Number);
        Assert.Equal(547, bank.Number);
        Assert.Equal(547, batch.Number);
        Assert.Equal(0, await CountAuditRowsAsync(organizationA));
    }

    [Fact]
    public async Task Same_tenant_invoice_that_is_not_on_the_allocation_is_not_rejected_by_the_foreign_key()
    {
        OrganizationId organizationId = OrganizationId.New();
        SeededAllocation seeded = await SeedAllocationAsync(
            organizationId,
            AllocationIdempotencyOperation.ExactMatch,
            Guid.NewGuid());
        Invoice otherInvoice = PersistenceTestData.CreateInvoice(organizationId, 80m, "INV-OTHER");
        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.Invoices.Add(otherInvoice);
            await context.SaveChangesAsync();
        }

        await InsertRawAsync(
            organizationId,
            seeded.AllocationId,
            otherInvoice.Id,
            seeded.BankTransactionId,
            batchExecutionId: null,
            AllocationIdempotencyOperation.ExactMatch,
            idempotencyKey: Guid.NewGuid());

        AuditRow row = await ReadSingleAsync(organizationId);
        Assert.Equal(seeded.AllocationId.Value, row.AllocationId);
        Assert.Equal(otherInvoice.Id.Value, row.InvoiceId);
        Assert.NotEqual(seeded.InvoiceId.Value, row.InvoiceId);
    }

    [Fact]
    public async Task Deleting_a_referenced_allocation_is_rejected()
    {
        OrganizationId organizationId = OrganizationId.New();
        SeededAllocation seeded = await SeedAllocationAsync(
            organizationId,
            AllocationIdempotencyOperation.ExactMatch,
            Guid.NewGuid());
        await AppendAndSaveAsync(seeded.AllocationId, "operator-a", batchExecutionId: null);

        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            Allocation allocation = await context.Allocations.SingleAsync(candidate => candidate.Id == seeded.AllocationId);
            context.Allocations.Remove(allocation);
            DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
                () => context.SaveChangesAsync());
            Assert.Equal(547, GetSqlException(exception).Number);
        }

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Assert.Equal(1, await reload.Allocations.CountAsync(candidate => candidate.Id == seeded.AllocationId));
        Assert.Equal(1, await CountAuditRowsAsync(organizationId));
    }

    [Fact]
    public async Task Existing_allocations_and_batch_executions_remain_valid_without_audit_rows()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 40m);
        BankTransaction bank = PersistenceTestData.CreateBankTransaction(organizationId, 40m);
        var historical = new Allocation(
            AllocationId.New(),
            organizationId,
            invoice.Id,
            bank.Id,
            new Money(15m, PersistenceTestData.Pln),
            ConfirmedAt);
        BatchExecution execution = BatchExecution.Start(
            BatchExecutionId.New(),
            organizationId,
            Guid.NewGuid(),
            new BatchExecutionFingerprint(new byte[BatchExecutionFingerprint.Length]),
            ConfirmedAt);

        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.Invoices.Add(invoice);
            context.BankTransactions.Add(bank);
            context.Allocations.Add(historical);
            context.BatchExecutions.Add(execution);
            await context.SaveChangesAsync();
        }

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Allocation loaded = await reload.Allocations.SingleAsync(candidate => candidate.Id == historical.Id);
        BatchExecution loadedExecution = await reload.BatchExecutions.SingleAsync(
            candidate => candidate.Id == execution.Id);
        Assert.Equal(AllocationStatus.Confirmed, loaded.Status);
        Assert.Null(loaded.IdempotencyKey);
        Assert.Equal(BatchExecutionStatus.Running, loadedExecution.Status);
        Assert.Equal(0, await CountAuditRowsAsync(organizationId));
    }

    [Fact]
    public void Module_registers_the_financial_audit_writer()
    {
        using ServiceProvider provider = _fixture.CreateServiceProvider();

        IFinancialAuditWriter writer = provider.GetRequiredService<IFinancialAuditWriter>();

        Assert.IsType<FinancialAuditWriter>(writer);
    }

    private async Task AppendAndSaveAsync(
        AllocationId allocationId,
        string actorId,
        BatchExecutionId? batchExecutionId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Allocation allocation = await context.Allocations.SingleAsync(candidate => candidate.Id == allocationId);
        new FinancialAuditWriter(context).AppendConfirmation(allocation, actorId, batchExecutionId);
        await context.SaveChangesAsync();
    }

    private async Task<SeededAllocation> SeedAllocationAsync(
        OrganizationId organizationId,
        AllocationIdempotencyOperation operation,
        Guid idempotencyKey,
        decimal amount = 25m)
    {
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 100m, "INV-" + Guid.NewGuid().ToString("N")[..8]);
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
        return new SeededAllocation(allocation.Id, invoice.Id, bank.Id);
    }

    private async Task<BatchExecutionId> SeedBatchAsync(OrganizationId organizationId, Guid idempotencyKey)
    {
        var execution = BatchExecution.Start(
            BatchExecutionId.New(),
            organizationId,
            idempotencyKey,
            new BatchExecutionFingerprint(new byte[BatchExecutionFingerprint.Length]),
            ConfirmedAt);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.BatchExecutions.Add(execution);
        await context.SaveChangesAsync();
        return execution.Id;
    }

    private async Task<int> CountAuditRowsAsync(OrganizationId organizationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.FinancialAuditEntries.CountAsync(
            entry => entry.OrganizationId == organizationId);
    }

    private async Task<AuditRow> ReadSingleAsync(OrganizationId organizationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        FinancialAuditEntry entry = await context.FinancialAuditEntries
            .AsNoTracking()
            .SingleAsync(candidate => candidate.OrganizationId == organizationId);
        return ToRow(entry);
    }

    private async Task<AuditRow> ReadSingleAsync(OrganizationId organizationId, AllocationId allocationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        FinancialAuditEntry entry = await context.FinancialAuditEntries
            .AsNoTracking()
            .SingleAsync(candidate => candidate.OrganizationId == organizationId
                && candidate.AllocationId == allocationId);
        return ToRow(entry);
    }

    private static AuditRow ToRow(FinancialAuditEntry entry)
    {
        return new AuditRow(
            entry.Id.Value,
            entry.OrganizationId.Value,
            entry.AllocationId.Value,
            entry.InvoiceId.Value,
            entry.BankTransactionId.Value,
            entry.BatchExecutionId?.Value,
            (int)entry.Action,
            entry.ActorId,
            entry.Amount.Amount,
            entry.Amount.Currency.Value,
            entry.RecordedAt,
            (int)entry.IdempotencyOperation,
            entry.IdempotencyKey);
    }

    private ReconciliationDbContext CreateCapturingContext(CommandCapture capture)
    {
        DbContextOptions<ReconciliationDbContext> options =
            new DbContextOptionsBuilder<ReconciliationDbContext>()
                .UseSqlServer(_fixture.ConnectionString)
                .AddInterceptors(capture)
                .Options;
        return new ReconciliationDbContext(options);
    }

    private async Task<int> CountAsync(string sql)
    {
        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task InsertRawAsync(
        OrganizationId organizationId,
        AllocationId allocationId,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        BatchExecutionId? batchExecutionId,
        AllocationIdempotencyOperation operation,
        Guid idempotencyKey,
        string actorId = "raw-operator")
    {
        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO [reconciliation].[FinancialAuditEntries]
                ([Id], [OrganizationId], [AllocationId], [InvoiceId], [BankTransactionId],
                 [BatchExecutionId], [Action], [ActorId], [RecordedAt], [IdempotencyOperation],
                 [IdempotencyKey], [Amount], [Currency])
            VALUES
                (@id, @organizationId, @allocationId, @invoiceId, @bankTransactionId,
                 @batchExecutionId, @action, @actorId, @recordedAt, @idempotencyOperation,
                 @idempotencyKey, @amount, @currency)
            """;
        command.Parameters.Add(Parameter("@id", Guid.NewGuid()));
        command.Parameters.Add(Parameter("@organizationId", organizationId.Value));
        command.Parameters.Add(Parameter("@allocationId", allocationId.Value));
        command.Parameters.Add(Parameter("@invoiceId", invoiceId.Value));
        command.Parameters.Add(Parameter("@bankTransactionId", bankTransactionId.Value));
        command.Parameters.Add(Parameter("@batchExecutionId", batchExecutionId?.Value));
        command.Parameters.Add(Parameter("@action", (int)FinancialAuditAction.Confirmation));
        command.Parameters.Add(new SqlParameter("@actorId", SqlDbType.NVarChar, FinancialAuditEntry.MaxActorIdLength)
        {
            Value = actorId,
        });
        command.Parameters.Add(Parameter("@recordedAt", ConfirmedAt));
        command.Parameters.Add(Parameter("@idempotencyOperation", (int)operation));
        command.Parameters.Add(Parameter("@idempotencyKey", idempotencyKey));
        SqlParameter amount = command.Parameters.Add("@amount", SqlDbType.Decimal);
        amount.Precision = 19;
        amount.Scale = 4;
        amount.Value = 25m;
        command.Parameters.Add(Parameter("@currency", "PLN"));
        await command.ExecuteNonQueryAsync();
    }

    private async Task<SqlException> InsertRawExpectingFailureAsync(
        OrganizationId organizationId,
        AllocationId allocationId,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        BatchExecutionId? batchExecutionId,
        AllocationIdempotencyOperation operation,
        string actorId = "raw-operator")
    {
        try
        {
            await InsertRawAsync(
                organizationId,
                allocationId,
                invoiceId,
                bankTransactionId,
                batchExecutionId,
                operation,
                Guid.NewGuid(),
                actorId);
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
            command.Parameters.Add(Parameter(name, value));
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

    private static SqlParameter Parameter(string name, object? value)
    {
        return new SqlParameter(name, value ?? DBNull.Value);
    }

    private static void AssertActorCheckViolation(SqlException exception)
    {
        Assert.Equal(547, exception.Number);
        Assert.Contains(
            FinancialAuditEntryConfiguration.ActorIdCheckName,
            exception.Message,
            StringComparison.Ordinal);
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

    private sealed record SeededAllocation(
        AllocationId AllocationId,
        InvoiceId InvoiceId,
        BankTransactionId BankTransactionId);

    private sealed record AuditRow(
        Guid Id,
        Guid OrganizationId,
        Guid AllocationId,
        Guid InvoiceId,
        Guid BankTransactionId,
        Guid? BatchExecutionId,
        int Action,
        string ActorId,
        decimal Amount,
        string Currency,
        DateTimeOffset RecordedAt,
        int IdempotencyOperation,
        Guid IdempotencyKey);

    private sealed class CommandCapture : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            Commands.Add(command.CommandText);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}