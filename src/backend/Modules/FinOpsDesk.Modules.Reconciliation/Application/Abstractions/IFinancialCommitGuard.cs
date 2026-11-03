using System.Data.Common;

namespace FinOpsDesk.Modules.Reconciliation.Application.Abstractions;

// DevelopmentJwt and non-write requests do not take a lock. A ProductionCookie
// financial write acquires the organization Shared application lock on the
// supplied transaction and revalidates the caller before any financial mutation
// on that transaction. The lock stays until the caller commits or rolls back.
public interface IFinancialCommitGuard
{
    bool RequiresCommitAuthorization { get; }

    ValueTask ProtectAsync(DbTransaction transaction, CancellationToken cancellationToken);
}

public sealed class NoFinancialCommitGuard : IFinancialCommitGuard
{
    public static NoFinancialCommitGuard Instance { get; } = new();

    public bool RequiresCommitAuthorization
    {
        get { return false; }
    }

    public ValueTask ProtectAsync(DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return ValueTask.CompletedTask;
    }
}