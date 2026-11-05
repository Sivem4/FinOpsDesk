using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.Reconciliation.Domain.ReviewCases;

public sealed class ReviewCaseAuditEntryTests
{
    private static readonly OrganizationId Organization = OrganizationId.New();

    private static readonly DateTimeOffset OpenedAt = new(2026, 11, 5, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Actor_limit_matches_the_financial_audit_actor_limit()
    {
        Assert.Equal(FinancialAuditEntry.MaxActorIdLength, ReviewCaseAuditEntry.MaxActorIdLength);
    }

    [Fact]
    public void ForOpened_copies_the_open_decision_and_its_time()
    {
        InvoiceId invoiceId = InvoiceId.New();
        ReviewCase reviewCase = ReviewCase.Open(
            ReviewCaseId.New(),
            Organization,
            ReviewCaseReason.NeedsInvestigation,
            invoiceId,
            null,
            ReviewCaseSource.Manual,
            " note ",
            OpenedAt);

        ReviewCaseAuditEntry entry = ReviewCaseAuditEntry.ForOpened(
            ReviewCaseAuditEntryId.New(),
            reviewCase,
            "operator-a");

        Assert.Equal(ReviewCaseAuditAction.Opened, entry.Action);
        Assert.Equal(reviewCase.Id, entry.ReviewCaseId);
        Assert.Equal(Organization, entry.OrganizationId);
        Assert.Equal("operator-a", entry.ActorId);
        Assert.Equal(OpenedAt, entry.RecordedAt);
        Assert.Equal(ReviewCaseReason.NeedsInvestigation, entry.Reason);
        Assert.Equal(invoiceId, entry.InvoiceId);
        Assert.Null(entry.BankTransactionId);
        Assert.Equal(ReviewCaseSource.Manual, entry.SourceLabel);
        Assert.Equal("note", entry.Note);
        Assert.Null(entry.Outcome);
        Assert.Null(entry.AllocationId);
        Assert.Null(entry.ResolutionNote);
    }

    [Fact]
    public void ForResolved_and_ForDismissed_copy_only_the_terminal_payload()
    {
        DateTimeOffset closedAt = OpenedAt.AddMinutes(3);
        ReviewCase resolved = Open();
        Assert.True(resolved.Resolve(
            ReviewCaseOutcome.NoActionRequired,
            null,
            " done ",
            closedAt).IsSuccess);
        ReviewCase dismissed = Open();
        Assert.True(dismissed.Dismiss(" stop ", closedAt).IsSuccess);

        ReviewCaseAuditEntry resolution = ReviewCaseAuditEntry.ForResolved(
            ReviewCaseAuditEntryId.New(),
            resolved,
            "operator-a");
        ReviewCaseAuditEntry dismissal = ReviewCaseAuditEntry.ForDismissed(
            ReviewCaseAuditEntryId.New(),
            dismissed,
            "operator-a");

        Assert.Equal(closedAt, resolution.RecordedAt);
        Assert.Equal(ReviewCaseOutcome.NoActionRequired, resolution.Outcome);
        Assert.Equal("done", resolution.ResolutionNote);
        Assert.Null(resolution.Reason);
        Assert.Null(resolution.InvoiceId);
        Assert.Null(resolution.AllocationId);
        Assert.Equal(closedAt, dismissal.RecordedAt);
        Assert.Equal("stop", dismissal.ResolutionNote);
        Assert.Null(dismissal.Outcome);
        Assert.Null(dismissal.Reason);
        Assert.Null(dismissal.SourceLabel);
    }

    [Fact]
    public void Factories_reject_the_wrong_case_state_and_an_unusable_actor()
    {
        ReviewCase open = Open();

        Assert.Throws<InvalidOperationException>(() =>
            ReviewCaseAuditEntry.ForResolved(ReviewCaseAuditEntryId.New(), open, "operator-a"));
        Assert.Throws<InvalidOperationException>(() =>
            ReviewCaseAuditEntry.ForDismissed(ReviewCaseAuditEntryId.New(), open, "operator-a"));
        Assert.Throws<ArgumentException>(() =>
            ReviewCaseAuditEntry.ForOpened(ReviewCaseAuditEntryId.New(), open, " operator-a"));
        Assert.Throws<ArgumentException>(() =>
            ReviewCaseAuditEntry.ForOpened(ReviewCaseAuditEntryId.New(), open, new string('a', 257)));
        Assert.Throws<ArgumentException>(() =>
            ReviewCaseAuditEntry.ForOpened(ReviewCaseAuditEntryId.New(), open, " "));
    }

    private static ReviewCase Open()
    {
        return ReviewCase.Open(
            ReviewCaseId.New(),
            Organization,
            ReviewCaseReason.NeedsInvestigation,
            InvoiceId.New(),
            null,
            null,
            null,
            OpenedAt);
    }
}