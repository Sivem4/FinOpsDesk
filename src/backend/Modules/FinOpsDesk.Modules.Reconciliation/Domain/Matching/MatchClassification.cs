namespace FinOpsDesk.Modules.Reconciliation.Domain.Matching;

public enum MatchClassification
{
    Exact = 1,
    Ambiguous = 2,
    NoMatch = 3,
    Ineligible = 4
}