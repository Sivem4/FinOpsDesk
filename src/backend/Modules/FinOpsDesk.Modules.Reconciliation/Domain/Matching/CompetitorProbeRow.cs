namespace FinOpsDesk.Modules.Reconciliation.Domain.Matching;

public sealed record CompetitorProbeRow
{
    public string? Reference { get; }

    public CompetitorProbeRow(string? reference)
    {
        Reference = reference;
    }
}