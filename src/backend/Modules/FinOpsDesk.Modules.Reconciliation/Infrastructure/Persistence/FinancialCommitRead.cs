using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;

internal static class FinancialCommitRead
{
    public static async Task<T> ExecuteAsync<T>(
        ReconciliationDbContext context,
        IFinancialCommitGuard guard,
        Func<CancellationToken, Task<T>> query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(query);

        if (!guard.RequiresCommitAuthorization)
        {
            return await query(cancellationToken);
        }

        if (context.Database.CurrentTransaction is not null)
        {
            await guard.ProtectAsync(context.Database.CurrentTransaction.GetDbTransaction(), cancellationToken);
            return await query(cancellationToken);
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await guard.ProtectAsync(transaction.GetDbTransaction(), cancellationToken);
            T value = await query(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return value;
        }
        catch
        {
            await RollbackQuietlyAsync(transaction, cancellationToken);
            throw;
        }
    }

    private static async Task RollbackQuietlyAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            await transaction.RollbackAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
        }
    }
}