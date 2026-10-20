using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Imports;
using FinOpsDesk.SharedKernel.Results;

namespace FinOpsDesk.UnitTests.Reconciliation.Domain.Imports;

public sealed class ImportRowErrorTests
{
    [Fact]
    public void Constructor_should_copy_error_code_and_message()
    {
        var rowError = new ImportRowError(
            2,
            ReconciliationErrors.ImportSourceConflict,
            sourceLineNumber: 15,
            columnName: " Amount ");

        Assert.Equal(2, rowError.DataRowIndex);
        Assert.Equal(15, rowError.SourceLineNumber);
        Assert.Equal(
            ReconciliationErrors.ImportSourceConflict.Code,
            rowError.ErrorCode);
        Assert.Equal(
            ReconciliationErrors.ImportSourceConflict.Message,
            rowError.Message);
        Assert.Equal("Amount", rowError.ColumnName);
    }

    [Fact]
    public void Blank_column_name_should_be_normalized_to_null()
    {
        var rowError = new ImportRowError(
            1,
            new Error("reconciliation.import.row_invalid", "The row is invalid."),
            columnName: "   ");

        Assert.Null(rowError.ColumnName);
        Assert.Null(rowError.SourceLineNumber);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_should_reject_non_positive_data_row_index(int dataRowIndex)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ImportRowError(
                dataRowIndex,
                ReconciliationErrors.ImportSourceConflict));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_should_reject_non_positive_source_line_number(
        int sourceLineNumber)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ImportRowError(
                1,
                ReconciliationErrors.ImportSourceConflict,
                sourceLineNumber));
    }

    [Fact]
    public void Constructor_should_reject_null_error()
    {
        Assert.Throws<ArgumentNullException>(
            () => new ImportRowError(1, null!));
    }

    [Fact]
    public void Equal_values_should_be_equal()
    {
        Error error = ReconciliationErrors.ImportSourceConflict;

        var left = new ImportRowError(4, error, sourceLineNumber: 9, columnName: "Amount");
        var right = new ImportRowError(4, error, sourceLineNumber: 9, columnName: " Amount ");

        Assert.Equal(left, right);
    }

    [Fact]
    public void Logical_index_should_differ_from_physical_line_number()
    {
        Error error = new(
            "reconciliation.import.row_invalid",
            "The row is invalid.");

        var logicalRow = new ImportRowError(1, error, sourceLineNumber: 8);
        var sameLineDifferentRow = new ImportRowError(2, error, sourceLineNumber: 8);
        var differentCode = new ImportRowError(
            1,
            ReconciliationErrors.ImportSourceConflict,
            sourceLineNumber: 8);

        Assert.Equal(1, logicalRow.DataRowIndex);
        Assert.Equal(8, logicalRow.SourceLineNumber);
        Assert.NotEqual(logicalRow, sameLineDifferentRow);
        Assert.NotEqual(logicalRow, differentCode);
    }
}