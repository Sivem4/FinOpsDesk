using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;

namespace FinOpsDesk.Modules.Reconciliation.Application.FinancialAudit;

public interface IFinancialAuditWriter
{
    void AppendConfirmation(
        Allocation allocation,
        string actorId,
        BatchExecutionId? batchExecutionId);
}