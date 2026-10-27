using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.Reconciliation.Domain.ReviewCases;

public sealed class ReviewCaseTests
{
    private static readonly OrganizationId Organization =
        new(new Guid("11111111-1111-1111-1111-111111111111"));

    private static readonly OrganizationId OtherOrganization =
        new(new Guid("33333333-3333-3333-3333-333333333333"));

    private static readonly InvoiceId Invoice =
        new(new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

    private static readonly BankTransactionId BankTransaction =
        new(new Guid("22222222-2222-2222-2222-222222222222"));

    private static readonly DateTimeOffset OpenedAt =
        new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset ClosedAt =
        new(2026, 10, 26, 10, 0, 0, TimeSpan.Zero);

    private static readonly CurrencyCode Pln = new("PLN");

    [Fact]
    public void Open_competing_match_requires_both_financial_ids_and_builds_subject_key()
    {
        ReviewCase reviewCase = OpenCompeting();

        Assert.Equal(ReviewCaseReason.CompetingMatch, reviewCase.Reason);
        Assert.Equal(ReviewCaseStatus.Open, reviewCase.Status);
        Assert.Equal(Organization, reviewCase.OrganizationId);
        Assert.Equal(Invoice, reviewCase.InvoiceId);
        Assert.Equal(BankTransaction, reviewCase.BankTransactionId);
        Assert.Equal(OpenedAt, reviewCase.OpenedAt);
        Assert.Null(reviewCase.Outcome);
        Assert.Null(reviewCase.AllocationId);
        Assert.Null(reviewCase.ResolutionNote);
        Assert.Null(reviewCase.ClosedAt);
        Assert.Equal(
            "competing:aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee:22222222-2222-2222-2222-222222222222",
            reviewCase.SubjectKey);
        Assert.Equal(83, reviewCase.SubjectKey.Length);
        Assert.Equal(96, ReviewCase.SubjectKeyMaxLength);
        Assert.True(reviewCase.SubjectKey.Length <= ReviewCase.SubjectKeyMaxLength);
    }

    [Fact]
    public void Competing_match_without_both_ids_is_rejected()
    {
        Assert.Throws<ArgumentException>(
            () => ReviewCase.Open(
                ReviewCaseId.New(),
                Organization,
                ReviewCaseReason.CompetingMatch,
                Invoice,
                bankTransactionId: null,
                sourceLabel: null,
                note: null,
                OpenedAt));
        Assert.Throws<ArgumentException>(
            () => ReviewCase.Open(
                ReviewCaseId.New(),
                Organization,
                ReviewCaseReason.CompetingMatch,
                invoiceId: null,
                BankTransaction,
                sourceLabel: null,
                note: null,
                OpenedAt));
    }

    [Fact]
    public void Open_investigation_accepts_exactly_one_financial_id()
    {
        ReviewCase invoiceCase = ReviewCase.Open(
            ReviewCaseId.New(),
            Organization,
            ReviewCaseReason.NeedsInvestigation,
            Invoice,
            bankTransactionId: null,
            sourceLabel: null,
            note: null,
            OpenedAt);
        ReviewCase bankCase = ReviewCase.Open(
            ReviewCaseId.New(),
            Organization,
            ReviewCaseReason.NeedsInvestigation,
            invoiceId: null,
            BankTransaction,
            sourceLabel: null,
            note: null,
            OpenedAt);

        Assert.Equal(
            "investigation:invoice:aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            invoiceCase.SubjectKey);
        Assert.Null(invoiceCase.BankTransactionId);
        Assert.Equal(
            "investigation:bank:22222222-2222-2222-2222-222222222222",
            bankCase.SubjectKey);
        Assert.Null(bankCase.InvoiceId);
    }

    [Fact]
    public void Investigation_with_both_ids_or_neither_id_is_rejected()
    {
        Assert.Throws<ArgumentException>(
            () => ReviewCase.Open(
                ReviewCaseId.New(),
                Organization,
                ReviewCaseReason.NeedsInvestigation,
                Invoice,
                BankTransaction,
                sourceLabel: null,
                note: null,
                OpenedAt));
        Assert.Throws<ArgumentException>(
            () => ReviewCase.Open(
                ReviewCaseId.New(),
                Organization,
                ReviewCaseReason.NeedsInvestigation,
                invoiceId: null,
                bankTransactionId: null,
                sourceLabel: null,
                note: null,
                OpenedAt));
    }

    [Fact]
    public void Subject_key_is_derived_by_the_domain_and_does_not_change_when_closed()
    {
        ReviewCase reviewCase = OpenCompeting();
        string subjectKey = reviewCase.SubjectKey;

        reviewCase.Resolve(
            ReviewCaseOutcome.NoActionRequired,
            allocationId: null,
            resolutionNote: null,
            ClosedAt);

        Assert.Equal(subjectKey, reviewCase.SubjectKey);
        Assert.Equal(OtherOrganization, OpenCompeting(OtherOrganization).OrganizationId);
        Assert.Equal(subjectKey, OpenCompeting(OtherOrganization).SubjectKey);
    }

    [Theory]
    [InlineData("manual")]
    [InlineData(" exact-1 ")]
    [InlineData("smart-1")]
    public void Approved_source_labels_are_accepted(string source)
    {
        ReviewCase reviewCase = ReviewCase.Open(
            ReviewCaseId.New(),
            Organization,
            ReviewCaseReason.CompetingMatch,
            Invoice,
            BankTransaction,
            ReviewCaseSource.Parse(source),
            note: null,
            OpenedAt);

        Assert.Equal(source.Trim(), reviewCase.SourceLabel!.Value);
    }

    [Theory]
    [InlineData("Manual")]
    [InlineData("exact")]
    [InlineData("smart-2")]
    public void Unknown_source_labels_are_rejected(string source)
    {
        Assert.Throws<ArgumentException>(() => ReviewCaseSource.Parse(source));
    }

    [Fact]
    public void Notes_are_trimmed_and_blank_notes_become_null()
    {
        ReviewCase reviewCase = OpenCompeting(note: "  Verified with bank  ");

        Assert.Equal("Verified with bank", reviewCase.Note);
        Assert.Null(OpenCompeting(note: "   ").Note);
        Assert.Equal(500, OpenCompeting(note: new string('a', 500)).Note!.Length);
        Assert.Throws<ArgumentException>(() => OpenCompeting(note: new string('a', 501)));
    }

    [Fact]
    public void Resolve_and_dismiss_move_an_open_case_to_the_matching_terminal_state()
    {
        ReviewCase resolved = OpenCompeting();
        AllocationId allocationId = AllocationId.New();
        var resolvedAt = ClosedAt;

        var resolve = resolved.Resolve(
            ReviewCaseOutcome.AllocationRecorded,
            allocationId,
            "  Allocation already recorded  ",
            resolvedAt);

        Assert.Equal(ReviewCaseDecision.Applied, resolve.Value);
        Assert.Equal(ReviewCaseStatus.Resolved, resolved.Status);
        Assert.Equal(ReviewCaseOutcome.AllocationRecorded, resolved.Outcome);
        Assert.Equal(allocationId, resolved.AllocationId);
        Assert.Equal("Allocation already recorded", resolved.ResolutionNote);
        Assert.Equal(resolvedAt, resolved.ClosedAt);
        Assert.Equal("Verified opening", resolved.Note);

        ReviewCase dismissed = OpenCompeting();
        var dismiss = dismissed.Dismiss("  Not actionable  ", ClosedAt);

        Assert.Equal(ReviewCaseDecision.Applied, dismiss.Value);
        Assert.Equal(ReviewCaseStatus.Dismissed, dismissed.Status);
        Assert.Null(dismissed.Outcome);
        Assert.Null(dismissed.AllocationId);
        Assert.Equal("Not actionable", dismissed.ResolutionNote);
        Assert.Equal(ClosedAt, dismissed.ClosedAt);
    }

    [Fact]
    public void Identical_resolve_is_a_replay_and_a_different_note_conflicts()
    {
        ReviewCase reviewCase = OpenCompeting();
        reviewCase.Resolve(
            ReviewCaseOutcome.NoActionRequired,
            allocationId: null,
            "Verified with bank",
            ClosedAt);

        var replay = reviewCase.Resolve(
            ReviewCaseOutcome.NoActionRequired,
            allocationId: null,
            "  Verified with bank  ",
            ClosedAt.AddHours(2));
        var conflict = reviewCase.Resolve(
            ReviewCaseOutcome.NoActionRequired,
            allocationId: null,
            "Different decision",
            ClosedAt.AddHours(3));

        Assert.Equal(ReviewCaseDecision.IdenticalReplay, replay.Value);
        Assert.Equal(ClosedAt, reviewCase.ClosedAt);
        Assert.Equal("Verified with bank", reviewCase.ResolutionNote);
        Assert.Equal(ReconciliationErrors.ReviewCaseTerminalConflict.Code, conflict.Error!.Code);
        Assert.Equal(ClosedAt, reviewCase.ClosedAt);
        Assert.Equal("Verified with bank", reviewCase.ResolutionNote);
    }

    [Fact]
    public void Identical_dismiss_is_a_replay_and_a_different_note_conflicts()
    {
        ReviewCase reviewCase = OpenCompeting();
        reviewCase.Dismiss("Verified with bank", ClosedAt);

        var replay = reviewCase.Dismiss("  Verified with bank  ", ClosedAt.AddHours(1));
        var conflict = reviewCase.Dismiss("Different decision", ClosedAt.AddHours(2));

        Assert.Equal(ReviewCaseDecision.IdenticalReplay, replay.Value);
        Assert.Equal(ClosedAt, reviewCase.ClosedAt);
        Assert.Equal("Verified with bank", reviewCase.ResolutionNote);
        Assert.Equal(ReconciliationErrors.ReviewCaseTerminalConflict.Code, conflict.Error!.Code);
        Assert.Equal("Verified with bank", reviewCase.ResolutionNote);
    }

    [Fact]
    public void Switching_terminal_decisions_is_a_conflict()
    {
        ReviewCase resolved = OpenCompeting();
        resolved.Resolve(
            ReviewCaseOutcome.HandledOutside,
            allocationId: null,
            resolutionNote: null,
            ClosedAt);
        ReviewCase dismissed = OpenCompeting();
        dismissed.Dismiss(resolutionNote: null, ClosedAt);

        var dismissResolved = resolved.Dismiss(resolutionNote: null, ClosedAt.AddMinutes(1));
        var resolveDismissed = dismissed.Resolve(
            ReviewCaseOutcome.HandledOutside,
            allocationId: null,
            resolutionNote: null,
            ClosedAt.AddMinutes(1));

        Assert.Equal(ReconciliationErrors.ReviewCaseTerminalConflict.Code, dismissResolved.Error!.Code);
        Assert.Equal(ReviewCaseStatus.Resolved, resolved.Status);
        Assert.Equal(ReconciliationErrors.ReviewCaseTerminalConflict.Code, resolveDismissed.Error!.Code);
        Assert.Equal(ReviewCaseStatus.Dismissed, dismissed.Status);
    }

    [Fact]
    public void Allocation_recorded_requires_an_allocation_id_and_other_outcomes_forbid_one()
    {
        ReviewCase missingAllocation = OpenCompeting();
        var missing = missingAllocation.Resolve(
            ReviewCaseOutcome.AllocationRecorded,
            allocationId: null,
            resolutionNote: null,
            ClosedAt);

        Assert.Equal(ReconciliationErrors.ReviewCaseAllocationRequired.Code, missing.Error!.Code);
        Assert.Equal(ReviewCaseStatus.Open, missingAllocation.Status);
        Assert.Null(missingAllocation.ClosedAt);

        AllocationId allocationId = AllocationId.New();
        ReviewCase noAction = OpenCompeting();
        var forbidden = noAction.Resolve(
            ReviewCaseOutcome.NoActionRequired,
            allocationId,
            resolutionNote: null,
            ClosedAt);
        ReviewCase handledOutside = OpenCompeting();
        var alsoForbidden = handledOutside.Resolve(
            ReviewCaseOutcome.HandledOutside,
            allocationId,
            resolutionNote: null,
            ClosedAt);

        Assert.Equal(ReconciliationErrors.ReviewCaseAllocationForbidden.Code, forbidden.Error!.Code);
        Assert.Equal(ReconciliationErrors.ReviewCaseAllocationForbidden.Code, alsoForbidden.Error!.Code);
        Assert.Equal(ReviewCaseStatus.Open, noAction.Status);
        Assert.Equal(ReviewCaseStatus.Open, handledOutside.Status);
    }

    [Fact]
    public void Close_time_before_open_time_is_rejected()
    {
        ReviewCase reviewCase = OpenCompeting();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => reviewCase.Resolve(
                ReviewCaseOutcome.NoActionRequired,
                allocationId: null,
                resolutionNote: null,
                OpenedAt.AddMinutes(-1)));
        Assert.Equal(ReviewCaseStatus.Open, reviewCase.Status);
        Assert.Null(reviewCase.ClosedAt);
    }

    [Fact]
    public void Review_transitions_do_not_mutate_invoice_or_bank_transaction_balances()
    {
        var invoice = new Invoice(
            Invoice,
            Organization,
            "INV-001",
            new Money(100m, Pln));
        var bankTransaction = new BankTransaction(
            BankTransaction,
            Organization,
            new Money(100m, Pln),
            new DateOnly(2026, 10, 26),
            "INV-001");
        ReviewCase reviewCase = OpenCompeting();

        reviewCase.Resolve(
            ReviewCaseOutcome.AllocationRecorded,
            AllocationId.New(),
            resolutionNote: null,
            ClosedAt);

        Assert.Equal(Money.Zero(Pln), invoice.AllocatedAmount);
        Assert.Equal(Money.Zero(Pln), bankTransaction.AllocatedAmount);
    }

    private static ReviewCase OpenCompeting(
        OrganizationId? organizationId = null,
        string? note = "Verified opening")
    {
        return ReviewCase.Open(
            ReviewCaseId.New(),
            organizationId ?? Organization,
            ReviewCaseReason.CompetingMatch,
            Invoice,
            BankTransaction,
            sourceLabel: null,
            note,
            OpenedAt);
    }
}