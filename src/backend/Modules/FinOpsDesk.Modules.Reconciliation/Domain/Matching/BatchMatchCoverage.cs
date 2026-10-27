namespace FinOpsDesk.Modules.Reconciliation.Domain.Matching;

public enum BatchMatchCoverage
{
    // The exact-1 classification was produced from the complete open book.
    Complete = 1,

    // The book was partial or otherwise not fully classified. Planning must not
    // treat any pair from that classification as eligible.
    Incomplete = 2
}