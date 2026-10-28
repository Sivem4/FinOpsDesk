using FinOpsDesk.Modules.Reconciliation.Application.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Repositories;

internal sealed class FinancialAuditWriter : IFinancialAuditWriter
{
    private readonly ReconciliationDbContext _dbContext;

    public FinancialAuditWriter(ReconciliationDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    public void AppendConfirmation(
        Allocation allocation,
        string actorId,
        BatchExecutionId? batchExecutionId)
    {
        ArgumentNullException.ThrowIfNull(allocation);
        ArgumentNullException.ThrowIfNull(actorId);

        FinancialAuditEntry entry = FinancialAuditEntry.ForConfirmation(
            FinancialAuditEntryId.New(),
            allocation,
            actorId,
            batchExecutionId);

        _dbContext.FinancialAuditEntries.Add(entry);
    }
}