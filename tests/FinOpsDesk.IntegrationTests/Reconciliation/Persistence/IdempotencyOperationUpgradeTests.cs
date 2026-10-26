using FinOpsDesk.Modules.Reconciliation;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Application.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class IdempotencyOperationUpgradeTests
{
    private static readonly DateTimeOffset ConfirmedAt =
        new(2026, 10, 25, 18, 0, 0, TimeSpan.Zero);

    private readonly SqlServerFixture _fixture;

    public IdempotencyOperationUpgradeTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Phase4C_records_and_keyless_allocations_remain_valid()
    {
        string databaseName = $"FinOpsDesk_IT_Idempotency_{Guid.NewGuid():N}";
        string connectionString = await CreateDatabaseAsync(databaseName);

        try
        {
            Guid organizationId = Guid.NewGuid();
            Guid exactInvoiceId = Guid.NewGuid();
            Guid exactBankTransactionId = Guid.NewGuid();
            Guid exactAllocationId = Guid.NewGuid();
            Guid keylessInvoiceId = Guid.NewGuid();
            Guid keylessBankTransactionId = Guid.NewGuid();
            Guid keylessAllocationId = Guid.NewGuid();
            Guid idempotencyKey = Guid.NewGuid();

            await using (ReconciliationDbContext baselineContext =
                _fixture.CreateContext(connectionString))
            {
                string baseline = baselineContext.Database.GetMigrations()
                    .Single(migration => migration.EndsWith(
                        "_AddAllocationIdempotencyKey",
                        StringComparison.Ordinal));
                await baselineContext.Database.MigrateAsync(baseline);
                await InsertPhase4CRecordsAsync(
                    baselineContext,
                    organizationId,
                    exactInvoiceId,
                    exactBankTransactionId,
                    exactAllocationId,
                    keylessInvoiceId,
                    keylessBankTransactionId,
                    keylessAllocationId,
                    idempotencyKey);
            }

            await using (ReconciliationDbContext upgradeContext =
                _fixture.CreateContext(connectionString))
            {
                await upgradeContext.Database.MigrateAsync();
            }

            await using ReconciliationDbContext context = _fixture.CreateContext(connectionString);
            Allocation exact = await context.Allocations.SingleAsync(
                allocation => allocation.Id == new AllocationId(exactAllocationId));
            Allocation keyless = await context.Allocations.SingleAsync(
                allocation => allocation.Id == new AllocationId(keylessAllocationId));

            Assert.Equal(idempotencyKey, exact.IdempotencyKey);
            Assert.Equal(AllocationIdempotencyOperation.ExactMatch, exact.IdempotencyOperation);
            Assert.Equal(new Money(1000m, PersistenceTestData.Pln), exact.Amount);
            Assert.Null(keyless.IdempotencyKey);
            Assert.Null(keyless.IdempotencyOperation);
            Assert.Equal(new Money(50m, PersistenceTestData.Pln), keyless.Amount);

            SqlIndex? index = await SqlServerCatalog.FindIndexAsync(
                context,
                "Allocations",
                "UX_Allocations_OrganizationId_IdempotencyKey");
            Assert.NotNull(index);
            Assert.True(index.IsUnique);
            string? check = await SqlServerCatalog.FindCheckDefinitionAsync(
                context,
                "Allocations",
                AllocationConfiguration.IdempotencyOperationCheckName);
            Assert.False(string.IsNullOrWhiteSpace(check));

            var services = new ServiceCollection();
            services.AddReconciliationModule(connectionString);
            await using ServiceProvider provider = services.BuildServiceProvider();
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            var handler = new ConfirmExactMatchHandler(
                new TestCurrentOrganizationContext(new OrganizationId(organizationId)),
                scope.ServiceProvider.GetRequiredService<IInvoiceRepository>(),
                scope.ServiceProvider.GetRequiredService<IBankTransactionRepository>(),
                scope.ServiceProvider.GetRequiredService<IAllocationRepository>(),
                scope.ServiceProvider.GetRequiredService<IAllocationIdempotencyLookup>(),
                scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
                TimeProvider.System);
            Result<ConfirmExactMatchResponse> replay = await handler.Handle(
                new ConfirmExactMatchCommand(
                    new InvoiceId(exactInvoiceId),
                    new BankTransactionId(exactBankTransactionId),
                    idempotencyKey),
                CancellationToken.None);

            Assert.True(replay.IsSuccess);
            Assert.NotNull(replay.Value);
            Assert.True(replay.Value.Replayed);
            Assert.Equal(exactAllocationId, replay.Value.AllocationId);
            Assert.Equal(1000m, replay.Value.Amount.Amount);

            await using ReconciliationDbContext afterReplay = _fixture.CreateContext(connectionString);
            Assert.Equal(2, await afterReplay.Allocations.CountAsync());
            Invoice storedInvoice = await afterReplay.Invoices.SingleAsync(
                invoice => invoice.Id == new InvoiceId(exactInvoiceId));
            Assert.Equal(new Money(1000m, PersistenceTestData.Pln), storedInvoice.AllocatedAmount);
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

    private static async Task InsertPhase4CRecordsAsync(
        ReconciliationDbContext context,
        Guid organizationId,
        Guid exactInvoiceId,
        Guid exactBankTransactionId,
        Guid exactAllocationId,
        Guid keylessInvoiceId,
        Guid keylessBankTransactionId,
        Guid keylessAllocationId,
        Guid idempotencyKey)
    {
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO [reconciliation].[Invoices]
                ([Id], [OrganizationId], [InvoiceNumber], [AllocatedAmount], [AllocatedCurrency],
                 [TotalAmount], [TotalCurrency])
            VALUES
                ({exactInvoiceId}, {organizationId}, N'INV-EXACT', {1000m}, {"PLN"}, {1000m}, {"PLN"}),
                ({keylessInvoiceId}, {organizationId}, N'INV-KEYLESS', {50m}, {"PLN"}, {200m}, {"PLN"});
            """);

        DateOnly bookingDate = new(2026, 8, 24);
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO [reconciliation].[BankTransactions]
                ([Id], [OrganizationId], [Reference], [BookingDate], [AllocatedAmount],
                 [AllocatedCurrency], [Amount], [Currency])
            VALUES
                ({exactBankTransactionId}, {organizationId}, N'INV-EXACT', {bookingDate}, {1000m},
                 {"PLN"}, {1000m}, {"PLN"}),
                ({keylessBankTransactionId}, {organizationId}, N'INV-KEYLESS', {bookingDate}, {50m},
                 {"PLN"}, {200m}, {"PLN"});
            """);

        int confirmed = (int)AllocationStatus.Confirmed;
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO [reconciliation].[Allocations]
                ([Id], [OrganizationId], [InvoiceId], [BankTransactionId], [Status], [ConfirmedAt],
                 [Amount], [Currency], [IdempotencyKey])
            VALUES
                ({exactAllocationId}, {organizationId}, {exactInvoiceId}, {exactBankTransactionId},
                 {confirmed}, {ConfirmedAt}, {1000m}, {"PLN"}, {idempotencyKey}),
                ({keylessAllocationId}, {organizationId}, {keylessInvoiceId}, {keylessBankTransactionId},
                 {confirmed}, {ConfirmedAt}, {50m}, {"PLN"}, NULL);
            """);
    }
}