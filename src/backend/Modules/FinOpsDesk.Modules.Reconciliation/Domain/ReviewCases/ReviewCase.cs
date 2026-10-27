using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;

public sealed class ReviewCase
{
    public const int SubjectKeyMaxLength = 96;

    public const int MaxNoteLength = 500;

    public ReviewCaseId Id { get; }

    public OrganizationId OrganizationId { get; }

    public ReviewCaseReason Reason { get; }

    public ReviewCaseStatus Status { get; private set; }

    public InvoiceId? InvoiceId { get; }

    public BankTransactionId? BankTransactionId { get; }

    // Active identity of this subject. Uniqueness applies only while Status is Open.
    // A resolved or dismissed case does not block a later open case for the same subject.
    // This key is not a lifetime idempotency key and is not supplied by the client.
    public string SubjectKey { get; }

    public ReviewCaseSource? SourceLabel { get; }

    public string? Note { get; }

    public ReviewCaseOutcome? Outcome { get; private set; }

    public string? ResolutionNote { get; private set; }

    public AllocationId? AllocationId { get; private set; }

    public DateTimeOffset OpenedAt { get; }

    public DateTimeOffset? ClosedAt { get; private set; }

#pragma warning disable CS8618
    private ReviewCase()
    {
    }
#pragma warning restore CS8618

    private ReviewCase(
        ReviewCaseId id,
        OrganizationId organizationId,
        ReviewCaseReason reason,
        InvoiceId? invoiceId,
        BankTransactionId? bankTransactionId,
        string subjectKey,
        ReviewCaseSource? sourceLabel,
        string? note,
        DateTimeOffset openedAt)
    {
        Id = id;
        OrganizationId = organizationId;
        Reason = reason;
        Status = ReviewCaseStatus.Open;
        InvoiceId = invoiceId;
        BankTransactionId = bankTransactionId;
        SubjectKey = subjectKey;
        SourceLabel = sourceLabel;
        Note = note;
        OpenedAt = openedAt;
    }

    public static ReviewCase Open(
        ReviewCaseId id,
        OrganizationId organizationId,
        ReviewCaseReason reason,
        InvoiceId? invoiceId,
        BankTransactionId? bankTransactionId,
        ReviewCaseSource? sourceLabel,
        string? note,
        DateTimeOffset openedAt)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(organizationId);

        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(
                nameof(reason),
                "Review reason is not defined.");
        }

        if (openedAt == default)
        {
            throw new ArgumentOutOfRangeException(
                nameof(openedAt),
                "Open time cannot be the default value.");
        }

        ValidateReasonShape(reason, invoiceId, bankTransactionId);
        string subjectKey = CreateSubjectKey(reason, invoiceId, bankTransactionId);
        if (subjectKey.Length > SubjectKeyMaxLength)
        {
            throw new InvalidOperationException(
                "Subject key exceeds the persisted maximum length.");
        }

        return new ReviewCase(
            id,
            organizationId,
            reason,
            invoiceId,
            bankTransactionId,
            subjectKey,
            sourceLabel,
            NormalizeNote(note),
            openedAt);
    }

    public Result<ReviewCaseDecision> Resolve(
        ReviewCaseOutcome outcome,
        AllocationId? allocationId,
        string? resolutionNote,
        DateTimeOffset closedAt)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(
                nameof(outcome),
                "Review outcome is not defined.");
        }

        EnsureClosable(closedAt);
        string? normalizedNote = NormalizeNote(resolutionNote);
        Result<ReviewCaseDecision>? shapeError = ValidateOutcomeShape(outcome, allocationId);
        if (shapeError is not null)
        {
            return shapeError;
        }

        if (Status != ReviewCaseStatus.Open)
        {
            if (MatchesTerminal(ReviewCaseStatus.Resolved, outcome, allocationId, normalizedNote))
            {
                return Result<ReviewCaseDecision>.Success(ReviewCaseDecision.IdenticalReplay);
            }

            return Result<ReviewCaseDecision>.Failure(
                ReconciliationErrors.ReviewCaseTerminalConflict);
        }

        Status = ReviewCaseStatus.Resolved;
        Outcome = outcome;
        AllocationId = allocationId;
        ResolutionNote = normalizedNote;
        ClosedAt = closedAt;
        return Result<ReviewCaseDecision>.Success(ReviewCaseDecision.Applied);
    }

    public Result<ReviewCaseDecision> Dismiss(
        string? resolutionNote,
        DateTimeOffset closedAt)
    {
        EnsureClosable(closedAt);
        string? normalizedNote = NormalizeNote(resolutionNote);

        if (Status != ReviewCaseStatus.Open)
        {
            if (MatchesTerminal(
                ReviewCaseStatus.Dismissed,
                outcome: null,
                allocationId: null,
                normalizedNote))
            {
                return Result<ReviewCaseDecision>.Success(ReviewCaseDecision.IdenticalReplay);
            }

            return Result<ReviewCaseDecision>.Failure(
                ReconciliationErrors.ReviewCaseTerminalConflict);
        }

        Status = ReviewCaseStatus.Dismissed;
        Outcome = null;
        AllocationId = null;
        ResolutionNote = normalizedNote;
        ClosedAt = closedAt;
        return Result<ReviewCaseDecision>.Success(ReviewCaseDecision.Applied);
    }

    internal static string? NormalizeNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return null;
        }

        string trimmed = note.Trim();
        if (trimmed.Length > MaxNoteLength)
        {
            throw new ArgumentException(
                $"Review note cannot exceed {MaxNoteLength} characters.",
                nameof(note));
        }

        return trimmed;
    }

    private static void ValidateReasonShape(
        ReviewCaseReason reason,
        InvoiceId? invoiceId,
        BankTransactionId? bankTransactionId)
    {
        bool hasInvoice = invoiceId is not null;
        bool hasBankTransaction = bankTransactionId is not null;

        if (reason == ReviewCaseReason.CompetingMatch)
        {
            if (!hasInvoice || !hasBankTransaction)
            {
                throw new ArgumentException(
                    "A competing match requires an invoice id and a bank transaction id.");
            }

            return;
        }

        if (hasInvoice == hasBankTransaction)
        {
            throw new ArgumentException(
                "An investigation requires exactly one financial record.");
        }
    }

    private static string CreateSubjectKey(
        ReviewCaseReason reason,
        InvoiceId? invoiceId,
        BankTransactionId? bankTransactionId)
    {
        if (reason == ReviewCaseReason.CompetingMatch)
        {
            return $"competing:{invoiceId!.Value:D}:{bankTransactionId!.Value:D}";
        }

        if (invoiceId is not null)
        {
            return $"investigation:invoice:{invoiceId.Value:D}";
        }

        return $"investigation:bank:{bankTransactionId!.Value:D}";
    }

    private static Result<ReviewCaseDecision>? ValidateOutcomeShape(
        ReviewCaseOutcome outcome,
        AllocationId? allocationId)
    {
        if (outcome == ReviewCaseOutcome.AllocationRecorded)
        {
            if (allocationId is null)
            {
                return Result<ReviewCaseDecision>.Failure(
                    ReconciliationErrors.ReviewCaseAllocationRequired);
            }

            return null;
        }

        if (allocationId is not null)
        {
            return Result<ReviewCaseDecision>.Failure(
                ReconciliationErrors.ReviewCaseAllocationForbidden);
        }

        return null;
    }

    private void EnsureClosable(DateTimeOffset closedAt)
    {
        if (closedAt < OpenedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(closedAt),
                "Close time cannot precede open time.");
        }
    }

    private bool MatchesTerminal(
        ReviewCaseStatus status,
        ReviewCaseOutcome? outcome,
        AllocationId? allocationId,
        string? resolutionNote)
    {
        return Status == status
            && Outcome == outcome
            && AllocationId == allocationId
            && ResolutionNote == resolutionNote;
    }
}