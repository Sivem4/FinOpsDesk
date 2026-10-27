using Microsoft.Data.SqlClient;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;

internal static class SqlServerUniqueIndexes
{
    public static bool IsViolation(Exception exception, string indexName)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException
                && sqlException.Number is 2601 or 2627
                && sqlException.Message.Contains(indexName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}