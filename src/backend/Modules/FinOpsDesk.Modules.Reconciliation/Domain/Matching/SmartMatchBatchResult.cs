namespace FinOpsDesk.Modules.Reconciliation.Domain.Matching;

public sealed class SmartMatchBatchResult
{
    public string AlgorithmVersion { get; }

    public bool Truncated { get; }

    public bool CoverageLimited { get; }

    public IReadOnlyList<SmartMatchSuggestion> Suggestions { get; }

    public SmartMatchBatchResult(
        string algorithmVersion,
        bool truncated,
        bool coverageLimited,
        IReadOnlyList<SmartMatchSuggestion> suggestions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(algorithmVersion);
        ArgumentNullException.ThrowIfNull(suggestions);

        SmartMatchSuggestion[] copy = new SmartMatchSuggestion[suggestions.Count];

        for (int index = 0; index < suggestions.Count; index++)
        {
            SmartMatchSuggestion suggestion = suggestions[index]
                ?? throw new ArgumentException(
                    "Suggestions cannot contain null entries.",
                    nameof(suggestions));

            if (suggestion.Rank != index + 1)
            {
                throw new ArgumentException(
                    "Suggestion ranks must be contiguous and start at 1.",
                    nameof(suggestions));
            }

            copy[index] = suggestion;
        }

        AlgorithmVersion = algorithmVersion;
        Truncated = truncated;
        CoverageLimited = coverageLimited;
        Suggestions = Array.AsReadOnly(copy);
    }
}