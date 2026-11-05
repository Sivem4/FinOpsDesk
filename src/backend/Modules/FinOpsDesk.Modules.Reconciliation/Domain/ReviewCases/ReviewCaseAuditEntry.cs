using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;

public sealed class ReviewCaseAuditEntry
{
    public const int MaxActorIdLength = 256;

    public ReviewCaseAuditEntryId Id { get; }

    public OrganizationId OrganizationId { get; }

    public ReviewCaseId ReviewCaseId { get; }

    public ReviewCaseAuditAction Action { get; }

    public string ActorId { get; }

    public DateTimeOffset RecordedAt { get; }

    public ReviewCaseReason? Reason { get; }

    public InvoiceId? InvoiceId { get; }

    public BankTransactionId? BankTransactionId { get; }

    public ReviewCaseSource? SourceLabel { get; }

    public string? Note { get; }

    public ReviewCaseOutcome? Outcome { get; }

    public AllocationId? AllocationId { get; }

    public string? ResolutionNote { get; }

#pragma warning disable CS8618
    private ReviewCaseAuditEntry()
    {
    }
#pragma warning restore CS8618

    private ReviewCaseAuditEntry(
        ReviewCaseAuditEntryId id,
        OrganizationId organizationId,
        ReviewCaseId reviewCaseId,
        ReviewCaseAuditAction action,
        string actorId,
        DateTimeOffset recordedAt,
        ReviewCaseReason? reason,
        InvoiceId? invoiceId,
        BankTransactionId? bankTransactionId,
        ReviewCaseSource? sourceLabel,
        string? note,
        ReviewCaseOutcome? outcome,
        AllocationId? allocationId,
        string? resolutionNote)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(reviewCaseId);

        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(
                nameof(action),
                "Review case audit action is not defined.");
        }

        if (recordedAt == default)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recordedAt),
                "Audit time cannot be the default value.");
        }

        Id = id;
        OrganizationId = organizationId;
        ReviewCaseId = reviewCaseId;
        Action = action;
        ActorId = NormalizeActorId(actorId);
        RecordedAt = recordedAt;
        Reason = reason;
        InvoiceId = invoiceId;
        BankTransactionId = bankTransactionId;
        SourceLabel = sourceLabel;
        Note = note;
        Outcome = outcome;
        AllocationId = allocationId;
        ResolutionNote = resolutionNote;
    }

    public static ReviewCaseAuditEntry ForOpened(
        ReviewCaseAuditEntryId id,
        ReviewCase reviewCase,
        string actorId)
    {
        ArgumentNullException.ThrowIfNull(reviewCase);

        if (reviewCase.Status != ReviewCaseStatus.Open
            || reviewCase.Outcome is not null
            || reviewCase.AllocationId is not null
            || reviewCase.ResolutionNote is not null
            || reviewCase.ClosedAt is not null)
        {
            throw new InvalidOperationException(
                "An opened audit requires a case that is currently open.");
        }

        return new ReviewCaseAuditEntry(
            id,
            reviewCase.OrganizationId,
            reviewCase.Id,
            ReviewCaseAuditAction.Opened,
            actorId,
            reviewCase.OpenedAt,
            reviewCase.Reason,
            reviewCase.InvoiceId,
            reviewCase.BankTransactionId,
            reviewCase.SourceLabel,
            reviewCase.Note,
            outcome: null,
            allocationId: null,
            resolutionNote: null);
    }

    public static ReviewCaseAuditEntry ForResolved(
        ReviewCaseAuditEntryId id,
        ReviewCase reviewCase,
        string actorId)
    {
        ArgumentNullException.ThrowIfNull(reviewCase);

        if (reviewCase.Status != ReviewCaseStatus.Resolved
            || reviewCase.Outcome is not ReviewCaseOutcome outcome
            || reviewCase.ClosedAt is not DateTimeOffset closedAt)
        {
            throw new InvalidOperationException(
                "A resolved audit requires a case that is currently resolved.");
        }

        return new ReviewCaseAuditEntry(
            id,
            reviewCase.OrganizationId,
            reviewCase.Id,
            ReviewCaseAuditAction.Resolved,
            actorId,
            closedAt,
            reason: null,
            invoiceId: null,
            bankTransactionId: null,
            sourceLabel: null,
            note: null,
            outcome,
            reviewCase.AllocationId,
            reviewCase.ResolutionNote);
    }

    public static ReviewCaseAuditEntry ForDismissed(
        ReviewCaseAuditEntryId id,
        ReviewCase reviewCase,
        string actorId)
    {
        ArgumentNullException.ThrowIfNull(reviewCase);

        if (reviewCase.Status != ReviewCaseStatus.Dismissed
            || reviewCase.Outcome is not null
            || reviewCase.AllocationId is not null
            || reviewCase.ClosedAt is not DateTimeOffset closedAt)
        {
            throw new InvalidOperationException(
                "A dismissed audit requires a case that is currently dismissed.");
        }

        return new ReviewCaseAuditEntry(
            id,
            reviewCase.OrganizationId,
            reviewCase.Id,
            ReviewCaseAuditAction.Dismissed,
            actorId,
            closedAt,
            reason: null,
            invoiceId: null,
            bankTransactionId: null,
            sourceLabel: null,
            note: null,
            outcome: null,
            allocationId: null,
            reviewCase.ResolutionNote);
    }

    private static string NormalizeActorId(string actorId)
    {
        ArgumentNullException.ThrowIfNull(actorId);

        if (actorId.Length == 0
            || actorId.Length > MaxActorIdLength
            || actorId != actorId.Trim())
        {
            throw new ArgumentException(
                "Actor id must be a trimmed non-blank value of at most 256 characters.",
                nameof(actorId));
        }

        return actorId;
    }
}