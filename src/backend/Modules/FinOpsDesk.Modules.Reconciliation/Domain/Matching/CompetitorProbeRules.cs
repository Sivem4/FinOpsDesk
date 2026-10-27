namespace FinOpsDesk.Modules.Reconciliation.Domain.Matching;

public static class CompetitorProbeRules
{
    // A later SQL probe reads at most this many other rows, plus one extra row
    // used only to discover that more rows exist. One hundred rows with the
    // truncation flag clear is a complete probe. One hundred and one rows is not.
    public const int MaxCompleteRowCount = 100;

    public static CompetitorProbeOutcome Evaluate(
        string? targetReference,
        IReadOnlyList<CompetitorProbeRow> otherRows,
        bool truncated)
    {
        ArgumentNullException.ThrowIfNull(otherRows);

        string? normalizedTarget = PaymentReferenceNormalizer.Normalize(targetReference);
        bool competitorFound = false;

        foreach (CompetitorProbeRow row in otherRows)
        {
            ArgumentNullException.ThrowIfNull(row);

            if (IsEligibleCompetitor(normalizedTarget, row.Reference))
            {
                competitorFound = true;
            }
        }

        if (competitorFound)
        {
            return CompetitorProbeOutcome.Ambiguous;
        }

        bool coverageLimited = truncated || otherRows.Count > MaxCompleteRowCount;

        if (normalizedTarget is null || coverageLimited)
        {
            return CompetitorProbeOutcome.CoverageLimited;
        }

        return CompetitorProbeOutcome.Unique;
    }

    private static bool IsEligibleCompetitor(string? normalizedTarget, string? reference)
    {
        if (normalizedTarget is null)
        {
            return false;
        }

        string? normalizedReference = PaymentReferenceNormalizer.Normalize(reference);

        return normalizedReference is not null
            && string.Equals(normalizedTarget, normalizedReference, StringComparison.Ordinal);
    }
}