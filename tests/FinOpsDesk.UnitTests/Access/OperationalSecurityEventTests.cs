using FinOpsDesk.Modules.Access.Domain;

namespace FinOpsDesk.UnitTests.Access;

public sealed class OperationalSecurityEventTests
{
    private static readonly DateTimeOffset OccurredAt = new(2026, 11, 3, 21, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Disable_user_event_keeps_only_the_user_target()
    {
        Guid userId = Guid.NewGuid();
        OperationalSecurityEvent securityEvent = OperationalSecurityEvent.Record(
            Guid.NewGuid(),
            OperationalSecurityOperation.DisableUser,
            OperationalSecurityOutcome.Applied,
            userId,
            null,
            null,
            "phase-7d5",
            OccurredAt);

        Assert.Equal(userId, securityEvent.TargetUserId);
        Assert.Null(securityEvent.TargetOrganizationId);
        Assert.Null(securityEvent.TargetMembershipId);
        Assert.Equal("phase-7d5", securityEvent.DeclaredOperator);
        Assert.Equal(OccurredAt, securityEvent.OccurredAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" operator")]
    public void Declared_operator_rejects_a_blank_or_padded_identity(string operatorName)
    {
        Assert.Throws<ArgumentException>(() => OperationalSecurityEvent.Record(
            Guid.NewGuid(),
            OperationalSecurityOperation.DisableOrganization,
            OperationalSecurityOutcome.AlreadyApplied,
            null,
            Guid.NewGuid(),
            null,
            operatorName,
            OccurredAt));
    }

    [Fact]
    public void Membership_revocation_requires_user_organization_and_membership()
    {
        Assert.Throws<ArgumentException>(() => OperationalSecurityEvent.Record(
            Guid.NewGuid(),
            OperationalSecurityOperation.RevokeMembership,
            OperationalSecurityOutcome.Applied,
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            "phase-7d5",
            OccurredAt));
    }
}