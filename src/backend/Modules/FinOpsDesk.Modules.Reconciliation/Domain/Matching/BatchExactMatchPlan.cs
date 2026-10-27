namespace FinOpsDesk.Modules.Reconciliation.Domain.Matching;

public sealed record BatchExactMatchPlan
{
    public string AlgorithmVersion { get; }

    public bool InputComplete { get; }

    public IReadOnlyList<ExactMatch> EligiblePairs { get; }

    public int EligibleCount { get; }

    public int ExcludedByReviewCount { get; }

    public int AmbiguousGroupCount { get; }

    public int UnmatchedCount { get; }

    public int IneligibleCount { get; }

    public BatchExactMatchPlan(
        string algorithmVersion,
        bool inputComplete,
        IReadOnlyList<ExactMatch> eligiblePairs,
        int excludedByReviewCount,
        int ambiguousGroupCount,
        int unmatchedCount,
        int ineligibleCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(algorithmVersion);
        ArgumentNullException.ThrowIfNull(eligiblePairs);

        if (excludedByReviewCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(excludedByReviewCount),
                "Excluded-by-review count cannot be negative.");
        }

        if (ambiguousGroupCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ambiguousGroupCount),
                "Ambiguous group count cannot be negative.");
        }

        if (unmatchedCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(unmatchedCount),
                "Unmatched count cannot be negative.");
        }

        if (ineligibleCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ineligibleCount),
                "Ineligible count cannot be negative.");
        }

        ExactMatch[] pairs = Copy(eligiblePairs);

        if (!inputComplete
            && (pairs.Length > 0
                || excludedByReviewCount > 0
                || ambiguousGroupCount > 0
                || unmatchedCount > 0
                || ineligibleCount > 0))
        {
            throw new ArgumentException(
                "An incomplete classification cannot report eligible pairs or census counts.",
                nameof(inputComplete));
        }

        AlgorithmVersion = algorithmVersion;
        InputComplete = inputComplete;
        EligiblePairs = Array.AsReadOnly(pairs);
        EligibleCount = pairs.Length;
        ExcludedByReviewCount = excludedByReviewCount;
        AmbiguousGroupCount = ambiguousGroupCount;
        UnmatchedCount = unmatchedCount;
        IneligibleCount = ineligibleCount;
    }

    private static ExactMatch[] Copy(IReadOnlyList<ExactMatch> values)
    {
        ExactMatch[] copy = new ExactMatch[values.Count];

        for (int index = 0; index < values.Count; index++)
        {
            ExactMatch value = values[index];
            ArgumentNullException.ThrowIfNull(value);
            copy[index] = value;
        }

        return copy;
    }
}