using FinOpsDesk.Modules.Reconciliation;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

public sealed class SqlServerFixture : IAsyncLifetime
{
    private const string DefaultLocalDbConnectionString =
        "Server=(localdb)\\mssqllocaldb;Trusted_Connection=True;TrustServerCertificate=True";

    private string? _masterConnectionString;
    private string? _databaseName;

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        string baseConnectionString =
            Environment.GetEnvironmentVariable("FINOPSDESK_TEST_SQLSERVER")
            ?? DefaultLocalDbConnectionString;

        _databaseName = $"FinOpsDesk_IT_{Guid.NewGuid():N}";

        SqlConnectionStringBuilder masterBuilder;
        SqlConnectionStringBuilder databaseBuilder;
        try
        {
            masterBuilder = new SqlConnectionStringBuilder(baseConnectionString)
            {
                InitialCatalog = "master"
            };
            databaseBuilder = new SqlConnectionStringBuilder(baseConnectionString)
            {
                InitialCatalog = _databaseName
            };
        }
        catch (Exception exception)
        {
            throw CreateUnavailableException(exception);
        }

        _masterConnectionString = masterBuilder.ConnectionString;
        ConnectionString = databaseBuilder.ConnectionString;

        try
        {
            await OpenMasterConnectionAsync();
        }
        catch (Exception exception)
        {
            throw CreateUnavailableException(exception);
        }

        await ExecuteMasterSqlAsync($"CREATE DATABASE [{_databaseName}]");

        try
        {
            await using ReconciliationDbContext context = CreateContext();
            await context.Database.MigrateAsync();
        }
        catch
        {
            await DeleteDatabaseAsync();
            throw;
        }
    }

    public ReconciliationDbContext CreateContext()
    {
        return CreateContext(ConnectionString);
    }

    public ReconciliationDbContext CreateContext(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        DbContextOptions<ReconciliationDbContext> options =
            new DbContextOptionsBuilder<ReconciliationDbContext>()
                .UseSqlServer(
                    connectionString,
                    sqlServer => sqlServer.MigrationsHistoryTable(
                        "__EFMigrationsHistory",
                        "reconciliation"))
                .Options;

        return new ReconciliationDbContext(options);
    }

    public ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddReconciliationModule(ConnectionString);
        return services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await DeleteDatabaseAsync();
    }

    private async Task OpenMasterConnectionAsync()
    {
        const int maxAttempts = 30;
        Exception? lastException = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await using var connection = new SqlConnection(_masterConnectionString);
                await connection.OpenAsync();
                return;
            }
            catch (Exception exception)
            {
                lastException = exception;
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        throw lastException
            ?? new InvalidOperationException("Could not open a SQL Server master connection.");
    }

    private async Task ExecuteMasterSqlAsync(string sql)
    {
        await using var connection = new SqlConnection(_masterConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task DeleteDatabaseAsync()
    {
        if (string.IsNullOrWhiteSpace(_masterConnectionString)
            || string.IsNullOrWhiteSpace(_databaseName))
        {
            return;
        }

        try
        {
            await ExecuteMasterSqlAsync(
                $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; "
                + $"DROP DATABASE [{_databaseName}];");
        }
        catch (Exception)
        {
            // Best-effort cleanup after a failed or interrupted test run.
        }
    }

    private static InvalidOperationException CreateUnavailableException(Exception exception)
    {
        return new InvalidOperationException(
            "A SQL Server instance is required for FinOpsDesk integration tests. "
            + "Set FINOPSDESK_TEST_SQLSERVER to a SQL Server connection string, "
            + "or install SQL Server LocalDB. Persistence tests are not skipped.",
            exception);
    }
}