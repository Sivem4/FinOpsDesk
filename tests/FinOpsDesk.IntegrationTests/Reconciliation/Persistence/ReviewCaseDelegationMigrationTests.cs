using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class ReviewCaseDelegationMigrationTests
{
    private const string PreviousSuffix = "AddAllocationDisputeCorrections";
    private const string DelegationSuffix = "AddReviewCaseDelegation";

    private readonly SqlServerFixture _fixture;

    public ReviewCaseDelegationMigrationTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Down_keeps_claim_and_release_and_refuses_when_a_delegate_row_exists()
    {
        await RunIsolatedAsync(async connectionString =>
        {
            Guid claimId = Guid.NewGuid();
            Guid releaseId = Guid.NewGuid();
            await SeedClaimAndReleaseAsync(connectionString, claimId, releaseId);

            await using (ReconciliationDbContext context = _fixture.CreateContext(connectionString))
            {
                await context.Database.MigrateAsync(MigrationId(context, PreviousSuffix));
            }

            Assert.Equal(0, await ColumnCountAsync(connectionString, "RecipientActorId"));
            string operationCheck = await ScalarAsync<string>(
                connectionString,
                "SELECT definition FROM sys.check_constraints WHERE name = N'CK_ReviewCaseAssignmentEntries_Operation'");
            Assert.Contains("[Operation]=(1)", operationCheck, StringComparison.Ordinal);
            Assert.Contains("[Operation]=(2)", operationCheck, StringComparison.Ordinal);
            Assert.DoesNotContain("[Operation]=(3)", operationCheck, StringComparison.Ordinal);
            Assert.Equal(1, await CountOperationAsync(connectionString, 1));
            Assert.Equal(1, await CountOperationAsync(connectionString, 2));
            Assert.Equal(claimId, await ScalarAsync<Guid>(
                connectionString,
                "SELECT [Id] FROM [reconciliation].[ReviewCaseAssignmentEntries] WHERE [Operation] = 1"));
            Assert.Equal(releaseId, await ScalarAsync<Guid>(
                connectionString,
                "SELECT [Id] FROM [reconciliation].[ReviewCaseAssignmentEntries] WHERE [Operation] = 2"));

            Guid delegateId = Guid.NewGuid();
            await using (ReconciliationDbContext context = _fixture.CreateContext(connectionString))
            {
                await context.Database.MigrateAsync();
                Assert.Contains(MigrationId(context, DelegationSuffix), await context.Database.GetAppliedMigrationsAsync());
            }

            await SeedDelegateAsync(connectionString, delegateId);
            await using (ReconciliationDbContext context = _fixture.CreateContext(connectionString))
            {
                Exception failure = await Assert.ThrowsAnyAsync<Exception>(
                    () => context.Database.MigrateAsync(MigrationId(context, PreviousSuffix)));
                SqlException sql = RequireSql(failure);
                Assert.Equal(50006, sql.Number);
                Assert.Contains("Review case delegation history cannot be downgraded.", sql.Message, StringComparison.Ordinal);
            }

            await using ReconciliationDbContext verify = _fixture.CreateContext(connectionString);
            Assert.Contains(MigrationId(verify, DelegationSuffix), await verify.Database.GetAppliedMigrationsAsync());
            Assert.Equal(1, await ColumnCountAsync(connectionString, "RecipientActorId"));
            Assert.Equal(1, await CountOperationAsync(connectionString, 3));
            Assert.Equal(delegateId, await ScalarAsync<Guid>(
                connectionString,
                "SELECT [Id] FROM [reconciliation].[ReviewCaseAssignmentEntries] WHERE [Operation] = 3"));
            Assert.Equal("colleague", await ScalarAsync<string>(
                connectionString,
                "SELECT [RecipientActorId] FROM [reconciliation].[ReviewCaseAssignmentEntries] WHERE [Id] = @id",
                new SqlParameter("@id", delegateId)));
            Assert.Equal("colleague", await ScalarAsync<string>(
                connectionString,
                "SELECT [AssignedActorId] FROM [reconciliation].[ReviewCases]"));
            string currentCheck = await ScalarAsync<string>(
                connectionString,
                "SELECT definition FROM sys.check_constraints WHERE name = N'CK_ReviewCaseAssignmentEntries_Operation'");
            Assert.Contains("[Operation]=(3)", currentCheck, StringComparison.Ordinal);
            Assert.Equal(claimId, await ScalarAsync<Guid>(
                connectionString,
                "SELECT [Id] FROM [reconciliation].[ReviewCaseAssignmentEntries] WHERE [Operation] = 1 AND [Id] = @id",
                new SqlParameter("@id", claimId)));
            Assert.Equal(releaseId, await ScalarAsync<Guid>(
                connectionString,
                "SELECT [Id] FROM [reconciliation].[ReviewCaseAssignmentEntries] WHERE [Operation] = 2 AND [Id] = @id",
                new SqlParameter("@id", releaseId)));
        });
    }

    [Fact]
    public async Task Recipient_check_matches_ordinal_identity_when_the_database_collation_ignores_case()
    {
        await RunIsolatedAsync(async connectionString =>
        {
            await SeedClaimAndReleaseAsync(connectionString, Guid.NewGuid(), Guid.NewGuid());
            string collation = await ScalarAsync<string>(
                connectionString,
                "SELECT CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS nvarchar(128))");
            Assert.Contains("_CI_", collation, StringComparison.Ordinal);
            string definition = await ScalarAsync<string>(
                connectionString,
                "SELECT definition FROM sys.check_constraints WHERE name = N'CK_ReviewCaseAssignmentEntries_RecipientActorId'");
            Assert.Contains("Latin1_General_100_BIN2", definition, StringComparison.Ordinal);

            Guid organizationId = await ScalarAsync<Guid>(
                connectionString,
                "SELECT TOP (1) [OrganizationId] FROM [reconciliation].[ReviewCases]");
            Guid reviewCaseId = await ScalarAsync<Guid>(
                connectionString,
                "SELECT TOP (1) [Id] FROM [reconciliation].[ReviewCases]");
            await ExecuteAsync(
                connectionString,
                InsertDelegateSql(),
                Parameters(organizationId, reviewCaseId, Guid.NewGuid(), "Owner", "owner", Guid.NewGuid()));
            Assert.Equal(1, await CountActorPairAsync(connectionString, "Owner", "owner"));

            SqlException rejected = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
                connectionString,
                InsertDelegateSql(),
                Parameters(organizationId, reviewCaseId, Guid.NewGuid(), "Owner", "Owner", Guid.NewGuid())));
            Assert.Equal(547, rejected.Number);
            Assert.Equal(0, await CountActorPairAsync(connectionString, "Owner", "Owner"));
        });
    }

    private static string InsertDelegateSql()
    {
        return """
            INSERT INTO [reconciliation].[ReviewCaseAssignmentEntries]
                ([Id], [OrganizationId], [ReviewCaseId], [Operation], [ActorSource], [ActorId],
                 [RequestConcurrencyToken], [IdempotencyKey], [RecordedAt], [AssignedAt], [RecipientActorId])
            VALUES
                (@id, @organizationId, @reviewCaseId, 3, 1, @actorId,
                 N'BBBBBBBBBBB=', @idempotencyKey, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET(), @recipient)
            """;
    }

    private static SqlParameter[] Parameters(
        Guid organizationId,
        Guid reviewCaseId,
        Guid id,
        string actorId,
        string recipient,
        Guid idempotencyKey)
    {
        return
        [
            new SqlParameter("@id", id),
            new SqlParameter("@organizationId", organizationId),
            new SqlParameter("@reviewCaseId", reviewCaseId),
            new SqlParameter("@actorId", actorId),
            new SqlParameter("@recipient", recipient),
            new SqlParameter("@idempotencyKey", idempotencyKey),
        ];
    }

    private static async Task ExecuteAsync(string connectionString, string sql, params SqlParameter[] parameters)
    {
        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = new(sql, connection);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountActorPairAsync(string connectionString, string actorId, string recipient)
    {
        return await ScalarAsync<int>(
            connectionString,
            """
            SELECT COUNT(*)
            FROM [reconciliation].[ReviewCaseAssignmentEntries]
            WHERE [ActorId] = @actorId COLLATE Latin1_General_100_BIN2
              AND [RecipientActorId] = @recipient COLLATE Latin1_General_100_BIN2
            """,
            new SqlParameter("@actorId", actorId),
            new SqlParameter("@recipient", recipient));
    }

    private async Task SeedClaimAndReleaseAsync(string connectionString, Guid claimId, Guid releaseId)
    {
        OrganizationId organizationId = OrganizationId.New();
        DateTimeOffset opened = new(2026, 11, 8, 8, 0, 0, TimeSpan.Zero);
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 25m, "INV-DOWN");
        ReviewCase reviewCase = ReviewCase.Open(
            ReviewCaseId.New(),
            organizationId,
            ReviewCaseReason.NeedsInvestigation,
            invoice.Id,
            null,
            null,
            null,
            opened);
        Assert.Null(reviewCase.Claim(ReviewCaseActorSource.DevelopmentJwt, "owner", opened.AddHours(1)));
        ReviewCaseAssignmentEntry claim = ReviewCaseAssignmentEntry.Create(
            new ReviewCaseAssignmentEntryId(claimId),
            organizationId,
            reviewCase.Id,
            ReviewCaseAssignmentOperation.Claim,
            ReviewCaseActorSource.DevelopmentJwt,
            "owner",
            "AAAAAAAAAAA=",
            Guid.NewGuid(),
            opened.AddHours(1),
            opened.AddHours(1));
        Assert.Null(reviewCase.Release(ReviewCaseActorSource.DevelopmentJwt, "owner"));
        ReviewCaseAssignmentEntry release = ReviewCaseAssignmentEntry.Create(
            new ReviewCaseAssignmentEntryId(releaseId),
            organizationId,
            reviewCase.Id,
            ReviewCaseAssignmentOperation.Release,
            ReviewCaseActorSource.DevelopmentJwt,
            "owner",
            "AAAAAAAAAAB=",
            Guid.NewGuid(),
            opened.AddHours(2),
            null);
        await using ReconciliationDbContext context = _fixture.CreateContext(connectionString);
        await context.Database.MigrateAsync();
        context.AddRange(invoice, reviewCase, claim, release);
        await context.SaveChangesAsync();
    }

    private async Task SeedDelegateAsync(string connectionString, Guid delegateId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext(connectionString);
        ReviewCase reviewCase = await context.ReviewCases.SingleAsync();
        Assert.Null(reviewCase.Claim(ReviewCaseActorSource.DevelopmentJwt, "owner", new DateTimeOffset(2026, 11, 8, 11, 0, 0, TimeSpan.Zero)));
        Assert.Null(reviewCase.Delegate(
            ReviewCaseActorSource.DevelopmentJwt,
            "owner",
            "colleague",
            new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero)));
        context.ReviewCaseAssignmentEntries.Add(ReviewCaseAssignmentEntry.Create(
            new ReviewCaseAssignmentEntryId(delegateId),
            reviewCase.OrganizationId,
            reviewCase.Id,
            ReviewCaseAssignmentOperation.Delegate,
            ReviewCaseActorSource.DevelopmentJwt,
            "owner",
            "AAAAAAAAAAC=",
            Guid.NewGuid(),
            new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero),
            "colleague"));
        await context.SaveChangesAsync();
    }

    private async Task RunIsolatedAsync(Func<string, Task> body)
    {
        string databaseName = "FinOpsDesk_IT_DelegationMig_" + Guid.NewGuid().ToString("N");
        string connectionString = await CreateDatabaseAsync(databaseName);
        if (string.Equals(connectionString, _fixture.ConnectionString, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The migration safety test must not use the shared fixture database.");
        }

        try
        {
            await body(connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
        }
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
        await using SqlConnection connection = new(master.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = new("CREATE DATABASE [" + databaseName + "]", connection);
        await command.ExecuteNonQueryAsync();
        return database.ConnectionString;
    }

    private async Task DropDatabaseAsync(string databaseName)
    {
        if (!databaseName.StartsWith("FinOpsDesk_IT_DelegationMig_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to drop a database that this test does not own.");
        }

        SqlConnectionStringBuilder master = new(_fixture.ConnectionString)
        {
            InitialCatalog = "master",
        };
        await using SqlConnection connection = new(master.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = new(
            "ALTER DATABASE [" + databaseName + "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [" + databaseName + "];",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string MigrationId(ReconciliationDbContext context, string suffix)
    {
        return context.Database.GetMigrations().Single(migration => migration.EndsWith(suffix, StringComparison.Ordinal));
    }

    private static async Task<int> ColumnCountAsync(string connectionString, string columnName)
    {
        return await ScalarAsync<int>(
            connectionString,
            """
            SELECT COUNT(*)
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'reconciliation.ReviewCaseAssignmentEntries')
              AND name = @name
            """,
            new SqlParameter("@name", columnName));
    }

    private static async Task<int> CountOperationAsync(string connectionString, int operation)
    {
        return await ScalarAsync<int>(
            connectionString,
            "SELECT COUNT(*) FROM [reconciliation].[ReviewCaseAssignmentEntries] WHERE [Operation] = @operation",
            new SqlParameter("@operation", operation));
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql, params SqlParameter[] parameters)
    {
        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = new(sql, connection);
        command.Parameters.AddRange(parameters);
        object? value = await command.ExecuteScalarAsync();
        return (T)value!;
    }

    private static SqlException RequireSql(Exception failure)
    {
        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql)
            {
                return sql;
            }
        }

        throw new Xunit.Sdk.XunitException("Expected a SQL exception but got " + failure.GetType().Name + ": " + failure.Message);
    }
}