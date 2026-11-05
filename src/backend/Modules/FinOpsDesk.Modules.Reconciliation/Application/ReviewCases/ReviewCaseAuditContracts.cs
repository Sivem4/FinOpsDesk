using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;

public sealed record GetReviewCaseAuditQuery(
    ReviewCaseId ReviewCaseId,
    int Page,
    int PageSize);

public sealed record ReviewCaseAuditEntryResponse(
    Guid Id,
    string Action,
    string ActorId,
    DateTimeOffset RecordedAt,
    string? Reason,
    Guid? InvoiceId,
    Guid? BankTransactionId,
    string? SourceLabel,
    string? Note,
    string? Outcome,
    Guid? AllocationId,
    string? ResolutionNote);

public sealed record ReviewCaseAuditListResponse(
    IReadOnlyList<ReviewCaseAuditEntryResponse> Items,
    int Page,
    int PageSize,
    int TotalCount);

public interface IReviewCaseAuditQueries
{
    Task<Result<ReviewCaseAuditListResponse>> GetForReviewCaseAsync(
        OrganizationId organizationId,
        ReviewCaseId reviewCaseId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
}