namespace FinOpsDesk.Modules.Reconciliation.Domain.Imports;

public enum ImportBatchStatus
{
    Processing = 1,
    Completed = 2,
    CompletedWithErrors = 3,
    Rejected = 4
}