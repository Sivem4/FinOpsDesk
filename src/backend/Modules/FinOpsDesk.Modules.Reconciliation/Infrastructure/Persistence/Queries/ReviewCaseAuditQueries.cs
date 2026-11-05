using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Queries;

internal sealed class ReviewCaseAuditQueries : IReviewCaseAuditQueries
{
    private readonly ReconciliationDbContext _dbContext;

    public ReviewCaseAuditQueries(ReconciliationDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    public async Task<Result<ReviewCaseAuditListResponse>> GetForReviewCaseAsync(
        OrganizationId organizationId,
        ReviewCaseId reviewCaseId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(reviewCaseId);

        bool reviewCaseExists = await _dbContext.ReviewCases
            .AsNoTracking()
            .AnyAsync(
                reviewCase => reviewCase.OrganizationId == organizationId
                    && reviewCase.Id == reviewCaseId,
                cancellationToken);
        if (!reviewCaseExists)
        {
            return Result<ReviewCaseAuditListResponse>.Failure(
                ReconciliationApplicationErrors.ReviewCaseNotFound);
        }

        IQueryable<ReviewCaseAuditEntry> entries = _dbContext.ReviewCaseAuditEntries
            .AsNoTracking()
            .Where(entry => entry.OrganizationId == organizationId
                && entry.ReviewCaseId == reviewCaseId);

        int totalCount = await entries.CountAsync(cancellationToken);
        long offset = ((long)page - 1L) * pageSize;
        List<AuditRow> rows = await entries
            .OrderBy(entry => entry.RecordedAt)
            .ThenBy(entry => entry.Action)
            .ThenBy(entry => entry.Id)
            .Skip(checked((int)offset))
            .Take(pageSize)
            .Select(entry => new AuditRow(
                entry.Id,
                entry.Action,
                entry.ActorId,
                entry.RecordedAt,
                entry.Reason,
                entry.InvoiceId,
                entry.BankTransactionId,
                entry.SourceLabel,
                entry.Note,
                entry.Outcome,
                entry.AllocationId,
                entry.ResolutionNote))
            .ToListAsync(cancellationToken);

        ReviewCaseAuditEntryResponse[] items = new ReviewCaseAuditEntryResponse[rows.Count];
        for (int index = 0; index < rows.Count; index++)
        {
            items[index] = ToResponse(rows[index]);
        }

        return Result<ReviewCaseAuditListResponse>.Success(
            new ReviewCaseAuditListResponse(items, page, pageSize, totalCount));
    }

    private static ReviewCaseAuditEntryResponse ToResponse(AuditRow row)
    {
        return new ReviewCaseAuditEntryResponse(
            row.Id.Value,
            ActionName(row.Action),
            row.ActorId,
            row.RecordedAt,
            row.Reason is null ? null : ReasonName(row.Reason.Value),
            row.InvoiceId?.Value,
            row.BankTransactionId?.Value,
            row.SourceLabel?.Value,
            row.Note,
            row.Outcome is null ? null : OutcomeName(row.Outcome.Value),
            row.AllocationId?.Value,
            row.ResolutionNote);
    }

    private static string ActionName(ReviewCaseAuditAction action)
    {
        return action switch
        {
            ReviewCaseAuditAction.Opened => "Opened",
            ReviewCaseAuditAction.Resolved => "Resolved",
            ReviewCaseAuditAction.Dismissed => "Dismissed",
            _ => throw new InvalidOperationException(
                "Review case audit action is not defined."),
        };
    }

    private static string ReasonName(ReviewCaseReason reason)
    {
        return reason switch
        {
            ReviewCaseReason.CompetingMatch => "CompetingMatch",
            ReviewCaseReason.NeedsInvestigation => "NeedsInvestigation",
            _ => throw new InvalidOperationException(
                "Review case reason is not defined."),
        };
    }

    private static string OutcomeName(ReviewCaseOutcome outcome)
    {
        return outcome switch
        {
            ReviewCaseOutcome.AllocationRecorded => "AllocationRecorded",
            ReviewCaseOutcome.NoActionRequired => "NoActionRequired",
            ReviewCaseOutcome.HandledOutside => "HandledOutside",
            _ => throw new InvalidOperationException(
                "Review case outcome is not defined."),
        };
    }

    private sealed record AuditRow(
        ReviewCaseAuditEntryId Id,
        ReviewCaseAuditAction Action,
        string ActorId,
        DateTimeOffset RecordedAt,
        ReviewCaseReason? Reason,
        InvoiceId? InvoiceId,
        BankTransactionId? BankTransactionId,
        ReviewCaseSource? SourceLabel,
        string? Note,
        ReviewCaseOutcome? Outcome,
        AllocationId? AllocationId,
        string? ResolutionNote);
}