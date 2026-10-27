namespace FinOpsDesk.Modules.Reconciliation.Domain.Matching;

public enum CompetitorProbeOutcome
{
    // No eligible competitor was present, and the fetched set was complete.
    // This is not an allocation and it is not permission to confirm one.
    Unique = 1,

    // At least one fetched row is an eligible competitor for the target reference.
    Ambiguous = 2,

    // The fetched set cannot prove that no competitor exists.
    CoverageLimited = 3
}