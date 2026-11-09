using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.Reconciliation.Domain.ReviewCases;

public sealed class ReviewCaseAssignmentTests
{
    private static readonly DateTimeOffset Opened = new(2026, 11, 6, 8, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Later = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Claim_sets_the_current_actor_and_release_clears_only_that_pair()
    {
        ReviewCase reviewCase = Open();

        Assert.Null(reviewCase.Claim(ReviewCaseActorSource.DevelopmentJwt, "same", Later));

        Assert.Equal(ReviewCaseActorSource.DevelopmentJwt, reviewCase.AssignedActorSource);
        Assert.Equal("same", reviewCase.AssignedActorId);
        Assert.Equal(Later, reviewCase.AssignedAt);
        Assert.Equal(
            ReviewCaseAssignmentRejection.NotAssignee,
            reviewCase.Release(ReviewCaseActorSource.ProductionCookie, "same"));
        Assert.Equal(ReviewCaseActorSource.DevelopmentJwt, reviewCase.AssignedActorSource);
        Assert.Null(reviewCase.Release(ReviewCaseActorSource.DevelopmentJwt, "same"));
        Assert.Null(reviewCase.AssignedActorSource);
        Assert.Null(reviewCase.AssignedActorId);
        Assert.Null(reviewCase.AssignedAt);
    }

    [Fact]
    public void A_second_claim_and_a_terminal_case_do_not_change_the_assignment()
    {
        ReviewCase reviewCase = Open();
        Assert.Null(reviewCase.Claim(ReviewCaseActorSource.DevelopmentJwt, "owner", Later));
        Assert.Equal(
            ReviewCaseAssignmentRejection.AlreadyAssigned,
            reviewCase.Claim(ReviewCaseActorSource.DevelopmentJwt, "owner", Later.AddMinutes(1)));
        Assert.Equal(Later, reviewCase.AssignedAt);

        reviewCase.Dismiss(null, Later.AddMinutes(2));
        Assert.Equal(ReviewCaseAssignmentRejection.Terminal, reviewCase.Release(ReviewCaseActorSource.DevelopmentJwt, "owner"));
        Assert.Equal("owner", reviewCase.AssignedActorId);
        Assert.Equal(Later.AddMinutes(2), reviewCase.ClosedAt);
    }

    [Fact]
    public void A_rejected_actor_leaves_every_field_unchanged_and_the_same_instance_can_be_claimed()
    {
        ReviewCase reviewCase = Open();

        Assert.Throws<ArgumentException>(() => reviewCase.Claim(ReviewCaseActorSource.DevelopmentJwt, " owner", Later));

        AssertUnchangedOpenCase(reviewCase);
        Assert.Null(reviewCase.Claim(ReviewCaseActorSource.DevelopmentJwt, "owner", Later));
        Assert.Equal(ReviewCaseActorSource.DevelopmentJwt, reviewCase.AssignedActorSource);
        Assert.Equal("owner", reviewCase.AssignedActorId);
        Assert.Equal(Later, reviewCase.AssignedAt);
        Assert.Equal(ReviewCaseStatus.Open, reviewCase.Status);
        Assert.Null(reviewCase.ClosedAt);
    }

    [Fact]
    public void Delegate_moves_the_case_to_another_actor_of_the_same_source()
    {
        ReviewCase reviewCase = Open();
        Assert.Equal(ReviewCaseAssignmentRejection.NotAssigned, reviewCase.Delegate(
            ReviewCaseActorSource.DevelopmentJwt,
            "owner",
            "colleague",
            Later));
        Assert.Null(reviewCase.Claim(ReviewCaseActorSource.DevelopmentJwt, "owner", Later));

        Assert.Equal(
            ReviewCaseAssignmentRejection.DelegateeUnavailable,
            reviewCase.Delegate(ReviewCaseActorSource.DevelopmentJwt, "owner", "owner", Later.AddMinutes(1)));
        Assert.Equal("owner", reviewCase.AssignedActorId);
        Assert.Equal(Later, reviewCase.AssignedAt);
        Assert.Throws<ArgumentOutOfRangeException>(() => reviewCase.Delegate(
            ReviewCaseActorSource.DevelopmentJwt,
            "owner",
            "colleague",
            default));
        Assert.Equal("owner", reviewCase.AssignedActorId);
        Assert.Equal(Later, reviewCase.AssignedAt);

        Assert.Null(reviewCase.Delegate(
            ReviewCaseActorSource.DevelopmentJwt,
            "owner",
            "colleague",
            Later.AddMinutes(2)));
        Assert.Equal(ReviewCaseActorSource.DevelopmentJwt, reviewCase.AssignedActorSource);
        Assert.Equal("colleague", reviewCase.AssignedActorId);
        Assert.Equal(Later.AddMinutes(2), reviewCase.AssignedAt);
        Assert.Throws<ArgumentException>(() => ReviewCaseAssignmentEntry.Create(
            ReviewCaseAssignmentEntryId.New(),
            reviewCase.OrganizationId,
            reviewCase.Id,
            ReviewCaseAssignmentOperation.Claim,
            ReviewCaseActorSource.DevelopmentJwt,
            "owner",
            "AAAAAAAAAAA=",
            Guid.NewGuid(),
            Later,
            Later,
            "colleague"));
        ReviewCaseAssignmentEntry delegated = ReviewCaseAssignmentEntry.Create(
            ReviewCaseAssignmentEntryId.New(),
            reviewCase.OrganizationId,
            reviewCase.Id,
            ReviewCaseAssignmentOperation.Delegate,
            ReviewCaseActorSource.DevelopmentJwt,
            "owner",
            "AAAAAAAAAAA=",
            Guid.NewGuid(),
            Later.AddMinutes(2),
            Later.AddMinutes(2),
            "colleague");
        Assert.Equal("colleague", delegated.RecipientActorId);
        Assert.Equal(ReviewCaseAssignmentOperation.Delegate, delegated.Operation);
    }

    [Fact]
    public void A_default_assignment_time_leaves_every_field_unchanged_and_the_same_instance_can_be_claimed()
    {
        ReviewCase reviewCase = Open();

        Assert.Throws<ArgumentOutOfRangeException>(() => reviewCase.Claim(
            ReviewCaseActorSource.DevelopmentJwt,
            "owner",
            default));

        AssertUnchangedOpenCase(reviewCase);
        Assert.Null(reviewCase.Claim(ReviewCaseActorSource.DevelopmentJwt, "owner", Later));
        Assert.Equal(ReviewCaseActorSource.DevelopmentJwt, reviewCase.AssignedActorSource);
        Assert.Equal("owner", reviewCase.AssignedActorId);
        Assert.Equal(Later, reviewCase.AssignedAt);
        Assert.Equal(ReviewCaseStatus.Open, reviewCase.Status);
        Assert.Null(reviewCase.ClosedAt);
    }

    [Fact]
    public void Release_of_an_empty_assignment_does_not_invent_one()
    {
        ReviewCase reviewCase = Open();

        Assert.Equal(ReviewCaseAssignmentRejection.NotAssigned, reviewCase.Release(ReviewCaseActorSource.DevelopmentJwt, "owner"));
        Assert.Null(reviewCase.AssignedActorId);
    }

    [Fact]
    public void Entry_matches_only_the_full_payload_and_pairs_assigned_at_with_claim()
    {
        ReviewCaseId reviewCaseId = ReviewCaseId.New();
        Guid key = Guid.NewGuid();
        ReviewCaseAssignmentEntry claim = Entry(
            reviewCaseId,
            ReviewCaseAssignmentOperation.Claim,
            ReviewCaseActorSource.DevelopmentJwt,
            "owner",
            "AAAAAAAAAAA=",
            key,
            Later);

        Assert.True(claim.Matches(
            reviewCaseId,
            ReviewCaseAssignmentOperation.Claim,
            ReviewCaseActorSource.DevelopmentJwt,
            "owner",
            "AAAAAAAAAAA="));
        Assert.False(claim.Matches(
            reviewCaseId,
            ReviewCaseAssignmentOperation.Release,
            ReviewCaseActorSource.DevelopmentJwt,
            "owner",
            "AAAAAAAAAAA="));
        Assert.False(claim.Matches(
            reviewCaseId,
            ReviewCaseAssignmentOperation.Claim,
            ReviewCaseActorSource.ProductionCookie,
            "owner",
            "AAAAAAAAAAA="));
        Assert.False(claim.Matches(
            ReviewCaseId.New(),
            ReviewCaseAssignmentOperation.Claim,
            ReviewCaseActorSource.DevelopmentJwt,
            "owner",
            "AAAAAAAAAAA="));
        Assert.False(claim.Matches(
            reviewCaseId,
            ReviewCaseAssignmentOperation.Claim,
            ReviewCaseActorSource.DevelopmentJwt,
            "other",
            "AAAAAAAAAAA="));
        Assert.False(claim.Matches(
            reviewCaseId,
            ReviewCaseAssignmentOperation.Claim,
            ReviewCaseActorSource.DevelopmentJwt,
            "owner",
            "BBBBBBBBBBB="));
        Assert.Throws<ArgumentException>(() => ReviewCaseAssignmentEntry.Create(
            ReviewCaseAssignmentEntryId.New(),
            OrganizationId.New(),
            reviewCaseId,
            ReviewCaseAssignmentOperation.Release,
            ReviewCaseActorSource.DevelopmentJwt,
            "owner",
            "AAAAAAAAAAA=",
            key,
            Later,
            Later));
    }

    private static void AssertUnchangedOpenCase(ReviewCase reviewCase)
    {
        Assert.Null(reviewCase.AssignedActorSource);
        Assert.Null(reviewCase.AssignedActorId);
        Assert.Null(reviewCase.AssignedAt);
        Assert.Equal(ReviewCaseStatus.Open, reviewCase.Status);
        Assert.Equal(ReviewCaseReason.NeedsInvestigation, reviewCase.Reason);
        Assert.NotNull(reviewCase.InvoiceId);
        Assert.Null(reviewCase.BankTransactionId);
        Assert.Null(reviewCase.SourceLabel);
        Assert.Null(reviewCase.Note);
        Assert.Null(reviewCase.Outcome);
        Assert.Null(reviewCase.ResolutionNote);
        Assert.Null(reviewCase.AllocationId);
        Assert.Equal(Opened, reviewCase.OpenedAt);
        Assert.Null(reviewCase.ClosedAt);
    }

    private static ReviewCase Open()
    {
        return ReviewCase.Open(
            ReviewCaseId.New(),
            OrganizationId.New(),
            ReviewCaseReason.NeedsInvestigation,
            InvoiceId.New(),
            null,
            null,
            null,
            Opened);
    }

    private static ReviewCaseAssignmentEntry Entry(
        ReviewCaseId reviewCaseId,
        ReviewCaseAssignmentOperation operation,
        ReviewCaseActorSource source,
        string actorId,
        string token,
        Guid key,
        DateTimeOffset assignedAt)
    {
        return ReviewCaseAssignmentEntry.Create(
            ReviewCaseAssignmentEntryId.New(),
            OrganizationId.New(),
            reviewCaseId,
            operation,
            source,
            actorId,
            token,
            key,
            assignedAt,
            operation == ReviewCaseAssignmentOperation.Claim ? assignedAt : null);
    }
}