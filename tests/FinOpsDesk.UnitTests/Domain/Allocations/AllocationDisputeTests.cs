using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.Domain.Allocations;

public sealed class AllocationDisputeTests
{
    [Fact]
    public void Trim_keeps_the_leading_bom_and_rejects_blank_or_long_text()
    {
        Assert.True(AllocationDispute.TryNormalizeStatement(
            "  \uFEFFcustomer challenge  ",
            out string normalized,
            out _));
        Assert.Equal("\uFEFFcustomer challenge", normalized);

        Assert.False(AllocationDispute.TryNormalizeStatement(
            "   ",
            out _,
            out AllocationDisputeStatementIssue required));
        Assert.Equal(AllocationDisputeStatementIssue.Required, required);

        Assert.False(AllocationDispute.TryNormalizeStatement(
            new string('a', 501),
            out _,
            out AllocationDisputeStatementIssue tooLong));
        Assert.Equal(AllocationDisputeStatementIssue.TooLong, tooLong);
    }

    [Fact]
    public void Match_compares_the_allocation_and_the_normalized_statement()
    {
        AllocationId allocationId = AllocationId.New();
        AllocationDispute dispute = AllocationDispute.Create(
            AllocationDisputeId.New(),
            OrganizationId.New(),
            allocationId,
            "  same text  ",
            ReviewCaseActorSource.DevelopmentJwt,
            "operator-a",
            new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero),
            Guid.NewGuid());

        Assert.True(dispute.Matches(allocationId, "same text"));
        Assert.False(dispute.Matches(allocationId, "same text "));
        Assert.False(dispute.Matches(AllocationId.New(), "same text"));
    }
}