namespace FinOpsDesk.Modules.Reconciliation.Domain.Imports;

public enum ImportRowOutcome
{
    Imported = 1,
    AlreadyExists = 2,
    Rejected = 3
}