using FinOpsDesk.SharedKernel.Results;

namespace FinOpsDesk.Modules.Reconciliation.Domain.Imports;

public sealed record ImportRowError
{
    public int DataRowIndex { get; }

    public int? SourceLineNumber { get; }

    public string ErrorCode { get; }

    public string Message { get; }

    public string? ColumnName { get; }

    public ImportRowError(
        int dataRowIndex,
        Error error,
        int? sourceLineNumber = null,
        string? columnName = null)
    {
        if (dataRowIndex < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dataRowIndex),
                "Data row index must be at least 1.");
        }

        ArgumentNullException.ThrowIfNull(error);

        if (sourceLineNumber is < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceLineNumber),
                "Source line number must be at least 1.");
        }

        DataRowIndex = dataRowIndex;
        SourceLineNumber = sourceLineNumber;
        ErrorCode = error.Code;
        Message = error.Message;
        ColumnName = NormalizeColumnName(columnName);
    }

    private static string? NormalizeColumnName(string? columnName)
    {
        return string.IsNullOrWhiteSpace(columnName)
            ? null
            : columnName.Trim();
    }
}