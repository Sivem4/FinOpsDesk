namespace FinOpsDesk.Modules.Reconciliation.Application.Abstractions;

public interface IUnitOfWork
{
    /// <summary>
    /// Commits tracked changes for the current business operation.
    /// </summary>
    /// <exception cref="ConcurrencyConflictException">
    /// Thrown when persisted row versions no longer match the loaded state.
    /// </exception>
    /// <exception cref="UniqueConstraintConflictException">
    /// Thrown when a database unique constraint rejects the commit.
    /// </exception>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}