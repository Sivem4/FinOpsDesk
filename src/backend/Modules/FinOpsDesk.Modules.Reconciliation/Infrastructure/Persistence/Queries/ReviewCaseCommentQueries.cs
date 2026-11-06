using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Queries;

internal sealed class ReviewCaseCommentQueries : IReviewCaseCommentQueries
{
    private readonly ReconciliationDbContext _dbContext;

    public ReviewCaseCommentQueries(ReconciliationDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    public Task<bool> ExistsAsync(
        OrganizationId organizationId,
        ReviewCaseId reviewCaseId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(reviewCaseId);

        return _dbContext.ReviewCases
            .AsNoTracking()
            .AnyAsync(
                reviewCase => reviewCase.OrganizationId == organizationId
                    && reviewCase.Id == reviewCaseId,
                cancellationToken);
    }

    public async Task<Result<ReviewCaseCommentListResponse>> GetForReviewCaseAsync(
        OrganizationId organizationId,
        ReviewCaseId reviewCaseId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(reviewCaseId);

        bool reviewCaseExists = await ExistsAsync(organizationId, reviewCaseId, cancellationToken);
        if (!reviewCaseExists)
        {
            return Result<ReviewCaseCommentListResponse>.Failure(
                ReconciliationApplicationErrors.ReviewCaseNotFound);
        }

        IQueryable<ReviewCaseComment> comments = _dbContext.ReviewCaseComments
            .AsNoTracking()
            .Where(comment => comment.OrganizationId == organizationId
                && comment.ReviewCaseId == reviewCaseId);

        long totalCount = await comments.LongCountAsync(cancellationToken);
        long offset = ((long)page - 1L) * pageSize;
        List<ReviewCaseComment> rows = await comments
            .OrderBy(comment => comment.RecordedAt)
            .ThenBy(comment => comment.Id)
            .Skip(checked((int)offset))
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        ReviewCaseCommentResponse[] items = new ReviewCaseCommentResponse[rows.Count];
        for (int index = 0; index < rows.Count; index++)
        {
            ReviewCaseComment row = rows[index];
            items[index] = new ReviewCaseCommentResponse(
                row.Id.Value,
                row.Text,
                row.ActorId,
                row.RecordedAt);
        }

        return Result<ReviewCaseCommentListResponse>.Success(
            new ReviewCaseCommentListResponse(items, page, pageSize, totalCount));
    }
}