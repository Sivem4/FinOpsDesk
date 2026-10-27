using FinOpsDesk.Modules.Reconciliation.Domain.Matching;

namespace FinOpsDesk.Modules.Reconciliation.Application.BatchExecutions;

internal sealed record CompetitorProbeQueryResult(
    bool InconsistentMoney,
    IReadOnlyList<CompetitorProbeRow> Rows,
    bool Truncated);