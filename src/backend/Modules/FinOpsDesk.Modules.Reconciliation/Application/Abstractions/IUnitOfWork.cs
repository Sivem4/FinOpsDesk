namespace FinOpsDesk.Modules.Reconciliation.Application.Abstractions;

public interface IUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);
}