using FinOpsDesk.Modules.Access.Infrastructure.Persistence;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace FinOpsDesk.ComposeMigrate;

public static class ComposeMigrator
{
    public static async Task<int> RunAsync(
        IConfiguration configuration,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(error);

        string? connectionString = configuration.GetConnectionString("FinOpsDesk");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            await error.WriteLineAsync("Connection string 'FinOpsDesk' is not configured.");
            return 1;
        }

        if (!await ApplyAsync(
                "Reconciliation",
                CreateReconciliation,
                connectionString,
                error,
                cancellationToken))
        {
            return 1;
        }

        if (!await ApplyAsync(
                "Access",
                CreateAccess,
                connectionString,
                error,
                cancellationToken))
        {
            return 1;
        }

        return 0;
    }

    private static async Task<bool> ApplyAsync(
        string name,
        Func<string, DbContext> create,
        string connectionString,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        try
        {
            await using DbContext context = create(connectionString);
            await context.Database.MigrateAsync(cancellationToken);
            if ((await context.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
            {
                await error.WriteLineAsync($"Pending migrations remain for {name}.");
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await error.WriteLineAsync($"Migration failed for {name}: {exception.GetType().Name}.");
            return false;
        }
    }

    private static ReconciliationDbContext CreateReconciliation(string connectionString)
    {
        DbContextOptions<ReconciliationDbContext> options = new DbContextOptionsBuilder<ReconciliationDbContext>()
            .UseSqlServer(
                connectionString,
                sqlServer => sqlServer.MigrationsHistoryTable(
                    "__EFMigrationsHistory",
                    "reconciliation"))
            .Options;

        return new ReconciliationDbContext(options);
    }

    private static AccessDbContext CreateAccess(string connectionString)
    {
        DbContextOptions<AccessDbContext> options = new DbContextOptionsBuilder<AccessDbContext>()
            .UseSqlServer(
                connectionString,
                sqlServer => sqlServer.MigrationsHistoryTable(
                    "__EFMigrationsHistory",
                    "access"))
            .Options;

        return new AccessDbContext(options);
    }
}