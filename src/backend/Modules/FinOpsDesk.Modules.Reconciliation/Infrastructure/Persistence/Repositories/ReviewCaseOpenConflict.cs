using FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;

using Microsoft.Data.SqlClient;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Repositories;

internal sealed class ReviewCaseOpenConflict : IReviewCaseOpenConflict
{
    public bool IsOpenSubjectConflict(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException
                && sqlException.Number is 2601 or 2627
                && sqlException.Message.Contains(
                    ReviewCaseConfiguration.OpenSubjectIndexName,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}