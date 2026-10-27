using FinOpsDesk.Modules.Reconciliation.Domain.Matching;

namespace FinOpsDesk.UnitTests.Reconciliation.Domain.Matching;

public sealed class CompetitorProbeRulesTests
{
    [Fact]
    public void Evaluate_should_return_ambiguous_when_a_fetched_row_matches()
    {
        CompetitorProbeOutcome outcome = CompetitorProbeRules.Evaluate(
            "INV-001",
            [
                new CompetitorProbeRow("OTHER"),
                new CompetitorProbeRow(" inv-001 ")
            ],
            truncated: false);

        Assert.Equal(CompetitorProbeOutcome.Ambiguous, outcome);
    }

    [Fact]
    public void Evaluate_should_return_coverage_limited_when_a_truncated_probe_shows_no_competitor()
    {
        CompetitorProbeOutcome outcome = CompetitorProbeRules.Evaluate(
            "INV-001",
            [new CompetitorProbeRow("OTHER"), new CompetitorProbeRow("   ")],
            truncated: true);

        Assert.Equal(CompetitorProbeOutcome.CoverageLimited, outcome);
    }

    [Fact]
    public void Evaluate_should_return_ambiguous_when_a_truncated_probe_shows_a_competitor()
    {
        CompetitorProbeOutcome outcome = CompetitorProbeRules.Evaluate(
            "inv-001",
            [new CompetitorProbeRow("nope"), new CompetitorProbeRow("INV-001")],
            truncated: true);

        Assert.Equal(CompetitorProbeOutcome.Ambiguous, outcome);
    }

    [Fact]
    public void Evaluate_should_return_unique_when_a_complete_probe_shows_no_competitor()
    {
        CompetitorProbeOutcome outcome = CompetitorProbeRules.Evaluate(
            "INV-001",
            [new CompetitorProbeRow("INV-002"), new CompetitorProbeRow(null)],
            truncated: false);

        Assert.Equal(CompetitorProbeOutcome.Unique, outcome);
        Assert.Equal(
            CompetitorProbeOutcome.CoverageLimited,
            CompetitorProbeRules.Evaluate(
                "   ",
                [new CompetitorProbeRow("INV-001")],
                truncated: false));
    }

    [Fact]
    public void Evaluate_should_treat_100_rows_as_complete_and_an_extra_row_as_coverage_limited()
    {
        CompetitorProbeRow[] oneHundred = Rows(CompetitorProbeRules.MaxCompleteRowCount, "OTHER");
        CompetitorProbeRow[] oneHundredAndOne = Rows(
            CompetitorProbeRules.MaxCompleteRowCount + 1,
            "OTHER");

        Assert.Equal(100, CompetitorProbeRules.MaxCompleteRowCount);
        Assert.Equal(
            CompetitorProbeOutcome.Unique,
            CompetitorProbeRules.Evaluate("INV-001", oneHundred, truncated: false));
        Assert.Equal(
            CompetitorProbeOutcome.CoverageLimited,
            CompetitorProbeRules.Evaluate("INV-001", oneHundred, truncated: true));
        Assert.Equal(
            CompetitorProbeOutcome.CoverageLimited,
            CompetitorProbeRules.Evaluate("INV-001", oneHundredAndOne, truncated: false));

        oneHundred[^1] = new CompetitorProbeRow("INV-001");
        oneHundredAndOne[^1] = new CompetitorProbeRow("inv-001");

        Assert.Equal(
            CompetitorProbeOutcome.Ambiguous,
            CompetitorProbeRules.Evaluate("INV-001", oneHundred, truncated: false));
        Assert.Equal(
            CompetitorProbeOutcome.Ambiguous,
            CompetitorProbeRules.Evaluate("INV-001", oneHundredAndOne, truncated: true));
    }

    private static CompetitorProbeRow[] Rows(int count, string reference)
    {
        CompetitorProbeRow[] rows = new CompetitorProbeRow[count];

        for (int index = 0; index < count; index++)
        {
            rows[index] = new CompetitorProbeRow(reference);
        }

        return rows;
    }
}