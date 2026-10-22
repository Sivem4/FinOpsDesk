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
        ArgumentNullException.ThrowIfNull(error);

        DataRowIndex = RequireDataRowIndex(dataRowIndex);
        SourceLineNumber = RequireSourceLineNumber(sourceLineNumber);
        ErrorCode = error.Code;
        Message = error.Message;
        ColumnName = NormalizeColumnName(columnName);
    }

    // EF Core binds constructor parameters to properties by name.
    // The public constructor's Error argument does not match a stored column.
    private ImportRowError(
        int dataRowIndex,
        string errorCode,
        string message,
        int? sourceLineNumber,
        string? columnName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        DataRowIndex = RequireDataRowIndex(dataRowIndex);
        SourceLineNumber = RequireSourceLineNumber(sourceLineNumber);
        ErrorCode = errorCode;
        Message = message;
        ColumnName = NormalizeColumnName(columnName);
    }

    private static int RequireDataRowIndex(int dataRowIndex)
    {
        if (dataRowIndex < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dataRowIndex),
                "Data row index must be at least 1.");
        }

        return dataRowIndex;
    }

    private static int? RequireSourceLineNumber(int? sourceLineNumber)
    {
        if (sourceLineNumber is < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceLineNumber),
                "Source line number must be at least 1.");
        }

        return sourceLineNumber;
    }

    private static string? NormalizeColumnName(string? columnName)
    {
        return string.IsNullOrWhiteSpace(columnName)
            ? null
            : columnName.Trim();
    }
}