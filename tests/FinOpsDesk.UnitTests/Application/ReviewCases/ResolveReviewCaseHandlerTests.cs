using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.Reconciliation.Application.ReviewCases;

public sealed class ResolveReviewCaseHandlerTests
{
    private static readonly OrganizationId OrganizationId =
        new(new Guid("11111111-1111-1111-1111-111111111111"));

    private static readonly OrganizationId OtherOrganizationId =
        new(new Guid("22222222-2222-2222-2222-222222222222"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-base64")]
    [InlineData("YQ==")]
    public async Task Handle_should_reject_a_malformed_concurrency_token_before_loading(
        string token)
    {
        RecordingOrganizationContext organization = new(OrganizationId);
        FakeReviewCaseRepository reviewCases = new();
        ResolveReviewCaseHandler handler = CreateHandler(organization, reviewCases);

        Result<ReviewCaseResponse> result = await handler.Handle(
            Command(ReviewCaseId.New(), ReviewCaseOutcome.NoActionRequired, null, null, token),
            CancellationToken.None);

        Assert.Equal(
            ReconciliationApplicationErrors.ReviewCaseInvalidConcurrencyToken.Code,
            result.Error!.Code);
        Assert.Equal(0, organization.ReadCount);
        Assert.Equal(0, reviewCases.GetCount);
    }

    [Fact]
    public async Task Handle_should_reject_allocation_shape_before_loading_or_looking_up_money()
    {
        RecordingOrganizationContext organization = new(OrganizationId);
        FakeReviewCaseRepository reviewCases = new();
        FakeAllocationLinkLookup allocations = new();
        ResolveReviewCaseHandler handler = CreateHandler(
            organization,
            reviewCases,
            allocations: allocations);
        string token = Token(ReviewCaseHandlerTestDoubles.CurrentVersion);

        Result<ReviewCaseResponse> missing = await handler.Handle(
            Command(
                ReviewCaseId.New(),
                ReviewCaseOutcome.AllocationRecorded,
                null,
                null,
                token),
            CancellationToken.None);
        Result<ReviewCaseResponse> forbidden = await handler.Handle(
            Command(
                ReviewCaseId.New(),
                ReviewCaseOutcome.NoActionRequired,
                AllocationId.New(),
                null,
                token),
            CancellationToken.None);

        Assert.Equal(ReconciliationErrors.ReviewCaseAllocationRequired.Code, missing.Error!.Code);
        Assert.Equal(ReconciliationErrors.ReviewCaseAllocationForbidden.Code, forbidden.Error!.Code);
        Assert.Equal(0, organization.ReadCount);
        Assert.Equal(0, reviewCases.GetCount);
        Assert.Equal(0, allocations.CallCount);
    }

    [Fact]
    public async Task Handle_should_hide_a_foreign_case()
    {
        ReviewCase foreign = OpenInvoiceCase(OtherOrganizationId);
        FakeReviewCaseRepository reviewCases = new();
        reviewCases.Seed(foreign, ReviewCaseHandlerTestDoubles.CurrentVersion);
        ResolveReviewCaseHandler handler = CreateHandler(
            new RecordingOrganizationContext(OrganizationId),
            reviewCases);

        Result<ReviewCaseResponse> result = await handler.Handle(
            Command(
                foreign.Id,
                ReviewCaseOutcome.NoActionRequired,
                null,
                null,
                Token(ReviewCaseHandlerTestDoubles.CurrentVersion)),
            CancellationToken.None);

        Assert.Equal(ReconciliationApplicationErrors.ReviewCaseNotFound.Code, result.Error!.Code);
        Assert.Equal(ReviewCaseStatus.Open, foreign.Status);
    }

    [Fact]
    public async Task Handle_should_resolve_a_confirmed_allocation_that_covers_the_case()
    {
        ReviewCase reviewCase = OpenCompetingCase();
        AllocationId allocationId = AllocationId.New();
        FakeReviewCaseRepository reviewCases = new();
        reviewCases.Seed(reviewCase, ReviewCaseHandlerTestDoubles.CurrentVersion);
        FakeAllocationLinkLookup allocations = new()
        {
            OwnerOrganizationId = OrganizationId,
            Link = new AllocationLink(
                allocationId,
                reviewCase.InvoiceId!,
                reviewCase.BankTransactionId!,
                AllocationStatus.Confirmed),
        };
        ScriptedUnitOfWork unitOfWork = new();
        FixedTimeProvider time = new(ReviewCaseHandlerTestDoubles.OpenedAt);
        using CancellationTokenSource cancellation = new();
        ResolveReviewCaseHandler handler = CreateHandler(
            new RecordingOrganizationContext(OrganizationId),
            reviewCases,
            allocations,
            unitOfWork: unitOfWork,
            time: time);

        Result<ReviewCaseResponse> result = await handler.Handle(
            Command(
                reviewCase.Id,
                ReviewCaseOutcome.AllocationRecorded,
                allocationId,
                "  recorded  ",
                Token(ReviewCaseHandlerTestDoubles.CurrentVersion)),
            cancellation.Token);

        Assert.True(result.IsSuccess);
        Assert.Equal("Resolved", result.Value.Status);
        Assert.Equal("AllocationRecorded", result.Value.Outcome);
        Assert.Equal(allocationId.Value, result.Value.AllocationId);
        Assert.Equal("recorded", result.Value.ResolutionNote);
        Assert.Equal(ReviewCaseHandlerTestDoubles.OpenedAt, result.Value.ClosedAt);
        Assert.Equal(1, unitOfWork.SaveCount);
        Assert.Equal(1, time.ReadCount);
        Assert.Equal(OrganizationId, allocations.OrganizationId);
        Assert.Equal(cancellation.Token, allocations.CancellationToken);
        Assert.Equal(ReviewCaseHandlerTestDoubles.CurrentVersion, reviewCases.ExpectedVersion);
    }

    [Theory]
    [InlineData(ReviewCaseOutcome.NoActionRequired)]
    [InlineData(ReviewCaseOutcome.HandledOutside)]
    public async Task Handle_should_resolve_without_an_allocation_lookup(ReviewCaseOutcome outcome)
    {
        ReviewCase reviewCase = OpenInvoiceCase(OrganizationId);
        FakeReviewCaseRepository reviewCases = new();
        reviewCases.Seed(reviewCase, ReviewCaseHandlerTestDoubles.CurrentVersion);
        FakeAllocationLinkLookup allocations = new();
        ScriptedUnitOfWork unitOfWork = new();
        ResolveReviewCaseHandler handler = CreateHandler(
            new RecordingOrganizationContext(OrganizationId),
            reviewCases,
            allocations,
            unitOfWork: unitOfWork);

        Result<ReviewCaseResponse> result = await handler.Handle(
            Command(
                reviewCase.Id,
                outcome,
                null,
                null,
                Token(ReviewCaseHandlerTestDoubles.CurrentVersion)),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(outcome.ToString(), result.Value.Outcome);
        Assert.Null(result.Value.AllocationId);
        Assert.Equal(0, allocations.CallCount);
        Assert.Equal(1, unitOfWork.SaveCount);
    }

    [Fact]
    public async Task Handle_should_reject_a_reversed_foreign_or_unrelated_allocation()
    {
        ReviewCase reviewCase = OpenCompetingCase();
        FakeReviewCaseRepository reviewCases = new();
        reviewCases.Seed(reviewCase, ReviewCaseHandlerTestDoubles.CurrentVersion);
        AllocationId allocationId = AllocationId.New();
        FakeAllocationLinkLookup allocations = new()
        {
            OwnerOrganizationId = OtherOrganizationId,
            Link = new AllocationLink(
                allocationId,
                reviewCase.InvoiceId!,
                reviewCase.BankTransactionId!,
                AllocationStatus.Confirmed),
        };
        ScriptedUnitOfWork unitOfWork = new();
        ResolveReviewCaseHandler handler = CreateHandler(
            new RecordingOrganizationContext(OrganizationId),
            reviewCases,
            allocations,
            unitOfWork: unitOfWork);

        Result<ReviewCaseResponse> foreign = await handler.Handle(
            Command(
                reviewCase.Id,
                ReviewCaseOutcome.AllocationRecorded,
                allocationId,
                null,
                Token(ReviewCaseHandlerTestDoubles.CurrentVersion)),
            CancellationToken.None);
        allocations.OwnerOrganizationId = OrganizationId;
        allocations.Link = allocations.Link! with { Status = AllocationStatus.Reversed };
        Result<ReviewCaseResponse> reversed = await handler.Handle(
            Command(
                reviewCase.Id,
                ReviewCaseOutcome.AllocationRecorded,
                allocationId,
                null,
                Token(ReviewCaseHandlerTestDoubles.CurrentVersion)),
            CancellationToken.None);
        allocations.Link = allocations.Link with
        {
            Status = AllocationStatus.Confirmed,
            InvoiceId = InvoiceId.New(),
        };
        Result<ReviewCaseResponse> unrelated = await handler.Handle(
            Command(
                reviewCase.Id,
                ReviewCaseOutcome.AllocationRecorded,
                allocationId,
                null,
                Token(ReviewCaseHandlerTestDoubles.CurrentVersion)),
            CancellationToken.None);

        Assert.Equal(ReconciliationApplicationErrors.ReviewCaseAllocationMismatch.Code, foreign.Error!.Code);
        Assert.Equal(ReconciliationApplicationErrors.ReviewCaseAllocationMismatch.Code, reversed.Error!.Code);
        Assert.Equal(ReconciliationApplicationErrors.ReviewCaseAllocationMismatch.Code, unrelated.Error!.Code);
        Assert.Equal(ReviewCaseStatus.Open, reviewCase.Status);
        Assert.Equal(0, unitOfWork.SaveCount);
    }

    [Fact]
    public async Task Handle_should_accept_invoice_only_and_bank_only_coverage()
    {
        ReviewCase invoiceCase = OpenInvoiceCase(OrganizationId);
        ReviewCase bankCase = OpenBankCase();
        FakeReviewCaseRepository reviewCases = new();
        reviewCases.Seed(invoiceCase, ReviewCaseHandlerTestDoubles.CurrentVersion);
        reviewCases.Seed(bankCase, ReviewCaseHandlerTestDoubles.CurrentVersion);
        AllocationId invoiceAllocationId = AllocationId.New();
        AllocationId bankAllocationId = AllocationId.New();
        FakeAllocationLinkLookup allocations = new()
        {
            OwnerOrganizationId = OrganizationId,
            Link = new AllocationLink(
                invoiceAllocationId,
                invoiceCase.InvoiceId!,
                BankTransactionId.New(),
                AllocationStatus.Confirmed),
        };
        ResolveReviewCaseHandler handler = CreateHandler(
            new RecordingOrganizationContext(OrganizationId),
            reviewCases,
            allocations);

        Result<ReviewCaseResponse> invoiceResult = await handler.Handle(
            Command(
                invoiceCase.Id,
                ReviewCaseOutcome.AllocationRecorded,
                invoiceAllocationId,
                null,
                Token(ReviewCaseHandlerTestDoubles.CurrentVersion)),
            CancellationToken.None);
        allocations.Link = new AllocationLink(
            bankAllocationId,
            InvoiceId.New(),
            bankCase.BankTransactionId!,
            AllocationStatus.Confirmed);
        Result<ReviewCaseResponse> bankResult = await handler.Handle(
            Command(
                bankCase.Id,
                ReviewCaseOutcome.AllocationRecorded,
                bankAllocationId,
                null,
                Token(ReviewCaseHandlerTestDoubles.CurrentVersion)),
            CancellationToken.None);

        Assert.True(invoiceResult.IsSuccess);
        Assert.True(bankResult.IsSuccess);
    }

    [Fact]
    public async Task Handle_should_replay_an_identical_terminal_decision_without_saving_or_reading_the_allocation()
    {
        ReviewCase reviewCase = OpenInvoiceCase(OrganizationId);
        AllocationId allocationId = AllocationId.New();
        Assert.True(reviewCase.Resolve(
            ReviewCaseOutcome.AllocationRecorded,
            allocationId,
            "done",
            ReviewCaseHandlerTestDoubles.OpenedAt).IsSuccess);
        FakeReviewCaseRepository reviewCases = new();
        reviewCases.Seed(reviewCase, ReviewCaseHandlerTestDoubles.OtherVersion);
        FakeAllocationLinkLookup allocations = new();
        ScriptedUnitOfWork unitOfWork = new();
        ResolveReviewCaseHandler handler = CreateHandler(
            new RecordingOrganizationContext(OrganizationId),
            reviewCases,
            allocations,
            unitOfWork: unitOfWork);

        Result<ReviewCaseResponse> result = await handler.Handle(
            Command(
                reviewCase.Id,
                ReviewCaseOutcome.AllocationRecorded,
                allocationId,
                "  done  ",
                Token(ReviewCaseHandlerTestDoubles.CurrentVersion)),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("done", result.Value.ResolutionNote);
        Assert.Equal(ReviewCaseHandlerTestDoubles.OpenedAt, result.Value.ClosedAt);
        Assert.Equal(
            ReviewCaseConcurrencyToken.Encode(ReviewCaseHandlerTestDoubles.OtherVersion),
            result.Value.ConcurrencyToken);
        Assert.Equal(0, allocations.CallCount);
        Assert.Equal(0, unitOfWork.SaveCount);
    }

    [Fact]
    public async Task Handle_should_conflict_when_the_terminal_note_differs()
    {
        ReviewCase reviewCase = OpenInvoiceCase(OrganizationId);
        Assert.True(reviewCase.Resolve(
            ReviewCaseOutcome.NoActionRequired,
            null,
            "kept",
            ReviewCaseHandlerTestDoubles.OpenedAt).IsSuccess);
        FakeReviewCaseRepository reviewCases = new();
        reviewCases.Seed(reviewCase, ReviewCaseHandlerTestDoubles.CurrentVersion);
        ScriptedUnitOfWork unitOfWork = new();
        ResolveReviewCaseHandler handler = CreateHandler(
            new RecordingOrganizationContext(OrganizationId),
            reviewCases,
            unitOfWork: unitOfWork);

        Result<ReviewCaseResponse> result = await handler.Handle(
            Command(
                reviewCase.Id,
                ReviewCaseOutcome.NoActionRequired,
                null,
                "changed",
                Token(ReviewCaseHandlerTestDoubles.CurrentVersion)),
            CancellationToken.None);

        Assert.Equal(ReconciliationErrors.ReviewCaseTerminalConflict.Code, result.Error!.Code);
        Assert.Equal("kept", reviewCase.ResolutionNote);
        Assert.Equal(0, unitOfWork.SaveCount);
    }

    [Fact]
    public async Task Handle_should_reject_a_stale_token_while_the_case_is_open()
    {
        ReviewCase reviewCase = OpenInvoiceCase(OrganizationId);
        FakeReviewCaseRepository reviewCases = new();
        reviewCases.Seed(reviewCase, ReviewCaseHandlerTestDoubles.CurrentVersion);
        ScriptedUnitOfWork unitOfWork = new();
        ResolveReviewCaseHandler handler = CreateHandler(
            new RecordingOrganizationContext(OrganizationId),
            reviewCases,
            unitOfWork: unitOfWork);

        Result<ReviewCaseResponse> result = await handler.Handle(
            Command(
                reviewCase.Id,
                ReviewCaseOutcome.HandledOutside,
                null,
                null,
                Token(ReviewCaseHandlerTestDoubles.OtherVersion)),
            CancellationToken.None);

        Assert.Equal(
            ReconciliationApplicationErrors.ReviewCaseConcurrencyConflict.Code,
            result.Error!.Code);
        Assert.Equal(ReviewCaseStatus.Open, reviewCase.Status);
        Assert.Equal(0, unitOfWork.SaveCount);
    }

    [Fact]
    public async Task Handle_should_replay_when_a_concurrent_winner_stored_the_same_decision()
    {
        ReviewCase reviewCase = OpenInvoiceCase(OrganizationId);
        FakeReviewCaseRepository reviewCases = new();
        reviewCases.Seed(reviewCase, ReviewCaseHandlerTestDoubles.CurrentVersion);
        ReviewCase winner = OpenInvoiceCase(OrganizationId);
        Assert.True(winner.Resolve(
            ReviewCaseOutcome.NoActionRequired,
            null,
            "same",
            ReviewCaseHandlerTestDoubles.OpenedAt).IsSuccess);
        FakeReviewCaseSnapshotLookup lookup = new()
        {
            ById = ReviewCaseHandlerTestDoubles.Snapshot(
                winner,
                ReviewCaseHandlerTestDoubles.OtherVersion) with
            {
                ReviewCaseId = reviewCase.Id,
            },
        };
        ScriptedUnitOfWork unitOfWork = new()
        {
            Exception = new ConcurrencyConflictException("lost"),
        };
        ResolveReviewCaseHandler handler = CreateHandler(
            new RecordingOrganizationContext(OrganizationId),
            reviewCases,
            lookup: lookup,
            unitOfWork: unitOfWork);

        Result<ReviewCaseResponse> result = await handler.Handle(
            Command(
                reviewCase.Id,
                ReviewCaseOutcome.NoActionRequired,
                null,
                "same",
                Token(ReviewCaseHandlerTestDoubles.CurrentVersion)),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            ReviewCaseConcurrencyToken.Encode(ReviewCaseHandlerTestDoubles.OtherVersion),
            result.Value.ConcurrencyToken);
        Assert.Equal(1, unitOfWork.SaveCount);
        Assert.Equal(1, lookup.IdLookups);
    }

    [Fact]
    public async Task Handle_should_not_reapply_after_a_different_concurrent_decision()
    {
        ReviewCase reviewCase = OpenInvoiceCase(OrganizationId);
        FakeReviewCaseRepository reviewCases = new();
        reviewCases.Seed(reviewCase, ReviewCaseHandlerTestDoubles.CurrentVersion);
        ReviewCase winner = OpenInvoiceCase(OrganizationId);
        Assert.True(winner.Dismiss("other", ReviewCaseHandlerTestDoubles.OpenedAt).IsSuccess);
        FakeReviewCaseSnapshotLookup lookup = new()
        {
            ById = ReviewCaseHandlerTestDoubles.Snapshot(
                winner,
                ReviewCaseHandlerTestDoubles.OtherVersion) with
            {
                ReviewCaseId = reviewCase.Id,
            },
        };
        ScriptedUnitOfWork unitOfWork = new()
        {
            Exception = new ConcurrencyConflictException("lost"),
        };
        ResolveReviewCaseHandler handler = CreateHandler(
            new RecordingOrganizationContext(OrganizationId),
            reviewCases,
            lookup: lookup,
            unitOfWork: unitOfWork);

        Result<ReviewCaseResponse> result = await handler.Handle(
            Command(
                reviewCase.Id,
                ReviewCaseOutcome.NoActionRequired,
                null,
                "mine",
                Token(ReviewCaseHandlerTestDoubles.CurrentVersion)),
            CancellationToken.None);

        Assert.Equal(ReconciliationErrors.ReviewCaseTerminalConflict.Code, result.Error!.Code);
        Assert.Equal(1, unitOfWork.SaveCount);
    }

    private static ResolveReviewCaseHandler CreateHandler(
        ICurrentOrganizationContext organization,
        FakeReviewCaseRepository reviewCases,
        FakeAllocationLinkLookup? allocations = null,
        FakeReviewCaseSnapshotLookup? lookup = null,
        ScriptedUnitOfWork? unitOfWork = null,
        TimeProvider? time = null)
    {
        return new ResolveReviewCaseHandler(
            organization,
            reviewCases,
            lookup ?? new FakeReviewCaseSnapshotLookup(),
            allocations ?? new FakeAllocationLinkLookup(),
            unitOfWork ?? new ScriptedUnitOfWork(),
            time ?? new FixedTimeProvider(ReviewCaseHandlerTestDoubles.OpenedAt));
    }

    private static ResolveReviewCaseCommand Command(
        ReviewCaseId reviewCaseId,
        ReviewCaseOutcome outcome,
        AllocationId? allocationId,
        string? note,
        string token)
    {
        return new ResolveReviewCaseCommand(reviewCaseId, outcome, allocationId, note, token);
    }

    private static ReviewCase OpenInvoiceCase(OrganizationId organizationId)
    {
        return ReviewCase.Open(
            ReviewCaseId.New(),
            organizationId,
            ReviewCaseReason.NeedsInvestigation,
            InvoiceId.New(),
            null,
            null,
            null,
            ReviewCaseHandlerTestDoubles.OpenedAt);
    }

    private static ReviewCase OpenBankCase()
    {
        return ReviewCase.Open(
            ReviewCaseId.New(),
            OrganizationId,
            ReviewCaseReason.NeedsInvestigation,
            null,
            BankTransactionId.New(),
            null,
            null,
            ReviewCaseHandlerTestDoubles.OpenedAt);
    }

    private static ReviewCase OpenCompetingCase()
    {
        return ReviewCase.Open(
            ReviewCaseId.New(),
            OrganizationId,
            ReviewCaseReason.CompetingMatch,
            InvoiceId.New(),
            BankTransactionId.New(),
            null,
            null,
            ReviewCaseHandlerTestDoubles.OpenedAt);
    }

    private static string Token(byte[] rowVersion)
    {
        return ReviewCaseConcurrencyToken.Encode(rowVersion);
    }
}