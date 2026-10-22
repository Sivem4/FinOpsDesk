using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class ImportSchemaUpgradeTests
{
    private readonly SqlServerFixture _fixture;

    public ImportSchemaUpgradeTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Phase3B_migration_preserves_phase2_financial_records()
    {
        string databaseName = $"FinOpsDesk_IT_Upgrade_{Guid.NewGuid():N}";
        string connectionString = await CreateDatabaseAsync(databaseName);

        try
        {
            Guid organizationId = Guid.NewGuid();
            Guid invoiceId = Guid.NewGuid();
            Guid bankTransactionId = Guid.NewGuid();
            Guid allocationId = Guid.NewGuid();
            DateTimeOffset confirmedAt = new(2026, 9, 4, 20, 0, 0, TimeSpan.Zero);

            await using (ReconciliationDbContext baselineContext =
                _fixture.CreateContext(connectionString))
            {
                string baseline = baselineContext.Database.GetMigrations()
                    .Single(migration => migration.EndsWith(
                        "_AddReconciliationConcurrencyTokens",
                        StringComparison.Ordinal));
                await baselineContext.Database.MigrateAsync(baseline);
                await InsertPhase2RecordsAsync(
                    baselineContext,
                    organizationId,
                    invoiceId,
                    bankTransactionId,
                    allocationId,
                    confirmedAt);
            }

            await using (ReconciliationDbContext upgradeContext =
                _fixture.CreateContext(connectionString))
            {
                await upgradeContext.Database.MigrateAsync();
            }

            await using ReconciliationDbContext context = _fixture.CreateContext(connectionString);
            Invoice invoice = await context.Invoices.SingleAsync();
            BankTransaction bankTransaction = await context.BankTransactions.SingleAsync();
            Allocation allocation = await context.Allocations.SingleAsync();

            Assert.Equal(invoiceId, invoice.Id.Value);
            Assert.Equal(organizationId, invoice.OrganizationId.Value);
            Assert.Equal("INV-LEGACY", invoice.InvoiceNumber);
            Assert.Equal(new Money(1000.2500m, PersistenceTestData.Pln), invoice.TotalAmount);
            Assert.Equal(new Money(250.5000m, PersistenceTestData.Pln), invoice.AllocatedAmount);
            Assert.Null(invoice.SourceIdentity);

            Assert.Equal(bankTransactionId, bankTransaction.Id.Value);
            Assert.Equal(new Money(800.0000m, PersistenceTestData.Pln), bankTransaction.Amount);
            Assert.Equal(new Money(250.5000m, PersistenceTestData.Pln), bankTransaction.AllocatedAmount);
            Assert.Equal(new DateOnly(2026, 8, 24), bankTransaction.BookingDate);
            Assert.Equal("INV-LEGACY", bankTransaction.Reference);
            Assert.Null(bankTransaction.SourceIdentity);

            Assert.Equal(allocationId, allocation.Id.Value);
            Assert.Equal(invoice.Id, allocation.InvoiceId);
            Assert.Equal(bankTransaction.Id, allocation.BankTransactionId);
            Assert.Equal(new Money(250.5000m, PersistenceTestData.Pln), allocation.Amount);
            Assert.Equal(AllocationStatus.Confirmed, allocation.Status);
            Assert.Equal(confirmedAt, allocation.ConfirmedAt);

            string? invoiceSourceSystem = await SqlServerCatalog.ScalarStringAsync(
                context,
                """
                SELECT [SourceSystem]
                FROM [reconciliation].[Invoices]
                WHERE [Id] = @id;
                """,
                ("@id", invoiceId));
            string? transactionExternalId = await SqlServerCatalog.ScalarStringAsync(
                context,
                """
                SELECT [ExternalId]
                FROM [reconciliation].[BankTransactions]
                WHERE [Id] = @id;
                """,
                ("@id", bankTransactionId));

            Assert.Null(invoiceSourceSystem);
            Assert.Null(transactionExternalId);
            Assert.NotNull(await SqlServerCatalog.FindIndexAsync(
                context,
                "Invoices",
                "UX_Invoices_OrganizationId_SourceSystem_ExternalId"));
            Assert.NotNull(await SqlServerCatalog.FindIndexAsync(
                context,
                "BankTransactions",
                "UX_BankTransactions_OrganizationId_SourceSystem_ExternalId"));
            Assert.NotNull(await SqlServerCatalog.FindCheckDefinitionAsync(
                context,
                "Invoices",
                "CK_Invoices_SourceIdentity_NullPair"));
            Assert.NotNull(await SqlServerCatalog.FindCheckDefinitionAsync(
                context,
                "ImportBatches",
                "CK_ImportBatches_TerminalStatus"));
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
                    $"The upgrade test failed and database '{databaseName}' could not be dropped.",
                    testFailure,
                    cleanupFailure);
            }

            throw;
        }

        await DropDatabaseAsync(databaseName);
    }

    private async Task<string> CreateDatabaseAsync(string databaseName)
    {
        SqlConnectionStringBuilder database = new(_fixture.ConnectionString)
        {
            InitialCatalog = databaseName
        };
        SqlConnectionStringBuilder master = new(database.ConnectionString)
        {
            InitialCatalog = "master"
        };

        await using var connection = new SqlConnection(master.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            $"CREATE DATABASE [{databaseName}]",
            connection);
        await command.ExecuteNonQueryAsync();

        return database.ConnectionString;
    }

    private async Task DropDatabaseAsync(string databaseName)
    {
        SqlConnectionStringBuilder master = new(_fixture.ConnectionString)
        {
            InitialCatalog = "master"
        };

        await using var connection = new SqlConnection(master.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; "
            + $"DROP DATABASE [{databaseName}];",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertPhase2RecordsAsync(
        ReconciliationDbContext context,
        Guid organizationId,
        Guid invoiceId,
        Guid bankTransactionId,
        Guid allocationId,
        DateTimeOffset confirmedAt)
    {
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO [reconciliation].[Invoices]
                ([Id], [OrganizationId], [InvoiceNumber], [AllocatedAmount], [AllocatedCurrency],
                 [TotalAmount], [TotalCurrency])
            VALUES
                ({invoiceId}, {organizationId}, N'INV-LEGACY', {250.5000m}, {"PLN"},
                 {1000.2500m}, {"PLN"});
            """);

        DateOnly bookingDate = new(2026, 8, 24);
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO [reconciliation].[BankTransactions]
                ([Id], [OrganizationId], [Reference], [BookingDate], [AllocatedAmount],
                 [AllocatedCurrency], [Amount], [Currency])
            VALUES
                ({bankTransactionId}, {organizationId}, N'INV-LEGACY', {bookingDate}, {250.5000m},
                 {"PLN"}, {800.0000m}, {"PLN"});
            """);

        int confirmed = (int)AllocationStatus.Confirmed;
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO [reconciliation].[Allocations]
                ([Id], [OrganizationId], [InvoiceId], [BankTransactionId], [Status], [ConfirmedAt],
                 [Amount], [Currency])
            VALUES
                ({allocationId}, {organizationId}, {invoiceId}, {bankTransactionId}, {confirmed},
                 {confirmedAt}, {250.5000m}, {"PLN"});
            """);
    }
}