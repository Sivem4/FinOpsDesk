using FinOpsDesk.Modules.Reconciliation.Domain.Matching;

namespace FinOpsDesk.UnitTests.Reconciliation.Domain.Matching;

public sealed class SmartReferenceNormalizerTests
{
    [Fact]
    public void Analyze_should_keep_exact_1_normalization_unchanged()
    {
        Assert.Equal("INV-001", PaymentReferenceNormalizer.Normalize(" inv-001 "));
        Assert.NotEqual(
            PaymentReferenceNormalizer.Normalize("INV-001"),
            PaymentReferenceNormalizer.Normalize("INV001"));
        Assert.NotEqual(
            PaymentReferenceNormalizer.Normalize("INV 001"),
            PaymentReferenceNormalizer.Normalize("INV001"));

        SmartReferenceAnalysis analysis = SmartReferenceNormalizer.Analyze(" inv-001 ");

        Assert.Equal("INV-001", analysis.ExactNormalized);
        Assert.Equal(["INV", "001"], analysis.Tokens);
    }

    [Fact]
    public void Analyze_should_fold_case_and_split_separators_without_dropping_zeros()
    {
        SmartReferenceAnalysis analysis = SmartReferenceNormalizer.Analyze(" fv / 2026 / 1042 ");

        Assert.Equal(["FV", "2026", "1042"], analysis.Tokens);
        Assert.Equal(["2026"], analysis.YearTokens);
        Assert.Equal("FV / 2026 / 1042", analysis.ExactNormalized);
    }

    [Fact]
    public void Analyze_should_treat_composed_and_decomposed_characters_as_the_same_token()
    {
        SmartReferenceAnalysis composed = SmartReferenceNormalizer.Analyze("Caf\u00e9-001234");
        SmartReferenceAnalysis decomposed = SmartReferenceNormalizer.Analyze("Cafe\u0301-001234");

        Assert.Equal(composed.Tokens, decomposed.Tokens);
        Assert.Equal("CAF\u00c9", composed.Tokens[0]);
        Assert.Equal("001234", composed.Tokens[1]);
    }

    [Fact]
    public void Analyze_should_preserve_a_reference_at_the_length_boundary()
    {
        string value = new('A', SmartReferenceNormalizer.MaxTrimmedLength);

        SmartReferenceAnalysis analysis = SmartReferenceNormalizer.Analyze(value);

        Assert.False(analysis.IsOversized);
        Assert.Equal([value], analysis.Tokens);
    }

    [Fact]
    public void Analyze_should_stop_past_the_length_boundary()
    {
        string value = new('A', SmartReferenceNormalizer.MaxTrimmedLength + 1);

        SmartReferenceAnalysis analysis = SmartReferenceNormalizer.Analyze(value);

        Assert.True(analysis.IsOversized);
        Assert.Null(analysis.ExactNormalized);
        Assert.Empty(analysis.Tokens);
        Assert.Empty(analysis.LongDigitRuns);
    }

    [Fact]
    public void Analyze_should_trim_before_applying_the_length_limit()
    {
        string value = " " + new string('A', SmartReferenceNormalizer.MaxTrimmedLength);

        SmartReferenceAnalysis analysis = SmartReferenceNormalizer.Analyze(value);

        Assert.False(analysis.IsOversized);
        Assert.Equal(new string('A', SmartReferenceNormalizer.MaxTrimmedLength), analysis.ExactNormalized);
    }

    [Fact]
    public void Analyze_should_preserve_sixteen_tokens_and_reject_seventeen()
    {
        string accepted = string.Join('/', Enumerable.Range(0, SmartReferenceNormalizer.MaxTokenCount).Select(index => $"P{index}"));
        string rejected = accepted + "/P16";

        SmartReferenceAnalysis withinLimit = SmartReferenceNormalizer.Analyze(accepted);
        SmartReferenceAnalysis overLimit = SmartReferenceNormalizer.Analyze(rejected);

        Assert.False(withinLimit.IsOversized);
        Assert.Equal(SmartReferenceNormalizer.MaxTokenCount, withinLimit.Tokens.Count);
        Assert.True(overLimit.IsOversized);
        Assert.Empty(overLimit.Tokens);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Analyze_should_treat_blank_references_as_missing(string? value)
    {
        SmartReferenceAnalysis analysis = SmartReferenceNormalizer.Analyze(value);

        Assert.True(analysis.IsMissing);
        Assert.False(analysis.IsOversized);
        Assert.Null(analysis.ExactNormalized);
        Assert.Empty(analysis.Tokens);
    }

    [Fact]
    public void Analyze_should_keep_maximal_digit_runs_and_ignore_short_or_embedded_runs()
    {
        SmartReferenceAnalysis analysis = SmartReferenceNormalizer.Analyze("INV001234-1001234-12345");

        Assert.Equal(["001234", "1001234"], analysis.LongDigitRuns);
        Assert.Equal(["INV001234", "1001234", "12345"], analysis.Tokens);
    }
}