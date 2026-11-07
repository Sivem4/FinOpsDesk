using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.Application.Allocations;

public sealed class AllocationDisputeResolutionTests
{
    private static readonly OrganizationId Organization = OrganizationId.New();

    private static readonly AllocationDisputeId Dispute = AllocationDisputeId.New();

    [Fact]
    public void Reason_trim_keeps_edge_feff_and_rejects_blank_or_too_long_text()
    {
        Assert.True(AllocationDisputeResolution.TryNormalizeReason(
            " \uFEFFkept\uFEFF ",
            out string kept,
            out _));
        Assert.Equal("\uFEFFkept\uFEFF", kept);
        Assert.False(AllocationDisputeResolution.TryNormalizeReason(
            " \t",
            out _,
            out AllocationDisputeResolutionReasonIssue blank));
        Assert.Equal(AllocationDisputeResolutionReasonIssue.Required, blank);
        Assert.False(AllocationDisputeResolution.TryNormalizeReason(
            new string('a', 501),
            out _,
            out AllocationDisputeResolutionReasonIssue tooLong));
        Assert.Equal(AllocationDisputeResolutionReasonIssue.TooLong, tooLong);
    }

    [Fact]
    public void Outcome_names_are_the_two_stored_values()
    {
        Assert.True(AllocationDisputeResolution.TryParseOutcome(
            "upheld",
            out AllocationDisputeResolutionOutcome upheld));
        Assert.Equal(AllocationDisputeResolutionOutcome.Upheld, upheld);
        Assert.True(AllocationDisputeResolution.TryParseOutcome(
            "not_upheld",
            out AllocationDisputeResolutionOutcome notUpheld));
        Assert.Equal(AllocationDisputeResolutionOutcome.NotUpheld, notUpheld);
        Assert.False(AllocationDisputeResolution.TryParseOutcome("Upheld", out _));
        Assert.False(AllocationDisputeResolution.TryParseOutcome("open", out _));
        Assert.Equal("upheld", AllocationDisputeResolution.OutcomeName(AllocationDisputeResolutionOutcome.Upheld));
        Assert.Equal("not_upheld", AllocationDisputeResolution.OutcomeName(AllocationDisputeResolutionOutcome.NotUpheld));
    }

    [Fact]
    public void Match_compares_dispute_outcome_and_reason_ordinally()
    {
        Guid key = Guid.NewGuid();
        AllocationDisputeResolution resolution = AllocationDisputeResolution.Create(
            Organization,
            Dispute,
            AllocationDisputeResolutionOutcome.Upheld,
            " The challenge stands. ",
            ReviewCaseActorSource.DevelopmentJwt,
            "operator-a",
            DateTimeOffset.Parse("2026-11-07T12:00:00Z"),
            key);
        Assert.Equal("The challenge stands.", resolution.Reason);
        Assert.True(resolution.Matches(
            Dispute,
            AllocationDisputeResolutionOutcome.Upheld,
            "The challenge stands."));
        Assert.False(resolution.Matches(
            Dispute,
            AllocationDisputeResolutionOutcome.NotUpheld,
            "The challenge stands."));
        Assert.False(resolution.Matches(
            AllocationDisputeId.New(),
            AllocationDisputeResolutionOutcome.Upheld,
            "The challenge stands."));
        Assert.Throws<ArgumentException>(() => AllocationDisputeResolution.Create(
            Organization,
            Dispute,
            AllocationDisputeResolutionOutcome.Upheld,
            "reason",
            ReviewCaseActorSource.DevelopmentJwt,
            " operator ",
            DateTimeOffset.Parse("2026-11-07T12:00:00Z"),
            key));
        Assert.Throws<ArgumentException>(() => AllocationDisputeResolution.Create(
            Organization,
            Dispute,
            AllocationDisputeResolutionOutcome.Upheld,
            "reason",
            ReviewCaseActorSource.DevelopmentJwt,
            "operator-a",
            DateTimeOffset.Parse("2026-11-07T12:00:00Z"),
            Guid.Empty));
    }
}