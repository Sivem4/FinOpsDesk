using System.Data;

using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class AllocationReversalMigrationSafetyTests
{
    private const string BaselineSuffix = "_AddFinancialAuditEntries";

    private const string ReversalSuffix = "_AddAllocationReversalPersistence";

    private static readonly DateTimeOffset ConfirmedAt = new(2026, 10, 27, 8, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset ReversedAt = ConfirmedAt.AddHours(2);

    private readonly SqlServerFixture _fixture;

    public AllocationReversalMigrationSafetyTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Up_preserves_pre_reversal_financial_history_and_enforces_new_audit_rules()
    {
        await RunIsolatedAsync(async connectionString =>
        {
            HistoricalRows rows = await SeedPreReversalSchemaAsync(connectionString);
            await UpgradeAsync(connectionString);

            await AssertHistoricalRowsUnchangedAsync(connectionString, rows);
            Assert.Equal(0, await ScalarAsync<int>(
                connectionString,
                """
                SELECT COUNT(*)
                FROM [reconciliation].[Allocations]
                WHERE [RowVersion] IS NULL OR DATALENGTH([RowVersion]) <> 8
                """));
            Assert.Equal(3, await ScalarAsync<int>(
                connectionString,
                """
                SELECT COUNT(*)
                FROM [reconciliation].[Allocations]
                WHERE [ReversalIdempotencyKey] IS NULL
                """));
            Assert.Equal(1, await ScalarAsync<int>(
                connectionString,
                """
                SELECT COUNT(*)
                FROM sys.indexes
                WHERE name = N'UX_Allocations_OrganizationId_ReversalIdempotencyKey'
                  AND is_unique = 1
                  AND has_filter = 1
                """));
            Assert.Equal(DBNull.Value, await ScalarAsync<object>(
                connectionString,
                """
                SELECT [Reason]
                FROM [reconciliation].[FinancialAuditEntries]
                WHERE [Id] = @id
                """,
                Parameter("@id", rows.ConfirmationAuditId)));

            SqlException wrongOperation = await InsertAuditExpectingFailureAsync(
                connectionString,
                rows.Historical,
                action: 2,
                operation: 1,
                reason: "customer refund");
            SqlException confirmationReason = await InsertAuditExpectingFailureAsync(
                connectionString,
                rows.Historical,
                action: 1,
                operation: 1,
                reason: "customer refund");
            SqlException missingReason = await InsertAuditExpectingFailureAsync(
                connectionString,
                rows.Historical,
                action: 2,
                operation: 4,
                reason: null);
            Assert.Equal(547, wrongOperation.Number);
            Assert.Equal(547, confirmationReason.Number);
            Assert.Equal(547, missingReason.Number);
            Assert.Equal(1, await ScalarAsync<int>(
                connectionString,
                "SELECT COUNT(*) FROM [reconciliation].[FinancialAuditEntries]"));

            await InsertAuditAsync(
                connectionString,
                rows.Confirmed,
                action: 2,
                operation: 4,
                reason: "customer refund",
                batchExecutionId: null);
            Assert.Equal(2, await ScalarAsync<int>(
                connectionString,
                "SELECT COUNT(*) FROM [reconciliation].[FinancialAuditEntries]"));

            SqlException mutation = await ExecuteExpectingFailureAsync(
                connectionString,
                """
                UPDATE [reconciliation].[FinancialAuditEntries]
                SET [ActorId] = [ActorId]
                WHERE [Id] = @id
                """,
                Parameter("@id", rows.ConfirmationAuditId));
            Assert.Equal(50001, mutation.Number);
            await AssertHistoricalRowsUnchangedAsync(connectionString, rows);
        });
    }

    [Fact]
    public async Task Down_is_rejected_before_reversal_history_is_destroyed()
    {
        await RunIsolatedAsync(async connectionString =>
        {
            HistoricalRows rows = await SeedPreReversalSchemaAsync(connectionString);
            await UpgradeAsync(connectionString);
            Guid reversalKey = Guid.NewGuid();
            Guid reversalAuditId = Guid.NewGuid();
            await ExecuteAsync(
                connectionString,
                """
                UPDATE [reconciliation].[Allocations]
                SET [ReversalIdempotencyKey] = @key
                WHERE [Id] = @id
                """,
                Parameter("@key", reversalKey),
                Parameter("@id", rows.Confirmed.AllocationId));
            await InsertAuditAsync(
                connectionString,
                rows.Confirmed,
                action: 2,
                operation: 4,
                reason: "customer refund",
                batchExecutionId: null,
                auditId: reversalAuditId,
                idempotencyKey: reversalKey);

            await using (ReconciliationDbContext context = _fixture.CreateContext(connectionString))
            {
                string baseline = MigrationId(context, BaselineSuffix);
                Exception failure = await Assert.ThrowsAnyAsync<Exception>(
                    () => context.Database.MigrateAsync(baseline));
                SqlException sql = RequireSql(failure);
                Assert.Equal(50002, sql.Number);
                Assert.Contains(
                    "Cannot downgrade AddAllocationReversalPersistence",
                    sql.Message,
                    StringComparison.Ordinal);
            }

            await using ReconciliationDbContext verify = _fixture.CreateContext(connectionString);
            List<string> applied = (await verify.Database.GetAppliedMigrationsAsync()).ToList();
            Assert.Contains(MigrationId(verify, ReversalSuffix), applied);
            Assert.Equal(reversalKey, await ScalarAsync<Guid>(
                connectionString,
                """
                SELECT [ReversalIdempotencyKey]
                FROM [reconciliation].[Allocations]
                WHERE [Id] = @id
                """,
                Parameter("@id", rows.Confirmed.AllocationId)));
            Assert.Equal(1, await ScalarAsync<int>(
                connectionString,
                """
                SELECT COUNT(*)
                FROM [reconciliation].[FinancialAuditEntries]
                WHERE [Id] = @id AND [Action] = 2 AND [Reason] = N'customer refund'
                """,
                Parameter("@id", reversalAuditId)));
            Assert.Equal(1, await ColumnCountAsync(connectionString, "Allocations", "RowVersion"));
            Assert.Equal(1, await ColumnCountAsync(connectionString, "Allocations", "ReversalIdempotencyKey"));
            Assert.Equal(1, await ColumnCountAsync(connectionString, "FinancialAuditEntries", "Reason"));
            await AssertHistoricalRowsUnchangedAsync(connectionString, rows);
        });
    }

    [Fact]
    public async Task Down_returns_to_the_financial_audit_schema_when_no_reversal_evidence_exists()
    {
        await RunIsolatedAsync(async connectionString =>
        {
            HistoricalRows rows = await SeedPreReversalSchemaAsync(connectionString);
            await UpgradeAsync(connectionString);

            await using (ReconciliationDbContext context = _fixture.CreateContext(connectionString))
            {
                await context.Database.MigrateAsync(MigrationId(context, BaselineSuffix));
            }

            await using ReconciliationDbContext verify = _fixture.CreateContext(connectionString);
            List<string> applied = (await verify.Database.GetAppliedMigrationsAsync()).ToList();
            Assert.Equal(MigrationId(verify, BaselineSuffix), applied[^1]);
            Assert.DoesNotContain(MigrationId(verify, ReversalSuffix), applied);
            Assert.Equal(0, await ColumnCountAsync(connectionString, "Allocations", "RowVersion"));
            Assert.Equal(0, await ColumnCountAsync(connectionString, "Allocations", "ReversalIdempotencyKey"));
            Assert.Equal(0, await ColumnCountAsync(connectionString, "FinancialAuditEntries", "Reason"));
            string actionCheck = await ScalarAsync<string>(
                connectionString,
                "SELECT definition FROM sys.check_constraints WHERE name = N'CK_FinancialAuditEntries_Action'");
            Assert.Contains("[Action]=(1)", actionCheck, StringComparison.Ordinal);
            Assert.DoesNotContain("[Action]=(2)", actionCheck, StringComparison.Ordinal);
            await AssertHistoricalRowsUnchangedAsync(connectionString, rows);

            SqlException reversalAudit = await InsertPreReversalAuditExpectingFailureAsync(
                connectionString,
                rows.Confirmed,
                action: 2,
                operation: 4);
            Assert.Equal(547, reversalAudit.Number);
            SqlException mutation = await ExecuteExpectingFailureAsync(
                connectionString,
                """
                UPDATE [reconciliation].[FinancialAuditEntries]
                SET [ActorId] = [ActorId]
                WHERE [Id] = @id
                """,
                Parameter("@id", rows.ConfirmationAuditId));
            Assert.Equal(50001, mutation.Number);
            Assert.Equal(1, await ScalarAsync<int>(
                connectionString,
                "SELECT COUNT(*) FROM [reconciliation].[FinancialAuditEntries]"));
        });
    }

    private async Task RunIsolatedAsync(Func<string, Task> body)
    {
        string databaseName = $"FinOpsDesk_IT_ReversalMig_{Guid.NewGuid():N}";
        string connectionString = await CreateDatabaseAsync(databaseName);
        if (string.Equals(connectionString, _fixture.ConnectionString, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The migration safety test must not use the shared fixture database.");
        }

        try
        {
            await body(connectionString);
        }
        catch (Exception testFailure)
        {
            try
            {
                await DropDatabaseAsync(databaseName);
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    $"The migration safety test failed and database '{databaseName}' could not be dropped.",
                    testFailure,
                    cleanupFailure);
            }

            throw;
        }

        await DropDatabaseAsync(databaseName);
    }

    private async Task<HistoricalRows> SeedPreReversalSchemaAsync(string connectionString)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext(connectionString);
        await context.Database.MigrateAsync(MigrationId(context, BaselineSuffix));
        HistoricalRows rows = HistoricalRows.Create();
        await InsertFinancialRowsAsync(connectionString, rows);
        return rows;
    }

    private async Task UpgradeAsync(string connectionString)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext(connectionString);
        await context.Database.MigrateAsync();
    }

    private async Task<string> CreateDatabaseAsync(string databaseName)
    {
        SqlConnectionStringBuilder database = new(_fixture.ConnectionString)
        {
            InitialCatalog = databaseName,
        };
        SqlConnectionStringBuilder master = new(database.ConnectionString)
        {
            InitialCatalog = "master",
        };

        await using var connection = new SqlConnection(master.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand($"CREATE DATABASE [{databaseName}]", connection);
        await command.ExecuteNonQueryAsync();
        return database.ConnectionString;
    }

    private async Task DropDatabaseAsync(string databaseName)
    {
        if (!databaseName.StartsWith("FinOpsDesk_IT_ReversalMig_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to drop a database that this test does not own.");
        }

        SqlConnectionStringBuilder master = new(_fixture.ConnectionString)
        {
            InitialCatalog = "master",
        };
        await using var connection = new SqlConnection(master.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}];",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string MigrationId(ReconciliationDbContext context, string suffix)
    {
        return context.Database.GetMigrations().Single(
            migration => migration.EndsWith(suffix, StringComparison.Ordinal));
    }

    private async Task AssertHistoricalRowsUnchangedAsync(string connectionString, HistoricalRows rows)
    {
        await AssertAllocationAsync(connectionString, rows.Confirmed, reversed: false, rows.LongReason);
        await AssertAllocationAsync(connectionString, rows.Reversed, reversed: true, rows.LongReason);
        await AssertAllocationAsync(connectionString, rows.Historical, reversed: false, rows.LongReason);
        Assert.Equal("phase5b-operator", await ScalarAsync<string>(
            connectionString,
            """
            SELECT [ActorId]
            FROM [reconciliation].[FinancialAuditEntries]
            WHERE [Id] = @id AND [Action] = 1 AND [IdempotencyOperation] = 1
              AND [IdempotencyKey] = @key AND [Amount] = 40 AND [Currency] = 'PLN'
              AND [BatchExecutionId] IS NULL
            """,
            Parameter("@id", rows.ConfirmationAuditId),
            Parameter("@key", rows.Confirmed.IdempotencyKey)));
        Assert.Equal(0, await ScalarAsync<int>(
            connectionString,
            """
            SELECT COUNT(*)
            FROM [reconciliation].[FinancialAuditEntries]
            WHERE [AllocationId] IN (@reversed, @historical)
            """,
            Parameter("@reversed", rows.Reversed.AllocationId),
            Parameter("@historical", rows.Historical.AllocationId)));
    }

    private async Task AssertAllocationAsync(
        string connectionString,
        SeededAllocation allocation,
        bool reversed,
        string longReason)
    {
        await using SqlConnection connection = await OpenAsync(connectionString);
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT [Status], [Amount], [Currency], [ConfirmedAt], [IdempotencyKey], [IdempotencyOperation],
                   [ReversedAt], [ReversalReason]
            FROM [reconciliation].[Allocations]
            WHERE [Id] = @id AND [OrganizationId] = @organizationId
              AND [InvoiceId] = @invoiceId AND [BankTransactionId] = @bankTransactionId
            """;
        command.Parameters.Add(Parameter("@id", allocation.AllocationId));
        command.Parameters.Add(Parameter("@organizationId", allocation.OrganizationId));
        command.Parameters.Add(Parameter("@invoiceId", allocation.InvoiceId));
        command.Parameters.Add(Parameter("@bankTransactionId", allocation.BankTransactionId));
        await using SqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(reversed ? 2 : 1, reader.GetInt32(0));
        Assert.Equal(allocation.Amount, reader.GetDecimal(1));
        Assert.Equal("PLN", reader.GetString(2));
        Assert.Equal(ConfirmedAt, reader.GetFieldValue<DateTimeOffset>(3));
        if (allocation.IdempotencyKey is Guid key)
        {
            Assert.Equal(key, reader.GetGuid(4));
            Assert.Equal(1, reader.GetInt32(5));
        }
        else
        {
            Assert.True(reader.IsDBNull(4));
            Assert.True(reader.IsDBNull(5));
        }

        if (reversed)
        {
            Assert.Equal(ReversedAt, reader.GetFieldValue<DateTimeOffset>(6));
            Assert.Equal(longReason, reader.GetString(7));
            Assert.True(longReason.Length > 500);
        }
        else
        {
            Assert.True(reader.IsDBNull(6));
            Assert.True(reader.IsDBNull(7));
        }

        Assert.False(await reader.ReadAsync());
    }

    private static async Task InsertFinancialRowsAsync(string connectionString, HistoricalRows rows)
    {
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO [reconciliation].[Invoices]
                ([Id], [OrganizationId], [InvoiceNumber], [AllocatedAmount], [AllocatedCurrency],
                 [TotalAmount], [TotalCurrency])
            VALUES
                (@confirmedInvoice, @organizationId, N'INV-CONFIRMED', 40, 'PLN', 100, 'PLN'),
                (@reversedInvoice, @organizationId, N'INV-REVERSED', 0, 'PLN', 25, 'PLN'),
                (@historicalInvoice, @organizationId, N'INV-HISTORICAL', 10, 'PLN', 80, 'PLN');
            """,
            Parameter("@confirmedInvoice", rows.Confirmed.InvoiceId),
            Parameter("@reversedInvoice", rows.Reversed.InvoiceId),
            Parameter("@historicalInvoice", rows.Historical.InvoiceId),
            Parameter("@organizationId", rows.Confirmed.OrganizationId));

        DateOnly bookingDate = new(2026, 10, 1);
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO [reconciliation].[BankTransactions]
                ([Id], [OrganizationId], [Reference], [BookingDate], [AllocatedAmount],
                 [AllocatedCurrency], [Amount], [Currency])
            VALUES
                (@confirmedBank, @organizationId, N'INV-CONFIRMED', @bookingDate, 40, 'PLN', 100, 'PLN'),
                (@reversedBank, @organizationId, N'INV-REVERSED', @bookingDate, 0, 'PLN', 25, 'PLN'),
                (@historicalBank, @organizationId, N'INV-HISTORICAL', @bookingDate, 10, 'PLN', 80, 'PLN');
            """,
            Parameter("@confirmedBank", rows.Confirmed.BankTransactionId),
            Parameter("@reversedBank", rows.Reversed.BankTransactionId),
            Parameter("@historicalBank", rows.Historical.BankTransactionId),
            Parameter("@organizationId", rows.Confirmed.OrganizationId),
            new SqlParameter("@bookingDate", SqlDbType.Date) { Value = bookingDate.ToDateTime(TimeOnly.MinValue) });

        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO [reconciliation].[Allocations]
                ([Id], [OrganizationId], [InvoiceId], [BankTransactionId], [Status], [ConfirmedAt],
                 [Amount], [Currency], [IdempotencyKey], [IdempotencyOperation], [ReversedAt], [ReversalReason])
            VALUES
                (@confirmed, @organizationId, @confirmedInvoice, @confirmedBank, 1, @confirmedAt,
                 @confirmedAmount, 'PLN', @confirmationKey, 1, NULL, NULL),
                (@reversed, @organizationId, @reversedInvoice, @reversedBank, 2, @confirmedAt,
                 @reversedAmount, 'PLN', NULL, NULL, @reversedAt, @longReason),
                (@historical, @organizationId, @historicalInvoice, @historicalBank, 1, @confirmedAt,
                 @historicalAmount, 'PLN', NULL, NULL, NULL, NULL);
            """,
            Parameter("@confirmed", rows.Confirmed.AllocationId),
            Parameter("@reversed", rows.Reversed.AllocationId),
            Parameter("@historical", rows.Historical.AllocationId),
            Parameter("@organizationId", rows.Confirmed.OrganizationId),
            Parameter("@confirmedInvoice", rows.Confirmed.InvoiceId),
            Parameter("@reversedInvoice", rows.Reversed.InvoiceId),
            Parameter("@historicalInvoice", rows.Historical.InvoiceId),
            Parameter("@confirmedBank", rows.Confirmed.BankTransactionId),
            Parameter("@reversedBank", rows.Reversed.BankTransactionId),
            Parameter("@historicalBank", rows.Historical.BankTransactionId),
            Parameter("@confirmedAt", ConfirmedAt),
            Parameter("@reversedAt", ReversedAt),
            DecimalParameter("@confirmedAmount", rows.Confirmed.Amount),
            DecimalParameter("@reversedAmount", rows.Reversed.Amount),
            DecimalParameter("@historicalAmount", rows.Historical.Amount),
            Parameter("@confirmationKey", rows.Confirmed.IdempotencyKey),
            Parameter("@longReason", rows.LongReason));

        await InsertPreReversalAuditAsync(connectionString, rows);
    }

    private static async Task InsertPreReversalAuditAsync(string connectionString, HistoricalRows rows)
    {
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO [reconciliation].[FinancialAuditEntries]
                ([Id], [OrganizationId], [AllocationId], [InvoiceId], [BankTransactionId],
                 [BatchExecutionId], [Action], [ActorId], [RecordedAt], [IdempotencyOperation],
                 [IdempotencyKey], [Amount], [Currency])
            VALUES
                (@id, @organizationId, @allocationId, @invoiceId, @bankTransactionId,
                 NULL, 1, N'phase5b-operator', @recordedAt, 1, @key, @amount, 'PLN');
            """,
            Parameter("@id", rows.ConfirmationAuditId),
            Parameter("@organizationId", rows.Confirmed.OrganizationId),
            Parameter("@allocationId", rows.Confirmed.AllocationId),
            Parameter("@invoiceId", rows.Confirmed.InvoiceId),
            Parameter("@bankTransactionId", rows.Confirmed.BankTransactionId),
            Parameter("@recordedAt", ConfirmedAt),
            Parameter("@key", rows.Confirmed.IdempotencyKey),
            DecimalParameter("@amount", rows.Confirmed.Amount));
    }

    private static async Task InsertAuditAsync(
        string connectionString,
        SeededAllocation allocation,
        int action,
        int operation,
        string? reason,
        Guid? batchExecutionId,
        Guid? auditId = null,
        Guid? idempotencyKey = null)
    {
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO [reconciliation].[FinancialAuditEntries]
                ([Id], [OrganizationId], [AllocationId], [InvoiceId], [BankTransactionId],
                 [BatchExecutionId], [Action], [ActorId], [RecordedAt], [IdempotencyOperation],
                 [IdempotencyKey], [Amount], [Currency], [Reason])
            VALUES
                (@id, @organizationId, @allocationId, @invoiceId, @bankTransactionId,
                 @batchExecutionId, @action, N'reversal-operator', @recordedAt, @operation,
                 @key, @amount, 'PLN', @reason);
            """,
            Parameter("@id", auditId ?? Guid.NewGuid()),
            Parameter("@organizationId", allocation.OrganizationId),
            Parameter("@allocationId", allocation.AllocationId),
            Parameter("@invoiceId", allocation.InvoiceId),
            Parameter("@bankTransactionId", allocation.BankTransactionId),
            Parameter("@batchExecutionId", batchExecutionId),
            Parameter("@action", action),
            Parameter("@recordedAt", ReversedAt),
            Parameter("@operation", operation),
            Parameter("@key", idempotencyKey ?? Guid.NewGuid()),
            DecimalParameter("@amount", allocation.Amount),
            Parameter("@reason", reason));
    }

    private static async Task<SqlException> InsertAuditExpectingFailureAsync(
        string connectionString,
        SeededAllocation allocation,
        int action,
        int operation,
        string? reason)
    {
        return await ExecuteExpectingFailureAsync(
            connectionString,
            """
            INSERT INTO [reconciliation].[FinancialAuditEntries]
                ([Id], [OrganizationId], [AllocationId], [InvoiceId], [BankTransactionId],
                 [BatchExecutionId], [Action], [ActorId], [RecordedAt], [IdempotencyOperation],
                 [IdempotencyKey], [Amount], [Currency], [Reason])
            VALUES
                (@id, @organizationId, @allocationId, @invoiceId, @bankTransactionId,
                 NULL, @action, N'reversal-operator', @recordedAt, @operation,
                 @key, @amount, 'PLN', @reason);
            """,
            Parameter("@id", Guid.NewGuid()),
            Parameter("@organizationId", allocation.OrganizationId),
            Parameter("@allocationId", allocation.AllocationId),
            Parameter("@invoiceId", allocation.InvoiceId),
            Parameter("@bankTransactionId", allocation.BankTransactionId),
            Parameter("@action", action),
            Parameter("@recordedAt", ReversedAt),
            Parameter("@operation", operation),
            Parameter("@key", Guid.NewGuid()),
            DecimalParameter("@amount", allocation.Amount),
            Parameter("@reason", reason));
    }

    private static async Task<SqlException> InsertPreReversalAuditExpectingFailureAsync(
        string connectionString,
        SeededAllocation allocation,
        int action,
        int operation)
    {
        return await ExecuteExpectingFailureAsync(
            connectionString,
            """
            INSERT INTO [reconciliation].[FinancialAuditEntries]
                ([Id], [OrganizationId], [AllocationId], [InvoiceId], [BankTransactionId],
                 [BatchExecutionId], [Action], [ActorId], [RecordedAt], [IdempotencyOperation],
                 [IdempotencyKey], [Amount], [Currency])
            VALUES
                (@id, @organizationId, @allocationId, @invoiceId, @bankTransactionId,
                 NULL, @action, N'reversal-operator', @recordedAt, @operation, @key, @amount, 'PLN');
            """,
            Parameter("@id", Guid.NewGuid()),
            Parameter("@organizationId", allocation.OrganizationId),
            Parameter("@allocationId", allocation.AllocationId),
            Parameter("@invoiceId", allocation.InvoiceId),
            Parameter("@bankTransactionId", allocation.BankTransactionId),
            Parameter("@action", action),
            Parameter("@recordedAt", ReversedAt),
            Parameter("@operation", operation),
            Parameter("@key", Guid.NewGuid()),
            DecimalParameter("@amount", allocation.Amount));
    }

    private static async Task<int> ColumnCountAsync(string connectionString, string table, string column)
    {
        return await ScalarAsync<int>(
            connectionString,
            """
            SELECT COUNT(*)
            FROM sys.columns AS c
            INNER JOIN sys.tables AS t ON t.object_id = c.object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE s.name = N'reconciliation' AND t.name = @table AND c.name = @column
            """,
            Parameter("@table", table),
            Parameter("@column", column));
    }

    private static async Task ExecuteAsync(string connectionString, string sql, params SqlParameter[] parameters)
    {
        await using SqlConnection connection = await OpenAsync(connectionString);
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<SqlException> ExecuteExpectingFailureAsync(
        string connectionString,
        string sql,
        params SqlParameter[] parameters)
    {
        try
        {
            await ExecuteAsync(connectionString, sql, parameters);
        }
        catch (Exception exception)
        {
            return RequireSql(exception);
        }

        throw new InvalidOperationException("The SQL statement was expected to fail.");
    }

    private static async Task<T> ScalarAsync<T>(
        string connectionString,
        string sql,
        params SqlParameter[] parameters)
    {
        await using SqlConnection connection = await OpenAsync(connectionString);
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        object? value = await command.ExecuteScalarAsync();
        if (value is null || value is DBNull)
        {
            return (T)(object)DBNull.Value;
        }

        return (T)value;
    }

    private static async Task<SqlConnection> OpenAsync(string connectionString)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static SqlException RequireSql(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql)
            {
                return sql;
            }
        }

        throw new InvalidOperationException("Expected a SQL Server exception.", exception);
    }

    private static SqlParameter Parameter(string name, object? value)
    {
        return new SqlParameter(name, value ?? DBNull.Value);
    }

    private static SqlParameter DecimalParameter(string name, decimal value)
    {
        return new SqlParameter(name, SqlDbType.Decimal)
        {
            Precision = 19,
            Scale = 4,
            Value = value,
        };
    }

    private sealed record HistoricalRows(
        SeededAllocation Confirmed,
        SeededAllocation Reversed,
        SeededAllocation Historical,
        Guid ConfirmationAuditId,
        string LongReason)
    {
        public static HistoricalRows Create()
        {
            Guid organizationId = Guid.NewGuid();
            return new HistoricalRows(
                SeededAllocation.Keyed(organizationId, 40m),
                SeededAllocation.Keyless(organizationId, 25m),
                SeededAllocation.Keyless(organizationId, 10m),
                Guid.NewGuid(),
                new string('R', 600));
        }
    }

    private sealed record SeededAllocation(
        Guid OrganizationId,
        Guid AllocationId,
        Guid InvoiceId,
        Guid BankTransactionId,
        Guid? IdempotencyKey,
        decimal Amount)
    {
        public static SeededAllocation Keyed(Guid organizationId, decimal amount)
        {
            return new SeededAllocation(
                organizationId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                amount);
        }

        public static SeededAllocation Keyless(Guid organizationId, decimal amount)
        {
            return new SeededAllocation(
                organizationId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                null,
                amount);
        }
    }
}