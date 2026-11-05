using FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Repositories;

internal sealed class ReviewCaseAuditWriter : IReviewCaseAuditWriter
{
    private readonly ReconciliationDbContext _dbContext;

    public ReviewCaseAuditWriter(ReconciliationDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    public void AppendOpened(ReviewCase reviewCase, string actorId)
    {
        ArgumentNullException.ThrowIfNull(reviewCase);
        ArgumentNullException.ThrowIfNull(actorId);

        _dbContext.ReviewCaseAuditEntries.Add(
            ReviewCaseAuditEntry.ForOpened(ReviewCaseAuditEntryId.New(), reviewCase, actorId));
    }

    public void AppendResolved(ReviewCase reviewCase, string actorId)
    {
        ArgumentNullException.ThrowIfNull(reviewCase);
        ArgumentNullException.ThrowIfNull(actorId);

        _dbContext.ReviewCaseAuditEntries.Add(
            ReviewCaseAuditEntry.ForResolved(ReviewCaseAuditEntryId.New(), reviewCase, actorId));
    }

    public void AppendDismissed(ReviewCase reviewCase, string actorId)
    {
        ArgumentNullException.ThrowIfNull(reviewCase);
        ArgumentNullException.ThrowIfNull(actorId);

        _dbContext.ReviewCaseAuditEntries.Add(
            ReviewCaseAuditEntry.ForDismissed(ReviewCaseAuditEntryId.New(), reviewCase, actorId));
    }
}