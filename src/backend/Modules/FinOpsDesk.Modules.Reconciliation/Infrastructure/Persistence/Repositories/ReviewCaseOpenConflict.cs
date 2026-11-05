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

internal sealed class ReviewCaseOpenAttemptConflict : IReviewCaseOpenAttemptConflict
{
    public bool IsPrimaryKeyConflict(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException
                && sqlException.Number == 2627
                && sqlException.Message.Contains(
                    ReviewCaseOpenAttemptConfiguration.PrimaryKeyName,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed class ReviewCaseAuditConflict : IReviewCaseAuditConflict
{
    public bool IsAuditUniquenessConflict(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException
                && sqlException.Number is 2601 or 2627
                && (sqlException.Message.Contains(
                        ReviewCaseAuditEntryConfiguration.ActionUniqueIndexName,
                        StringComparison.Ordinal)
                    || sqlException.Message.Contains(
                        ReviewCaseAuditEntryConfiguration.TerminalUniqueIndexName,
                        StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }
}