namespace FinOpsDesk.Modules.Reconciliation.Domain.Matching;

public sealed class SmartReferenceAnalysis
{
    private SmartReferenceAnalysis(
        bool isMissing,
        bool isOversized,
        string? exactNormalized,
        IReadOnlyList<string> tokens,
        IReadOnlyList<string> longDigitRuns,
        IReadOnlyList<string> yearTokens,
        string? tokenSetKey,
        string? compactForm)
    {
        IsMissing = isMissing;
        IsOversized = isOversized;
        ExactNormalized = exactNormalized;
        Tokens = tokens;
        LongDigitRuns = longDigitRuns;
        YearTokens = yearTokens;
        TokenSetKey = tokenSetKey;
        CompactForm = compactForm;
    }

    public bool IsMissing { get; }

    public bool IsOversized { get; }

    public string? ExactNormalized { get; }

    public IReadOnlyList<string> Tokens { get; }

    public IReadOnlyList<string> LongDigitRuns { get; }

    public IReadOnlyList<string> YearTokens { get; }

    internal string? TokenSetKey { get; }

    internal string? CompactForm { get; }

    internal bool HasLongDigitRun => LongDigitRuns.Count > 0;

    internal static SmartReferenceAnalysis Missing(string? exactNormalized)
    {
        return new SmartReferenceAnalysis(
            isMissing: true,
            isOversized: false,
            exactNormalized,
            [],
            [],
            [],
            tokenSetKey: null,
            compactForm: null);
    }

    internal static SmartReferenceAnalysis Oversized(string? exactNormalized)
    {
        return new SmartReferenceAnalysis(
            isMissing: false,
            isOversized: true,
            exactNormalized,
            [],
            [],
            [],
            tokenSetKey: null,
            compactForm: null);
    }

    internal static SmartReferenceAnalysis Create(
        string? exactNormalized,
        IReadOnlyList<string> tokens,
        IReadOnlyList<string> longDigitRuns,
        IReadOnlyList<string> yearTokens,
        string? tokenSetKey,
        string? compactForm)
    {
        return new SmartReferenceAnalysis(
            isMissing: false,
            isOversized: false,
            exactNormalized,
            tokens,
            longDigitRuns,
            yearTokens,
            tokenSetKey,
            compactForm);
    }
}