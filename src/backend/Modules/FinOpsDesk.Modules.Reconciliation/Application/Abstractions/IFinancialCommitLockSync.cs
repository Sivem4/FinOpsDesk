namespace FinOpsDesk.Modules.Reconciliation.Application.Abstractions;

// Test seam. Production does not wait. The callback runs only after the Shared
// application lock and the authorization revalidation have succeeded, and
// before the financial mutation on that same transaction.
public interface IFinancialCommitLockSync
{
    Task AfterSharedAuthorizationAsync(CancellationToken cancellationToken);
}

public sealed class NoFinancialCommitLockSync : IFinancialCommitLockSync
{
    public static NoFinancialCommitLockSync Instance { get; } = new();

    public Task AfterSharedAuthorizationAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}