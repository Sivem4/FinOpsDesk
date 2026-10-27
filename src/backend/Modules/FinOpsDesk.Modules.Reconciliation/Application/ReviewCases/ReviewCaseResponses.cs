using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;

namespace FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;

internal static class ReviewCaseResponses
{
    public static ReviewCaseResponse From(ReviewCase reviewCase, byte[] rowVersion)
    {
        return Create(
            reviewCase.Id.Value,
            reviewCase.Reason,
            reviewCase.Status,
            reviewCase.InvoiceId?.Value,
            reviewCase.BankTransactionId?.Value,
            reviewCase.SourceLabel?.Value,
            reviewCase.Note,
            reviewCase.Outcome,
            reviewCase.ResolutionNote,
            reviewCase.AllocationId?.Value,
            reviewCase.OpenedAt,
            reviewCase.ClosedAt,
            rowVersion);
    }

    public static ReviewCaseResponse From(ReviewCaseSnapshot snapshot)
    {
        return Create(
            snapshot.ReviewCaseId.Value,
            snapshot.Reason,
            snapshot.Status,
            snapshot.InvoiceId?.Value,
            snapshot.BankTransactionId?.Value,
            snapshot.SourceLabel?.Value,
            snapshot.Note,
            snapshot.Outcome,
            snapshot.ResolutionNote,
            snapshot.AllocationId?.Value,
            snapshot.OpenedAt,
            snapshot.ClosedAt,
            snapshot.RowVersion);
    }

    public static bool IsIdenticalResolution(
        ReviewCase reviewCase,
        ReviewCaseOutcome outcome,
        AllocationId? allocationId,
        string? resolutionNote)
    {
        return reviewCase.Status == ReviewCaseStatus.Resolved
            && reviewCase.Outcome == outcome
            && reviewCase.AllocationId == allocationId
            && reviewCase.ResolutionNote == resolutionNote;
    }

    public static bool IsIdenticalResolution(
        ReviewCaseSnapshot snapshot,
        ReviewCaseOutcome outcome,
        AllocationId? allocationId,
        string? resolutionNote)
    {
        return snapshot.Status == ReviewCaseStatus.Resolved
            && snapshot.Outcome == outcome
            && snapshot.AllocationId == allocationId
            && snapshot.ResolutionNote == resolutionNote;
    }

    public static bool IsIdenticalDismissal(ReviewCase reviewCase, string? resolutionNote)
    {
        return reviewCase.Status == ReviewCaseStatus.Dismissed
            && reviewCase.Outcome is null
            && reviewCase.AllocationId is null
            && reviewCase.ResolutionNote == resolutionNote;
    }

    public static bool IsIdenticalDismissal(ReviewCaseSnapshot snapshot, string? resolutionNote)
    {
        return snapshot.Status == ReviewCaseStatus.Dismissed
            && snapshot.Outcome is null
            && snapshot.AllocationId is null
            && snapshot.ResolutionNote == resolutionNote;
    }

    public static bool SameRowVersion(byte[] left, byte[] right)
    {
        return left.AsSpan().SequenceEqual(right);
    }

    private static ReviewCaseResponse Create(
        Guid reviewCaseId,
        ReviewCaseReason reason,
        ReviewCaseStatus status,
        Guid? invoiceId,
        Guid? bankTransactionId,
        string? sourceLabel,
        string? note,
        ReviewCaseOutcome? outcome,
        string? resolutionNote,
        Guid? allocationId,
        DateTimeOffset openedAt,
        DateTimeOffset? closedAt,
        byte[] rowVersion)
    {
        return new ReviewCaseResponse(
            reviewCaseId,
            reason.ToString(),
            status.ToString(),
            invoiceId,
            bankTransactionId,
            sourceLabel,
            note,
            outcome?.ToString(),
            resolutionNote,
            allocationId,
            openedAt,
            closedAt,
            ReviewCaseConcurrencyToken.Encode(rowVersion));
    }
}