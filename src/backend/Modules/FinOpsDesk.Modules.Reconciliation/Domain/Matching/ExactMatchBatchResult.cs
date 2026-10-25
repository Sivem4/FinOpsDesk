namespace FinOpsDesk.Modules.Reconciliation.Domain.Matching;

public sealed record ExactMatchBatchResult
{
    public string AlgorithmVersion { get; }

    public IReadOnlyList<ExactMatch> ExactMatches { get; }

    public IReadOnlyList<AmbiguousMatchGroup> AmbiguousGroups { get; }

    public IReadOnlyList<ClassifiedMatchRecord> Unmatched { get; }

    public IReadOnlyList<ClassifiedMatchRecord> Ineligible { get; }

    public ExactMatchBatchResult(
        string algorithmVersion,
        IReadOnlyList<ExactMatch> exactMatches,
        IReadOnlyList<AmbiguousMatchGroup> ambiguousGroups,
        IReadOnlyList<ClassifiedMatchRecord> unmatched,
        IReadOnlyList<ClassifiedMatchRecord> ineligible)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(algorithmVersion);
        ArgumentNullException.ThrowIfNull(exactMatches);
        ArgumentNullException.ThrowIfNull(ambiguousGroups);
        ArgumentNullException.ThrowIfNull(unmatched);
        ArgumentNullException.ThrowIfNull(ineligible);

        AlgorithmVersion = algorithmVersion;
        ExactMatches = Array.AsReadOnly(Copy(exactMatches));
        AmbiguousGroups = Array.AsReadOnly(Copy(ambiguousGroups));
        Unmatched = Array.AsReadOnly(Copy(unmatched));
        Ineligible = Array.AsReadOnly(Copy(ineligible));
    }

    private static T[] Copy<T>(IReadOnlyList<T> values)
    {
        T[] copy = new T[values.Count];

        for (int index = 0; index < values.Count; index++)
        {
            T value = values[index];
            ArgumentNullException.ThrowIfNull(value);
            copy[index] = value;
        }

        return copy;
    }
}