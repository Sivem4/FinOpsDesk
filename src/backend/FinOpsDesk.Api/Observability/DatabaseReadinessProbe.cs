using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace FinOpsDesk.Api.Observability;

internal interface IDatabaseReadinessProbe
{
    Task ProbeAsync(CancellationToken cancellationToken);
}

internal sealed class DatabaseReadinessProbe : IDatabaseReadinessProbe
{
    internal const string ApplicationName = "FinOpsDesk.Readiness";

    internal const string AlternateApplicationName = "FinOpsDesk.Readiness.Probe";

    internal const int TimeoutSeconds = 2;

    private readonly string _connectionString;

    public DatabaseReadinessProbe(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        string? configured = configuration.GetConnectionString("FinOpsDesk");
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                "Connection string 'FinOpsDesk' is not configured.");
        }

        _connectionString = CreateProbeConnectionString(configured);
    }

    public async Task ProbeAsync(CancellationToken cancellationToken)
    {
        await using SqlConnection connection = new(_connectionString);
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        command.CommandTimeout = TimeoutSeconds;
        await connection.OpenAsync(cancellationToken);
        await command.ExecuteScalarAsync(cancellationToken);
    }

    // The probe keeps the configured server, database, and credentials, then
    // forces its own timeout, disables SqlClient connect retries, and selects
    // an application name that differs from the module connection string.
    // SqlClient pools by the full connection string, so this pool is not the
    // Access or Reconciliation pool. A successful connection returns to this
    // pool. A failed open does not. PoolBlockingPeriod stays at the provider
    // default: Auto blocks non-Azure servers after a connection error, so the
    // next probe can fail until that provider period ends. This type does not
    // remember the failure and does not clear the pool.
    internal static string CreateProbeConnectionString(string configured)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configured);

        SqlConnectionStringBuilder builder = new(configured)
        {
            ConnectTimeout = TimeoutSeconds,
            ConnectRetryCount = 0,
        };
        builder.ApplicationName = string.Equals(
            builder.ApplicationName,
            ApplicationName,
            StringComparison.Ordinal)
            ? AlternateApplicationName
            : ApplicationName;
        return builder.ConnectionString;
    }
}