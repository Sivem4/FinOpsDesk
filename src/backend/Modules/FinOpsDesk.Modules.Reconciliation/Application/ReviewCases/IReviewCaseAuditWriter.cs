using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;

namespace FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;

public interface IReviewCaseAuditWriter
{
    void AppendOpened(ReviewCase reviewCase, string actorId);

    void AppendResolved(ReviewCase reviewCase, string actorId);

    void AppendDismissed(ReviewCase reviewCase, string actorId);
}