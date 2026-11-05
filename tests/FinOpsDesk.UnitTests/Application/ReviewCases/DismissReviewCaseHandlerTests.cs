using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.Reconciliation.Application.ReviewCases;

public sealed class DismissReviewCaseHandlerTests
{
    private static readonly OrganizationId OrganizationId =
        new(new Guid("11111111-1111-1111-1111-111111111111"));

    [Fact]
    public async Task Handle_should_dismiss_an_open_case_with_the_caller_token()
    {
        ReviewCase reviewCase = OpenCase();
        FakeReviewCaseRepository reviewCases = new();
        reviewCases.Seed(reviewCase, ReviewCaseHandlerTestDoubles.CurrentVersion);
        ScriptedUnitOfWork unitOfWork = new();
        FixedTimeProvider time = new(ReviewCaseHandlerTestDoubles.OpenedAt);
        using CancellationTokenSource cancellation = new();
        DismissReviewCaseHandler handler = CreateHandler(
            new RecordingOrganizationContext(OrganizationId),
            reviewCases,
            unitOfWork: unitOfWork,
            time: time);

        Result<ReviewCaseResponse> result = await handler.Handle(
            new DismissReviewCaseCommand(
                reviewCase.Id,
                "  not ours  ",
                ReviewCaseConcurrencyToken.Encode(ReviewCaseHandlerTestDoubles.CurrentVersion)),
            cancellation.Token);

        Assert.True(result.IsSuccess);
        Assert.Equal("Dismissed", result.Value.Status);
        Assert.Null(result.Value.Outcome);
        Assert.Null(result.Value.AllocationId);
        Assert.Equal("not ours", result.Value.ResolutionNote);
        Assert.Equal(ReviewCaseHandlerTestDoubles.OpenedAt, result.Value.ClosedAt);
        Assert.Equal(1, unitOfWork.SaveCount);
        Assert.Equal(1, time.ReadCount);
        Assert.Equal(cancellation.Token, reviewCases.CancellationToken);
        Assert.Equal(ReviewCaseHandlerTestDoubles.CurrentVersion, reviewCases.ExpectedVersion);
    }

    [Fact]
    public async Task Handle_should_replay_an_identical_dismissal_without_saving()
    {
        ReviewCase reviewCase = OpenCase();
        Assert.True(reviewCase.Dismiss("closed", ReviewCaseHandlerTestDoubles.OpenedAt).IsSuccess);
        DateTimeOffset? closedAt = reviewCase.ClosedAt;
        FakeReviewCaseRepository reviewCases = new();
        reviewCases.Seed(reviewCase, ReviewCaseHandlerTestDoubles.CurrentVersion);
        ScriptedUnitOfWork unitOfWork = new();
        DismissReviewCaseHandler handler = CreateHandler(
            new RecordingOrganizationContext(OrganizationId),
            reviewCases,
            unitOfWork: unitOfWork);

        Result<ReviewCaseResponse> result = await handler.Handle(
            new DismissReviewCaseCommand(
                reviewCase.Id,
                " closed ",
                ReviewCaseConcurrencyToken.Encode(ReviewCaseHandlerTestDoubles.OtherVersion)),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(closedAt, result.Value.ClosedAt);
        Assert.Equal(
            ReviewCaseConcurrencyToken.Encode(ReviewCaseHandlerTestDoubles.CurrentVersion),
            result.Value.ConcurrencyToken);
        Assert.Equal(0, unitOfWork.SaveCount);
    }

    [Fact]
    public async Task Handle_should_conflict_when_a_dismissal_note_or_terminal_state_differs()
    {
        ReviewCase dismissed = OpenCase();
        Assert.True(dismissed.Dismiss("kept", ReviewCaseHandlerTestDoubles.OpenedAt).IsSuccess);
        ReviewCase resolved = OpenCase();
        Assert.True(resolved.Resolve(
            ReviewCaseOutcome.HandledOutside,
            null,
            null,
            ReviewCaseHandlerTestDoubles.OpenedAt).IsSuccess);
        FakeReviewCaseRepository reviewCases = new();
        reviewCases.Seed(dismissed, ReviewCaseHandlerTestDoubles.CurrentVersion);
        reviewCases.Seed(resolved, ReviewCaseHandlerTestDoubles.CurrentVersion);
        ScriptedUnitOfWork unitOfWork = new();
        DismissReviewCaseHandler handler = CreateHandler(
            new RecordingOrganizationContext(OrganizationId),
            reviewCases,
            unitOfWork: unitOfWork);
        string token = ReviewCaseConcurrencyToken.Encode(ReviewCaseHandlerTestDoubles.CurrentVersion);

        Result<ReviewCaseResponse> differentNote = await handler.Handle(
            new DismissReviewCaseCommand(dismissed.Id, "changed", token),
            CancellationToken.None);
        Result<ReviewCaseResponse> resolvedCase = await handler.Handle(
            new DismissReviewCaseCommand(resolved.Id, null, token),
            CancellationToken.None);

        Assert.Equal(ReconciliationErrors.ReviewCaseTerminalConflict.Code, differentNote.Error!.Code);
        Assert.Equal(ReconciliationErrors.ReviewCaseTerminalConflict.Code, resolvedCase.Error!.Code);
        Assert.Equal("kept", dismissed.ResolutionNote);
        Assert.Equal(0, unitOfWork.SaveCount);
    }

    [Fact]
    public async Task Handle_should_reject_a_stale_token_and_a_missing_token()
    {
        ReviewCase reviewCase = OpenCase();
        FakeReviewCaseRepository reviewCases = new();
        reviewCases.Seed(reviewCase, ReviewCaseHandlerTestDoubles.CurrentVersion);
        ScriptedUnitOfWork unitOfWork = new();
        RecordingOrganizationContext organization = new(OrganizationId);
        DismissReviewCaseHandler handler = CreateHandler(organization, reviewCases, unitOfWork: unitOfWork);

        Result<ReviewCaseResponse> stale = await handler.Handle(
            new DismissReviewCaseCommand(
                reviewCase.Id,
                null,
                ReviewCaseConcurrencyToken.Encode(ReviewCaseHandlerTestDoubles.OtherVersion)),
            CancellationToken.None);
        Result<ReviewCaseResponse> missing = await handler.Handle(
            new DismissReviewCaseCommand(reviewCase.Id, null, ""),
            CancellationToken.None);

        Assert.Equal(
            ReconciliationApplicationErrors.ReviewCaseConcurrencyConflict.Code,
            stale.Error!.Code);
        Assert.Equal(
            ReconciliationApplicationErrors.ReviewCaseInvalidConcurrencyToken.Code,
            missing.Error!.Code);
        Assert.Equal(ReviewCaseStatus.Open, reviewCase.Status);
        Assert.Equal(0, unitOfWork.SaveCount);
        Assert.Equal(1, organization.ReadCount);
    }

    [Fact]
    public async Task Handle_should_return_the_concurrent_winner_only_when_the_dismissal_matches()
    {
        Result<ReviewCaseResponse> replay = await DismissAgainstWinnerAsync("same", "same");
        Result<ReviewCaseResponse> conflict = await DismissAgainstWinnerAsync("same", "different");

        Assert.True(replay.IsSuccess);
        Assert.Equal(ReconciliationErrors.ReviewCaseTerminalConflict.Code, conflict.Error!.Code);
    }

    private static async Task<Result<ReviewCaseResponse>> DismissAgainstWinnerAsync(
        string requestedNote,
        string winningNote)
    {
        ReviewCase reviewCase = OpenCase();
        FakeReviewCaseRepository reviewCases = new();
        reviewCases.Seed(reviewCase, ReviewCaseHandlerTestDoubles.CurrentVersion);
        ReviewCase winner = OpenCase();
        Assert.True(winner.Dismiss(winningNote, ReviewCaseHandlerTestDoubles.OpenedAt).IsSuccess);
        FakeReviewCaseSnapshotLookup lookup = new()
        {
            ById = ReviewCaseHandlerTestDoubles.Snapshot(
                winner,
                ReviewCaseHandlerTestDoubles.OtherVersion) with
            {
                ReviewCaseId = reviewCase.Id,
            },
        };
        DismissReviewCaseHandler handler = CreateHandler(
            new RecordingOrganizationContext(OrganizationId),
            reviewCases,
            lookup,
            new ScriptedUnitOfWork
            {
                Exception = new ConcurrencyConflictException("lost"),
            });

        return await handler.Handle(
            new DismissReviewCaseCommand(
                reviewCase.Id,
                requestedNote,
                ReviewCaseConcurrencyToken.Encode(ReviewCaseHandlerTestDoubles.CurrentVersion)),
            CancellationToken.None);
    }

    [Fact]
    public async Task Handle_should_append_one_dismissed_entry_and_skip_an_identical_replay()
    {
        ReviewCase reviewCase = OpenCase();
        FakeReviewCaseRepository reviewCases = new();
        reviewCases.Seed(reviewCase, ReviewCaseHandlerTestDoubles.CurrentVersion);
        DateTimeOffset closedAt = ReviewCaseHandlerTestDoubles.OpenedAt.AddMinutes(2);
        FixedTimeProvider time = new(closedAt);
        RecordingReviewCaseAuditWriter audit = new();
        ScriptedUnitOfWork unitOfWork = new();
        DismissReviewCaseHandler handler = CreateHandler(
            new RecordingOrganizationContext(OrganizationId),
            reviewCases,
            unitOfWork: unitOfWork,
            time: time,
            audit: audit);

        Result<ReviewCaseResponse> applied = await handler.Handle(
            new DismissReviewCaseCommand(
                reviewCase.Id,
                " closed ",
                Convert.ToBase64String(ReviewCaseHandlerTestDoubles.CurrentVersion)),
            CancellationToken.None);

        Assert.True(applied.IsSuccess);
        ReviewCaseAuditEntry entry = Assert.Single(audit.Entries);
        Assert.Equal(ReviewCaseAuditAction.Dismissed, entry.Action);
        Assert.Equal("operator-a", entry.ActorId);
        Assert.Equal(closedAt, entry.RecordedAt);
        Assert.Equal("closed", entry.ResolutionNote);
        Assert.Null(entry.Outcome);
        Assert.Equal(1, time.ReadCount);

        DismissReviewCaseHandler replay = CreateHandler(
            new RecordingOrganizationContext(OrganizationId),
            reviewCases,
            unitOfWork: unitOfWork,
            time: time,
            actor: new FixedActorContext(null),
            audit: audit);
        Result<ReviewCaseResponse> repeated = await replay.Handle(
            new DismissReviewCaseCommand(
                reviewCase.Id,
                "closed",
                Convert.ToBase64String(ReviewCaseHandlerTestDoubles.CurrentVersion)),
            CancellationToken.None);

        Assert.True(repeated.IsFailure);
        Assert.Equal(ReconciliationApplicationErrors.ActorRequired.Code, repeated.Error.Code);
        Assert.Single(audit.Entries);
        Assert.Equal(1, unitOfWork.SaveCount);
    }

    private static DismissReviewCaseHandler CreateHandler(
        ICurrentOrganizationContext organization,
        FakeReviewCaseRepository reviewCases,
        FakeReviewCaseSnapshotLookup? lookup = null,
        ScriptedUnitOfWork? unitOfWork = null,
        TimeProvider? time = null,
        ICurrentActorContext? actor = null,
        RecordingReviewCaseAuditWriter? audit = null)
    {
        return new DismissReviewCaseHandler(
            organization,
            actor ?? new FixedActorContext(),
            reviewCases,
            lookup ?? new FakeReviewCaseSnapshotLookup(),
            audit ?? new RecordingReviewCaseAuditWriter(),
            new NoReviewCaseAuditConflict(),
            unitOfWork ?? new ScriptedUnitOfWork(),
            time ?? new FixedTimeProvider(ReviewCaseHandlerTestDoubles.OpenedAt));
    }

    private static ReviewCase OpenCase()
    {
        return ReviewCase.Open(
            ReviewCaseId.New(),
            OrganizationId,
            ReviewCaseReason.NeedsInvestigation,
            InvoiceId.New(),
            null,
            null,
            null,
            ReviewCaseHandlerTestDoubles.OpenedAt);
    }
}