using System.Text.RegularExpressions;

using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.E2ETests;

internal sealed class E2EDatabase : IAsyncDisposable
{
    private const string DefaultServerConnectionString =
        "Server=(localdb)\\mssqllocaldb;Trusted_Connection=True;TrustServerCertificate=True";

    private static readonly Regex OwnedNamePattern = new(
        "^FinOpsDesk_E2E_[0-9a-f]{32}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly string _masterConnectionString;
    private bool _created;
    private bool _dropped;

    private E2EDatabase(string databaseName, string connectionString, string masterConnectionString)
    {
        DatabaseName = databaseName;
        ConnectionString = connectionString;
        _masterConnectionString = masterConnectionString;
    }

    public string DatabaseName { get; }

    public string ConnectionString { get; }

    public static async Task<E2EDatabase> CreateAsync(CancellationToken cancellationToken)
    {
        string supplied = Environment.GetEnvironmentVariable("FINOPSDESK_TEST_SQLSERVER")
            ?? DefaultServerConnectionString;
        string ownedName = "FinOpsDesk_E2E_" + Guid.NewGuid().ToString("N");
        RequireOwnedName(ownedName);

        SqlConnectionStringBuilder suppliedBuilder = ParseServerConnection(supplied);
        RejectSuppliedCatalog(suppliedBuilder.InitialCatalog);
        RejectAttach(suppliedBuilder);

        SqlConnectionStringBuilder masterBuilder = new(suppliedBuilder.ConnectionString)
        {
            InitialCatalog = "master",
            AttachDBFilename = string.Empty,
        };
        SqlConnectionStringBuilder databaseBuilder = new(suppliedBuilder.ConnectionString)
        {
            InitialCatalog = ownedName,
            AttachDBFilename = string.Empty,
        };

        if (!string.Equals(databaseBuilder.InitialCatalog, ownedName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "E2E could not set the dedicated database name. No migration was applied.");
        }

        RequireOwnedName(databaseBuilder.InitialCatalog);
        RequireMasterCatalog(masterBuilder.InitialCatalog);

        E2EDatabase database = new(
            ownedName,
            databaseBuilder.ConnectionString,
            masterBuilder.ConnectionString);

        await database.OpenMasterAsync(cancellationToken);
        await database.ExecuteMasterAsync($"CREATE DATABASE [{ownedName}]", cancellationToken);
        database._created = true;

        try
        {
            RequireOwnedName(new SqlConnectionStringBuilder(database.ConnectionString).InitialCatalog);
            await using ReconciliationDbContext context = CreateContext(database.ConnectionString);
            await context.Database.MigrateAsync(cancellationToken);
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }

        return database;
    }

    public static ReconciliationDbContext CreateContext(string connectionString)
    {
        SqlConnectionStringBuilder builder = new(connectionString);
        RequireOwnedName(builder.InitialCatalog);

        DbContextOptions<ReconciliationDbContext> options = new DbContextOptionsBuilder<ReconciliationDbContext>()
            .UseSqlServer(
                connectionString,
                sqlServer => sqlServer.MigrationsHistoryTable(
                    "__EFMigrationsHistory",
                    "reconciliation"))
            .Options;

        return new ReconciliationDbContext(options);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_created || _dropped)
        {
            return;
        }

        RequireOwnedName(DatabaseName);
        RequireOwnedName(new SqlConnectionStringBuilder(ConnectionString).InitialCatalog);

        Exception? lastException = null;
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await ExecuteMasterAsync(
                    $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; "
                    + $"DROP DATABASE [{DatabaseName}];",
                    CancellationToken.None);
                _dropped = true;
                return;
            }
            catch (Exception exception)
            {
                lastException = exception;
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }

        throw new InvalidOperationException(
            $"E2E could not drop owned database '{DatabaseName}'. No other database was targeted.",
            lastException);
    }

    private async Task OpenMasterAsync(CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await using SqlConnection connection = new(_masterConnectionString);
                await connection.OpenAsync(cancellationToken);
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lastException = exception;
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }

        throw new InvalidOperationException(
            "E2E could not open the SQL Server master connection. "
            + "Install LocalDB or set FINOPSDESK_TEST_SQLSERVER to a test instance without a database name. "
            + "The connection string was not written to the log.",
            lastException);
    }

    private async Task ExecuteMasterAsync(string sql, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = new(_masterConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqlCommand command = new(sql, connection)
        {
            CommandTimeout = 60,
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SqlConnectionStringBuilder ParseServerConnection(string connectionString)
    {
        try
        {
            return new SqlConnectionStringBuilder(connectionString);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "FINOPSDESK_TEST_SQLSERVER could not be parsed. The value was not written to the log.",
                exception);
        }
    }

    private static void RejectSuppliedCatalog(string catalog)
    {
        if (string.IsNullOrEmpty(catalog)
            || catalog.Equals("master", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            $"E2E refused supplied catalog '{catalog}' before opening a connection. "
            + "Omit the database name, or set it to master. "
            + "FinOpsDesk and FinOpsDesk_IT databases are not E2E targets.");
    }

    private static void RejectAttach(SqlConnectionStringBuilder builder)
    {
        if (!string.IsNullOrEmpty(builder.AttachDBFilename))
        {
            throw new InvalidOperationException(
                "E2E does not accept AttachDBFilename. No database was created.");
        }
    }

    private static void RequireMasterCatalog(string catalog)
    {
        if (!catalog.Equals("master", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "E2E admin commands must use the master catalog. No database was changed.");
        }
    }

    internal static void RequireOwnedName(string databaseName)
    {
        if (string.IsNullOrWhiteSpace(databaseName)
            || !OwnedNamePattern.IsMatch(databaseName)
            || IsProtected(databaseName))
        {
            throw new InvalidOperationException(
                "E2E ownership of the target database could not be proven. The database was left untouched.");
        }
    }

    private static bool IsProtected(string databaseName)
    {
        if (databaseName.Equals("FinOpsDesk", StringComparison.OrdinalIgnoreCase)
            || databaseName.StartsWith("FinOpsDesk_IT", StringComparison.OrdinalIgnoreCase)
            || databaseName.Equals("master", StringComparison.OrdinalIgnoreCase)
            || databaseName.Equals("model", StringComparison.OrdinalIgnoreCase)
            || databaseName.Equals("msdb", StringComparison.OrdinalIgnoreCase)
            || databaseName.Equals("tempdb", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}